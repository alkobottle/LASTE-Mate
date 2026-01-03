using System.Text.Json.Serialization;

namespace LASTE_Mate.Models;

public class AppConfig
{
    [JsonPropertyName("tcpPort")]
    public int TcpPort { get; set; } = 10309;

    [JsonPropertyName("autoUpdate")]
    public bool AutoUpdate { get; set; } = true;

    [JsonPropertyName("dcsBiosPort")]
    public int DcsBiosPort { get; set; } = 7778;

    [JsonPropertyName("tcpListenerEnabled")]
    public bool TcpListenerEnabled { get; set; } = true;
}

