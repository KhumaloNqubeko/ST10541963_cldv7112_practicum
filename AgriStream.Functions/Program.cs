using AgriStream.Functions;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) => {
        services.AddSingleton(_ => StorageClients.CreateAuditTable(context.Configuration));
        services.AddSingleton<DroneImageAuditWriter>();
        services.AddSingleton(new SensorTelemetryReader(() => StorageClients.CreateTelemetryTable(context.Configuration)));
        services.AddSingleton(TimeProvider.System);
    })
    .Build();
await host.RunAsync();
