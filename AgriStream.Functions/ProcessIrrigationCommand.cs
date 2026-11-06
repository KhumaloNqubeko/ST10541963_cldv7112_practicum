using System.Text.Json;
using AgriStream.Irrigation;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgriStream.Functions;

public sealed class ProcessIrrigationCommand(IIrrigationStore store,IConfiguration config,TimeProvider clock,ILogger<ProcessIrrigationCommand> logger)
{
    [Function("ProcessIrrigationCommand")]
    public async Task RunAsync([QueueTrigger("irrigation-commands",Connection="IrrigationQueueConnection")] string message,CancellationToken ct)
    {
        Guid? id=null;
        try {
            if(message.Length>16384)throw new InvalidIrrigationCommandException("Oversized command.");
            var envelope=JsonSerializer.Deserialize<IrrigationEnvelope>(message,new JsonSerializerOptions(JsonSerializerDefaults.Web));
            id=envelope?.Command?.CommandId;
            if(!IrrigationSecurity.VerifyEnvelope(envelope,config["IrrigationAccess:SigningKey"]??""))throw new InvalidIrrigationCommandException("Invalid command or queue signature.");
            var outcome=await store.ProcessAsync(envelope!,clock.GetUtcNow(),ct);
            logger.LogInformation("Irrigation command {CommandId}: {Outcome}. Registry records requested state only.",id,outcome);
        }
        catch(Exception ex) {
            var reason=ex is InvalidIrrigationCommandException?ex.Message:ex is JsonException?"Malformed command JSON.":"Service failure; invocation must retry.";
            logger.LogError("Irrigation command {CommandId} failed. ErrorType={ErrorType}; Reason={Reason}; permanent invalid messages reach irrigation-commands-poison after runtime retries.",id,ex.GetType().Name,reason);
            throw;
        }
    }
}
