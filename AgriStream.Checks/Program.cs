using System.Linq.Expressions;
using System.Security.Claims;
using AgriStream.Data;
using AgriStream.Models;
using AgriStream.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.AspNetCore.Identity;
using System.Text.Json;

if (args.Contains("--live")) {
    await LiveChecks();
    return;
}

// No database connection is opened: verify the access predicates and SQL model.
using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
    .UseSqlServer("Server=localhost;Database=ModelChecks;Integrated Security=True;Encrypt=True").Options);
var access = new FarmAccess(db);
var owned = new Farm { Id = 1, FarmerId = "owner" };
owned.Agronomists.Add(new FarmAgronomist { FarmId = 1, AgronomistId = "expert" });
var other = new Farm { Id = 2, FarmerId = "someone-else" };

bool CanSee(string id, string role, Farm farm)
{
    var query = access.VisibleTo(User(id, role));
    var call = (MethodCallExpression)query.Expression;
    var predicate = (Expression<Func<Farm, bool>>)((UnaryExpression)call.Arguments[1]).Operand;
    // Also ensure EF can translate the complete authorization filter to SQL.
    Check(query.ToQueryString().Contains("WHERE"), "Scoped SQL must include a filter");
    return predicate.Compile()(farm);
}

Check(CanSee("owner", SystemRoles.Farmer, owned), "Farmer can see own farm");
Check(!CanSee("owner", SystemRoles.Farmer, other), "Farmer cannot see another farm");
Check(CanSee("expert", SystemRoles.Agronomist, owned), "Assigned agronomist can see farm");
Check(!CanSee("expert", SystemRoles.Agronomist, other), "Unassigned agronomist cannot see farm");
Check(!CanSee("owner", "Unknown", owned), "Unknown role cannot use ownership to gain access");
Check(!CanSee("expert", SystemRoles.Farmer, owned), "Agronomist assignment alone cannot grant farmer access");
Check(access.VisibleTo(User("admin", SystemRoles.Admin)).Expression is not MethodCallExpression,
    "Admin has unfiltered farm scope");

var reading = db.Model.FindEntityType(typeof(SensorReading))!;
Check(reading.GetIndexes().Any(i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "SensorId", "SourceEventId" })), "Telemetry replay key is unique");
Check(reading.GetIndexes().Any(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "SensorId", "RecordedAt" })), "Reading history index exists");
var image = db.Model.FindEntityType(typeof(DroneImage))!;
Check(!image.GetProperties().Any(p => p.ClrType == typeof(byte[])), "Drone image bytes are not stored in SQL");
Check(db.Model.FindEntityType(typeof(FarmAgronomist))!.FindPrimaryKey()!.Properties.Count == 2, "Assignments use a composite key");
Console.WriteLine("All farm-access, SQL translation and model checks passed.");
await TelemetryMvcChecks.RunAsync();

static ClaimsPrincipal User(string id, string role) => new(new ClaimsIdentity([
    new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Role, role)
], "Checks"));
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task LiveChecks()
{
    using var settings = JsonDocument.Parse(File.ReadAllText("AgriStream/appsettings.json"));
    var connection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
        ?? settings.RootElement.GetProperty("ConnectionStrings").GetProperty("DefaultConnection").GetString();
    using var credentials = JsonDocument.Parse(File.ReadAllText(".local/demo-credentials.json"));
    var password = credentials.RootElement.GetProperty("Password").GetString()!;
    await using var live = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connection, sql => sql.EnableRetryOnFailure()).Options);
    Check(!(await live.Database.GetPendingMigrationsAsync()).Any(), "All migrations must be applied");
    var access = new FarmAccess(live);
    foreach (var (email, role) in DemoDataSeeder.Accounts) {
        var account = await live.Users.SingleAsync(u => u.NormalizedUserName == email.ToUpperInvariant());
        var hasher = new PasswordHasher<IdentityUser>();
        Check(hasher.VerifyHashedPassword(account, account.PasswordHash!, password) != PasswordVerificationResult.Failed, $"Password verification failed for {email}");
        var roleId = await live.Roles.Where(r => r.Name == role).Select(r => r.Id).SingleAsync();
        Check(await live.UserRoles.AnyAsync(r => r.UserId == account.Id && r.RoleId == roleId), $"Missing role for {email}");
        var dashboard = await access.DashboardAsync(User(account.Id, role), role);
        var expected = role == SystemRoles.Admin ? 3 : email.Contains("1@") ? 2 : 1;
        Check(dashboard.FarmCount == expected, $"Incorrect farm visibility for {email}");
        Check(dashboard.SensorCount == expected * 2, $"Incorrect sensor count for {email}");
        Check(dashboard.OpenAlertCount == expected * 2, $"Incorrect alerts for {email}");
        Check(dashboard.PendingImageCount == expected * 2, $"Incorrect image count for {email}");
        Check(dashboard.Readings.Count == 20, $"Missing readings for {email}");
        Check(dashboard.Recommendations.Count == expected, $"Incorrect recommendations for {email}");
        var visibleIds = dashboard.Farms.Select(f => f.Id).ToHashSet();
        Check(dashboard.Readings.All(r => visibleIds.Contains(r.Sensor.FarmId)) && dashboard.Images.All(i => visibleIds.Contains(i.FarmId)) && dashboard.Alerts.All(a => visibleIds.Contains(a.FarmId)) && dashboard.Recommendations.All(r => visibleIds.Contains(r.FarmId)), $"Cross-farm record exposure for {email}");
        Console.WriteLine($"Verified {role} account {email}: password hash, role, {expected} visible farms and scoped monitoring data.");
    }
    Check(await live.SensorReadings.CountAsync() == 288, "Seed replay duplicated readings");
    Check(await live.DroneImages.CountAsync() == 12 && await live.FarmAlerts.CountAsync() == 9 && await live.Recommendations.CountAsync() == 3, "Seed replay duplicated records");
    Console.WriteLine("Live Azure SQL checks passed.");
}
