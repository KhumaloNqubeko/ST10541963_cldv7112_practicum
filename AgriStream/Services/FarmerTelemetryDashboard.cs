using System.Security.Claims;
using System.Text.Json;
using AgriStream.Data;
using AgriStream.Models;
using AgriStream.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace AgriStream.Services;

public sealed class FarmerTelemetryDashboard(FarmAccess access, FieldTelemetryClient client,
    IConfiguration configuration, ILogger<FarmerTelemetryDashboard> logger)
{
    public IReadOnlyDictionary<string, int> FieldMappings() => configuration.GetSection("TelemetryFunctions:Fields")
        .GetChildren().Where(c => FieldIdentifier.IsValid(c.Key) && int.TryParse(c["FarmId"], out var id) && id > 0)
        .ToDictionary(c => c.Key, c => int.Parse(c["FarmId"]!), StringComparer.Ordinal);

    public async Task<bool> PopulateAsync(MonitoringDashboardViewModel model, ClaimsPrincipal user, string? fieldId, CancellationToken ct)
    {
        if (!user.IsInRole(SystemRoles.Farmer)) return false;
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return false;
        // Admin/Agronomist visibility must not broaden this Farmer ownership check.
        var ownedIds = await access.VisibleTo(user).Where(f => f.FarmerId == userId).Select(f => f.Id).ToListAsync(ct);
        model.TelemetryFields = FieldMappings().Where(f => ownedIds.Contains(f.Value)).Select(f => f.Key).Order(StringComparer.Ordinal).ToList();
        if (fieldId is not null && !model.TelemetryFields.Contains(fieldId, StringComparer.Ordinal)) return false;
        model.UsesFieldTelemetry = true;
        model.SelectedFieldId = fieldId ?? model.TelemetryFields.FirstOrDefault();
        if (model.SelectedFieldId is null) { model.TelemetryMessage = "Live field readings aren't available for your farms yet."; return true; }
        try {
            model.FieldTelemetry = await client.GetAsync(model.SelectedFieldId, userId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or InvalidDataException or JsonException or NotSupportedException) {
            logger.LogWarning("Field telemetry service unavailable. ErrorType={ErrorType}", ex.GetType().Name);
            model.TelemetryMessage = "Live field readings are temporarily unavailable. Please try again shortly.";
        }
        return true;
    }
}
