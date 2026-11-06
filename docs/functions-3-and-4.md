# Functions 3 and 4 — irrigation commands

## Assessment and scope

Read-only inspection of Azure SQL on 6 November 2026 found the existing Farms, Login/roles and Sensors tables. Sensors has Id, FarmId, DeviceId, Name and IsActive only. There was no irrigation controller registry, operational state or command ledger. Existing SQL owners are Farmer 1 for farms 1/3 and Farmer 2 for farm 2. Soil sensor IDs are not irrigation actuators, so no soil devices were relabelled or given control state.

The implementation adds the minimum separate registry/ledger and reuses existing Identity authentication, field mappings, SQL connection convention, Functions v4 isolated worker and MVC dashboard. The existing Function 1/2 handlers and telemetry reader are unchanged. No Azure resource was created, no migration applied, and no deployment performed.

## Architecture and request flow

1. An Admin registers an irrigation controller against an existing FarmId and mapped FieldId in MVC. The registry starts at Idle.
2. The Farmer dashboard lists controllers only from farms owned by that authenticated Farmer, filtered by configured field mapping and selected field. Enabled devices show **Trigger Irrigation** and **Request stop**.
3. The form posts to MVC with antiforgery protection. A server-protected, user-bound form token contains a generated Guid command ID and immutable device/farm/field/action, expiring after 10 minutes. Ownership/enabled status are rechecked on POST. Duplicate clicks/retries use the same command ID. Browser JavaScript disables the submitted button; database idempotency provides the substantive duplicate protection.
4. MVC calls **SubmitIrrigationCommand** server-to-server using its Function-specific key and a separate command-bound HMAC assertion. The assertion expires after 60 seconds. No key or assertion is exposed to the browser.
5. Function 3 independently checks device, farm and field against SQL IrrigationDevices and verifies current Farmer ownership/role through Farms, LoginRoles and Roles. It does not trust a plain caller-supplied user header or farm ID. A completed command ID with another payload returns 409. A matching completed command returns 202/AlreadyProcessed without requeueing.
6. Function 3 signs the internal queue envelope (domain-separated from the HTTP signature) and awaits a durable Azure Queue send to **irrigation-commands** before returning **202 Accepted**. Sender uses Base64 encoding, matching the queue extension default. It never creates a queue automatically.
7. **ProcessIrrigationCommand** verifies/deserializes the envelope and rechecks current SQL authorisation. A serializable transaction updates the registry and inserts a permanent command ledger row. SQL Server uses a transaction-scoped per-command application lock plus UPDLOCK/HOLDLOCK on the device row. Concurrent deliveries cannot repeat an already completed command. Same ID/different payload is invalid; older accepted commands are recorded as Superseded rather than regressing device state.
8. MVC displays immediate queued feedback. Refreshing the dashboard shows the registry's last processing status, command ID, action, UTC processing time and outcome. Failed/uncertain POSTs offer **Retry same command**, retaining the original ID, rather than issuing a new one.

An HTTP send can be retried and create multiple queue messages for one command ID; this is safe because processing is idempotent. There is no SQL/Queue distributed transaction or exactly-once transport guarantee. One successfully committed command ledger row is the processing boundary. Keep the ledger to retain deduplication history.

## Function contracts

### Function 3

Name: **SubmitIrrigationCommand**. Function-level authorization. Route:

```text
POST /api/irrigation/commands
```

Example JSON (the MVC backend constructs this; a browser cannot bypass the protected form):

```json
{
  "commandId": "5f41efcc-a35c-4bb2-8edc-b4aaab342c11",
  "deviceId": "irrigator-001",
  "farmId": 1,
  "fieldId": "field-001",
  "action": "Start",
  "source": "Farmer"
}
```

Actions are exactly Start/Stop; source is Farmer/Sensor. Field/device IDs follow the existing safe 1–64 character identifier convention. Body limit is 8 KiB. Required headers are x-functions-key and X-AgriStream-Irrigation-Access. A Function key alone is insufficient. The signed actor is extracted from the assertion and checked against authoritative SQL ownership, rather than read from an untrusted ownership field.

Responses: 202 with `{ "commandId": "...", "status": "Queued" }` or AlreadyProcessed; 400 for invalid JSON/command; 403 for missing/forged/expired assertion or unowned/disabled/mismatched device; 409 for reuse of a completed ID with another payload; 503 for SQL/queue/configuration failure. No queue success is reported before persistence. Error logs contain command IDs and safe error types, not keys, SQL connections or raw requests.

### Function 4

Name: **ProcessIrrigationCommand**. Queue trigger: `irrigation-commands`, connection name **IrrigationQueueConnection**. Queue envelopes contain the command, validated actor, server acceptance timestamp and internal signature. Do not manually compose unsigned production messages in Portal: use MVC/Function 3.

Transient SQL/storage failures are thrown so the Functions runtime retries. SQL Server's execution strategy also retries transient transaction failures. Malformed JSON, invalid signatures, mismatched IDs and permanently unauthorized devices are logged and thrown; after the existing host maxDequeueCount of 5, the runtime moves them to **irrigation-commands-poison**. Monitor that queue and function failures. Investigate and replay only valid signed commands after correcting the cause. No separate poison handler or silent discard was introduced.

## SQL migration and expected state

Migration **IrrigationWorkflow** belongs to the existing MVC ApplicationDbContext. It creates only:

- **IrrigationDevices**: DeviceId PK, FarmId FK to existing Farms, FieldId, Name, IsEnabled, optional SensorSourceId, OperationalStatus, LastCommandId, LastAction, LastProcessedAt, LastAcceptedAt and LastOutcome.
- **IrrigationCommands**: CommandId PK, DeviceId FK, farm/field/action/source/actor, payload hash, acceptance/processing timestamps and outcome, with an index for device history.

The shared `AgriStream.Irrigation` library provides these entities and a scoped irrigation-only EF context for the Functions. The MVC context owns the migration; Functions never run EnsureCreated/Migrate against production. Existing Farms, Sensors, telemetry, audit and Identity tables are preserved.

For an enabled device:

| Event | Registry state | Ledger |
| --- | --- | --- |
| Registration | Idle | No command |
| Function 3 queues Start | Remains previous state | Not yet processed |
| Function 4 commits Start | IrrigationRequested | Processed |
| Same completed command delivered again | Unchanged | Same single row |
| Function 4 commits Stop | StopRequested | Processed |
| Older accepted command arrives | Unchanged | Superseded |

LastOutcome explicitly says **Requested state recorded; no physical actuator integration.** This pipeline does not activate hardware. The command ledger records processing success, not measured watering or physical completion. Queue ordering is not guaranteed; the first processed envelope for a given command ID establishes that command's processing result.

## Required Azure settings

Keep existing Function App **func-st10541963-agristream**, Storage Account **st10541963agristreamiot**, Windows B1, Functions v4, .NET 10 isolated, 64-bit, and Always On. Keep all Function 1/2 settings unchanged. Configure the queue connection **before publishing**, since an unresolved queue-trigger connection can prevent correct host indexing/listening.

Function App additions:

| Setting | Value/purpose |
| --- | --- |
| `IrrigationQueueConnection` | Private Azure Storage connection string for st10541963agristreamiot, with queue access |
| `ConnectionStrings__DefaultConnection` | Existing Azure SQL connection, securely configured for the deployed identity/login |
| `IrrigationAccess__SigningKey` | New independent random secret, at least 32 UTF-8 bytes; same value in MVC |
| `IrrigationAccess__SensorSigningKey` | Optional separate secret only for a trusted future sensor automation backend; omit for the exam Farmer flow |

Identity alternative: omit the queue connection string and configure `IrrigationQueueConnection__queueServiceUri=https://st10541963agristreamiot.queue.core.windows.net`, `IrrigationQueueConnection__credential=managedidentity`, and optional `IrrigationQueueConnection__clientId` for a user-assigned identity. Grant Storage Queue Data Contributor on the source account for sending/processing/poison handling. The SDK uses DefaultAzureCredential; local developer sign-in is not the deployed identity.

For SQL managed identity, grant database access to the Function App's identity and use the corresponding Azure SQL authentication connection string. It needs SELECT on Farms, LoginRoles and Roles; SELECT/UPDATE on IrrigationDevices; SELECT/INSERT on IrrigationCommands; and permission to obtain transaction-owned application locks (normally available through the public database principal). Use a migration/deployment identity with schema privileges separately. SQL credentials from local developer tooling do not automatically exist in Azure.

MVC additions (environment variables in hosting, user-secrets locally):

| Setting | Purpose |
| --- | --- |
| `Irrigation__Enabled=true` | Enable after migration/settings are ready; default false protects the working dashboard before schema deployment |
| `IrrigationFunctions__BaseUrl` | Optional HTTPS Function App URL ending `/api`; falls back to existing corrected TelemetryFunctions:BaseUrl |
| `IrrigationFunctions__FunctionKey` | New SubmitIrrigationCommand function-specific key |
| `IrrigationAccess__SigningKey` | Same new irrigation signing secret as Function App; do not reuse/expose the telemetry key |

Existing `TelemetryFunctions:Fields:*:FarmId` mappings are reused for controller provisioning/control. Do not change their ownership meaning. No secret is hardcoded or committed.

## Exact setup and publishing steps

1. Review the migration locally. From the repository root:

   ```powershell
   dotnet tool restore
   dotnet ef migrations script --idempotent --project AgriStream --context ApplicationDbContext --output .local/irrigation-migrations.sql
   ```

   Inspect the new migration (two additive tables only). The idempotent script also includes guards for earlier migrations. Apply it manually through Azure SQL Query Editor/your SQL tool, or deliberately run the following with the existing configured SQL connection:

   ```powershell
   dotnet ef database update --project AgriStream --context ApplicationDbContext
   ```

   These application commands were **not** executed against Azure by this implementation task.

2. In Azure Portal → st10541963agristreamiot → Storage browser → Queues, create **irrigation-commands**. The queue's existence has not been assumed or verified by this task. The runtime creates **irrigation-commands-poison** when needed, provided it has permission; you may provision it explicitly for monitoring.
3. In Function App → Settings → Environment variables, add the three required irrigation settings above. Retain AzureWebJobsStorage, DroneStorageConnection, DroneAuditStorageConnection, TelemetryAccess signing configuration, FUNCTIONS_WORKER_RUNTIME and FUNCTIONS_EXTENSION_VERSION. Never paste credentials into source or screenshots.
4. In Azure SQL logical server → Networking, allow the Function App's outbound IP addresses (from its Properties) or use your already configured private access path. Verify its actual database identity/permissions. Do not assume a successful local SQL connection proves cloud access.
5. Open **AgriStream/AgriStream.slnx** in Visual Studio. Build Release. Right-click **AgriStream.Functions → Publish**, select the existing profile for **func-st10541963-agristream**, confirm the target, and publish only when you approve deployment. No new Function App or storage account is needed. This package contains all four functions; Functions 1/2 are preserved.
6. In Portal → Function App → Functions, confirm **SubmitIrrigationCommand** and **ProcessIrrigationCommand** appear alongside the two existing functions. Under SubmitIrrigationCommand → Function Keys, create/copy its dedicated key to private MVC configuration. Match the irrigation signing secret in both apps.
7. Configure MVC, enable the feature and restart your local MVC app or deploy MVC separately through its existing publishing workflow. Example local setting names:

   ```powershell
   dotnet user-secrets set "Irrigation:Enabled" "true" --project AgriStream
   # Set IrrigationFunctions:FunctionKey and IrrigationAccess:SigningKey privately.
   # The existing TelemetryFunctions:BaseUrl already includes /api and may be reused.
   ```

8. Sign in as Admin → Dashboard → **Register irrigation device**. Register `irrigator-001` with Farm 1 / field-001, `irrigator-002` with Farm 2 / field-002, and `irrigator-003` with Farm 3 / field-003 as desired. These are explicit demo controller registrations, not discoveries of physical hardware. No registry records were inserted automatically.

## Demonstrate the complete pipeline

1. Sign in as Farmer 1, open field-001, and confirm existing telemetry still renders.
2. Observe the registered device at Idle. Click **Trigger Irrigation**. Capture the command ID and queued acknowledgement.
3. In Function App logs, find the Function 3 queued outcome and Function 4 Processed outcome for the same command ID. The consumer may process quickly, so the message need not remain visible in Storage browser.
4. Refresh the dashboard; see IrrigationRequested, last action Start, command ID and UTC processing time. In SQL, confirm exactly one corresponding ledger row:

   ```sql
   SELECT DeviceId,FarmId,FieldId,OperationalStatus,LastCommandId,LastAction,LastProcessedAt,LastOutcome
   FROM dbo.IrrigationDevices ORDER BY DeviceId;
   SELECT CommandId,DeviceId,Action,Source,AcceptedAt,ProcessedAt,Outcome
   FROM dbo.IrrigationCommands ORDER BY ProcessedAt DESC;
   ```

5. Use **Request stop**; verify StopRequested and a second command. Attempt field-002 as Farmer 1; the ownership checks deny access/control. Farmer 2 sees only its own controllers.
6. For duplicate evidence, resubmit the same protected form/request before its expiry or replay the same unchanged signed queue message; confirm no second ledger row for that command ID. A fresh button/form intentionally creates a new command ID.
7. In a controlled test, an invalid queue message is retried and ultimately appears in irrigation-commands-poison. Do not disable live account permissions casually. Never label SQL requested state as physical watering.
8. Recheck Function 1 with an image upload and Function 2 with the field dashboard after publishing.

Suggested exam screenshots: all four Functions in Portal; queue names; masked setting names (never values); Admin registry form; Farmer telemetry plus irrigation controls; queued acknowledgement/command ID; Function 3/4 logs matched by command ID; SQL registry and unique ledger row; poison queue in a deliberate invalid-message test; Farmer 2's isolated device view.

## Sensor automation boundary

There is no existing ingestion/threshold automation to wire up. No automatic sensor-trigger-to-irrigation workflow is claimed. Function 3 accepts Sensor commands only when a separate SensorSigningKey is configured and the target registry device has a matching SensorSourceId. A future trusted backend must generate a command-bound assertion using that secret, post the same command contract and use stable event-derived command IDs. Do not put that backend signing secret in untrusted sensors or browser JavaScript. Function 4 rechecks the provisioned sensor association. Without those deliberate provisions, Sensor requests are rejected.

## Verification

### Verified submission repair — 6 November 2026

For command `f8a8e4d0-78f0-4fca-9b0f-db7204014861`, investigation found three configuration blockers:

1. The local `IrrigationFunctions:BaseUrl` was the Function App origin without `/api`, overriding the correct telemetry fallback. The resulting POST returned 404. It was corrected to `https://func-st10541963-agristream-aub3dxh6hwaxdsgg.southafricanorth-01.azurewebsites.net/api`.
2. The deployed SQL connection used Active Directory Default but the Function App had no managed identity. With explicit user approval, its system-assigned identity was enabled and the deployed SQL connection authentication changed to **Active Directory Managed Identity**. Signing keys matched and were left unchanged.
3. SQL firewall rules allowed only the developer's client IP. With approval, seven exact rules were added for the Function App's listed outbound addresses, retaining existing rules. The app's contained Entra SQL user was granted CONNECT; SELECT on Farms/LoginRoles/Roles; SELECT/UPDATE on IrrigationDevices; SELECT/INSERT on IrrigationCommands. No broad database role or schema permission was granted.

The irrigation queue was verified in the existing storage account. Before retry, it was empty and the command had no SQL ledger row. A signed non-owner probe subsequently reached SQL and returned 403, proving key/signature/expiry/database authorisation worked. Retrying the original Farmer request with the **same command ID** returned 202/Queued in approximately 1.2 seconds. Function 4 recorded one Processed ledger row and set device101 to IrrigationRequested with that last command ID; a subsequent replay returned 202/AlreadyProcessed without queueing another command. Function 2 still returned all three field-001 readings. No physical device activation is claimed.

MVC logging now records command ID, safe error type, HTTP status and timeout flag without response bodies, credentials or signatures. Release build passed with zero warnings/errors; MVC irrigation checks passed 14 assertions, Function/SQL irrigation checks passed 30, and the existing telemetry/farm/Function 1 regression checks passed. No function source changed or code was deployed during the repair. Restart local MVC after a configuration change; deployed MVC environments require the same corrected URL in `IrrigationFunctions__BaseUrl`.

Run:

```powershell
dotnet build AgriStream/AgriStream.slnx -c Release
dotnet run --project AgriStream.Functions.Checks -c Release
dotnet run --project AgriStream.Checks -c Release
```

Tests use real EF transactions in a local in-memory SQLite database for the processing store, SDK/request doubles for HTTP/queues and in-memory EF for MVC ownership checks. They verify POST enqueue, invalid/unauthorized input, cross-farm protection, deserialization, requested state and ledger updates, completed-command deduplication, payload collisions, stop/out-of-order handling, invalid/poison exception propagation and simulated transient SQL retry. SQL Server-specific UPDLOCK/HOLDLOCK/application-lock concurrency is implemented but not exercised by SQLite; real Azure queue retry/poison transfer, SQL networking and a physical actuator have not been tested. Do not claim measured end-to-end performance or hardware activation.

Local Release verification: clean solution build, zero warnings/errors; existing Function 1 suite and Function 2's 17 assertions passed; 30 new Function/SQL irrigation assertions passed; existing farm-access suite and 13 telemetry MVC assertions passed; 12 new MVC irrigation assertions passed. Tests include ledger-failure transaction rollback, current ownership reassignment, and optional trusted sensor association. The migration has been generated and reviewed but not applied. Azure configuration, registry provisioning and end-to-end tests follow manual deployment.

Official references: [Queue trigger retries and poison handling](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-storage-queue-trigger), [queue configuration and encoding](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-storage-queue).
