# Function 1: ProcessDroneImageUpload

## A. Existing implementation assessment

Assessment completed before modifying code. The repository contains `AgriStream` (ASP.NET Core MVC, .NET 10) and `AgriStream.Checks` (console verification runner). There was no Azure Functions project, worker model, Blob SDK integration, upload endpoint, Table Storage client, audit entity or Function 1 test.

The web application uses ASP.NET Core Identity/roles, EF Core SQL Server, constructor injection, application configuration/environment variables and normal ASP.NET error handling. Its packages include Identity EF/UI 10.0.12, EF SQL Server/Design 10.0.12 and a cryptography patch. Its `DroneImage` entity stores SQL image metadata, not Table audit records. Existing checks cover farm permissions, SQL model constraints, and optional live SQL data. Those checks are reusable for web regression verification but do not cover storage events.

The SQL `DroneImage` entity has different responsibilities and fields from the required audit entity, so reusing it as a Table entity would couple unrelated storage concerns. The seeded `demo-drone-images` metadata is illustrative, not an existing Blob container or upload implementation. No existing implementation conflicts with Function 1. No web/authentication/schema changes are required.

## B. Changes made

- `AgriStream.Functions/AgriStream.Functions.csproj`: new standalone Functions v4, .NET 10 isolated worker project because no Functions project existed.
- `Program.cs`: worker startup and singleton, thread-safe SDK client/writer/time provider registration.
- `ProcessDroneImageUpload.cs`: the sole function, named **ProcessDroneImageUpload**, bound to `drone-images/{name}` through `DroneStorageConnection`, using the standard polling source (`LogsAndContainerScan`). Retrieves properties without downloading image bytes, logs upload details, awaits persistence, and logs/rethrows failures.
- `DroneImageAuditEntity.cs`: `ITableEntity` with all requested fields plus blob ETag/version and last-modified metadata. `UploadedAt` is the creation timestamp where present; processing time is UTC. Missing creation/MIME metadata remains null. `BlobPath` includes container and complete nested name; `FileName` is its leaf name. Blob URI excludes SAS/query credentials.
- `DroneImageAuditWriter.cs`: **AddEntityAsync only**. An atomic `EntityAlreadyExists` conflict is a safe duplicate; every other failure propagates. No upsert, status reset, read-before-write race or local deduplication cache.
- `StorageClients.cs`: configured Table client using a secret connection string or HTTPS endpoint plus Entra credentials. It does not create a table automatically.
- `host.json`: Functions host settings, bounded per-instance blob concurrency and queue retry limit.
- `local.settings.example.json`: emulator placeholders only; actual local settings are ignored and excluded from publishing.
- `AgriStream.Functions.Checks`: focused console checks and an explicit read-only Azure resource verification mode.
- `.gitignore`: excludes local Functions secrets and publish folders.
- `README.md`: links this implementation and deployment guide.

Partition key: blob **last-modified/upload-update date in UTC**, formatted `yyyyMMdd`. Row key: SHA-256 of a JSON tuple of credential-free URI, ETag and version ID. This keeps keys stable even across retry dates, separates updates and avoids forbidden key characters. All instances insert the same revision key. An existing `Completed`/other status and the original processing timestamp are preserved.

This is an upload detection/audit function only. No image analysis, SQL writes, upload UI or Functions 2–4 are implemented. The SDK reads the revision currently available when the handler runs. For an audit of every intermediate overwrite, use immutable upload names (recommended), and retain blob versions/event metadata as a future extension; fast overwrites can supersede a revision before properties are read. Updating a blob produces a separate audit when its new revision is observed.

## C. Build, tests and local publishing

From the repository root:

```powershell
dotnet build AgriStream
dotnet run --project AgriStream.Checks
dotnet build AgriStream.Functions/AgriStream.Functions.csproj
dotnet run --project AgriStream.Functions.Checks
dotnet run --project AgriStream.Functions.Checks -- --verify-azure
dotnet publish AgriStream.Functions/AgriStream.Functions.csproj -c Release -o .local/function1-publish
tar -a -c -f .local/function1.zip -C .local/function1-publish .
```

`dotnet publish` prepares local deployment files; it does not deploy. The archive must contain `host.json`, function metadata, dependencies and `.azurefunctions` at its root. Do not include local.settings.json. Upload/deploy the package manually using the deployment mechanism supported by your chosen Function App plan. No deployment command has been run.

The focused checks invoke the real handler with Azure SDK test doubles. They check the Function/Blob binding metadata, upload fields and logs, `Received` persistence, sequential/concurrent duplicates, preserving `Completed`, distinct updated revisions, optional metadata, and logged/rethrown metadata/table errors (including non-duplicate 409 errors). They do not simulate the Functions host or actual polling-based upload detection. That must be tested after deployment.

Executed results on 6 November 2026: Function 1 checks passed; existing `AgriStream.Checks` passed; MVC build succeeded with zero warnings/errors; Functions build and Release publish succeeded. An initial sandbox restore/audit network warning was resolved by rerunning publish with network access. The generated Functions metadata was inspected and contains exactly one `ProcessDroneImageUpload` polling-based BlobTrigger with the intended container/connection. Local deployment output is `.local/function1-publish` and archive `.local/function1.zip`. No live Blob upload or audit write test was performed; the read-only resource check below found the table missing.

Read-only Azure resource check performed on 6 November 2026 using existing Entra credentials:

| Resource | Observed result |
| --- | --- |
| `st10541963agristreamiot` / `drone-images` | Container verified present |
| `st10541963agristreamiot` / `DroneImageAudit` | Table query succeeded, table not found |

No resources, table rows or image blobs were created by verification. The check does not verify the account region, deployed Function App write permissions or actual polling execution.

## D. Azure Portal configuration

1. In the existing storage account **st10541963agristreamiot**, retain the private-access **drone-images** Blob container. Under Storage browser / Tables, manually create **DroneImageAudit** (case-sensitive). Do not create a Cosmos DB SQL table instead.
2. Create/configure a Function App in **South Africa North**, Functions **v4**, **.NET 10 isolated**. The existing Windows Basic B1 App Service plan is compatible. Keep Always On enabled, the worker 64-bit, the .NET stack at 10, and the worker model isolated. This polling trigger does not support Flex Consumption. Enable Application Insights/logging for invocation monitoring.
3. Configure these application environment variables using private settings or Key Vault references. They may all target the same storage account:

| Setting | Purpose/value |
| --- | --- |
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` |
| `FUNCTIONS_EXTENSION_VERSION` | `~4` (where supported/required by the selected plan) |
| `AzureWebJobsStorage` | Real host storage connection string, kept out of source |
| `DroneStorageConnection` | Real connection string for blob and queue services in the source account |
| `DroneAuditStorageConnection` | Real Table-capable connection string for the account containing DroneImageAudit |

For secret-free system-assigned managed identity configuration, omit the corresponding connection-string settings and use:

```text
AzureWebJobsStorage__accountName=st10541963agristreamiot
AzureWebJobsStorage__credential=managedidentity
DroneStorageConnection__blobServiceUri=https://st10541963agristreamiot.blob.core.windows.net
DroneStorageConnection__queueServiceUri=https://st10541963agristreamiot.queue.core.windows.net
DroneStorageConnection__credential=managedidentity
DroneAuditStorageConnection__tableServiceUri=https://st10541963agristreamiot.table.core.windows.net
```

The Table SDK uses DefaultAzureCredential; in Azure this can use the enabled managed identity. For a user-assigned identity, configure the source/host identity client IDs according to Functions connection guidance, and `DroneAuditStorageConnection__clientId` for the Table SDK. The developer's local Azure login does not become the deployed app's identity.

For managed identity, assign **Storage Blob Data Owner** and **Storage Queue Data Contributor** for the Blob trigger/host storage, and **Storage Table Data Contributor** for audit writes. Host storage can require additional account-management permission (Storage Account Contributor) for its initialization/management operations; follow the hosting/identity guidance for the selected setup. A management-plane Contributor/Owner assignment alone does not grant these data-plane permissions. Ensure storage networking permits the Function App to reach Blob, Queue and Table services. Never paste keys into code or committed JSON.

4. Republish through Visual Studio to **func-st10541963-agristream**. The standard polling listener monitors `drone-images/{name}` through `DroneStorageConnection`. No Event Grid subscription, System Topic, webhook or `Microsoft.EventGrid/systemTopics/write` permission is required. Keep host storage and source-account Queue Storage accessible for receipts, trigger coordination and poison handling.
5. Monitor invocation failures and the **webjobs-blobtrigger-poison** queue after the configured retry limit. Fix storage/permission problems and replay intentionally. Failed audit writes fail the invocation and never count as successful inserts. Polling detection is asynchronous; do not expect immediate processing after every upload.

Required direct NuGet packages (versions pinned in the project):

| Package | Version |
| --- | --- |
| Microsoft.Azure.Functions.Worker | 2.52.0 |
| Microsoft.Azure.Functions.Worker.Sdk | 2.1.0 |
| Microsoft.Azure.Functions.Worker.Extensions.Storage.Blobs | 6.8.2 |
| Azure.Data.Tables | 12.13.0 |
| Azure.Core | 1.62.0 |
| Azure.Identity | 1.21.0 |

Azure.Storage.Blobs is supplied by the Blob extension dependency. The matching Azure.Identity version forwards credentials supplied by the newer Azure.Core, avoiding duplicate type definitions from older Identity packages.

## E. Deployment readiness and post-deployment test

The implementation is prepared for manual deployment; operational readiness requires creating the missing table, configuring the Function App, network/RBAC/settings and polling storage access. Nothing has been deployed. Azure Functions Core Tools is not installed on this machine, so the Functions host/event-delivery path has not been run locally.

After deploying:

1. Upload a small image under a unique name, for example `drone-images/test-farm/field-001.jpg`, using Portal Storage browser or Storage Explorer. Confirm upload details in Function/Application Insights logs and a `Received` row in DroneImageAudit with matching properties, date key and deterministic hash key.
2. Upload another image at the same path (new ETag) and confirm a distinct audit row. Prefer unique names for production captures to retain every observation.
3. Retry the same unchanged blob invocation in a controlled test; confirm the same row is retained and no duplicate is inserted. Simply uploading identical bytes again is an update with a new ETag, not a duplicate-event test.
4. In a test row, manually set ProcessingStatus to Completed, then retry its unchanged blob invocation. Confirm Completed and its original audit fields remain unchanged.
5. In a controlled non-production test, temporarily deny the Table write permission or configure an invalid audit endpoint, upload a new image, and confirm error logs/failed invocation with no success log. Restore access and replay the failure; confirm one audit row and inspect the poison queue if retries were exhausted.

Official references: [isolated-worker runtime guidance](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide), [Blob trigger and identity configuration](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-storage-blob-trigger), [Functions connection configuration](https://learn.microsoft.com/en-us/azure/azure-functions/manage-connections).
