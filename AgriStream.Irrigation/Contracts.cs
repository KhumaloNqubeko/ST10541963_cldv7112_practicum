using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgriStream.Telemetry;

namespace AgriStream.Irrigation;

public sealed record IrrigationCommand(Guid CommandId, string DeviceId, int FarmId, string FieldId, string Action, string Source);
public sealed record IrrigationEnvelope(IrrigationCommand Command, string ActorId, DateTimeOffset AcceptedAt, string Signature);
public sealed record IrrigationReceipt(Guid CommandId, string Status);

public static class IrrigationValidation
{
    public static bool Valid(IrrigationCommand? c) => c is not null && c.CommandId != Guid.Empty && c.FarmId > 0
        && FieldIdentifier.IsValid(c.DeviceId) && FieldIdentifier.IsValid(c.FieldId)
        && c.Action is "Start" or "Stop" && c.Source is "Farmer" or "Sensor";
    public static string Hash(IrrigationCommand c) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(c)));
}

public static class IrrigationSecurity
{
    public const string Header = "X-AgriStream-Irrigation-Access";
    private sealed record Access(string CommandHash, string ActorId, long IssuedAt, long ExpiresAt);
    private static string Mac(string value, string secret) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(value)));
    private static bool Equal(string a, string b) { try { return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(a), Convert.FromBase64String(b)); } catch (FormatException) { return false; } }
    public static string SignRequest(IrrigationCommand c, string actor, string secret, DateTimeOffset now)
    {
        if (!IrrigationValidation.Valid(c) || string.IsNullOrWhiteSpace(actor) || !FieldAccessAssertion.IsConfigured(secret)) throw new InvalidOperationException("Invalid irrigation signing configuration or command.");
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Access(IrrigationValidation.Hash(c),actor,now.ToUnixTimeSeconds(),now.AddMinutes(1).ToUnixTimeSeconds())));
        return payload+"."+Mac("irrigation-http-v1:"+payload,secret);
    }
    public static string? VerifyRequest(IrrigationCommand c,string? token,string secret,DateTimeOffset now)
    {
        if (!FieldAccessAssertion.IsConfigured(secret) || token is null || token.Length>4096) return null;
        try {
            var parts=token.Split('.');
            if(parts.Length!=2 || !Equal(Mac("irrigation-http-v1:"+parts[0],secret),parts[1]))return null;
            var access=JsonSerializer.Deserialize<Access>(Convert.FromBase64String(parts[0]));
            var time=now.ToUnixTimeSeconds();
            return access is not null && access.CommandHash==IrrigationValidation.Hash(c) && !string.IsNullOrWhiteSpace(access.ActorId)
                && access.IssuedAt<=time+5 && access.ExpiresAt>time && access.ExpiresAt>access.IssuedAt && access.ExpiresAt-access.IssuedAt<=60 ? access.ActorId:null;
        } catch(Exception ex) when(ex is FormatException or JsonException){return null;}
    }
    public static IrrigationEnvelope Seal(IrrigationCommand c,string actor,DateTimeOffset acceptedAt,string secret)
        => new(c,actor,acceptedAt,Mac("irrigation-queue-v1:"+JsonSerializer.Serialize(new {Command=c,ActorId=actor,AcceptedAt=acceptedAt}),secret));
    public static bool VerifyEnvelope(IrrigationEnvelope? e,string secret) => e is not null && IrrigationValidation.Valid(e.Command)
        && FieldAccessAssertion.IsConfigured(secret) && !string.IsNullOrWhiteSpace(e.ActorId) && e.AcceptedAt!=default
        && e.Signature is not null && Equal(Seal(e.Command,e.ActorId,e.AcceptedAt,secret).Signature,e.Signature);
}
