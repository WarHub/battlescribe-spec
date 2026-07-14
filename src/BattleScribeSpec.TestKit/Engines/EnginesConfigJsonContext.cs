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

    /// <summary>
    /// PROCESS axis: hard ceiling on concurrent adapter <b>processes</b>; 0 = unlimited. The same
    /// quantity the <c>describe</c> handshake advertises as <c>capabilities.maxParallel</c>
    /// (docs/adapter-guide.md). It does <b>not</b> bound the harness's in-process browser-context pool —
    /// that is <see cref="MaxContexts"/>, a different axis with a different ceiling.
    /// </summary>
    [JsonPropertyName("maxParallel")]
    public int MaxParallel { get; set; }

    /// <summary>
    /// CONTEXT axis: hard ceiling on browser <b>contexts</b> in one in-process pool; 0 = unlimited.
    /// Declare it only if your engine physically cannot hold more than N contexts at once (the built-in
    /// <c>battlescribe-ui</c> declares 1: it drives a single desktop app). <see cref="MaxParallel"/> used
    /// to clamp this axis too, so <c>{"maxParallel": 2, "contextPoolSize": 4}</c> — "don't run more than
    /// 2 of my processes", exactly what the protocol says that field means — silently halved the pool.
    /// </summary>
    [JsonPropertyName("maxContexts")]
    public int MaxContexts { get; set; }

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

    /// <summary>
    /// <b>LOAD axis — where this engine's traffic lands, which is not a performance property at all.</b>
    /// <c>"local"</c> = its service runs on this machine (a frozen replay, a local server, an in-process
    /// engine), so its concurrency is a throughput question and it gets the machine's full measured
    /// width. <c>"third-party-live"</c> = it drives someone else's production site, so its concurrency is
    /// a <em>load</em> question and it is held to <c>ConcurrencyPolicy.ThirdPartyLiveLoadLimit</c>.
    /// <c>"url-var:NAME"</c> = it is live iff the <c>NAME</c> environment variable holds a non-loopback
    /// URL (how the built-in NewRecruit engines work, via <c>NR_ENGINE_URL</c>).
    /// <para>
    /// <b>Omitted ⇒ undeclared ⇒ treated as third-party-live.</b> That is deliberate and it is the only
    /// default here that is <em>pessimistic</em>: getting this wrong towards "local" spends a stranger's
    /// production capacity, and no adapter we did not write gets the benefit of that doubt. Declaring
    /// <c>"local"</c> is a one-line opt-in to the full worker count.
    /// </para>
    /// </summary>
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }
}

[JsonSourceGenerationOptions(ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(EnginesConfig))]
public sealed partial class EnginesConfigJsonContext : JsonSerializerContext;
