namespace MesServer.Models;

public class MqttOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ClientId { get; set; } = "mes-server";
    public int KeepAliveSec { get; set; } = 60;
    public int[] ReconnectBackoffSec { get; set; } = [1, 2, 5, 15, 30, 60];
}
