using AgriStream.Functions;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgriStream.Irrigation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) => {
        services.AddSingleton(_ => StorageClients.CreateAuditTable(context.Configuration));
        services.AddSingleton<DroneImageAuditWriter>();
        services.AddSingleton(new SensorTelemetryReader(() => StorageClients.CreateTelemetryTable(context.Configuration)));
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<IrrigationDbContext>(options=>options.UseSqlServer(
            context.Configuration.GetConnectionString("DefaultConnection")??throw new InvalidOperationException("Configure ConnectionStrings:DefaultConnection for irrigation SQL access."),sql=>sql.EnableRetryOnFailure()));
        services.AddScoped<IIrrigationStore,IrrigationStore>();
        services.AddSingleton<IIrrigationQueue,IrrigationQueue>();
    })
    .Build();
await host.RunAsync();
