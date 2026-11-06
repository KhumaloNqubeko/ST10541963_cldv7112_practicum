using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace AgriStream.Models;

public class Farm
{
    public int Id { get; set; }
    [Required, StringLength(150)] public string Name { get; set; } = "";
    [Required, StringLength(250)] public string Location { get; set; } = "";
    [Required] public string FarmerId { get; set; } = "";
    public IdentityUser Farmer { get; set; } = null!;
    public ICollection<FarmAgronomist> Agronomists { get; set; } = [];
    public ICollection<Sensor> Sensors { get; set; } = [];
}

public class FarmAgronomist
{
    public int FarmId { get; set; }
    public Farm Farm { get; set; } = null!;
    public string AgronomistId { get; set; } = "";
    public IdentityUser Agronomist { get; set; } = null!;
}

public class Sensor
{
    public int Id { get; set; }
    public int FarmId { get; set; }
    public Farm Farm { get; set; } = null!;
    [Required, StringLength(128)] public string DeviceId { get; set; } = "";
    [Required, StringLength(100)] public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class SensorReading
{
    public long Id { get; set; }
    public int SensorId { get; set; }
    public Sensor Sensor { get; set; } = null!;
    // Stable source-event ID makes replayed telemetry detectable.
    [Required, StringLength(128)] public string SourceEventId { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
    [Range(0, 100)] public decimal SoilMoisturePercent { get; set; }
    public decimal TemperatureCelsius { get; set; }
    [Range(0, 100)] public decimal HumidityPercent { get; set; }
}

public enum ImageProcessingStatus { Pending, Processing, Completed, Failed }
public class DroneImage
{
    public long Id { get; set; }
    public int FarmId { get; set; }
    public Farm Farm { get; set; } = null!;
    [Required, StringLength(128)] public string SourceEventId { get; set; } = "";
    [Required, StringLength(63)] public string BlobContainer { get; set; } = "";
    [Required, StringLength(1024)] public string BlobName { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
    public ImageProcessingStatus Status { get; set; }
    [StringLength(4000)] public string? AnalysisSummary { get; set; }
}

public enum AlertSeverity { Information, Warning, Critical }
public class FarmAlert
{
    public long Id { get; set; }
    public int FarmId { get; set; }
    public Farm Farm { get; set; } = null!;
    [Required, StringLength(500)] public string Message { get; set; } = "";
    public AlertSeverity Severity { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsResolved { get; set; }
}

public class Recommendation
{
    public long Id { get; set; }
    public int FarmId { get; set; }
    public Farm Farm { get; set; } = null!;
    public string AgronomistId { get; set; } = "";
    public IdentityUser Agronomist { get; set; } = null!;
    [Required, StringLength(4000)] public string Advice { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class MonitoringDashboardViewModel
{
    public bool UsesFieldTelemetry { get; set; }
    public List<string> TelemetryFields { get; set; } = [];
    public string? SelectedFieldId { get; set; }
    public string? TelemetryMessage { get; set; }
    public AgriStream.Telemetry.FieldTelemetryResponse? FieldTelemetry { get; set; }
    public string Audience { get; set; } = "";
    public int FarmCount { get; set; }
    public int SensorCount { get; set; }
    public int OpenAlertCount { get; set; }
    public int PendingImageCount { get; set; }
    public List<Farm> Farms { get; set; } = [];
    public List<SensorReading> Readings { get; set; } = [];
    public List<DroneImage> Images { get; set; } = [];
    public List<FarmAlert> Alerts { get; set; } = [];
    public List<Recommendation> Recommendations { get; set; } = [];
}
