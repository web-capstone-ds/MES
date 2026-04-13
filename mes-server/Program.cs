using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using MesServer.Infrastructure;
using MesServer.Services;
using MesServer.Scenarios;
using MesServer.Models;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices((hostContext, services) =>
        {
            // Configuration
            services.Configure<MqttOptions>(hostContext.Configuration.GetSection("Mqtt"));

            // MQTT Infrastructure
            services.AddSingleton<MqttClientService>();
            services.AddSingleton<IMqttClientService>(sp => sp.GetRequiredService<MqttClientService>());
            services.AddHostedService(sp => sp.GetRequiredService<MqttClientService>());
            
            // Domain Services
            services.AddSingleton<LotControlService>();
            services.AddHostedService(sp => sp.GetRequiredService<LotControlService>());

            services.AddSingleton<RecipeControlService>();

            services.AddSingleton<EquipmentMonitorService>();
            services.AddHostedService(sp => sp.GetRequiredService<EquipmentMonitorService>());
            
            // Scenario Management
            services.AddSingleton<ScenarioLoader>();
        });

    using var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
