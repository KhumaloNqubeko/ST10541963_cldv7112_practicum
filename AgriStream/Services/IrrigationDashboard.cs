using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AgriStream.Data;
using AgriStream.Irrigation;
using AgriStream.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AgriStream.Services;

public sealed record IrrigationForm(IrrigationCommand Command,string UserId,DateTimeOffset ExpiresAt);
public sealed record IrrigationDeviceCard(IrrigationDevice Device,string StartToken,string StopToken);
public sealed record IrrigationResult(Guid CommandId,string Message,string? RetryToken);

public class IrrigationDashboard(ApplicationDbContext db,FarmerTelemetryDashboard fields,IDataProtectionProvider protection,IConfiguration config,TimeProvider clock)
{
    private readonly IDataProtector protector=protection.CreateProtector("AgriStream.Irrigation.Form.v1");
    public bool Enabled=>config.GetValue<bool>("Irrigation:Enabled");
    public string Issue(IrrigationDevice device,string user,string action)=>protector.Protect(JsonSerializer.Serialize(new IrrigationForm(new(Guid.NewGuid(),device.DeviceId,device.FarmId,device.FieldId,action,"Farmer"),user,clock.GetUtcNow().AddMinutes(10))));
    public IrrigationForm? Read(string? token)
    {
        if(token is null || token.Length>8192)return null;
        try{var form=JsonSerializer.Deserialize<IrrigationForm>(protector.Unprotect(token));return form is not null && form.ExpiresAt>clock.GetUtcNow() && IrrigationValidation.Valid(form.Command)?form:null;}
        catch(Exception ex)when(ex is System.Security.Cryptography.CryptographicException or JsonException){return null;}
    }
    public async Task<bool> OwnsAsync(ClaimsPrincipal user,IrrigationCommand command,CancellationToken ct)
    {
        var id=user.FindFirstValue(ClaimTypes.NameIdentifier);
        if(!Enabled || !user.IsInRole("Farmer") || id is null || !fields.FieldMappings().TryGetValue(command.FieldId,out var mapped) || mapped!=command.FarmId)return false;
        return await db.IrrigationDevices.AnyAsync(d=>d.DeviceId==command.DeviceId && d.IsEnabled && d.FarmId==command.FarmId && d.FieldId==command.FieldId && db.Farms.Any(f=>f.Id==d.FarmId && f.FarmerId==id),ct);
    }
    public async Task PopulateAsync(MonitoringDashboardViewModel model,ClaimsPrincipal user,CancellationToken ct)
    {
        if(!Enabled || !user.IsInRole("Farmer"))return;
        var userId=user.FindFirstValue(ClaimTypes.NameIdentifier);
        var devices=await db.IrrigationDevices.AsNoTracking().Where(d=>db.Farms.Any(f=>f.Id==d.FarmId && f.FarmerId==userId)).OrderBy(d=>d.DeviceId).Take(50).ToListAsync(ct);
        var mappings=fields.FieldMappings();
        model.IrrigationEnabled=true;
        model.IrrigationDevices=devices.Where(d=>mappings.TryGetValue(d.FieldId,out var id) && id==d.FarmId && (model.SelectedFieldId is null || d.FieldId==model.SelectedFieldId))
            .Select(d=>new IrrigationDeviceCard(d,Issue(d,userId!,"Start"),Issue(d,userId!,"Stop"))).ToList();
    }
}

public sealed class IrrigationCommandClient(HttpClient http,IConfiguration config,TimeProvider clock)
{
    public async Task<IrrigationReceipt> SubmitAsync(IrrigationCommand command,string user,CancellationToken ct)
    {
        var url=config["IrrigationFunctions:BaseUrl"]??config["TelemetryFunctions:BaseUrl"];
        var key=config["IrrigationFunctions:FunctionKey"];
        var secret=config["IrrigationAccess:SigningKey"];
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) || uri.Scheme!="https" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || string.IsNullOrWhiteSpace(key) || !AgriStream.Telemetry.FieldAccessAssertion.IsConfigured(secret))throw new InvalidOperationException("Irrigation client is not configured.");
        using var request=new HttpRequestMessage(HttpMethod.Post,uri.AbsoluteUri.TrimEnd('/')+"/irrigation/commands"){Content=JsonContent.Create(command)};
        request.Headers.Add("x-functions-key",key);
        request.Headers.Add(IrrigationSecurity.Header,IrrigationSecurity.SignRequest(command,user,secret!,clock.GetUtcNow()));
        using var response=await http.SendAsync(request,ct);response.EnsureSuccessStatusCode();
        var receipt=await response.Content.ReadFromJsonAsync<IrrigationReceipt>(cancellationToken:ct);
        if(receipt is null || receipt.CommandId!=command.CommandId || receipt.Status is not("Queued" or "AlreadyProcessed"))throw new InvalidDataException("Invalid irrigation response.");
        return receipt;
    }
}
