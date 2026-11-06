using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace AgriStream.Irrigation;

public class IrrigationDevice
{
    [Key,MaxLength(64)] public string DeviceId {get;set;}="";
    public int FarmId {get;set;}
    [MaxLength(64)] public string FieldId {get;set;}="";
    [MaxLength(100)] public string Name {get;set;}="";
    public bool IsEnabled {get;set;}=true;
    // Optional provisioned sensor principal; absent means automated commands are disabled for this device.
    [MaxLength(128)] public string? SensorSourceId {get;set;}
    [MaxLength(40)] public string OperationalStatus {get;set;}="Idle";
    public Guid? LastCommandId {get;set;}
    [MaxLength(10)] public string? LastAction {get;set;}
    public DateTimeOffset? LastProcessedAt {get;set;}
    public DateTimeOffset? LastAcceptedAt {get;set;}
    [MaxLength(200)] public string? LastOutcome {get;set;}
}
public class IrrigationCommandRecord
{
    public Guid CommandId {get;set;}
    [MaxLength(64)] public string DeviceId {get;set;}="";
    public int FarmId {get;set;}
    [MaxLength(64)] public string FieldId {get;set;}="";
    [MaxLength(10)] public string Action {get;set;}="";
    [MaxLength(10)] public string Source {get;set;}="";
    [MaxLength(450)] public string ActorId {get;set;}="";
    [MaxLength(64)] public string CommandHash {get;set;}="";
    public DateTimeOffset AcceptedAt {get;set;}
    public DateTimeOffset ProcessedAt {get;set;}
    [MaxLength(40)] public string Outcome {get;set;}="";
}
public static class IrrigationModel
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<IrrigationDevice>().ToTable("IrrigationDevices");
        b.Entity<IrrigationDevice>().HasIndex(d=>new{d.FarmId,d.FieldId});
        b.Entity<IrrigationCommandRecord>().ToTable("IrrigationCommands").HasKey(c=>c.CommandId);
        b.Entity<IrrigationCommandRecord>().HasOne<IrrigationDevice>().WithMany().HasForeignKey(c=>c.DeviceId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<IrrigationCommandRecord>().HasIndex(c=>new{c.DeviceId,c.ProcessedAt});
    }
}
public class IrrigationDbContext(DbContextOptions<IrrigationDbContext> options):DbContext(options)
{
    public DbSet<IrrigationDevice> Devices=>Set<IrrigationDevice>();
    public DbSet<IrrigationCommandRecord> Commands=>Set<IrrigationCommandRecord>();
    protected override void OnModelCreating(ModelBuilder b)=>IrrigationModel.Configure(b);
}
