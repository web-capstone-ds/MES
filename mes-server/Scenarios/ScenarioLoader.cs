using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MesServer.Infrastructure;
using MesServer.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;

namespace MesServer.Scenarios;

public class ScenarioLoader : BackgroundService
{
    private readonly ILogger<ScenarioLoader> _logger;
    private readonly IMqttClientService _mqttClient;
    private readonly MqttOptions _options;
    private readonly string _scenarioPath;

    private const MqttQualityOfServiceLevel QoS1 = MqttQualityOfServiceLevel.AtLeastOnce;
    private const MqttQualityOfServiceLevel QoS2 = MqttQualityOfServiceLevel.ExactlyOnce;

    public ScenarioLoader(ILogger<ScenarioLoader> logger, IMqttClientService mqttClient, IOptions<MqttOptions> options, IConfiguration configuration)
    {
        _logger = logger;
        _mqttClient = mqttClient;
        _options = options.Value;
        _scenarioPath = configuration["ScenarioPath"] ?? "../EAP_mock_data/scenarios/multi_equipment_4x.json";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for MQTT to connect
        await Task.Delay(3000, stoppingToken);

        _logger.LogInformation("Starting ScenarioLoader with path: {Path}", _scenarioPath);

        try
        {
            var config = await LoadConfigAsync(stoppingToken);
            if (config == null) return;

            // 1. 사전 검증: mock_sequence 파일 전체 존재 여부
            var mockDir = Path.GetDirectoryName(_scenarioPath) ?? "";
            var baseDir = Path.GetFullPath(Path.Combine(mockDir, "..")); // ../EAP_mock_data

            foreach (var eq in config.Equipments)
            {
                foreach (var fileName in eq.MockSequence)
                {
                    var filePath = Path.Combine(baseDir, fileName.EndsWith(".json") ? fileName : fileName + ".json");
                    if (!File.Exists(filePath))
                    {
                        throw new FileNotFoundException($"Mock file NOT FOUND: {filePath}");
                    }
                }
            }

            _logger.LogInformation("Pre-validation PASSED: All mock sequence files exist.");

            // 2. 통합 시나리오 실행
            var sw = Stopwatch.StartNew();
            int totalPublished = 0;

            var tasks = config.Equipments.Select(async eq =>
            {
                try
                {
                    int publishedCount = await RunEquipmentScenarioAsync(eq, baseDir, stoppingToken);
                    Interlocked.Add(ref totalPublished, publishedCount);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{EqId}] Scenario failed", eq.EquipmentId);
                }
            });

            await Task.WhenAll(tasks);
            sw.Stop();

            _logger.LogInformation("Scenario Completed: 총 {N}건 발행, 소요 {S}s", totalPublished, sw.Elapsed.TotalSeconds.ToString("F1"));
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Scenario execution CRITICAL failure.");
        }
    }

    private async Task<ScenarioConfig?> LoadConfigAsync(CancellationToken ct)
    {
        if (!File.Exists(_scenarioPath))
        {
            _logger.LogError("Scenario config not found: {Path}", _scenarioPath);
            return null;
        }

        var json = await File.ReadAllTextAsync(_scenarioPath, ct);
        return JsonSerializer.Deserialize<ScenarioConfig>(json);
    }

    private async Task<int> RunEquipmentScenarioAsync(EquipmentScenarioConfig eq, string baseDir, CancellationToken ct)
    {
        int count = 0;
        foreach (var fileName in eq.MockSequence)
        {
            if (ct.IsCancellationRequested) break;

            var filePath = Path.Combine(baseDir, fileName.EndsWith(".json") ? fileName : fileName + ".json");
            var json = await File.ReadAllTextAsync(filePath, ct);

            // 3. equipment_id 동적 치환 (페이로드 내부 포함)
            var node = JsonNode.Parse(json);
            if (node is JsonObject obj)
            {
                obj["equipment_id"] = eq.EquipmentId;
                json = obj.ToJsonString();
            }

            var eventType = node?["event_type"]?.ToString();
            if (string.IsNullOrEmpty(eventType)) continue;

            // 4. GetTopicMeta() 라우팅 테이블 (§7.2)
            var (suffix, qos, retain) = GetTopicMeta(eventType);
            var topic = $"ds/{eq.EquipmentId}/{suffix}";

            await _mqttClient.PublishAsync(topic, JsonDocument.Parse(json), qos, retain, ct);
            count++;

            // Simulation delay
            await Task.Delay(500, ct);
        }
        return count;
    }

    private static (string Suffix, MqttQualityOfServiceLevel Qos, bool Retain)
        GetTopicMeta(string eventType) => eventType switch
    {
        "HEARTBEAT"         => ("heartbeat", QoS1,  Retain: false),
        "STATUS_UPDATE"     => ("status",    QoS1,  Retain: true),
        "INSPECTION_RESULT" => ("result",    QoS1,  Retain: false),
        "LOT_END"           => ("lot",       QoS2,  Retain: true),
        "HW_ALARM"          => ("alarm",     QoS2,  Retain: true),
        "RECIPE_CHANGED"    => ("recipe",    QoS2,  Retain: true),
        "CONTROL_CMD"       => ("control",   QoS2,  Retain: false),
        "ORACLE_ANALYSIS"   => ("oracle",    QoS2,  Retain: true),
        _ => throw new ArgumentException($"Unknown event_type: {eventType}")
    };
}
