using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LASTE_Mate.Models;

public class DcsCommand
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

public class ButtonPressCommand : DcsCommand
{
    [JsonPropertyName("button")]
    public string Button { get; set; } = string.Empty;

    [JsonPropertyName("device_id")]
    public int? DeviceId { get; set; }

    [JsonPropertyName("action_id")]
    public int? ActionId { get; set; }
}

public class TestButtonCommand : DcsCommand
{
    [JsonPropertyName("buttons")]
    public List<string> Buttons { get; set; } = new();
}

public class WindDataCommand : DcsCommand
{
    [JsonPropertyName("windLines")]
    public List<WindLineData> WindLines { get; set; } = new();
}

public class WindLineData
{
    [JsonPropertyName("alt")]
    public string Alt { get; set; } = string.Empty;

    [JsonPropertyName("brgPlusSpd")]
    public string BrgPlusSpd { get; set; } = string.Empty;

    [JsonPropertyName("tmp")]
    public string Tmp { get; set; } = string.Empty;
}

