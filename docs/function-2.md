# Function 2 — GetLatestFieldTelemetry

## A. Assessment

The MVC application uses .NET 10, Identity cookies, Farmer/Agronomist/Admin roles, Azure SQL Farms/Sensors/SensorReadings and the existing FarmAccess service. Before this change, every dashboard fetched its latest 20 SQL readings. There is no separate Field SQL entity, no telemetry Table entity, no HTTP telemetry client and no equivalent function. Function 1 already uses the .NET 10 isolated worker, a polling Blob trigger and DroneAuditStorageConnection for Table audit writes.

Read-only inspection on 6 November 2026 verified **SensorTelemetry exists and is empty**, using the existing Function App connection after explicit user approval. The local Entra identity initially received a Table read 403. No credential was printed or saved, and no records or Azure settings were changed. Since the table has no rows and there is no Table telemetry producer in this repository, there is no existing key/property schema to preserve. A farm/field relationship cannot be inferred safely from empty storage. The implementation therefore requires an explicit server-configured field-to-SQL-farm mapping.

## B. Implementation

- `AgriStream.Telemetry`: small shared library for JSON DTOs, safe field/sensor identifiers and short-lived HMAC field-access assertions.
- `AgriStream.Functions/GetLatestFieldTelemetry.cs`: GET route, input/authentication checks, JSON output and safe 400/403/503 responses.
- `SensorTelemetryReader.cs`: asynchronous partition/prefix query of SensorTelemetry with bounded snapshot results and actual measurement-time ordering.
- `StorageClients.cs`: factors the existing Table factory to reuse DroneAuditStorageConnection for either table. The original audit client still targets DroneImageAudit. The telemetry client is separately registered and lazily initialized, so missing Function 2 configuration does not prevent Function 1 from running.
- Functions project/Program: adds the HTTP extension (3.3.0), shared project and telemetry reader registration. Function 1 handler, audit entity and audit writer remain unchanged.
- MVC typed `FieldTelemetryClient`: HTTPS-only, five-second HttpClient timeout, redirects disabled, credentials in headers, response validation and cancellation support.
- MVC `FarmerTelemetryDashboard`: uses existing FarmAccess and SQL FarmerId to check ownership before signing or making an HTTP call.
- Dashboard controller/view/model: reuses the existing layout; Farmer view calls Function 2 for the selected owned field and displays latest readings, empty results or a friendly unavailable message. Agronomist/Admin SQL views are preserved. Farmer requests skip the unused SQL history query.
- New checks cover the function, signed access, field isolation, MVC ownership and service failure handling. No SQL schema changes or Functions 3–4 were introduced.

## C. HTTP contract and security

Function name: **GetLatestFieldTelemetry**. Authorization: **Function**. Method: **GET**. Route:

```text
GET /api/fields/{fieldId}/telemetry/latest
```

Field/sensor identifiers accept 1–64 ASCII letters/digits/underscore/hyphen, beginning with a letter/digit. Example response:

```json
{
  "fieldId": "field-001",
  "readings": [
    {
      "sensorId": "sensor-001",
      "soilMoisturePercent": 35.5,
      "temperatureCelsius": 24,
      "humidityPercent": 50,
      "recordedAt": "2026-11-06T10:00:00+00:00"
    }
  ]
}
```

Returns 200 with an empty readings array when no snapshots exist; 400 for invalid identifiers; 403 for missing/invalid field-access assertions; 503 for storage/schema/limit problems or missing signing configuration. Responses use Cache-Control: no-store and never expose exception details. The Functions host enforces the Function key. The function has no independent authoritative field catalogue, so an empty partition alone cannot establish a 404. MVC returns 404 for unmapped or unowned fields before any Function request, and 400 for malformed identifiers.

The browser submits only a field identifier to MVC. The server checks the configured field-to-farm relationship and confirms that the authenticated Farmer owns that SQL FarmId. It then calls the function using x-functions-key and a signed X-AgriStream-Field-Access header. The assertion contains the field, authenticated user ID and a 60-second expiry. Function 2 verifies the signature/expiry/field binding before querying storage. A Function key or a spoofed user/field header alone cannot grant access. Keep the signing key independent of the Function key, restrict both secrets to trusted backends and do not expose either in JavaScript. The assertion is a short-lived bearer credential; use HTTPS and do not log it. A trusted MVC server is responsible for ownership verification and signs only after that check.

## D. SensorTelemetry schema and query

This is a **latest-per-sensor snapshot read model**, not a history scan:

| Property | Value/type |
| --- | --- |
| PartitionKey | Exact configured field ID, e.g. `field-001` |
| RowKey | `latest|` followed by SensorId, e.g. `latest|sensor-001` |
| SensorId | String matching the suffix of RowKey |
| SoilMoisturePercent | Required numeric value, 0–100; store as Table Double |
| TemperatureCelsius | Optional numeric value / Table Double |
| HumidityPercent | Optional numeric value, 0–100 / Table Double |
| RecordedAt | Required Table DateTimeOffset: actual sensor measurement time |

No Table is created automatically. No telemetry has been seeded or migrated. Existing/history rows are never changed by the GET function. History may coexist under other RowKey prefixes; dashboard queries explicitly exclude it.

The query uses PartitionKey equality plus `RowKey ge 'latest|' and RowKey lt 'latest}'`. It projects only required columns, reads asynchronously with cancellation and stops after at most 101 snapshots. Up to 100 sensors are returned, ordered by RecordedAt descending and then SensorId. Exceeding the limit returns 503 rather than returning an arbitrary incomplete set. Table Timestamp is never substituted for the actual measurement time.

**Producer prerequisite:** The telemetry writer must maintain one snapshot per field/sensor and retain the newest measurement. Use ETag-conditional updates with timestamp comparisons to prevent late/out-of-order messages replacing newer snapshots. That producer does not currently exist in this repository and is outside Function 2. If a separate producer already exists, align it with this contract before expecting populated dashboards. Arbitrary historical rows will not automatically appear as latest snapshots. No measured latency claim is made; indexed bounded reads avoid normal dashboard history scans.

## E. Configuration

Function App **func-st10541963-agristream** retains Windows B1, Functions v4, .NET 10 isolated, 64-bit and Always On. Function 1 retains its polling trigger and settings. Add:

| Function App setting | Purpose |
| --- | --- |
| `DroneAuditStorageConnection` | Existing storage connection with SensorTelemetry read access; unchanged |
| `TelemetryAccess__SigningKey` | New shared random secret of at least 32 UTF-8 bytes; must match MVC |

Existing identity-based `DroneAuditStorageConnection__tableServiceUri` and optional `__clientId` are also supported. Grant the deployed identity Table read permission if using identity; Function 1 separately needs audit write permission.

MVC environment variables:

| Setting | Value |
| --- | --- |
| `TelemetryFunctions__BaseUrl` | Copy the actual Function App HTTPS hostname from Portal, ending `/api` |
| `TelemetryFunctions__FunctionKey` | GetLatestFieldTelemetry-specific Function key after publishing |
| `TelemetryAccess__SigningKey` | Same independently generated secret as Function App |
| `TelemetryFunctions__Fields__field-001__FarmId` | Actual SQL FarmId that owns field-001; configure each field explicitly |

`AgriStream/appsettings.Telemetry.example.json` is an example only, not automatically loaded. It contains no real key and its sample mapping is not an assertion of actual ownership. Put real configuration in environment variables, local user-secrets or private configuration. Never commit populated secrets. Missing/unavailable configuration displays a friendly message rather than crashing the Farmer dashboard.

Generate a strong shared secret locally, for example using `[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))`, and store it privately in both apps. Do not reuse SQL, storage-account or Function keys as the signing secret.

## F. Build, tests and manual publishing

```powershell
dotnet build AgriStream.Functions/AgriStream.Functions.csproj -c Release
dotnet run --project AgriStream.Functions.Checks -c Release
dotnet build AgriStream/AgriStream.csproj -c Release
dotnet run --project AgriStream.Checks -c Release
```

Console verification suites invoke the real function handler with SDK/request doubles and the real MVC ownership service against test-only in-memory EF. Runtime Function-key enforcement, actual HTTP hosting and live Azure data-plane writes are not simulated. No telemetry producer, latency/load test or end-to-end deployed dashboard test was run.

Verified on 6 November 2026: Functions and MVC Release builds succeeded with zero warnings/errors. The existing Function 1 and farm-access suites passed; Function 2 passed 17 new assertions and MVC telemetry integration passed 13 new assertions (zero failed). An intermediate sandbox MSBuild invocation failed without compiler diagnostics; the clean verification builds outside the sandbox passed. Release publishing to local disk also succeeded. Generated metadata contains exactly the GET Function 2 route and the unchanged polling Function 1; the local package includes AgriStream.Telemetry.dll. SHA-256 comparison confirmed Function 1's handler, audit entity and audit writer were unchanged. No Azure deployment or live end-to-end invocation was performed.

Publish the existing AgriStream.Functions project through Visual Studio to the same Function App; both functions are included. Do not create another Function App. Configure the signing secret, obtain the new function-specific key, configure MVC HTTPS URL/key and validated field mappings, and insert/produce a valid owned-field snapshot through your authorized telemetry producer. Then sign in as that farm's Farmer and open Monitoring or `/Dashboard/Field?fieldId=field-001`. Confirm the timestamp/value display, empty-field behaviour and a friendly message if the function is unavailable. Verify another Farmer cannot open the field. Test Function 1 with an image upload after republishing.

There is no Event Grid requirement for either function. Neither publishing nor Azure configuration/data mutation was performed by this implementation task.
