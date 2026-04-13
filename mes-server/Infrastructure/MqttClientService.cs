using System.Text.Json;
using System.Threading.Channels;
using MesServer.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace MesServer.Infrastructure;

public class MqttClientService : BackgroundService, IMqttClientService
{
    private readonly ILogger<MqttClientService> _logger;
    private readonly MqttOptions _options;
    private readonly IMqttClient _mqttClient;
    private readonly MqttClientOptions _mqttOptions;
    private readonly Channel<(string Topic, string Payload)> _inboundChannel;
    private readonly Dictionary<string, (MqttQualityOfServiceLevel Qos, List<Func<MqttApplicationMessage, Task>> Handlers)> _handlers = new();

    private static readonly int[] BackoffSeconds = [1, 2, 5, 15, 30, 60];

    public MqttClientService(ILogger<MqttClientService> logger, IOptions<MqttOptions> options)
    {
        _logger = logger;
        _options = options.Value;

        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();

        _mqttOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithCredentials(_options.Username, _options.Password)
            .WithClientId(_options.ClientId)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(_options.KeepAliveSec))
            .WithWillTopic($"ds/{_options.ClientId}/status")
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithWillPayload(JsonSerializer.SerializeToUtf8Bytes(new 
            { 
                equipment_id = _options.ClientId,
                event_type = "EAP_DISCONNECTED", 
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") 
            }))
            .Build();

        _inboundChannel = Channel.CreateBounded<(string, string)>(
            new BoundedChannelOptions(capacity: 1000)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });

        _mqttClient.ApplicationMessageReceivedAsync += e =>
        {
            _logger.LogDebug("Received message on topic: {Topic}", e.ApplicationMessage.Topic);
            
            if (_handlers.TryGetValue(e.ApplicationMessage.Topic, out var entry))
            {
                foreach (var handler in entry.Handlers)
                {
                    _ = handler(e.ApplicationMessage);
                }
            }
            // Basic wildcard support for ds/#
            else if (e.ApplicationMessage.Topic.StartsWith("ds/"))
            {
                foreach (var h in _handlers.Where(kv => IsMatch(kv.Key, e.ApplicationMessage.Topic)))
                {
                    foreach (var handler in h.Value.Handlers)
                    {
                        _ = handler(e.ApplicationMessage);
                    }
                }
            }
            
            return Task.CompletedTask;
        };
    }

    private static bool IsMatch(string filter, string topic)
    {
        if (filter == topic) return true;
        if (filter == "ds/#") return topic.StartsWith("ds/");
        if (filter.Contains("+"))
        {
            var filterParts = filter.Split('/');
            var topicParts = topic.Split('/');
            if (filterParts.Length != topicParts.Length) return false;
            for (int i = 0; i < filterParts.Length; i++)
            {
                if (filterParts[i] != "+" && filterParts[i] != topicParts[i]) return false;
            }
            return true;
        }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_mqttClient.IsConnected)
                {
                    _logger.LogInformation("Connecting to MQTT Broker {Host}:{Port}...", _options.Host, _options.Port);
                    var result = await _mqttClient.ConnectAsync(_mqttOptions, stoppingToken);

                    if (result.ResultCode == MqttClientConnectResultCode.Success)
                    {
                        _logger.LogInformation("Connected to MQTT Broker successfully.");
                        attempt = 0;

                        foreach (var kvp in _handlers)
                        {
                            await _mqttClient.SubscribeAsync(kvp.Key, kvp.Value.Qos, stoppingToken);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Failed to connect to MQTT Broker: {Result}", result.ResultCode);
                        await DelayWithBackoff(attempt++, stoppingToken);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error occurred during MQTT connection loop.");
                await DelayWithBackoff(attempt++, stoppingToken);
            }

            await Task.Delay(5000, stoppingToken);
        }
    }

    private async Task DelayWithBackoff(int attempt, CancellationToken ct)
    {
        var delay = GetBackoffDelay(attempt);
        _logger.LogInformation("Reconnecting in {Delay}s...", delay.TotalSeconds);
        await Task.Delay(delay, ct);
    }

    private static TimeSpan GetBackoffDelay(int attempt)
    {
        var baseSec = BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)];
        var jitter = baseSec * (0.8 + Random.Shared.NextDouble() * 0.4); // ±20%
        return TimeSpan.FromSeconds(jitter);
    }

    public async Task PublishAsync<T>(string topic, T payload, MqttQualityOfServiceLevel qos, bool retain, CancellationToken ct)
    {
        if (retain && MustNotRetain(topic))
        {
            throw new InvalidOperationException($"Topic '{topic}' MUST NOT be retained as per GEMINI.md §1.1");
        }

        if (!retain && MustRetain(topic))
        {
            retain = true;
        }

        var json = JsonSerializer.Serialize(payload);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(json)
            .WithQualityOfServiceLevel(qos)
            .WithRetainFlag(retain)
            .Build();

        if (_mqttClient.IsConnected)
        {
            await _mqttClient.PublishAsync(message, ct);
        }
        else
        {
            _logger.LogWarning("MqttClient is not connected. Publish failed for topic: {Topic}", topic);
        }
    }

    public async Task SubscribeAsync(string topicFilter, MqttQualityOfServiceLevel qos, Func<MqttApplicationMessage, Task> handler, CancellationToken ct)
    {
        if (!_handlers.ContainsKey(topicFilter))
        {
            _handlers[topicFilter] = (qos, new List<Func<MqttApplicationMessage, Task>>());
            if (_mqttClient.IsConnected)
            {
                await _mqttClient.SubscribeAsync(topicFilter, qos, ct);
            }
        }
        _handlers[topicFilter].Handlers.Add(handler);
    }

    private static bool MustRetain(string topic) =>
        topic.EndsWith("/status") ||
        topic.EndsWith("/lot") ||
        topic.EndsWith("/alarm") ||
        topic.EndsWith("/recipe") ||
        topic.EndsWith("/oracle");

    private static bool MustNotRetain(string topic) =>
        topic.EndsWith("/heartbeat") ||
        topic.EndsWith("/result") ||
        topic.EndsWith("/control");

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_mqttClient.IsConnected)
        {
            await _mqttClient.DisconnectAsync(new MqttClientDisconnectOptions(), cancellationToken);
        }
        await base.StopAsync(cancellationToken);
    }
}
