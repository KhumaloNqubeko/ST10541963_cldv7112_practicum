using System.Net.Http.Json;
using AgriStream.Telemetry;

namespace AgriStream.Services;

public sealed class FieldTelemetryClient(HttpClient http, IConfiguration configuration, TimeProvider clock)
{
    public async Task<FieldTelemetryResponse> GetAsync(string fieldId, string userId, CancellationToken cancellationToken)
    {
        var baseUrl = configuration["TelemetryFunctions:BaseUrl"];
        var key = configuration["TelemetryFunctions:FunctionKey"];
        var signingKey = configuration["TelemetryAccess:SigningKey"];
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != "https" ||
            !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment) || !string.IsNullOrEmpty(baseUri.UserInfo) ||
            string.IsNullOrWhiteSpace(key) || !FieldAccessAssertion.IsConfigured(signingKey))
            throw new InvalidOperationException("Telemetry service configuration is incomplete.");
        var uri = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + $"/fields/{Uri.EscapeDataString(fieldId)}/telemetry/latest");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("x-functions-key", key);
        request.Headers.Add(FieldAccessAssertion.HeaderName, FieldAccessAssertion.Create(fieldId, userId, signingKey!, clock.GetUtcNow()));
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<FieldTelemetryResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Empty telemetry response.");
        if (data.FieldId != fieldId || data.Readings is null || data.Readings.Count > 100 ||
            data.Readings.Any(r => r is null || !FieldIdentifier.IsValid(r.SensorId) || r.RecordedAt == default || r.SoilMoisturePercent is < 0 or > 100 || r.HumidityPercent is < 0 or > 100) ||
            data.Readings.Select(r => r.SensorId).Distinct(StringComparer.Ordinal).Count() != data.Readings.Count)
            throw new InvalidDataException("Unexpected telemetry response.");
        return data;
    }
}
