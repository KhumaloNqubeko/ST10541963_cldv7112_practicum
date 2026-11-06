using AgriStream.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AgriStream.Data;

/// <summary>Explicit, repeatable demonstration data. Never runs during normal web startup.</summary>
public static class DemoDataSeeder
{
    public static readonly (string Email, string Role)[] Accounts = [
        ("admin@agristream.example", SystemRoles.Admin),
        ("farmer1@agristream.example", SystemRoles.Farmer),
        ("farmer2@agristream.example", SystemRoles.Farmer),
        ("agronomist1@agristream.example", SystemRoles.Agronomist),
        ("agronomist2@agristream.example", SystemRoles.Agronomist)
    ];

    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration)
    {
        var password = configuration["DemoSeed:Password"];
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Set DemoSeed:Password or use scripts/Initialize-DemoData.ps1.");
        var db = services.GetRequiredService<ApplicationDbContext>();
        var users = services.GetRequiredService<UserManager<IdentityUser>>();
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        // Fail before migrations or writes if this is not the configured database.
        await db.Database.OpenConnectionAsync();
        Console.WriteLine($"Connected to {db.Database.GetDbConnection().DataSource} / {db.Database.GetDbConnection().Database}.");
        await db.Database.CloseConnectionAsync();
        await db.Database.MigrateAsync();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () => {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync();
            // Keep the lock identity stable across the app rename to coordinate all seed versions.
            await db.Database.ExecuteSqlRawAsync("DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = 'AgriPortal.DemoSeed.v1', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000; IF @result < 0 THROW 50001, 'Could not acquire demo seed lock.', 1;");
            foreach (var role in SystemRoles.All)
                if (!await roles.RoleExistsAsync(role)) Check(await roles.CreateAsync(new IdentityRole(role)));
            var accounts = new Dictionary<string, IdentityUser>();
            foreach (var (email, role) in Accounts) {
                var user = await users.FindByNameAsync(email);
                if (user is null) {
                    user = new IdentityUser { Id = $"agristream-demo-{email.Split('@')[0]}", UserName = email, Email = email, EmailConfirmed = true };
                    Check(await users.CreateAsync(user, password));
                    Check(await users.AddToRoleAsync(user, role));
                }
                else if (user.Id != $"agristream-demo-{email.Split('@')[0]}" || !await users.IsInRoleAsync(user, role))
                    throw new InvalidOperationException($"Existing account {email} does not match this seed. No account permissions will be changed.");
                accounts[email] = user;
            }
            var anchor = new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);
            string[] names = ["DEMO · Green Valley Farm", "DEMO · Sunrise Orchards", "DEMO · Riverbend Fields"];
            string[] locations = ["Stellenbosch, Western Cape", "Tzaneen, Limpopo", "Bothaville, Free State"];
            for (var farmIndex = 0; farmIndex < names.Length; farmIndex++) {
                var owner = accounts[$"farmer{(farmIndex == 1 ? 2 : 1)}@agristream.example"];
                var expert = accounts[$"agronomist{(farmIndex == 1 ? 2 : 1)}@agristream.example"];
                var name = names[farmIndex];
                var farm = await db.Farms.SingleOrDefaultAsync(f => f.Name == name && f.FarmerId == owner.Id);
                if (farm is null) {
                    farm = new Farm { Name = name, Location = locations[farmIndex], FarmerId = owner.Id };
                    db.Farms.Add(farm);
                    await db.SaveChangesAsync();
                }
                if (!await db.FarmAgronomists.AnyAsync(a => a.FarmId == farm.Id && a.AgronomistId == expert.Id))
                    db.FarmAgronomists.Add(new FarmAgronomist { FarmId = farm.Id, AgronomistId = expert.Id });
                for (var deviceIndex = 0; deviceIndex < 2; deviceIndex++) {
                    var deviceId = $"agristream-demo-f{farmIndex + 1}-s{deviceIndex + 1}";
                    var sensor = await db.Sensors.SingleOrDefaultAsync(s => s.DeviceId == deviceId);
                    if (sensor is null) {
                        sensor = new Sensor { FarmId = farm.Id, DeviceId = deviceId, Name = $"Soil station {deviceIndex + 1}", IsActive = true };
                        db.Sensors.Add(sensor);
                        await db.SaveChangesAsync();
                    }
                    if (sensor.FarmId != farm.Id) throw new InvalidOperationException("Demo device belongs to another farm.");
                    var existingEvents = await db.SensorReadings.Where(r => r.SensorId == sensor.Id && r.SourceEventId.StartsWith("demo-v1-"))
                        .Select(r => r.SourceEventId).ToListAsync();
                    for (var sample = 0; sample < 48; sample++) {
                        var eventId = $"demo-v1-{sample:D3}";
                        if (existingEvents.Contains(eventId)) continue;
                        db.SensorReadings.Add(new SensorReading {
                            SensorId = sensor.Id, SourceEventId = eventId, RecordedAt = anchor.AddMinutes(-30 * (47 - sample)),
                            SoilMoisturePercent = 24 + farmIndex * 9 + deviceIndex * 3 + sample % 8,
                            TemperatureCelsius = 18 + farmIndex * 2 + deviceIndex + sample % 12 * 0.5m,
                            HumidityPercent = 45 + farmIndex * 5 + sample % 18
                        });
                    }
                }
                for (var imageIndex = 0; imageIndex < 4; imageIndex++) {
                    var eventId = $"demo-v1-image-{imageIndex}";
                    if (await db.DroneImages.AnyAsync(i => i.FarmId == farm.Id && i.SourceEventId == eventId)) continue;
                    db.DroneImages.Add(new DroneImage {
                        FarmId = farm.Id, SourceEventId = eventId, BlobContainer = "demo-drone-images",
                        BlobName = $"demo/farm-{farmIndex + 1}/observation-{imageIndex + 1}.jpg",
                        CapturedAt = anchor.AddHours(-imageIndex), Status = (ImageProcessingStatus)imageIndex,
                        AnalysisSummary = imageIndex == 2 ? "DEMO: Simulated aerial observation; vegetation cover appears consistent. No image file or analysis service exists for this record."
                            : imageIndex == 3 ? "DEMO: Simulated processing failure for dashboard testing; no actual image was uploaded." : null
                    });
                }
                string[] alerts = ["DEMO: Soil moisture below the demonstration threshold in the northern block.", "DEMO: Elevated temperature recorded at the southern soil station.", "DEMO: Previous humidity observation reviewed and resolved."];
                for (var a = 0; a < alerts.Length; a++) {
                    var message = alerts[a];
                    if (!await db.FarmAlerts.AnyAsync(x => x.FarmId == farm.Id && x.Message == message))
                        db.FarmAlerts.Add(new FarmAlert { FarmId = farm.Id, Message = message, Severity = a == 0 ? AlertSeverity.Critical : a == 1 ? AlertSeverity.Warning : AlertSeverity.Information, CreatedAt = anchor.AddHours(-a), IsResolved = a == 2 });
                }
                var advice = "DEMO: Inspect soil moisture in the northern block and verify sensor calibration before adjusting irrigation. This is sample advice for testing, not a live field assessment.";
                if (!await db.Recommendations.AnyAsync(r => r.FarmId == farm.Id && r.AgronomistId == expert.Id && r.Advice == advice))
                    db.Recommendations.Add(new Recommendation { FarmId = farm.Id, AgronomistId = expert.Id, Advice = advice, CreatedAt = anchor });
                await db.SaveChangesAsync();
            }
            await transaction.CommitAsync();
        });
        Console.WriteLine("Demo seed complete. Existing passwords and application records were preserved.");
        Console.WriteLine($"Database totals: {await db.Users.CountAsync()} users; {await db.Roles.CountAsync()} roles; {await db.Farms.CountAsync()} farms; {await db.FarmAgronomists.CountAsync()} assignments; {await db.Sensors.CountAsync()} sensors; {await db.SensorReadings.CountAsync()} readings; {await db.DroneImages.CountAsync()} image records; {await db.FarmAlerts.CountAsync()} alerts; {await db.Recommendations.CountAsync()} recommendations.");
    }

    private static void Check(IdentityResult result) {
        if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
    }
}
