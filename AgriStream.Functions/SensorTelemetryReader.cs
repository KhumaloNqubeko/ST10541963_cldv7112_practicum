using System.Globalization;
using AgriStream.Telemetry;
using Azure.Data.Tables;

namespace AgriStream.Functions;

public sealed class SensorTelemetryReader(Func<TableClient> createTable)
{
    private readonly Lazy<TableClient> table = new(createTable);
    public SensorTelemetryReader(TableClient table) : this(() => table) { }
    public const int MaxSensors = 100;
    public async Task<FieldTelemetryResponse> ReadAsync(string fieldId, CancellationToken cancellationToken)
    {
        if (!FieldIdentifier.IsValid(fieldId)) throw new ArgumentException("Invalid field ID.", nameof(fieldId));
        // SensorTelemetry is empty at assessment. Reserve latest| rows for per-sensor snapshots;
        // history may coexist under other prefixes without being scanned by dashboard requests.
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {fieldId} and RowKey ge {"latest|"} and RowKey lt {"latest}"}");
        var readings = new Dictionary<string, FieldTelemetryReading>(StringComparer.Ordinal);
        var count = 0;
        await foreach (var row in table.Value.QueryAsync<TableEntity>(filter, maxPerPage: MaxSensors + 1,
            select: ["PartitionKey", "RowKey", "SensorId", "SoilMoisturePercent", "TemperatureCelsius", "HumidityPercent", "RecordedAt"], cancellationToken: cancellationToken)) {
            if (++count > MaxSensors) throw new InvalidDataException("Field exceeds supported snapshot limit.");
            if (row.PartitionKey != fieldId || !row.RowKey.StartsWith("latest|", StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected telemetry partition or row key.");
            var sensorId = row.GetString("SensorId");
            if (!FieldIdentifier.IsValid(sensorId) || row.RowKey != $"latest|{sensorId}")
                throw new InvalidDataException("Telemetry sensor ID does not match its snapshot key.");
            var recordedAt = row.GetDateTimeOffset("RecordedAt") ?? throw new InvalidDataException("Measurement timestamp is required.");
            var moisture = Number(row, "SoilMoisturePercent") ?? throw new InvalidDataException("Soil moisture is required.");
            var humidity = Number(row, "HumidityPercent");
            if (moisture is < 0 or > 100 || humidity is < 0 or > 100) throw new InvalidDataException("Invalid telemetry percentage.");
            var reading = new FieldTelemetryReading(sensorId!, moisture, Number(row, "TemperatureCelsius"), humidity, recordedAt);
            if (!readings.TryGetValue(sensorId!, out var existing) || reading.RecordedAt > existing.RecordedAt) readings[sensorId!] = reading;
        }
        return new FieldTelemetryResponse(fieldId, readings.Values.OrderByDescending(r => r.RecordedAt).ThenBy(r => r.SensorId, StringComparer.Ordinal).ToArray());
    }

    private static decimal? Number(TableEntity row, string name)
    {
        if (!row.TryGetValue(name, out var value) || value is null) return null;
        if (value is not (double or int or long or decimal)) throw new InvalidDataException("Unexpected telemetry numeric type.");
        return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }
}
