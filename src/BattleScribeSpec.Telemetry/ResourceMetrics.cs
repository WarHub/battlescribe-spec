using System.Diagnostics.Metrics;

namespace BattleScribeSpec.Telemetry;

/// <summary>
/// Metrics for expensive, pooled resources (JVMs, browsers, browser contexts, adapter processes).
/// </summary>
/// <remarks>
/// <para>
/// <c>harness.resource.count</c> is the signal that makes the harness's parallelism visible.
/// Several in-process browser-context pools and a JVM can be alive at once — xUnit runs collections
/// in parallel, and its own thread count (<c>maxParallelThreads: "0.5x"</c>, declared in both
/// <c>xunit.runner.json</c> files and pinned by <c>ConcurrencyConfigurationDriftTests</c>) does
/// <b>not</b> bound them: a pool's real browser concurrency is <c>ConcurrencyPlan.PoolSize</c>
/// inside a single <c>[Fact]</c>, which xUnit cannot see at all. Nothing else in the system reports
/// the product (issue #314). A span cannot express "how many are alive right now" — only an up-down
/// counter can.
/// </para>
/// <para>
/// <b>Every peak read off this counter is a lower bound, not an exact maximum.</b> The value only
/// exists in the exported artifact at each periodic export tick (2 seconds in this repo's test/CLI
/// telemetry setup) — a concurrency spike that both rises and falls entirely between two export
/// ticks never appears in the artifact at all. Reading a peak of N off a time series therefore
/// only proves "at least N were alive at once, at some point" — the true peak may have been
/// higher and simply fell in a gap between samples. Anyone using a peak read from this counter
/// (e.g. to bound harness parallelism) should treat it as a floor, not a ceiling.
/// </para>
/// </remarks>
public static class ResourceMetrics
{
    private static readonly Meter Meter = new(HarnessTelemetry.MeterName);

    // OTel naming: UpDownCounter names SHOULD NOT be pluralized -> "resource.count", not
    // "resources.live". The "{resource}" unit annotation is correct as a singular.
    private static readonly UpDownCounter<int> Live =
        Meter.CreateUpDownCounter<int>("harness.resource.count", unit: "{resource}",
            description: "Expensive resources currently alive, by kind.");

    // OTel: "When instruments are measuring durations, seconds (i.e. `s`) SHOULD be used."
    // The SDK's default explicit buckets ([0,5,10,25,...,10000]) are millisecond-tuned, so a
    // seconds-valued histogram would land EVERY engine start in a single bucket and make p50/p95
    // meaningless. Supply boundaries fitted to what we actually observe: ~1.6s for a Chromium
    // relaunch, considerably more for a JVM + JavaFX cold start.
    // A Counter (monotonic, never decrements) is the correct OTel instrument here — unlike
    // Live/Released above, a death is a one-way event, not a live-count adjustment. Kept as a
    // SEPARATE instrument (rather than folding into Released) precisely so a death is a
    // distinguishable signal: a dashboard built on Released alone cannot tell "the process exited
    // cleanly" from "the process crashed and the harness had to recover" — those are very
    // different facts about the run's health.
    private static readonly Counter<long> Deaths =
        Meter.CreateCounter<long>("harness.resource.death.count", unit: "{resource}",
            description: "Resources of a kind that died unexpectedly (crashed) rather than being released normally.");

    private static readonly Histogram<double> EngineStart =
        Meter.CreateHistogram<double>(
            "harness.engine.start.duration",
            unit: "s",
            description: "Engine acquisition cost, split by whether it was a cold start or a warm reuse.",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [0.01, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60],
            });

    /// <summary>Record that a resource of <paramref name="kind"/> became alive (e.g. "jvm", "browser", "browser-context", "adapter-process").</summary>
    public static void Acquired(string kind) =>
        Live.Add(1, new KeyValuePair<string, object?>("harness.resource.kind", kind));

    /// <summary>Record that a resource of <paramref name="kind"/> was released.</summary>
    public static void Released(string kind) =>
        Live.Add(-1, new KeyValuePair<string, object?>("harness.resource.kind", kind));

    /// <summary>
    /// Record that a resource of <paramref name="kind"/> died (crashed) rather than being released
    /// normally — e.g. an adapter process (a real engine self-terminating under warm-reuse) found
    /// exited when the harness's spec-suite runner checks it after a spec.
    /// Emitted IN ADDITION TO (not instead of) the eventual <see cref="Released(string)"/> call
    /// that still fires when the dead process is disposed — this is the distinguishing signal that
    /// a release was NOT graceful.
    /// </summary>
    public static void Died(string kind) =>
        Deaths.Add(1, new KeyValuePair<string, object?>("harness.resource.kind", kind));

    /// <summary>
    /// Record what an engine cost to obtain, in <b>seconds</b>. <paramref name="reused"/> distinguishes
    /// a warm reuse from a cold start — this is the warm-reuse question, asked continuously rather
    /// than by a one-off benchmark script.
    /// </summary>
    /// <remarks>
    /// Tagged <c>harness.engine.kind</c> (values: <c>roster-engine</c>, <c>gamedata-engine</c>) —
    /// deliberately a DIFFERENT attribute key from <see cref="Acquired"/>/<see cref="Released"/>'s
    /// <c>harness.resource.kind</c> (values: <c>jvm</c>, <c>browser</c>, <c>browser-context</c>,
    /// <c>adapter-process</c>). The two metrics classify along disjoint vocabularies — a domain
    /// (which engine kind was started) vs. a process/handle kind (what is currently alive) — and
    /// sharing one attribute key would let a dashboard group-by on it silently mix the two.
    /// </remarks>
    public static void RecordEngineStart(string kind, bool reused, double seconds) =>
        EngineStart.Record(seconds,
            new KeyValuePair<string, object?>("harness.engine.kind", kind),
            new KeyValuePair<string, object?>("harness.engine.reused", reused));
}
