using MesServer.Api;
using MesServer.Infrastructure;
using MesServer.Models;
using MesServer.Scenarios;
using MesServer.Services;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();
    builder.WebHost.UseUrls(
        Environment.GetEnvironmentVariable("MES_DASHBOARD_URLS")
        ?? "http://127.0.0.1:8081");

    // Configuration
    builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));

    // MQTT Infrastructure
    builder.Services.AddSingleton<MqttClientService>();
    builder.Services.AddSingleton<IMqttClientService>(sp => sp.GetRequiredService<MqttClientService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttClientService>());

    // 제어 추천(경보) 저장소 + 모바일 관제용 MQTT 발행 (ds/{eq}/recommendation)
    builder.Services.AddSingleton<RecommendationService>();

    // Oracle 임계값 제안 캐시: MES 콘솔/대시보드 treco/tapprove/treject 지원
    builder.Services.AddSingleton<ThresholdProposalService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ThresholdProposalService>());

    // Domain Services
    builder.Services.AddSingleton<LotControlService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<LotControlService>());

    builder.Services.AddSingleton<RecipeControlService>();
    builder.Services.AddSingleton<RecipeCatalogService>();

    builder.Services.AddSingleton<EquipmentMonitorService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<EquipmentMonitorService>());

    // 운영자 수동 제어: MES 로컬 운영 인터페이스 전용. 외부 Web-Backend에서 접근 불가.
    builder.Services.AddSingleton<OperatorConsoleService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OperatorConsoleService>());

    // Scenario Management
    builder.Services.AddSingleton<ScenarioLoader>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ScenarioLoader>());

    var app = builder.Build();
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapMesDashboardEndpoints();

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
