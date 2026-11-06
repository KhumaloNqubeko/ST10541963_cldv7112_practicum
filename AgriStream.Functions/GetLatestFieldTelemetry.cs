using System.Net;
using System.Text.Json;
using AgriStream.Telemetry;
using Azure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgriStream.Functions;

public class GetLatestFieldTelemetry(SensorTelemetryReader reader, IConfiguration configuration,
    TimeProvider clock, ILogger<GetLatestFieldTelemetry> logger)
{
    [Function("GetLatestFieldTelemetry")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "fields/{fieldId}/telemetry/latest")] HttpRequestData request,
        string fieldId, CancellationToken cancellationToken)
    {
        if (!FieldIdentifier.IsValid(fieldId)) return await Json(request, HttpStatusCode.BadRequest, new { error = "Invalid field identifier." }, cancellationToken);
        var secret = configuration["TelemetryAccess:SigningKey"];
        if (!FieldAccessAssertion.IsConfigured(secret)) return await Unavailable(request, cancellationToken);
        var tokens = request.Headers.TryGetValues(FieldAccessAssertion.HeaderName, out var headers) ? headers.ToArray() : [];
        if (tokens.Length != 1 || !FieldAccessAssertion.Validate(tokens[0], fieldId, secret!, clock.GetUtcNow()))
            return await Json(request, HttpStatusCode.Forbidden, new { error = "Field access was not authorised." }, cancellationToken);
        try {
            var response = await reader.ReadAsync(fieldId, cancellationToken);
            return await Json(request, HttpStatusCode.OK, response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is RequestFailedException or InvalidDataException or InvalidOperationException or ArgumentException or FormatException or OverflowException or InvalidCastException) {
            logger.LogError("Latest telemetry request failed. ErrorType={ErrorType}, Status={Status}, ErrorCode={ErrorCode}",
                ex.GetType().Name, (ex as RequestFailedException)?.Status, (ex as RequestFailedException)?.ErrorCode);
            return await Unavailable(request, cancellationToken);
        }
    }

    private static Task<HttpResponseData> Unavailable(HttpRequestData request, CancellationToken ct) =>
        Json(request, HttpStatusCode.ServiceUnavailable, new { error = "Telemetry is temporarily unavailable." }, ct);

    private static async Task<HttpResponseData> Json(HttpRequestData request, HttpStatusCode status, object body, CancellationToken ct)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await JsonSerializer.SerializeAsync(response.Body, body, body.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
        return response;
    }
}
