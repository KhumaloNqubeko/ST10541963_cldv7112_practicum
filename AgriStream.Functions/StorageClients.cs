using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace AgriStream.Functions;

public static class StorageClients
{
    public const string ContainerName = "drone-images";
    public const string TableName = "DroneImageAudit";

    public static TableClient CreateAuditTable(IConfiguration configuration)
        => CreateTable(configuration, TableName);

    public static TableClient CreateTelemetryTable(IConfiguration configuration)
        => CreateTable(configuration, "SensorTelemetry");

    private static TableClient CreateTable(IConfiguration configuration, string tableName)
    {
        var connection = configuration["DroneAuditStorageConnection"];
        if (!string.IsNullOrWhiteSpace(connection)) return new TableClient(connection, tableName);
        var endpoint = configuration["DroneAuditStorageConnection:tableServiceUri"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new InvalidOperationException("Configure DroneAuditStorageConnection or DroneAuditStorageConnection__tableServiceUri.");
        var clientId = configuration["DroneAuditStorageConnection:clientId"];
        return new TableClient(uri, tableName, new DefaultAzureCredential(new DefaultAzureCredentialOptions {
            ManagedIdentityClientId = clientId
        }));
    }
}
