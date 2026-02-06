using System.Text.Json.Serialization;
using LASTE_Mate.Models;

namespace LASTE_Mate.Serialization;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
internal partial class AppConfigJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(DcsExportData))]
[JsonSerializable(typeof(WindData))]
[JsonSerializable(typeof(MissionInfo))]
internal partial class DcsJsonContext : JsonSerializerContext
{
}
