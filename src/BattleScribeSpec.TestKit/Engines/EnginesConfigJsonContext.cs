using System.Text.Json.Serialization;
using BattleScribeSpec.Concurrency;

namespace BattleScribeSpec.Engines;

/// <summary>engines.json config models + source-generated JSON context.</summary>
public sealed class EnginesConfig
{
    [JsonPropertyName("engines")]
    public Dictionary<string, EngineConfigEntry> Engines { get; set; } = [];
}

/// <summary>
/// A third-party adapter's engines.json declaration. Mirrors <see cref="EngineProfile"/> so a
/// custom adapter can declare the same properties a built-in engine does. Defaults are the
/// conservative values: no parallelism assumed beyond serial, cheap to construct, no reuse
/// claimed — reuse must be earned (see <see cref="EngineProfile"/>'s remarks).
/// </summary>
public sealed class EngineConfigEntry
{
    [JsonPropertyName("exec")]
    public string? Exec { get; set; }

    [JsonPropertyName("domains")]
    public List<string>? Domains { get; set; }

    [JsonPropertyName("maxParallel")]
    public int MaxParallel { get; set; }

    [JsonPropertyName("coldStartCost")]
    public ColdStartCost ColdStartCost { get; set; } = ColdStartCost.Cheap;

    [JsonPropertyName("reuseSafeRoster")]
    public bool ReuseSafeRoster { get; set; }

    [JsonPropertyName("reuseSafeGameData")]
    public bool ReuseSafeGameData { get; set; }

    [JsonPropertyName("memPerInstanceBytes")]
    public long MemPerInstanceBytes { get; set; }

    [JsonPropertyName("oversubscriptionFactor")]
    public double OversubscriptionFactor { get; set; } = 1.0;
}

[JsonSourceGenerationOptions(ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(EnginesConfig))]
public sealed partial class EnginesConfigJsonContext : JsonSerializerContext;
