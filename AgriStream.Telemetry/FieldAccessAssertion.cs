using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgriStream.Telemetry;

/// <summary>MVC signs only after its server-side ownership check. No browser-supplied ownership claims.</summary>
public static class FieldAccessAssertion
{
    public const string HeaderName = "X-AgriStream-Field-Access";
    public static bool IsConfigured(string? secret) => secret is not null && Encoding.UTF8.GetByteCount(secret) >= 32;

    public static string Create(string fieldId, string userId, string secret, DateTimeOffset now)
    {
        if (!IsConfigured(secret) || !FieldIdentifier.IsValid(fieldId) || string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("Valid field access signing configuration and identity are required.");
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Access(fieldId, userId, now.ToUnixTimeSeconds(), now.AddMinutes(1).ToUnixTimeSeconds())));
        return payload + "." + Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));
    }

    public static bool Validate(string? token, string fieldId, string secret, DateTimeOffset now)
    {
        if (!IsConfigured(secret) || token is null || token.Length > 4096) return false;
        try {
            var parts = token.Split('.');
            if (parts.Length != 2) return false;
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(parts[0]));
            if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(parts[1]))) return false;
            var access = JsonSerializer.Deserialize<Access>(Convert.FromBase64String(parts[0]));
            var seconds = now.ToUnixTimeSeconds();
            return access is not null && access.FieldId == fieldId && !string.IsNullOrWhiteSpace(access.UserId)
                && access.IssuedAt <= seconds + 5 && access.ExpiresAt > seconds && access.ExpiresAt > access.IssuedAt
                && access.ExpiresAt - access.IssuedAt <= 60;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return false; }
    }

    private sealed record Access(string FieldId, string UserId, long IssuedAt, long ExpiresAt);
}
