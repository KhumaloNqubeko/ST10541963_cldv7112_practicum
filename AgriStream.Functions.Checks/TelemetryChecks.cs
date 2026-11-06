using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AgriStream.Functions;
using AgriStream.Telemetry;
using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class TelemetryChecks
{
    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
        const string secret = "test-only-signing-secret-with-at-least-32-bytes";
        var now = DateTimeOffset.Parse("2026-11-06T10:00:00Z");
        var clock = new TestClock(now);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["TelemetryAccess:SigningKey"] = secret }).Build();
        var table = new TelemetryTable();
        table.Rows.Add(Row("field-a", "sensor-a", now.AddMinutes(-2)));
        table.Rows.Add(Row("field-a", "sensor-b", now));
        table.Rows.Add(Row("field-b", "sensor-c", now.AddMinutes(1)));
        var reader = new SensorTelemetryReader(table);
        var function = new GetLatestFieldTelemetry(reader, config, clock, NullLogger<GetLatestFieldTelemetry>.Instance);
        TestRequest Request(string? field = "field-a") {
            var request = new TestRequest();
            if (field is not null) request.Headers.Add(FieldAccessAssertion.HeaderName, FieldAccessAssertion.Create(field, "farmer-a", secret, now));
            return request;
        }
        var method = typeof(GetLatestFieldTelemetry).GetMethod("RunAsync")!;
        var trigger = method.GetParameters()[0].GetCustomAttribute<HttpTriggerAttribute>()!;
        Check(method.GetCustomAttribute<FunctionAttribute>()!.Name == "GetLatestFieldTelemetry" && trigger.AuthLevel == AuthorizationLevel.Function && trigger.Methods.SequenceEqual(["get"]) && trigger.Route == "fields/{fieldId}/telemetry/latest", "GET route and Function-level auth");
        var response = await function.RunAsync(Request(), "field-a", default);
        var data = await Body<FieldTelemetryResponse>(response);
        Check(response.StatusCode == HttpStatusCode.OK && data.FieldId == "field-a" && data.Readings.Count == 2, "Valid GET returns field telemetry");
        Check(data.Readings.All(r => r.SensorId != "sensor-c") && table.Filter!.Contains("PartitionKey eq 'field-a'"), "Partition-scoped isolation");
        Check(data.Readings[0].SensorId == "sensor-b" && data.Readings[0].RecordedAt == now && data.Readings[1].RecordedAt == now.AddMinutes(-2), "Actual measurement time ordering, not row-key ordering");
        Check(table.Filter!.Contains("RowKey ge 'latest|'", StringComparison.Ordinal) && table.PageSize == 101, "Indexed latest-prefix query with bounded results");
        Check(data.Readings.All(r => r.SoilMoisturePercent == 35.5m && r.TemperatureCelsius == 24m && r.HumidityPercent is null), "Numeric and optional values decoded");
        Check(response.Headers.GetValues("Cache-Control").Single() == "no-store", "No public caching of field telemetry");
        response = await function.RunAsync(Request("field-empty"), "field-empty", default);
        Check(response.StatusCode == HttpStatusCode.OK && (await Body<FieldTelemetryResponse>(response)).Readings.Count == 0, "Empty field returns 200 and empty readings");
        Check((await function.RunAsync(Request(), "bad/field", default)).StatusCode == HttpStatusCode.BadRequest, "Invalid field rejected");
        Check((await function.RunAsync(Request(null), "field-a", default)).StatusCode == HttpStatusCode.Forbidden, "Function key alone is insufficient");
        Check((await function.RunAsync(Request("field-b"), "field-a", default)).StatusCode == HttpStatusCode.Forbidden, "Access assertion cannot authorise another field");
        var forged = Request(null); forged.Headers.Add(FieldAccessAssertion.HeaderName, "tampered.signature");
        Check((await function.RunAsync(forged, "field-a", default)).StatusCode == HttpStatusCode.Forbidden, "Forged assertion denied");
        var expired = Request(null); expired.Headers.Add(FieldAccessAssertion.HeaderName, FieldAccessAssertion.Create("field-a", "farmer-a", secret, now.AddMinutes(-2)));
        Check((await function.RunAsync(expired, "field-a", default)).StatusCode == HttpStatusCode.Forbidden, "Expired assertion denied");
        var unconfigured = new GetLatestFieldTelemetry(reader, new ConfigurationBuilder().Build(), clock, NullLogger<GetLatestFieldTelemetry>.Instance);
        Check((await unconfigured.RunAsync(Request(), "field-a", default)).StatusCode == HttpStatusCode.ServiceUnavailable, "Missing signing secret fails closed");
        table.Failure = new RequestFailedException(503, "private storage details", "ServerBusy", null);
        response = await function.RunAsync(Request(), "field-a", default);
        response.Body.Position = 0;
        Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && !(await new StreamReader(response.Body).ReadToEndAsync()).Contains("private storage details"), "Storage unavailable returns safe 503");
        table.Failure = null;
        table.Rows.Clear(); table.Rows.Add(Row("field-a", "sensor-a", now)); table.Rows[0].Remove("RecordedAt");
        Check((await function.RunAsync(Request(), "field-a", default)).StatusCode == HttpStatusCode.ServiceUnavailable, "Missing actual measurement time does not use Table Timestamp");
        table.Rows.Clear(); for(var i=0;i<101;i++) table.Rows.Add(Row("field-a", $"sensor-{i}", now));
        Check((await function.RunAsync(Request(), "field-a", default)).StatusCode == HttpStatusCode.ServiceUnavailable, "Oversized snapshot partitions fail instead of silently dropping newer readings");
        Console.WriteLine($"Function 2 checks passed: {count} assertions.");
    }

    private static TableEntity Row(string field, string sensor, DateTimeOffset time) => new(field, $"latest|{sensor}") {
        ["SensorId"] = sensor, ["SoilMoisturePercent"] = 35.5, ["TemperatureCelsius"] = 24.0, ["RecordedAt"] = time
    };
    private static async Task<T> Body<T>(HttpResponseData response) { response.Body.Position=0; return (await JsonSerializer.DeserializeAsync<T>(response.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!; }
    private sealed class TestClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class TelemetryTable : TableClient
    {
        public List<TableEntity> Rows { get; } = [];
        public string? Filter { get; private set; }
        public int? PageSize { get; private set; }
        public Exception? Failure { get; set; }
        public override AsyncPageable<T> QueryAsync<T>(string? filter = null, int? maxPerPage = null, IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            cancellationToken.ThrowIfCancellationRequested();
            Filter=filter; PageSize=maxPerPage;
            var rows = Rows.Where(r => filter!.Contains($"PartitionKey eq '{r.PartitionKey}'", StringComparison.Ordinal)).Select(r => (T)(object)r).ToArray();
            return AsyncPageable<T>.FromPages([Page<T>.FromValues(rows, null, new FakeResponse(200))]);
        }
    }
    private sealed class TestContext : FunctionContext
    {
        public override string InvocationId => "test";
        public override string FunctionId => "test";
        public override TraceContext TraceContext => null!;
        public override BindingContext BindingContext => null!;
        public override RetryContext RetryContext => null!;
        public override IServiceProvider InstanceServices { get; set; } = null!;
        public override FunctionDefinition FunctionDefinition => null!;
        public override IDictionary<object, object> Items { get; set; } = new Dictionary<object,object>();
        public override IInvocationFeatures Features => null!;
    }
    private sealed class TestRequest() : HttpRequestData(new TestContext())
    {
        public override Stream Body => Stream.Null;
        public override HttpHeadersCollection Headers { get; } = new();
        public override IReadOnlyCollection<IHttpCookie> Cookies => [];
        public override Uri Url => new("https://localhost/api/fields/field-a/telemetry/latest");
        public override IEnumerable<ClaimsIdentity> Identities => [];
        public override string Method => "GET";
        public override HttpResponseData CreateResponse() => new TestResponse(FunctionContext);
    }
    private sealed class TestResponse(FunctionContext context) : HttpResponseData(context)
    {
        public override HttpStatusCode StatusCode { get; set; }
        public override HttpHeadersCollection Headers { get; set; } = new();
        public override Stream Body { get; set; } = new MemoryStream();
        public override HttpCookies Cookies => null!;
    }
}
