using MQTTnet;
using MQTTnet.Protocol;

namespace MesServer.Infrastructure;

public interface IMqttClientService
{
    Task PublishAsync<T>(
        string topic, T payload,
        MqttQualityOfServiceLevel qos,
        bool retain,
        CancellationToken ct);

    Task SubscribeAsync(
        string topicFilter,
        MqttQualityOfServiceLevel qos,
        Func<MqttApplicationMessage, Task> handler,
        CancellationToken ct);

    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
