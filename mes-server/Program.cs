using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

            // 제어 추천(경보) 저장소 + 모바일 관제용 MQTT 발행 (ds/{eq}/recommendation)
            services.AddSingleton<RecommendationService>();

            // Domain Services
            services.AddSingleton<LotControlService>();
            services.AddHostedService(sp => sp.GetRequiredService<LotControlService>());

            services.AddSingleton<RecipeControlService>();

            services.AddSingleton<EquipmentMonitorService>();
            services.AddHostedService(sp => sp.GetRequiredService<EquipmentMonitorService>());

            // 운영자 수동 제어: MES 콘솔(stdin) 전용. 외부(네트워크/Web-Backend)에서 접근 불가.
            services.AddSingleton<OperatorConsoleService>();
            services.AddHostedService(sp => sp.GetRequiredService<OperatorConsoleService>());

            // Scenario Management
            services.AddSingleton<ScenarioLoader>();
            services.AddHostedService(sp => sp.GetRequiredService<ScenarioLoader>());
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
