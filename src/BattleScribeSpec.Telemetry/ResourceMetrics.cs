using System.Diagnostics.Metrics;

namespace BattleScribeSpec.Telemetry;

/// <summary>
/// Metrics for expensive, pooled resources (JVMs, browsers, browser contexts, adapter processes).
/// </summary>
/// <remarks>
/// <para>
/// <c>harness.resource.count</c> is the signal that makes the harness's unbounded parallelism
/// visible. Three in-process browser-context pools and a JVM can currently be alive at once
/// (xUnit's <c>maxParallelThreads</c> is unset, so collections run up to CPU-count wide) and
/// nothing in the system reports it. A span cannot express "how many are alive right now" —
/// only an up-down counter can.
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
    /// Record what an engine cost to obtain, in <b>seconds</b>. <paramref name="reused"/> distinguishes
    /// a warm reuse from a cold start — this is the warm-reuse question, asked continuously rather
    /// than by a one-off benchmark script.
    /// </summary>
    public static void RecordEngineStart(string kind, bool reused, double seconds) =>
        EngineStart.Record(seconds,
            new KeyValuePair<string, object?>("harness.resource.kind", kind),
            new KeyValuePair<string, object?>("harness.engine.reused", reused));
}
