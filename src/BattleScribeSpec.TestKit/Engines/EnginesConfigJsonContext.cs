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

    /// <summary>PROCESS axis: bytes per adapter process family. 0 = undeclared (conservatively capped).</summary>
    [JsonPropertyName("memPerInstanceBytes")]
    public long MemPerInstanceBytes { get; set; }

    /// <summary>PROCESS axis: the `k` in <c>workers ≈ cpuCount × k</c>.</summary>
    [JsonPropertyName("oversubscriptionFactor")]
    public double OversubscriptionFactor { get; set; } = 1.0;

    /// <summary>
    /// CONTEXT axis: the measured optimal browser-context pool size, as an <b>absolute count</b> —
    /// not a factor of cpuCount, which the measurements show does not move this optimum. 0 =
    /// undeclared → <c>ConcurrencyPolicy.UndeclaredContextPoolSize</c>. See
    /// <see cref="EngineProfile.ContextPoolSize"/>; this is a different quantity from
    /// <see cref="OversubscriptionFactor"/> and must not be derived from it.
    /// </summary>
    [JsonPropertyName("contextPoolSize")]
    public int ContextPoolSize { get; set; }

    /// <summary>
    /// CONTEXT axis: bytes per additional browser context (~6× smaller than
    /// <see cref="MemPerInstanceBytes"/> — a context is not a process family). 0 = undeclared → the
    /// pool gets no memory bound, which is safe only because the undeclared pool size is small.
    /// </summary>
    [JsonPropertyName("memPerContextBytes")]
    public long MemPerContextBytes { get; set; }
}

[JsonSourceGenerationOptions(ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(EnginesConfig))]
public sealed partial class EnginesConfigJsonContext : JsonSerializerContext;
