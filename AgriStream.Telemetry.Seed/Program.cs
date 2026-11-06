using System.Globalization;
using System.Text.Json;
using AgriStream.Functions;
using AgriStream.Telemetry.Seed;
using Microsoft.Extensions.Configuration;

if (args.Contains("--self-test")) {
    await SeedChecks.RunAsync();
    return;
}
var apply = args.Contains("--apply");
var verifyOnly = args.Contains("--verify-only");
if (apply && verifyOnly) throw new ArgumentException("Choose --apply or --verify-only, not both.");
var anchor = DemoTelemetrySeed.DefaultAnchor;
for (var i=0; i<args.Length; i++) {
    if (args[i] is "--apply" or "--verify-only") continue;
    if (args[i] == "--recorded-at" && ++i < args.Length &&
        DateTimeOffset.TryParseExact(args[i], "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out anchor)) continue;
    throw new ArgumentException("Usage: [--apply | --verify-only] [--recorded-at yyyy-MM-ddTHH:mm:ssZ]");
}
var root = new DirectoryInfo(Directory.GetCurrentDirectory());
while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgriStream", "AgriStream.csproj"))) root = root.Parent;
if (root is null) throw new InvalidOperationException("Run this utility from the repository root or one of its subdirectories.");
var configuration = new ConfigurationBuilder().SetBasePath(root.FullName)
    .AddJsonFile("AgriStream/appsettings.json", optional: false)
    .AddJsonFile("AgriStream/appsettings.Development.json", optional: true)
    .AddUserSecrets("AgriPortal-local-development")
    .AddEnvironmentVariables().Build();
foreach (var (field, expectedFarm) in DemoTelemetrySeed.Mappings) {
    var configured = configuration[$"TelemetryFunctions:Fields:{field}:FarmId"];
    if (!int.TryParse(configured, out var actualFarm) || actualFarm != expectedFarm)
        throw new InvalidOperationException($"MVC mapping for {field} must be explicitly configured as Farm ID {expectedFarm}; no mapping was changed.");
    Console.WriteLine($"Verified local MVC mapping: {field} -> Farm ID {actualFarm}.");
}
var plan = DemoTelemetrySeed.Plan(anchor);
if (!apply && !verifyOnly) {
    Console.WriteLine("PREVIEW ONLY — no storage client created and no Azure connection or writes performed.");
    Console.WriteLine(JsonSerializer.Serialize(plan.Select(r => r.ToDictionary(p => p.Key, p => p.Value)), new JsonSerializerOptions { WriteIndented=true }));
    Console.WriteLine("Review the plan. Only --apply enables inserts; existing rows are always preserved.");
    return;
}
try {
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    Console.WriteLine(string.IsNullOrWhiteSpace(configuration["DroneAuditStorageConnection"])
        ? "Storage authentication: Entra identity via configured Table service URI."
        : "Storage authentication: configured DroneAuditStorageConnection (environment overrides user-secrets). Credential is not displayed.");
    var table = StorageClients.CreateTelemetryTable(configuration);
    DemoTelemetrySeed.ValidateTarget(table);
    if (apply) {
        await DemoTelemetrySeed.PreflightAsync(table, plan, timeout.Token);
        var inserted=0;
        foreach (var row in plan) {
            var added=await DemoTelemetrySeed.InsertMissingAsync(table,row,timeout.Token);
            if(added) inserted++;
            Console.WriteLine($"{(added ? "Inserted" : "Preserved existing")}: {row.PartitionKey} / {row.RowKey}");
        }
        Console.WriteLine($"Inserted {inserted}; preserved {plan.Count-inserted} existing rows.");
    }
    var actual=await DemoTelemetrySeed.VerifyAsync(table,plan,timeout.Token);
    foreach(var row in actual.OrderBy(r=>r.PartitionKey).ThenBy(r=>r.RowKey)) {
        var temperature=row.TryGetValue("TemperatureCelsius",out var t)?t:null;
        var humidity=row.TryGetValue("HumidityPercent",out var h)?h:null;
        Console.WriteLine($"Verified {row.PartitionKey} / {row.RowKey}: moisture={row["SoilMoisturePercent"]}, temperature={temperature}, humidity={humidity}, measured={row.GetDateTimeOffset("RecordedAt"):O}");
    }
    Console.WriteLine("All nine snapshot keys verified through point reads and the unchanged Function 2 reader.");
}
catch (Exception ex) {
    Console.Error.WriteLine(SeedErrors.Describe(ex));
    Console.Error.WriteLine("No existing rows were overwritten. Rerunning safely inserts only missing keys.");
    Environment.ExitCode=1;
}
