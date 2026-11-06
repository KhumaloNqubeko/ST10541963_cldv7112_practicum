using System.Collections.Concurrent;
using System.Reflection;
using AgriStream.Functions;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

if (args.Contains("--verify-azure")) {
    Environment.ExitCode = await VerifyAzure();
    return;
}

var created = new DateTimeOffset(2026, 11, 6, 7, 0, 0, TimeSpan.Zero);
var modified = created.AddMinutes(5);
var clock = new FixedClock(created.AddHours(1));
var table = new FakeTable();
var logger = new RecordingLogger();
var function = new ProcessDroneImageUpload(new DroneImageAuditWriter(table), clock, logger);
var blob = new FakeBlob(new Uri("https://example.blob.core.windows.net/drone-images/farm-1/aerial.jpg?sig=secret"),
    Properties("revision-1", created, modified, "version-1"));

var method = typeof(ProcessDroneImageUpload).GetMethod(nameof(ProcessDroneImageUpload.RunAsync))!;
Assert(method.GetCustomAttribute<FunctionAttribute>()?.Name == "ProcessDroneImageUpload", "Function name metadata");
var binding = method.GetParameters()[0].GetCustomAttribute<BlobTriggerAttribute>();
Assert(binding?.BlobPath == "drone-images/{name}" && binding.Connection == "DroneStorageConnection" && binding.Source == BlobTriggerSource.LogsAndContainerScan, "Polling Blob trigger metadata");
await function.RunAsync(blob, "farm-1/aerial.jpg", default);
var first = table.Rows.Values.Single();
Assert(first.FileName == "aerial.jpg" && first.BlobPath == "drone-images/farm-1/aerial.jpg", "Filename and full nested path");
Assert(first.BlobUri == "https://example.blob.core.windows.net/drone-images/farm-1/aerial.jpg", "URI excludes SAS credentials");
Assert(first.FileSizeBytes == 9876543210 && first.ContentType == "image/jpeg", "Long file size and content type");
Assert(first.UploadedAt == created && first.ProcessedAt == clock.GetUtcNow(), "Upload and processing timestamps");
Assert(first.ProcessingStatus == "Received" && first.Message == "Drone image upload detected successfully", "Received status and message");
Assert(first.PartitionKey == "20261106" && first.RowKey.Length == 64 && first.RowKey.All(Uri.IsHexDigit), "Safe stable table keys");
Assert(logger.Messages.Any(m => m.Contains("FileSizeBytes=9876543210")) && logger.Messages.All(m => !m.Contains("secret")), "Upload details logged without SAS");
await function.RunAsync(blob, "farm-1/aerial.jpg", default);
Assert(table.Rows.Count == 1 && table.Rows.Values.Single().ProcessedAt == first.ProcessedAt, "Duplicate execution preserves original audit");
first.ProcessingStatus = "Completed";
await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => function.RunAsync(blob, "farm-1/aerial.jpg", default)));
Assert(table.Rows.Count == 1 && first.ProcessingStatus == "Completed", "Concurrent retries preserve later status");
blob.Properties = Properties("revision-2", created, modified.AddDays(1), "version-2");
await function.RunAsync(blob, "farm-1/aerial.jpg", default);
Assert(table.Rows.Count == 2 && table.Rows.Values.Any(r => r.PartitionKey == "20261107"), "Updated blob revision gets separate audit");
var absent = DroneImageAuditEntity.FromBlob("a.jpg", blob.Uri, Properties("revision-3", default, modified, null, null), clock.GetUtcNow());
Assert(absent.UploadedAt is null && absent.ContentType is null, "Optional creation time and MIME type");
var missingEtag = false;
try { DroneImageAuditEntity.FromBlob("a.jpg", blob.Uri, Properties("", created, modified), clock.GetUtcNow()); }
catch (InvalidOperationException) { missingEtag = true; }
Assert(missingEtag, "Do not audit an unidentifiable revision");

table.Failure = new RequestFailedException(503, "Simulated outage", "ServerBusy", null);
var propagated = false;
try { await function.RunAsync(blob, "farm-1/aerial.jpg", default); }
catch (RequestFailedException ex) when (ex.Status == 503) { propagated = true; }
Assert(propagated && logger.Errors > 0, "Audit failures are logged and propagated");
table.Failure = new RequestFailedException(409, "Different conflict", "TableBeingDeleted", null);
propagated = false;
try { await function.RunAsync(blob, "farm-1/aerial.jpg", default); }
catch (RequestFailedException ex) when (ex.ErrorCode == "TableBeingDeleted") { propagated = true; }
Assert(propagated, "Only entity-exists conflicts count as duplicates");
table.Failure = null;
blob.Failure = new RequestFailedException(403, "Simulated metadata denial", "AuthorizationFailure", null);
var before = table.Rows.Count;
propagated = false;
try { await function.RunAsync(blob, "farm-1/aerial.jpg", default); }
catch (RequestFailedException ex) when (ex.Status == 403) { propagated = true; }
Assert(propagated && table.Rows.Count == before, "Metadata failures propagate without fake success");
Console.WriteLine("All Function 1 checks passed (trigger metadata, upload details, persistence, duplicate/concurrent delivery, version changes, later-status protection, optional metadata, failure propagation and logging).");
await TelemetryChecks.RunAsync();

static BlobProperties Properties(string etag, DateTimeOffset created, DateTimeOffset modified, string? version = null, string? contentType = "image/jpeg") =>
    BlobsModelFactory.BlobProperties(createdOn: created, lastModified: modified, eTag: new ETag(etag), contentLength: 9876543210, contentType: contentType, versionId: version);
static void Assert(bool value, string description) {
    if (!value) throw new InvalidOperationException($"FAILED: {description}");
}

static async Task<int> VerifyAzure()
{
    // Read-only resource checks. No CreateIfNotExists, uploads, audit writes or deployment.
    var credential = new DefaultAzureCredential();
    var options = new BlobClientOptions();
    options.Retry.MaxRetries = 0;
    var tableOptions = new TableClientOptions();
    tableOptions.Retry.MaxRetries = 0;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var failures = 0;
    try {
        var container = new BlobContainerClient(new Uri("https://st10541963agristreamiot.blob.core.windows.net/drone-images"), credential, options);
        var exists = (await container.ExistsAsync(timeout.Token)).Value;
        Console.WriteLine($"Blob container drone-images: {(exists ? "VERIFIED PRESENT" : "NOT FOUND")}");
        if (!exists) failures++;
    }
    catch (Exception ex) { Report("Blob container drone-images", ex); failures++; }
    try {
        var service = new TableServiceClient(new Uri("https://st10541963agristreamiot.table.core.windows.net"), credential, tableOptions);
        var found = false;
        await foreach (var table in service.QueryAsync(filter: "TableName eq 'DroneImageAudit'", cancellationToken: timeout.Token)) found = table.Name == "DroneImageAudit";
        Console.WriteLine($"Table DroneImageAudit: {(found ? "VERIFIED PRESENT" : "NOT FOUND")}");
        if (!found) failures++;
    }
    catch (Exception ex) { Report("Table DroneImageAudit", ex); failures++; }
    return failures == 0 ? 0 : 1;
}
static void Report(string resource, Exception ex) {
    Console.WriteLine($"{resource}: UNVERIFIED. {ex.GetType().Name}; status={(ex as RequestFailedException)?.Status}; code={(ex as RequestFailedException)?.ErrorCode}. Check credentials, data-plane RBAC and network access.");
}

sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
sealed class FakeBlob(Uri uri, BlobProperties properties) : BlobClient
{
    public override Uri Uri => uri;
    public BlobProperties Properties { get; set; } = properties;
    public Exception? Failure { get; set; }
    public override Task<Response<BlobProperties>> GetPropertiesAsync(BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default) =>
        Failure is null ? Task.FromResult(Response.FromValue(Properties, new FakeResponse(200))) : Task.FromException<Response<BlobProperties>>(Failure);
}
sealed class FakeTable : TableClient
{
    public ConcurrentDictionary<string, DroneImageAuditEntity> Rows { get; } = new();
    public Exception? Failure { get; set; }
    public override Task<Response> AddEntityAsync<T>(T entity, CancellationToken cancellationToken = default)
    {
        if (Failure is not null) return Task.FromException<Response>(Failure);
        var audit = (DroneImageAuditEntity)(object)entity;
        if (!Rows.TryAdd($"{audit.PartitionKey}/{audit.RowKey}", audit))
            return Task.FromException<Response>(new RequestFailedException(409, "Entity exists", "EntityAlreadyExists", null));
        return Task.FromResult<Response>(new FakeResponse(204));
    }
}
sealed class RecordingLogger : ILogger<ProcessDroneImageUpload>
{
    public ConcurrentBag<string> Messages { get; } = [];
    private int errors;
    public int Errors => errors;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        Messages.Add(formatter(state, exception));
        if (level == LogLevel.Error) Interlocked.Increment(ref errors);
    }
}
sealed class FakeResponse(int status) : Response
{
    public override int Status => status;
    public override string ReasonPhrase => "Test";
    public override Stream? ContentStream { get; set; }
    public override string ClientRequestId { get; set; } = "test";
    public override void Dispose() { }
    protected override bool ContainsHeader(string name) => false;
    protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
    protected override bool TryGetHeader(string name, out string value) { value = ""; return false; }
    protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values) { values = []; return false; }
}
