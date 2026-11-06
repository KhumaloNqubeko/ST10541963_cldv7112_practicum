using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs.Models;

namespace AgriStream.Functions;

public class DroneImageAuditEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "";
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string FileName { get; set; } = "";
    public string BlobPath { get; set; } = "";
    public string BlobUri { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string? ContentType { get; set; }
    public DateTimeOffset? UploadedAt { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
    public string ProcessingStatus { get; set; } = "Received";
    public string Message { get; set; } = "Drone image upload detected successfully";
    // Preserve revision information for investigation; never store SAS query strings.
    public string BlobETag { get; set; } = "";
    public string? BlobVersionId { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; }

    public static DroneImageAuditEntity FromBlob(string name, Uri uri, BlobProperties properties, DateTimeOffset processedAt)
    {
        var safeUri = new UriBuilder(uri) { Query = "", Fragment = "", UserName = "", Password = "" }.Uri.GetLeftPart(UriPartial.Path);
        var etag = properties.ETag.ToString();
        if (string.IsNullOrEmpty(etag)) throw new InvalidOperationException("Blob ETag is required to identify a revision safely.");
        var identity = JsonSerializer.Serialize(new[] { safeUri, etag, properties.VersionId });
        return new DroneImageAuditEntity {
            // LastModified represents the upload/update date, and remains stable on retries.
            PartitionKey = properties.LastModified.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            RowKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))),
            FileName = name[(name.LastIndexOf('/') + 1)..],
            BlobPath = $"{StorageClients.ContainerName}/{name}", BlobUri = safeUri,
            FileSizeBytes = properties.ContentLength, ContentType = properties.ContentType,
            UploadedAt = properties.CreatedOn == default ? null : properties.CreatedOn,
            ProcessedAt = processedAt.ToUniversalTime(), BlobETag = etag,
            BlobVersionId = properties.VersionId, LastModifiedAt = properties.LastModified
        };
    }
}
