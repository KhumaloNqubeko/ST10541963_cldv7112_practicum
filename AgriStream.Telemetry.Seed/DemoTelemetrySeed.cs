using AgriStream.Functions;
using Azure;
using Azure.Data.Tables;

namespace AgriStream.Telemetry.Seed;

public static class DemoTelemetrySeed
{
    public const string AccountName = "st10541963agristreamiot";
    public const string TableName = "SensorTelemetry";
    public static readonly DateTimeOffset DefaultAnchor = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
    public static readonly IReadOnlyDictionary<string, int> Mappings = new Dictionary<string, int> {
        ["field-001"] = 1, ["field-002"] = 2, ["field-003"] = 3
    };

    public static IReadOnlyList<TableEntity> Plan(DateTimeOffset anchor)
    {
        // Stable IDs deliberately distinguish demonstration sensors from real farm devices.
        var values = new (double Moisture, double Temperature, double Humidity)[] {
            (34.8,24.2,58.4), (29.6,25.1,55.7), (41.3,23.6,62.1),
            (38.5,27.4,68.2), (44.2,26.8,72.5), (32.7,28.1,64.9),
            (22.4,29.3,42.8), (27.9,28.6,46.3), (31.6,27.7,49.5)
        };
        var rows = new List<TableEntity>();
        for (var field = 1; field <= 3; field++) for (var sensor = 1; sensor <= 3; sensor++) {
            var value = values[(field-1)*3+sensor-1];
            var sensorId = $"demo-f{field:D3}-s{sensor:D2}";
            rows.Add(new TableEntity($"field-{field:D3}", $"latest|{sensorId}") {
                ["SensorId"] = sensorId,
                ["SoilMoisturePercent"] = value.Moisture,
                ["TemperatureCelsius"] = value.Temperature,
                ["HumidityPercent"] = value.Humidity,
                ["RecordedAt"] = anchor.ToUniversalTime().AddMinutes(-((field-1)*3 + (sensor-1)*2)),
                ["IsDemo"] = true,
                ["SeedBatch"] = "function2-exam-v1"
            });
        }
        return rows;
    }

    public static void ValidateTarget(TableClient table)
    {
        if (table.Uri.Scheme != "https" || table.Uri.Host != $"{AccountName}.table.core.windows.net" || table.Name != TableName)
            throw new InvalidOperationException("Seed target must be the existing SensorTelemetry table in st10541963agristreamiot over HTTPS.");
    }

    public static async Task PreflightAsync(TableClient table, IReadOnlyList<TableEntity> rows, CancellationToken ct)
    {
        foreach (var group in rows.GroupBy(r => r.PartitionKey)) {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var filter = TableClient.CreateQueryFilter($"PartitionKey eq {group.Key} and RowKey ge {"latest|"} and RowKey lt {"latest}"}");
            await foreach (var row in table.QueryAsync<TableEntity>(filter, 101, ["PartitionKey", "RowKey"], ct)) {
                keys.Add(row.RowKey);
                if (keys.Count > SensorTelemetryReader.MaxSensors) throw new InvalidOperationException("Existing field exceeds Function 2's sensor limit.");
            }
            if (keys.Union(group.Select(r => r.RowKey), StringComparer.Ordinal).Count() > SensorTelemetryReader.MaxSensors)
                throw new InvalidOperationException("Demo records would exceed Function 2's 100-sensor field limit. No seed inserts have started.");
        }
    }

    public static async Task<bool> InsertMissingAsync(TableClient table, TableEntity row, CancellationToken ct)
    {
        try {
            // Atomic insert only: never update/upsert/delete, regardless of existing timestamp/status.
            await table.AddEntityAsync(row, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409 && ex.ErrorCode == "EntityAlreadyExists") { return false; }
    }

    public static async Task<IReadOnlyList<TableEntity>> VerifyAsync(TableClient table, IReadOnlyList<TableEntity> planned, CancellationToken ct)
    {
        var actual = new List<TableEntity>();
        foreach (var row in planned) {
            var saved = (await table.GetEntityAsync<TableEntity>(row.PartitionKey, row.RowKey, cancellationToken: ct)).Value;
            if (saved.GetString("SensorId") != row.GetString("SensorId") || saved.GetDateTimeOffset("RecordedAt") is null)
                throw new InvalidDataException("A stored demo-key row does not match Function 2's required schema; it was left unchanged.");
            actual.Add(saved);
        }
        // Exercise the exact production reader and verify that planned keys are visible in GET results.
        var reader = new SensorTelemetryReader(table);
        foreach (var group in planned.GroupBy(r => r.PartitionKey)) {
            var response = await reader.ReadAsync(group.Key, ct);
            if (!group.All(r => response.Readings.Any(reading => reading.SensorId == r.GetString("SensorId"))))
                throw new InvalidDataException("A planned sensor is missing from Function 2's reader output.");
        }
        return actual;
    }
}
