using Azure;

namespace AgriStream.Telemetry.Seed;

internal static class SeedErrors
{
    public static string Describe(Exception exception)
    {
        if (exception is not RequestFailedException error)
            return $"Seed/verification failed: {exception.GetType().Name}. HTTP status: n/a. Azure error code: n/a. Message: Check local storage configuration, credentials and input/schema. Raw exception details are omitted to protect credentials.";
        // Deliberately do not print Message/ToString: SDK request details can include SAS or keys.
        var message = error.Status switch {
            401 => "Storage authentication failed. Check the account connection string or sign-in.",
            403 => "Storage access denied. Check the account key/SAS permissions, network restrictions, or identity Table data permissions. Set DroneAuditStorageConnection to the existing account connection string to use key authentication.",
            404 => "The requested table or snapshot was not found. Confirm SensorTelemetry exists in the configured account; verification requires all planned keys.",
            409 => "Storage reported a conflict other than a safely handled existing entity. Check whether the table is being deleted.",
            429 => "Storage is throttling requests. Wait and retry.",
            >= 500 => "Azure Storage is temporarily unavailable. Check connectivity/service health and retry.",
            _ => "Azure Storage could not complete the request. Check the reported status/code, configuration, permissions and entity schema."
        };
        return $"Seed/verification failed: RequestFailedException. HTTP status: {error.Status}. Azure error code: {error.ErrorCode ?? "(not supplied)"}. Message: {message}";
    }
}
