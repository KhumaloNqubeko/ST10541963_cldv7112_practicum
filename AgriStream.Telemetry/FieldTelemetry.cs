using System.Text.RegularExpressions;

namespace AgriStream.Telemetry;

public sealed record FieldTelemetryResponse(string FieldId, IReadOnlyList<FieldTelemetryReading> Readings);
public sealed record FieldTelemetryReading(string SensorId, decimal SoilMoisturePercent,
    decimal? TemperatureCelsius, decimal? HumidityPercent, DateTimeOffset RecordedAt);

public static partial class FieldIdentifier
{
    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
