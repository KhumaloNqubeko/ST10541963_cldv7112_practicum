using Azure;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AgriStream.Functions;

public class ProcessDroneImageUpload(DroneImageAuditWriter writer, TimeProvider clock, ILogger<ProcessDroneImageUpload> logger)
{
    [Function("ProcessDroneImageUpload")]
    public async Task RunAsync(
        [BlobTrigger("drone-images/{name}", Connection = "DroneStorageConnection", Source = BlobTriggerSource.LogsAndContainerScan)] BlobClient blob,
        string name, CancellationToken cancellationToken)
    {
        try {
            var properties = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
            var audit = DroneImageAuditEntity.FromBlob(name, blob.Uri, properties, clock.GetUtcNow());
            logger.LogInformation("Drone upload detected: FileName={FileName}, BlobPath={BlobPath}, BlobUri={BlobUri}, FileSizeBytes={FileSizeBytes}, ContentType={ContentType}, UploadedAt={UploadedAt}, ProcessedAt={ProcessedAt}, BlobETag={BlobETag}",
                audit.FileName, audit.BlobPath, audit.BlobUri, audit.FileSizeBytes, audit.ContentType, audit.UploadedAt, audit.ProcessedAt, audit.BlobETag);
            var inserted = await writer.TryInsertAsync(audit, cancellationToken);
            if (inserted) logger.LogInformation("Received audit persisted: PartitionKey={PartitionKey}, RowKey={RowKey}", audit.PartitionKey, audit.RowKey);
            else logger.LogInformation("Audit already exists; preserved existing status: PartitionKey={PartitionKey}, RowKey={RowKey}", audit.PartitionKey, audit.RowKey);
        }
        catch (Exception ex) {
            // SDK messages can contain request details; log safe diagnostics, never connection strings/SAS.
            logger.LogError("Drone upload audit failed; invocation will fail for runtime retry. ErrorType={ErrorType}, Status={Status}, ErrorCode={ErrorCode}",
                ex.GetType().Name, (ex as RequestFailedException)?.Status, (ex as RequestFailedException)?.ErrorCode);
            throw;
        }
    }
}
