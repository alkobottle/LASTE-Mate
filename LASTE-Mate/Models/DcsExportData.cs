using System.Text.Json.Serialization;

namespace LASTE_Mate.Models;

public class DcsExportData
{
    [JsonPropertyName("ground")]
    public WindData? Ground { get; set; }

    [JsonPropertyName("at2000m")]
    public WindData? At2000m { get; set; }

    [JsonPropertyName("at8000m")]
    public WindData? At8000m { get; set; }

    [JsonPropertyName("groundTemp")]
    public int? GroundTemp { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("mission")]
    public MissionInfo? Mission { get; set; }
}

public class WindData
{
    [JsonPropertyName("speed")]
    public double Speed { get; set; }

    [JsonPropertyName("direction")]
    public double Direction { get; set; }

    [JsonPropertyName("navDirection")]
    public double? NavDirection { get; set; }
}

public class MissionInfo
{
    [JsonPropertyName("theatre")]
    public string? Theatre { get; set; }

    [JsonPropertyName("sortie")]
    public string? Sortie { get; set; }

    [JsonPropertyName("start_time")]
    public int? StartTime { get; set; }
}

