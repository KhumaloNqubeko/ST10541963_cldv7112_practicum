# AgriStream

ASP.NET Core MVC (.NET 10), Entity Framework Core and Azure SQL Database. ASP.NET Core Identity provides login, logout, password hashing, lockout and role authorization.

## Database

- `Login`: Identity user ID, `Username`, `PasswordHash`, and Identity security/lockout fields. Plaintext passwords are never stored.
- `Roles`: Farmer, Agronomist, Admin.
- `LoginRoles`: user/role membership with foreign keys.
- Identity also creates claims, external login and token tables.

Accounts use email addresses as usernames because the built-in Identity login UI signs in using email. Public registration is disabled. Admin creates accounts at `/Admin/CreateUser`; each new account receives one selected role. Each dashboard endpoint enforces its corresponding role on the server.

## Agricultural monitoring model

- `Farms`: farm name, location and owning Farmer account.
- `FarmAgronomists`: explicit agronomist/farm assignments.
- `Sensors`: farm devices with a unique device identifier and active status.
- `SensorReadings`: timestamped soil moisture, temperature and humidity. Sensor/event uniqueness guards against duplicate source events, and percentages have database constraints.
- `DroneImages`: Blob Storage container/name, capture time, source event ID, processing status and analysis summary. Image bytes and signed access URLs are not stored in SQL.
- `FarmAlerts`: severity, message, timestamp and resolution status.
- `Recommendations`: advice from an agronomist for an assigned farm.

The monitoring dashboard uses actual SQL records and shows empty states when data is absent. Farmers see owned farms; Agronomists see explicitly assigned farms; Admins see all farms. Every dashboard query applies that scope before loading counts or records. Lists are bounded (50 farms, 20 readings, 10 images/alerts/recommendations); counters cover all accessible farms. UTC times are labelled explicitly.

For an already initialized database, apply the new schema with `dotnet ef database update --project AgriStream` after setting the connection string. The included checks verify farm-access predicates, SQL translation and important model constraints without opening a database connection; they do not replace end-to-end Azure SQL testing.

After initialization, create Farmer and Agronomist accounts as Admin, then use **Add farm** to assign them to a farm. The agronomist can then use **Share recommendation**, and the farmer can read that advice. Ownership/assignment role checks are enforced on submitted values. Editing assignments and device provisioning screens remain future work.

This implementation provides the MVC application and relational model, [Function 1: ProcessDroneImageUpload](docs/function-1.md), [Function 2: GetLatestFieldTelemetry](docs/function-2.md), and [Functions 3–4: irrigation commands](docs/functions-3-and-4.md) in `AgriStream.Functions`. Functions 1–2 were deployed by the user. Functions 3–4 add an authenticated Farmer POST, Azure Storage Queue processing, and an additive SQL device registry/command ledger; their migration and deployment remain pending. Irrigation controls are disabled until explicitly enabled after setup. Requested state is recorded without claiming physical activation. Function 2 requires explicit field ownership mappings, keys and a producer that maintains its documented snapshot schema. IoT Hub ingestion, sensor threshold automation, physical actuator control, Blob upload UI, image analysis, automatic alert generation and production load testing are not implemented.

## Configure and run

The app is configured for `st10541963-agristream-iot-db` on `st10541963-agristream-iot-server.database.windows.net` using `Active Directory Default`. Local development requires an authenticated supported Microsoft Entra credential (for example Visual Studio or Azure CLI), database permissions, and a permitted network path. Environment variables can override this connection.

To apply migrations and seed all demo records, run `./scripts/Initialize-DemoData.ps1` from PowerShell 7. It generates a random demo password in the git-ignored `.local/demo-credentials.json` file, creates five role-based accounts, three farms and assignments, six sensors, 288 readings, 12 image metadata records, nine alerts and three recommendations. All sample farm names, alerts and advice are labelled DEMO. Image records are illustrative metadata only; no blobs are uploaded. The sample readings have fixed timestamps ending 6 November 2026 at 08:00 UTC, so reruns preserve the same dataset.

Seeding uses a transaction and database application lock, checks stable record keys, preserves existing passwords and user data, and does not run during web startup. The shared random password is for demonstration accounts only. If these accounts already exist, use the credentials from their original seed run; deleting the local file does not reset database passwords.

Demo account usernames are `admin@agristream.example`, `farmer1@agristream.example`, `farmer2@agristream.example`, `agronomist1@agristream.example` and `agronomist2@agristream.example`. The first farmer/agronomist pair has two farms; the second pair has one. All use the generated password in `.local/demo-credentials.json`.

Run `dotnet run --project AgriStream.Checks -- --live` from the repository root to verify the seeded Azure SQL database, password hashes, role membership, scoped dashboard queries and expected demo counts. This explicit check requires database network access and the local credentials file and assumes the demo dataset has not been modified. Normal checks do not connect to Azure.

Install the .NET 10 SDK. Create an Azure SQL database and allow your development IP through the SQL server firewall. Set configuration outside source control (PowerShell, from this repository):

```powershell
$env:ConnectionStrings__DefaultConnection = 'Server=tcp:YOUR-SERVER.database.windows.net,1433;Initial Catalog=YOUR-DATABASE;User ID=YOUR-SQL-USER;Password=YOUR-SQL-PASSWORD;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
$env:BootstrapAdmin__Username = 'admin@example.com'
$env:BootstrapAdmin__Password = 'YOUR-UNIQUE-STRONG-PASSWORD'
dotnet tool restore
dotnet run --project AgriStream -- --initialize-database
Remove-Item Env:BootstrapAdmin__Password
dotnet dev-certs https --trust
dotnet run --project AgriStream --launch-profile https
```

The explicit initialization command applies checked-in migrations, creates the three roles, and creates the initial Admin. It does not reset existing passwords or elevate existing non-admin accounts. Use an email address for the bootstrap username and a password of at least 12 characters including uppercase, lowercase, a number, and a symbol. Keep the initialization credentials out of deployed runtime configuration after setup. Normal startup does not change the schema.

Open the HTTPS address printed by the application and select **Sign in**. Admin can open Dashboard and create Farmer/Agronomist/Admin accounts. Cookies require HTTPS. Identity's email delivery and password recovery have not been configured.

In Azure App Service, set `ConnectionStrings__DefaultConnection` in application configuration (or configure a named `DefaultConnection` connection string). A managed identity connection can use `Authentication=Active Directory Default` instead of SQL credentials after granting the identity database access. Apply migrations using a deployment identity with schema permissions; runtime only needs normal application data access.

## Development

```powershell
dotnet build AgriStream
dotnet run --project AgriStream.Checks
dotnet ef migrations add YOUR_CHANGE --project AgriStream
dotnet ef migrations script --idempotent --project AgriStream --output database.sql
```

Azure resources and credentials are not included. Database initialization and end-to-end sign-in require a reachable configured SQL database.
