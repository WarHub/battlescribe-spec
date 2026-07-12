using System.Globalization;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;

namespace BattleScribeSpec.Telemetry.Collector;

/// <summary>
/// A compact, human-readable digest of one run's OTLP artifact: spec-duration percentiles, engine
/// cold-start vs warm-reuse counts, and peak live-resource counts. An artifact nobody reads is not
/// observability — this is what turns the bytes <see cref="OtlpArtifactReader"/> parses into an
/// answer a human sees without standing up a backend.
/// </summary>
/// <param name="SpecCount">Number of spec spans found (one per spec execution).</param>
/// <param name="TotalWall">
/// Wall-clock span of the run: the duration of the top-level <c>run</c> span when present,
/// otherwise the range from the earliest span start to the latest span end in the artifact.
/// </param>
/// <param name="P50SpecMs">Median spec duration, in milliseconds.</param>
/// <param name="P95SpecMs">95th-percentile spec duration, in milliseconds.</param>
/// <param name="ColdStarts">
/// Number of engine acquisitions recorded as a cold start (<c>harness.engine.reused</c> = false)
/// on the <c>harness.engine.start.duration</c> histogram.
/// </param>
/// <param name="Reuses">
/// Number of engine acquisitions recorded as a warm reuse (<c>harness.engine.reused</c> = true).
/// </param>
/// <param name="PeakLiveResources">
/// The highest total live-resource count observed at any single export timestamp, summed across
/// every <c>harness.resource.kind</c> alive at that instant. This is a LOWER BOUND, not an exact
/// maximum: a spike that both rises and falls between two periodic metric exports never appears
/// in the artifact at all (see the remarks on <c>ResourceMetrics</c>).
/// </param>
/// <param name="PeakLiveResourcesByKind">
/// The peak live count for each <c>harness.resource.kind</c> individually (each kind's own
/// maximum over time — not necessarily all reached at the same instant as each other, or as
/// <see cref="PeakLiveResources"/>). Also a lower bound, for the same reason.
/// </param>
/// <param name="SlowestSpecs">The 10 slowest specs by duration, descending.</param>
public sealed record TraceSummary(
    int SpecCount,
    TimeSpan TotalWall,
    double P50SpecMs,
    double P95SpecMs,
    int ColdStarts,
    int Reuses,
    long PeakLiveResources,
    IReadOnlyDictionary<string, long> PeakLiveResourcesByKind,
    IReadOnlyList<TraceSummary.SlowSpec> SlowestSpecs)
{
    /// <summary>One spec's identity and duration, as ranked in <see cref="SlowestSpecs"/>.</summary>
    /// <param name="Id">The spec id (<c>test.case.name</c>).</param>
    /// <param name="Category">The spec's category (<c>test.suite.name</c>).</param>
    /// <param name="DurationMs">The span's wall duration, in milliseconds.</param>
    public sealed record SlowSpec(string Id, string Category, double DurationMs);

    /// <summary>
    /// The summary for an artifact with no spec spans — a missing artifact (telemetry disabled),
    /// an externally-exported run (no local artifact was written), or a genuinely empty one.
    /// </summary>
    public static TraceSummary Empty { get; } = new(
        SpecCount: 0,
        TotalWall: TimeSpan.Zero,
        P50SpecMs: 0,
        P95SpecMs: 0,
        ColdStarts: 0,
        Reuses: 0,
        PeakLiveResources: 0,
        PeakLiveResourcesByKind: new Dictionary<string, long>(),
        SlowestSpecs: []);

    /// <summary>
    /// Build a summary from a run artifact written by <see cref="OtlpArtifactWriter"/>. Fail-open:
    /// returns <see cref="Empty"/> when the artifact is missing, unreadable, or carries no spec
    /// spans — telemetry is a bonus, printing a summary must never be a reason to fail a run.
    /// </summary>
    /// <param name="basePath">The base artifact path passed to <see cref="OtlpArtifactWriter"/>.</param>
    public static TraceSummary FromArtifact(string basePath)
    {
        var scan = ScanTraces(basePath);
        if (scan.Specs.Count == 0)
        {
            return Empty;
        }

        var durations = scan.Specs.Select(s => s.DurationMs).OrderBy(d => d).ToList();

        var totalWall = scan.RunStartNano is { } runStart && scan.RunEndNano is { } runEnd
            ? NanosToTimeSpan(runEnd - runStart)
            : NanosToTimeSpan((scan.MaxEndNano ?? 0) - (scan.MinStartNano ?? 0));

        var (coldStarts, reuses) = CollectEngineStarts(basePath);
        var (peakTotal, peakByKind) = CollectPeakLiveResources(basePath);

        var slowest = scan.Specs
            .OrderByDescending(s => s.DurationMs)
            .Take(10)
            .Select(s => new SlowSpec(s.Id, s.Category, s.DurationMs))
            .ToList();

        return new TraceSummary(
            SpecCount: scan.Specs.Count,
            TotalWall: totalWall,
            P50SpecMs: Percentile(durations, 0.50),
            P95SpecMs: Percentile(durations, 0.95),
            ColdStarts: coldStarts,
            Reuses: reuses,
            PeakLiveResources: peakTotal,
            PeakLiveResourcesByKind: peakByKind,
            SlowestSpecs: slowest);
    }

    /// <summary>Render this summary as a compact plain-text table.</summary>
    /// <param name="writer">Destination for the table — the CLI writes this to stderr.</param>
    public void WriteTable(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine("Trace summary:");
        writer.WriteLine(FormattableString.Invariant(
            $"  specs:               {SpecCount} (wall {TotalWall.TotalSeconds:F1}s)"));
        writer.WriteLine(FormattableString.Invariant(
            $"  spec duration:       p50={P50SpecMs:F1}ms  p95={P95SpecMs:F1}ms"));
        writer.WriteLine(FormattableString.Invariant(
            $"  engine starts:       {ColdStarts} cold, {Reuses} reused"));
        writer.WriteLine(FormattableString.Invariant(
            $"  peak live resources (>= lower bound): {PeakLiveResources} total"));

        foreach (var (kind, peak) in PeakLiveResourcesByKind.OrderByDescending(kv => kv.Value))
        {
            writer.WriteLine(FormattableString.Invariant($"    - {kind}: {peak}"));
        }

        if (SlowestSpecs.Count > 0)
        {
            writer.WriteLine("  slowest specs:");
            foreach (var spec in SlowestSpecs)
            {
                writer.WriteLine(FormattableString.Invariant(
                    $"    {spec.DurationMs,8:F1}ms  {spec.Category}/{spec.Id}"));
            }
        }
    }

    private static TimeSpan NanosToTimeSpan(ulong nanos) => TimeSpan.FromTicks((long)(nanos / 100));

    /// <summary>Linear-interpolation percentile (the "R-7"/Excel-default method) over a sorted list.</summary>
    private static double Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0)
        {
            return 0;
        }

        if (sortedAscending.Count == 1)
        {
            return sortedAscending[0];
        }

        var rank = p * (sortedAscending.Count - 1);
        var lowerIndex = (int)Math.Floor(rank);
        var upperIndex = (int)Math.Ceiling(rank);
        var weight = rank - lowerIndex;
        return sortedAscending[lowerIndex] + (weight * (sortedAscending[upperIndex] - sortedAscending[lowerIndex]));
    }

    private readonly record struct SpecSpanData(string Id, string Category, double DurationMs);

    private sealed record TraceScan(
        List<SpecSpanData> Specs,
        ulong? RunStartNano,
        ulong? RunEndNano,
        ulong? MinStartNano,
        ulong? MaxEndNano);

    /// <summary>
    /// One pass over every span in the artifact: collect spec spans (identified by the
    /// <c>test.case.name</c> attribute <see cref="HarnessTelemetry.StartSpec"/> sets) and the
    /// overall time range, preferring the top-level <c>run</c> span
    /// (<see cref="HarnessTelemetry.StartOp"/>) when one exists.
    /// </summary>
    private static TraceScan ScanTraces(string basePath)
    {
        var specs = new List<SpecSpanData>();
        ulong? runStart = null;
        ulong? runEnd = null;
        ulong? minStart = null;
        ulong? maxEnd = null;

        foreach (var request in OtlpArtifactReader.ReadTraces(basePath))
        {
            foreach (var resourceSpans in request.ResourceSpans)
            {
                foreach (var scopeSpans in resourceSpans.ScopeSpans)
                {
                    foreach (var span in scopeSpans.Spans)
                    {
                        minStart = minStart is { } ms ? Math.Min(ms, span.StartTimeUnixNano) : span.StartTimeUnixNano;
                        maxEnd = maxEnd is { } me ? Math.Max(me, span.EndTimeUnixNano) : span.EndTimeUnixNano;

                        if (span.Name == "run")
                        {
                            runStart = runStart is { } rs ? Math.Min(rs, span.StartTimeUnixNano) : span.StartTimeUnixNano;
                            runEnd = runEnd is { } re ? Math.Max(re, span.EndTimeUnixNano) : span.EndTimeUnixNano;
                        }

                        var specId = FindStringAttribute(span.Attributes, "test.case.name");
                        if (specId is null)
                        {
                            continue;
                        }

                        var category = FindStringAttribute(span.Attributes, "test.suite.name") ?? "";
                        var durationMs = (span.EndTimeUnixNano - span.StartTimeUnixNano) / 1_000_000.0;
                        specs.Add(new SpecSpanData(specId, category, durationMs));
                    }
                }
            }
        }

        return new TraceScan(specs, runStart, runEnd, minStart, maxEnd);
    }

    /// <summary>
    /// Cold-start/reuse counts from the <c>harness.engine.start.duration</c> histogram. The SDK
    /// exports this metric with CUMULATIVE temporality (the default), so summing every exported
    /// data point's <c>count</c> across the whole run would massively over-count — each export
    /// re-reports the running total since the process started. Instead, group data points by
    /// their full attribute set (so distinct resource kinds are never conflated) and take only the
    /// LATEST (highest <c>time_unix_nano</c>) data point per group — its <c>count</c> already is
    /// the cumulative total for that series.
    /// </summary>
    private static (int ColdStarts, int Reuses) CollectEngineStarts(string basePath)
    {
        var latestByAttributeSet = new Dictionary<string, (ulong TimeNano, ulong Count, bool Reused)>(StringComparer.Ordinal);

        foreach (var request in OtlpArtifactReader.ReadMetrics(basePath))
        {
            foreach (var resourceMetrics in request.ResourceMetrics)
            {
                foreach (var scopeMetrics in resourceMetrics.ScopeMetrics)
                {
                    foreach (var metric in scopeMetrics.Metrics)
                    {
                        if (metric.Name != "harness.engine.start.duration" ||
                            metric.DataCase != Metric.DataOneofCase.Histogram)
                        {
                            continue;
                        }

                        foreach (var point in metric.Histogram.DataPoints)
                        {
                            if (!TryFindBoolAttribute(point.Attributes, "harness.engine.reused", out var reused))
                            {
                                continue;
                            }

                            var key = AttributeSetKey(point.Attributes);
                            if (!latestByAttributeSet.TryGetValue(key, out var existing) ||
                                point.TimeUnixNano > existing.TimeNano)
                            {
                                latestByAttributeSet[key] = (point.TimeUnixNano, point.Count, reused);
                            }
                        }
                    }
                }
            }
        }

        var coldStarts = latestByAttributeSet.Values.Where(v => !v.Reused).Sum(v => (long)v.Count);
        var reuses = latestByAttributeSet.Values.Where(v => v.Reused).Sum(v => (long)v.Count);
        return (checked((int)coldStarts), checked((int)reuses));
    }

    /// <summary>
    /// Peak live-resource counts from the <c>harness.resource.count</c> up-down counter. Unlike a
    /// monotonic counter, a cumulative data point on an up-down counter already IS the live value
    /// at that instant, so — unlike <see cref="CollectEngineStarts"/> above — the maximum across
    /// data points is the correct read, not a "latest wins" one. The per-kind peak is each kind's
    /// own maximum over time; the total peak sums every kind alive at the SAME export timestamp,
    /// then takes the maximum of those per-timestamp sums (kinds peaking at different instants
    /// must not be added together as if simultaneous).
    /// </summary>
    private static (long Total, IReadOnlyDictionary<string, long> ByKind) CollectPeakLiveResources(string basePath)
    {
        var byTimestamp = new Dictionary<ulong, Dictionary<string, long>>();
        var peakByKind = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var request in OtlpArtifactReader.ReadMetrics(basePath))
        {
            foreach (var resourceMetrics in request.ResourceMetrics)
            {
                foreach (var scopeMetrics in resourceMetrics.ScopeMetrics)
                {
                    foreach (var metric in scopeMetrics.Metrics)
                    {
                        if (metric.Name != "harness.resource.count" || metric.DataCase != Metric.DataOneofCase.Sum)
                        {
                            continue;
                        }

                        foreach (var point in metric.Sum.DataPoints)
                        {
                            var kind = FindStringAttribute(point.Attributes, "harness.resource.kind") ?? "unknown";
                            var value = point.ValueCase == NumberDataPoint.ValueOneofCase.AsInt
                                ? point.AsInt
                                : (long)point.AsDouble;

                            if (!byTimestamp.TryGetValue(point.TimeUnixNano, out var atTimestamp))
                            {
                                atTimestamp = new Dictionary<string, long>(StringComparer.Ordinal);
                                byTimestamp[point.TimeUnixNano] = atTimestamp;
                            }

                            atTimestamp[kind] = value;

                            if (!peakByKind.TryGetValue(kind, out var existingPeak) || value > existingPeak)
                            {
                                peakByKind[kind] = value;
                            }
                        }
                    }
                }
            }
        }

        var peakTotal = byTimestamp.Count == 0 ? 0 : byTimestamp.Values.Max(atTimestamp => atTimestamp.Values.Sum());
        return (peakTotal, peakByKind);
    }

    private static string? FindStringAttribute(IEnumerable<KeyValue> attributes, string key)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.Key == key && attribute.Value.ValueCase == AnyValue.ValueOneofCase.StringValue)
            {
                return attribute.Value.StringValue;
            }
        }

        return null;
    }

    private static bool TryFindBoolAttribute(IEnumerable<KeyValue> attributes, string key, out bool value)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.Key == key && attribute.Value.ValueCase == AnyValue.ValueOneofCase.BoolValue)
            {
                value = attribute.Value.BoolValue;
                return true;
            }
        }

        value = false;
        return false;
    }

    /// <summary>Canonical key identifying a data point's full attribute set, so two distinct timeseries never collide.</summary>
    private static string AttributeSetKey(IEnumerable<KeyValue> attributes) =>
        string.Join('|', attributes
            .OrderBy(a => a.Key, StringComparer.Ordinal)
            .Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.Key}={AttributeValueText(a.Value)}")));

    private static string AttributeValueText(AnyValue value) => value.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => value.StringValue,
        AnyValue.ValueOneofCase.BoolValue => value.BoolValue ? "true" : "false",
        AnyValue.ValueOneofCase.IntValue => value.IntValue.ToString(CultureInfo.InvariantCulture),
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue.ToString(CultureInfo.InvariantCulture),
        _ => "",
    };
}
