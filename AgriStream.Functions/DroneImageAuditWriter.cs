using Azure;
using Azure.Data.Tables;

namespace AgriStream.Functions;

public class DroneImageAuditWriter(TableClient table)
{
    public async Task<bool> TryInsertAsync(DroneImageAuditEntity audit, CancellationToken cancellationToken)
    {
        try {
            // Add is atomic across function instances. Never upsert/replace a later status.
            await table.AddEntityAsync(audit, cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409 && ex.ErrorCode == "EntityAlreadyExists") {
            return false;
        }
    }
}
