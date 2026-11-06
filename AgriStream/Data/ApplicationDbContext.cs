using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using AgriStream.Models;
namespace AgriStream.Data;
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<Farm> Farms => Set<Farm>();
    public DbSet<FarmAgronomist> FarmAgronomists => Set<FarmAgronomist>();
    public DbSet<Sensor> Sensors => Set<Sensor>();
    public DbSet<SensorReading> SensorReadings => Set<SensorReading>();
    public DbSet<DroneImage> DroneImages => Set<DroneImage>();
    public DbSet<FarmAlert> FarmAlerts => Set<FarmAlert>();
    public DbSet<Recommendation> Recommendations => Set<Recommendation>();
    protected override void OnModelCreating(ModelBuilder b) {
        base.OnModelCreating(b);
        b.Entity<Farm>().HasOne(f => f.Farmer).WithMany().HasForeignKey(f => f.FarmerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<FarmAgronomist>().HasKey(a => new { a.FarmId, a.AgronomistId });
        b.Entity<FarmAgronomist>().HasOne(a => a.Farm).WithMany(f => f.Agronomists).HasForeignKey(a => a.FarmId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<FarmAgronomist>().HasOne(a => a.Agronomist).WithMany().HasForeignKey(a => a.AgronomistId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Recommendation>().HasOne(r => r.Agronomist).WithMany().HasForeignKey(r => r.AgronomistId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Sensor>().HasIndex(s => s.DeviceId).IsUnique();
        b.Entity<SensorReading>().HasIndex(r => new { r.SensorId, r.SourceEventId }).IsUnique();
        b.Entity<SensorReading>().HasIndex(r => new { r.SensorId, r.RecordedAt });
        b.Entity<SensorReading>().Property(r => r.SoilMoisturePercent).HasPrecision(5, 2);
        b.Entity<SensorReading>().Property(r => r.HumidityPercent).HasPrecision(5, 2);
        b.Entity<SensorReading>().Property(r => r.TemperatureCelsius).HasPrecision(6, 2);
        b.Entity<SensorReading>().ToTable(t => {
            t.HasCheckConstraint("CK_Reading_Moisture", "[SoilMoisturePercent] BETWEEN 0 AND 100");
            t.HasCheckConstraint("CK_Reading_Humidity", "[HumidityPercent] BETWEEN 0 AND 100");
        });
        b.Entity<DroneImage>().HasIndex(i => new { i.FarmId, i.SourceEventId }).IsUnique();
        b.Entity<DroneImage>().HasIndex(i => new { i.FarmId, i.CapturedAt });
        b.Entity<FarmAlert>().HasIndex(a => new { a.FarmId, a.IsResolved, a.CreatedAt });
        b.Entity<Recommendation>().HasIndex(r => new { r.FarmId, r.CreatedAt });
        b.Entity<IdentityUser>().ToTable("Login");
        b.Entity<IdentityUser>().Property(u => u.UserName).HasColumnName("Username");
        b.Entity<IdentityRole>().ToTable("Roles");
        b.Entity<IdentityUserRole<string>>().ToTable("LoginRoles");
    }
}
public static class SystemRoles {
    public const string Farmer = "Farmer", Agronomist = "Agronomist", Admin = "Admin";
    public static readonly string[] All = [Farmer, Agronomist, Admin];
}
