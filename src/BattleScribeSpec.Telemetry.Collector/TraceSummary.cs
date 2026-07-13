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
/// The highest total live-resource count observed at any single export time window, summed
/// across every <c>harness.resource.kind</c> AND every OTel <c>Resource</c> (i.e. every
/// <c>--workers</c> child process, each of which exports under its own <c>service.instance.id</c>)
/// alive in that window. This is a LOWER BOUND, not an exact maximum: a spike that both rises and
/// falls between two periodic metric exports never appears in the artifact at all (see the
/// remarks on <c>ResourceMetrics</c>). Meaningful only when <see cref="PeakLiveResourcesSampled"/>
/// is true — see that member.
/// </param>
/// <param name="PeakLiveResourcesByKind">
/// The peak live count for each <c>harness.resource.kind</c> individually, summed across every
/// OTel <c>Resource</c> alive in the window (each kind's own maximum over time — not necessarily
/// all reached at the same instant as each other, or as <see cref="PeakLiveResources"/>). Also a
/// lower bound, for the same reason.
/// </param>
/// <param name="PeakLiveResourcesSampled">
/// False when no <c>harness.resource.count</c> data point was ever exported — e.g. a batch that
/// finished faster than the export interval. In that case <see cref="PeakLiveResources"/> and
/// <see cref="PeakLiveResourcesByKind"/> are meaningless zeros: "not sampled" is not the same
/// claim as "genuinely zero resources were ever live," and <see cref="WriteTable"/> renders the
/// two differently for exactly that reason.
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
    bool PeakLiveResourcesSampled,
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
        PeakLiveResourcesSampled: false,
        SlowestSpecs: []);

    /// <summary>
    /// Build a summary from a run artifact written by <see cref="OtlpArtifactWriter"/>. Fail-open:
    /// returns <see cref="Empty"/> when the artifact is missing, unreadable, or carries neither spec
    /// spans nor cold-start/live-resource metrics — telemetry is a bonus, printing a summary must
    /// never be a reason to fail a run.
    /// </summary>
    /// <remarks>
    /// A dotnet-test artifact (<c>TelemetryAssemblyFixture</c> in <c>tests/Infrastructure</c>)
    /// legitimately has <see cref="SpecCount"/> == 0: its per-spec
    /// xUnit <c>[Fact]</c>/<c>[Theory]</c> tests call <c>GameDataRunner</c>/<c>RosterRunner</c>
    /// directly rather than through <c>SpecSuiteRunner</c> (the only emitter of the <c>test.case.name</c>
    /// spans this reads), so no spec spans ever land in that artifact. Its engine pools still emit
    /// <c>harness.resource.count</c>/<c>harness.engine.start.duration</c> directly, so gating the
    /// WHOLE summary on <see cref="SpecCount"/> alone would discard real, otherwise-unobtainable
    /// concurrency data purely because no spec spans happen to exist. Only an artifact with
    /// literally nothing in any of the three signals collapses to <see cref="Empty"/>.
    /// </remarks>
    /// <param name="basePath">The base artifact path passed to <see cref="OtlpArtifactWriter"/>.</param>
    public static TraceSummary FromArtifact(string basePath)
    {
        try
        {
            var scan = ScanTraces(basePath);
            var (coldStarts, reuses) = CollectEngineStarts(basePath);
            var (peakTotal, peakByKind, peakSampled) = CollectPeakLiveResources(basePath);

            if (scan.Specs.Count == 0 && coldStarts == 0 && reuses == 0 && !peakSampled)
            {
                return Empty;
            }

            var durations = scan.Specs.Select(s => s.DurationMs).OrderBy(d => d).ToList();

            var totalWall = scan.RunStartNano is { } runStart && scan.RunEndNano is { } runEnd
                ? NanosToTimeSpan(runEnd - runStart)
                : NanosToTimeSpan((scan.MaxEndNano ?? 0) - (scan.MinStartNano ?? 0));

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
                PeakLiveResourcesSampled: peakSampled,
                SlowestSpecs: slowest);
        }
        catch (Exception ex)
        {
            // Fail-open, for real this time: a missing artifact already returns Empty from the
            // ScanTraces/CollectEngineStarts/CollectPeakLiveResources helpers (they treat "no
            // files" as "no data"), but anything else that can go wrong while reading the three
            // artifact files — a locked file (IOException from File.OpenRead; a Windows AV/indexer
            // lock is realistic), a truncated/corrupt write (InvalidProtocolBufferException), or a
            // cumulative counter overflowing the `checked((int)...)` casts below — must not turn
            // into an exception that reaches a caller. Printing a trace summary is a bonus; it must
            // never be a reason a `dotnet test` run (see TelemetryAssemblyFixture.DisposeAsync,
            // which calls this from an assembly fixture's teardown with no surrounding try/catch of
            // its own) or a CLI run fails.
            Console.Error.WriteLine($"[telemetry] could not summarize artifact '{basePath}': {ex.Message}");
            return Empty;
        }
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

        if (PeakLiveResourcesSampled)
        {
            writer.WriteLine(FormattableString.Invariant(
                $"  peak live resources (>= lower bound): {PeakLiveResources} total"));

            foreach (var (kind, peak) in PeakLiveResourcesByKind.OrderByDescending(kv => kv.Value))
            {
                writer.WriteLine(FormattableString.Invariant($"    - {kind}: {peak}"));
            }
        }
        else
        {
            writer.WriteLine("  peak live resources: not sampled (run shorter than the export interval)");
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

    /// <summary>
    /// Best-effort: when running under GitHub Actions (<c>GITHUB_STEP_SUMMARY</c> names a file every
    /// step in the job appends markdown to), render this summary as a fenced code block under
    /// <paramref name="heading"/> and append it there — so wall time, cold-starts vs reuses, and
    /// peak live resources show up on the job's summary page without downloading the artifact. A
    /// no-op when the env var is unset (i.e. everywhere except GitHub Actions). Never throws:
    /// telemetry is a bonus and must never fail a build, mirroring <see cref="OtlpArtifactWriter"/>
    /// and <see cref="HarnessCollector"/>'s fail-open handling elsewhere in this project.
    /// </summary>
    /// <param name="heading">A short label for this summary's section (e.g. the engine/job name).</param>
    public void AppendToGitHubStepSummary(string heading)
    {
        ArgumentNullException.ThrowIfNull(heading);

        var path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            using var body = new StringWriter();
            WriteTable(body);
            var markdown = string.Concat(
                "### ", heading, Environment.NewLine, Environment.NewLine,
                "```text", Environment.NewLine, body.ToString(), "```", Environment.NewLine, Environment.NewLine);
            File.AppendAllText(path, markdown);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[telemetry] could not append to GITHUB_STEP_SUMMARY: {ex.Message}");
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
    /// (resource, full attribute set) — so distinct resource kinds are never conflated, AND
    /// distinct <c>--workers</c> child processes are never conflated with each other — and take
    /// only the LATEST (highest <c>time_unix_nano</c>) data point per group; its <c>count</c>
    /// already is the cumulative total for that series.
    /// </summary>
    /// <remarks>
    /// Under <c>--workers N</c>, every child exports the SAME attribute set for the same kind of
    /// cold start (e.g. <c>harness.resource.kind=adapter-process|harness.engine.reused=false</c>)
    /// — only the enclosing OTel <c>Resource</c> (via <c>service.instance.id</c>, set per-worker in
    /// <c>RunBatch</c>) tells two workers' identical-looking series apart. A key built from point
    /// attributes alone collapses N workers' cold starts into one series, and "latest wins" then
    /// keeps only one worker's count. The fix keys "latest wins" by resource+attributes (so the
    /// cumulative-temporality dedup above still holds per series) and then SUMS the latest value
    /// across every resource that shares the same point attributes, so N workers' independent
    /// series each contribute their own count.
    /// </remarks>
    private static (int ColdStarts, int Reuses) CollectEngineStarts(string basePath)
    {
        var latestBySeries = new Dictionary<(string ResourceKey, string AttributeKey), (ulong TimeNano, ulong Count, bool Reused)>();

        foreach (var request in OtlpArtifactReader.ReadMetrics(basePath))
        {
            foreach (var resourceMetrics in request.ResourceMetrics)
            {
                // Resource identity = its full attribute set (service.instance.id among them, for
                // a --workers run). This is more general than keying on service.instance.id alone:
                // it also correctly treats a single, non-batch run (no service.instance.id at all)
                // as one shared resource, without a special case. Resource is an unset (null)
                // singular protobuf message field when the exporter never attached one — treat
                // that as the empty attribute set rather than throwing.
                var resourceKey = AttributeSetKey(resourceMetrics.Resource?.Attributes ?? []);

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

                            var seriesKey = (resourceKey, AttributeSetKey(point.Attributes));
                            if (!latestBySeries.TryGetValue(seriesKey, out var existing) ||
                                point.TimeUnixNano > existing.TimeNano)
                            {
                                latestBySeries[seriesKey] = (point.TimeUnixNano, point.Count, reused);
                            }
                        }
                    }
                }
            }
        }

        var coldStarts = latestBySeries.Values.Where(v => !v.Reused).Sum(v => (long)v.Count);
        var reuses = latestBySeries.Values.Where(v => v.Reused).Sum(v => (long)v.Count);
        return (checked((int)coldStarts), checked((int)reuses));
    }

    /// <summary>
    /// The metrics SDK's default periodic export interval for <c>--workers</c> children (see
    /// <c>OTEL_METRIC_EXPORT_INTERVAL</c> in <see cref="HarnessCollector.ChildEnvironment"/>). Used
    /// as the bucket width in <see cref="CollectPeakLiveResources"/>: independent worker processes
    /// have independent clocks and export schedules, so this is the natural granularity at which
    /// two workers' concurrent exports should be considered "the same instant".
    /// </summary>
    private const ulong PeakBucketWidthNanos = 1_000_000_000UL;

    /// <summary>
    /// Peak live-resource counts from the <c>harness.resource.count</c> up-down counter. Unlike a
    /// monotonic counter, a cumulative data point on an up-down counter already IS the live value
    /// at that instant, so — unlike <see cref="CollectEngineStarts"/> above — the maximum across
    /// data points is the correct read, not a "latest wins" one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bucketing by exact <c>TimeUnixNano</c> equality (as this used to do) only ever merges data
    /// points from the SAME resource: under <c>--workers N</c>, each child has its own clock and
    /// export schedule, so two different workers' data points essentially never share a
    /// bit-identical nanosecond timestamp — even when the two workers' resources are genuinely
    /// alive at the same time. Every point then lands in its own singleton bucket, and the
    /// reported "peak" degenerates into "the largest single worker's own export tick," silently
    /// dropping true cross-worker concurrency.
    /// </para>
    /// <para>
    /// The fix buckets by a time WINDOW (<see cref="PeakBucketWidthNanos"/>, one export interval)
    /// instead of exact equality, so genuinely-concurrent points from different workers land in
    /// the same bucket and get summed. Within a bucket, points are first grouped by (resource,
    /// kind) — the same resource can legitimately report itself only once per bucket, so a second
    /// point for the same (resource, kind) in the same bucket is treated as a later reading of the
    /// same series (latest wins), not an additional live resource. The total for a bucket is the
    /// sum of every (resource, kind) series' latest value in that bucket; the per-kind total for a
    /// bucket sums across every resource reporting that kind. The reported peak is then the
    /// maximum bucket total across the whole run — for the total, and independently for each kind
    /// (kinds are not required to have peaked in the same bucket as each other or as the total).
    /// </para>
    /// <para>
    /// This is still a fixed grid: two points a few nanoseconds apart but straddling a bucket
    /// boundary can be split into adjacent buckets and so never summed. That is an accepted,
    /// documented residual of the existing "peak is a LOWER BOUND" caveat, not a new one — a
    /// bucket on the order of the export interval keeps this rare in practice.
    /// </para>
    /// </remarks>
    private static (long Total, IReadOnlyDictionary<string, long> ByKind, bool Sampled) CollectPeakLiveResources(string basePath)
    {
        // bucket index -> (resource, kind) -> that series' latest value seen in this bucket.
        var byBucket = new Dictionary<ulong, Dictionary<(string ResourceKey, string Kind), long>>();

        foreach (var request in OtlpArtifactReader.ReadMetrics(basePath))
        {
            foreach (var resourceMetrics in request.ResourceMetrics)
            {
                var resourceKey = AttributeSetKey(resourceMetrics.Resource?.Attributes ?? []);

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

                            var bucket = point.TimeUnixNano / PeakBucketWidthNanos;
                            if (!byBucket.TryGetValue(bucket, out var atBucket))
                            {
                                atBucket = [];
                                byBucket[bucket] = atBucket;
                            }

                            atBucket[(resourceKey, kind)] = value;
                        }
                    }
                }
            }
        }

        if (byBucket.Count == 0)
        {
            return (0, new Dictionary<string, long>(StringComparer.Ordinal), false);
        }

        long peakTotal = 0;
        var peakByKind = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var atBucket in byBucket.Values)
        {
            var kindTotalsThisBucket = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var ((_, kind), value) in atBucket)
            {
                kindTotalsThisBucket[kind] = kindTotalsThisBucket.GetValueOrDefault(kind) + value;
            }

            var bucketTotal = kindTotalsThisBucket.Values.Sum();
            if (bucketTotal > peakTotal)
            {
                peakTotal = bucketTotal;
            }

            foreach (var (kind, kindTotal) in kindTotalsThisBucket)
            {
                if (!peakByKind.TryGetValue(kind, out var existingPeak) || kindTotal > existingPeak)
                {
                    peakByKind[kind] = kindTotal;
                }
            }
        }

        return (peakTotal, peakByKind, true);
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
