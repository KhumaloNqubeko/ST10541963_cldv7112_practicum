using System.Text.Json;
using AgriStream.Irrigation;
using Azure.Identity;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Configuration;

namespace AgriStream.Functions;

public interface IIrrigationQueue { Task SendAsync(IrrigationEnvelope envelope,CancellationToken ct); }
public sealed class IrrigationQueue(IConfiguration configuration):IIrrigationQueue
{
    public const string Name="irrigation-commands";
    private readonly Lazy<QueueClient> client=new(()=>{
        var options=new QueueClientOptions{MessageEncoding=QueueMessageEncoding.Base64};
        var connection=configuration["IrrigationQueueConnection"];
        if(!string.IsNullOrWhiteSpace(connection))return new QueueClient(connection,Name,options);
        var endpoint=configuration["IrrigationQueueConnection:queueServiceUri"];
        if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri) || uri.Scheme!="https")throw new InvalidOperationException("Configure IrrigationQueueConnection.");
        return new QueueClient(new Uri(uri.AbsoluteUri.TrimEnd('/')+"/"+Name),new DefaultAzureCredential(new DefaultAzureCredentialOptions{ManagedIdentityClientId=configuration["IrrigationQueueConnection:clientId"]}),options);
    });
    public async Task SendAsync(IrrigationEnvelope envelope,CancellationToken ct)
        =>await client.Value.SendMessageAsync(JsonSerializer.Serialize(envelope,new JsonSerializerOptions(JsonSerializerDefaults.Web)),cancellationToken:ct);
}
