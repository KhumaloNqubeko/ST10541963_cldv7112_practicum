# Function 2 exam demonstration data — prepared, not inserted

## Verified mappings

On 6 November 2026, all three mappings were read from the MVC application's existing local user-secrets (`AgriPortal-local-development`): field-001 → Farm ID 1, field-002 → Farm ID 2, field-003 → Farm ID 3. Neither mapping configuration nor database records were changed. This verifies local MVC development configuration, not a deployed MVC App Service's settings. Each environment hosting MVC must have the same intended mappings.

## Exact SensorTelemetry schema

Storage account: `st10541963agristreamiot`. Existing table: `SensorTelemetry`.

| Property | Stored type | Requirement |
| --- | --- | --- |
| PartitionKey | String | Exact field ID, e.g. `field-001` |
| RowKey | String | `latest|` + SensorId, e.g. `latest|demo-f001-s01` |
| SensorId | String | Must equal the row-key suffix; 1–64 ASCII letters/digits/underscore/hyphen, beginning with a letter/digit |
| SoilMoisturePercent | Double | Required; 0–100 |
| TemperatureCelsius | Double | Supported, optional |
| HumidityPercent | Double | Supported, optional; 0–100 |
| RecordedAt | DateTimeOffset / Table DateTime | Required actual measurement time, stored as UTC |
| IsDemo | Boolean | Additional marker written by this utility; ignored by Function 2 |
| SeedBatch | String | `function2-exam-v1`; additional marker ignored by Function 2 |

Azure also maintains Timestamp and ETag. The utility does not supply these. Table Timestamp is not a replacement for RecordedAt. The reader accepts integer/long numeric values as well, but the utility stores Double consistently. Do not store a formatted number as a String or pass a CLR Decimal directly to Azure Tables. RecordedAt is passed to Azure.Data.Tables as an actual DateTimeOffset, **not a String**. Its display/JSON form is ISO 8601, such as `2026-11-06T10:00:00+00:00`; the utility's optional CLI timestamp uses `yyyy-MM-ddTHH:mm:ssZ`.

Function 2 queries one partition with `RowKey ge 'latest|' and RowKey lt 'latest}'`, projects these properties, and sorts the results by RecordedAt descending. It supports up to 100 latest sensor snapshots per field. The test plan is compatible with the existing reader without modifying either Function.

## Proposed nine records

Default anchor: **6 November 2026, 10:00 UTC / 12:00 South African time**. These are fixed synthetic demonstration values, not live observations. Use the same `--recorded-at` value for preview and apply if you choose another anchor.

| Field | SensorId | Moisture % | Temperature °C | Humidity % | RecordedAt (UTC) |
| --- | --- | ---: | ---: | ---: | --- |
| field-001 | demo-f001-s01 | 34.8 | 24.2 | 58.4 | 2026-11-06T10:00:00Z |
| field-001 | demo-f001-s02 | 29.6 | 25.1 | 55.7 | 2026-11-06T09:58:00Z |
| field-001 | demo-f001-s03 | 41.3 | 23.6 | 62.1 | 2026-11-06T09:56:00Z |
| field-002 | demo-f002-s01 | 38.5 | 27.4 | 68.2 | 2026-11-06T09:57:00Z |
| field-002 | demo-f002-s02 | 44.2 | 26.8 | 72.5 | 2026-11-06T09:55:00Z |
| field-002 | demo-f002-s03 | 32.7 | 28.1 | 64.9 | 2026-11-06T09:53:00Z |
| field-003 | demo-f003-s01 | 22.4 | 29.3 | 42.8 | 2026-11-06T09:54:00Z |
| field-003 | demo-f003-s02 | 27.9 | 28.6 | 46.3 | 2026-11-06T09:52:00Z |
| field-003 | demo-f003-s03 | 31.6 | 27.7 | 49.5 | 2026-11-06T09:50:00Z |

The new demo sensor identifiers do not impersonate existing physical devices. No SQL sensor records are inserted: Function 2's current access check maps field to farm, and its Table reader uses SensorId directly rather than joining SQL Sensors.

## Utility and safety behaviour

`AgriStream.Telemetry.Seed` is a small .NET 10 console utility referencing the existing Functions project to reuse Azure.Data.Tables, StorageClients and the exact SensorTelemetryReader. It does not start the Functions host and changes no Function code.

- Default mode is **preview**: checks local development MVC mappings and prints the plan, without creating a storage client or making an Azure request.
- `--self-test` uses test doubles only and makes no Azure requests.
- Only `--apply` allows inserts. The URI/table guard rejects any target other than the named HTTPS Azure Table endpoint and SensorTelemetry.
- Before insertion, it checks all three field partitions and refuses if adding the planned sensors would exceed the reader's 100-sensor limit. A concurrent producer can still change counts; the post-insert reader verification will detect resulting limit/schema problems.
- Uses **AddEntityAsync only**. An EntityAlreadyExists conflict preserves the entire existing row, whether its timestamp is newer, older or equal. There is no upsert, update, delete or ETag wildcard overwrite.
- Stable field/sensor keys prevent duplicates on reruns and are safe across concurrent seed processes. The utility does not create the table or change existing rows. Earlier successful inserts may remain if a later insert fails; rerun to insert only missing keys.
- After inserting, point-reads every planned key, then invokes the unchanged Function 2 reader for each field to validate schema, inclusion and query compatibility. Reports actual stored values; preserved rows may differ from the preview.
- `--verify-only` performs the same post-insert reads without inserting anything. A missing row causes failure rather than being reported as seeded.
- The utility loads MVC appsettings.json, appsettings.Development.json, the existing user-secrets ID and environment variables in precedence order. Environment variables override user-secrets. Real credentials are not printed, committed or fetched from Azure App Service.

## Commands

From the repository root, preview and run local safety tests (these do not connect to Azure):

```powershell
dotnet build AgriStream.Telemetry.Seed/AgriStream.Telemetry.Seed.csproj -c Release
dotnet run --project AgriStream.Telemetry.Seed -c Release --no-build -- --self-test
dotnet run --project AgriStream.Telemetry.Seed -c Release --no-build
```

**After explicit approval to insert**, provide a suitable storage identity or privately configured connection. For Entra authentication, use an authenticated supported developer credential and grant it **Storage Table Data Contributor** on the specified account/table. The local identity previously had insufficient Table read permission; do not assume a management-plane role provides data access. The existing connection-string setting `DroneAuditStorageConnection` is also supported if you configure it privately. A connection string, when present, takes precedence over the service URI.

```powershell
$env:DroneAuditStorageConnection__tableServiceUri = 'https://st10541963agristreamiot.table.core.windows.net'
dotnet run --project AgriStream.Telemetry.Seed -c Release --no-build -- --apply
dotnet run --project AgriStream.Telemetry.Seed -c Release --no-build -- --verify-only
```

To choose another measurement anchor, review it first, then use the same value when applying after approval:

```powershell
dotnet run --project AgriStream.Telemetry.Seed -c Release --no-build -- --recorded-at '2026-11-06T10:00:00Z'
dotnet run --project AgriStream.Telemetry.Seed -c Release --no-build -- --apply --recorded-at '2026-11-06T10:00:00Z'
```

Changing the anchor does not replace existing snapshots. It affects only missing demo keys. This task did not execute --apply or --verify-only and did not insert anything into Azure.

## Validation performed

Release build passed with zero warnings/errors. All 12 offline seed safety assertions passed, including duplicate runs, preservation of newer and older existing rows, capacity/target guards and verification through the real Function 2 reader against test doubles. The default preview independently confirmed all three effective local MVC mappings and printed the nine planned entities without creating a storage client. Existing Function 1 checks and Function 2's 17 assertions also passed. Neither Function source was modified. Actual Azure insertion and post-insert Azure verification remain pending approval.
