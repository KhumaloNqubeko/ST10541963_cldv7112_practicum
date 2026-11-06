using System.Net;
using System.Text.Json;
using AgriStream.Irrigation;
using AgriStream.Telemetry;
using Azure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgriStream.Functions;

public sealed class SubmitIrrigationCommand(IIrrigationStore store,IIrrigationQueue queue,IConfiguration config,TimeProvider clock,ILogger<SubmitIrrigationCommand> logger)
{
    [Function("SubmitIrrigationCommand")]
    public async Task<HttpResponseData> RunAsync([HttpTrigger(AuthorizationLevel.Function,"post",Route="irrigation/commands")] HttpRequestData request,CancellationToken ct)
    {
        IrrigationCommand? command;
        try {
            var bytes=new byte[8193]; var total=0;
            while(total<bytes.Length){var read=await request.Body.ReadAsync(bytes.AsMemory(total),ct);if(read==0)break;total+=read;}
            if(total>8192)return await Reply(request,HttpStatusCode.BadRequest,new{error="Command is too large."},ct);
            command=JsonSerializer.Deserialize<IrrigationCommand>(bytes.AsSpan(0,total),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }catch(JsonException){return await Reply(request,HttpStatusCode.BadRequest,new{error="Invalid JSON command."},ct);}
        if(!IrrigationValidation.Valid(command))return await Reply(request,HttpStatusCode.BadRequest,new{error="Valid command ID, device, farm, field, Start/Stop action and Farmer/Sensor source are required."},ct);
        var c=command!;
        var queueSecret=config["IrrigationAccess:SigningKey"];
        if(!FieldAccessAssertion.IsConfigured(queueSecret))return await Reply(request,HttpStatusCode.ServiceUnavailable,new{error="Irrigation is not configured."},ct);
        var secret=c.Source=="Sensor"?config["IrrigationAccess:SensorSigningKey"]:queueSecret;
        var tokens=request.Headers.TryGetValues(IrrigationSecurity.Header,out var values)?values.ToArray():[];
        var actor=FieldAccessAssertion.IsConfigured(secret) && tokens.Length==1 ? IrrigationSecurity.VerifyRequest(c,tokens[0],secret!,clock.GetUtcNow()):null;
        if(actor is null || actor.Length>450)return await Reply(request,HttpStatusCode.Forbidden,new{error="Command access is not authorised."},ct);
        try {
            if(!await store.AuthorisedAsync(c,actor,ct))return await Reply(request,HttpStatusCode.Forbidden,new{error="Device control is not authorised."},ct);
            var existing=await store.FindAsync(c.CommandId,ct);
            if(existing is not null){
                if(existing.CommandHash!=IrrigationValidation.Hash(c) || existing.ActorId!=actor)return await Reply(request,HttpStatusCode.Conflict,new{error="Command ID is already in use."},ct);
                return await Reply(request,HttpStatusCode.Accepted,new IrrigationReceipt(c.CommandId,"AlreadyProcessed"),ct);
            }
            await queue.SendAsync(IrrigationSecurity.Seal(c,actor,clock.GetUtcNow(),queueSecret!),ct);
            logger.LogInformation("Irrigation command {CommandId} queued.",c.CommandId);
            return await Reply(request,HttpStatusCode.Accepted,new IrrigationReceipt(c.CommandId,"Queued"),ct);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex) when(ex is RequestFailedException or DbUpdateException or System.Data.Common.DbException or InvalidOperationException or ArgumentException or OperationCanceledException or HttpRequestException){
            logger.LogError("Irrigation submission {CommandId} failed. ErrorType={ErrorType}",c.CommandId,ex.GetType().Name);
            return await Reply(request,HttpStatusCode.ServiceUnavailable,new{error="Irrigation command service is temporarily unavailable. Retry using the same command ID."},ct);
        }
    }
    private static async Task<HttpResponseData> Reply(HttpRequestData request,HttpStatusCode status,object body,CancellationToken ct)
    {
        var response=request.CreateResponse(status);response.Headers.Add("Content-Type","application/json; charset=utf-8");response.Headers.Add("Cache-Control","no-store");
        await JsonSerializer.SerializeAsync(response.Body,body,body.GetType(),new JsonSerializerOptions(JsonSerializerDefaults.Web),ct);return response;
    }
}
