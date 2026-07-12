using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using BattleScribeSpec.Telemetry.Collector;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// <see cref="TraceSummary"/> turns a run's OTLP artifact into a compact, human-readable digest —
/// this is what proves the artifact is actually read, not just written. Every artifact here is
/// built by hand with the generated protobuf types and written via <see cref="OtlpArtifactWriter"/>,
/// exactly as a real run would produce it, so these tests are hermetic and never spawn a browser
/// or a JVM.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TraceSummaryTests
{
    // Arbitrary epoch far from zero, so a bug that treats "unset" (0) as a real timestamp would
    // stand out rather than accidentally landing in a plausible range.
    private const ulong BaseNanos = 1_700_000_000_000_000_000UL;

    [Fact]
    public async Task FromArtifact_ComputesPercentilesColdStartsReusesAndPeakResources()
    {
        var artifact = Path.Combine(Path.GetTempPath(), $"bsspec-tracesummary-{Guid.NewGuid():N}");
        try
        {
            await using (var writer = new OtlpArtifactWriter(artifact))
            {
                // Five spec spans, 1 second apart, with durations 10/20/30/100/200 ms. Sorted
                // ascending these give an exact median (rank lands on an index, no interpolation)
                // and a P95 that DOES require interpolation — so both code paths in the percentile
                // function are exercised.
                await writer.WriteAsync(MakeTraceRequest(
                    ("fast", "cat-a", BaseNanos, 10),
                    ("medium", "cat-a", BaseNanos + 1_000_000_000, 20),
                    ("mid2", "cat-b", BaseNanos + 2_000_000_000, 30),
                    ("slow", "cat-b", BaseNanos + 3_000_000_000, 100),
                    ("slowest", "cat-c", BaseNanos + 4_000_000_000, 200)));

                // harness.engine.start.duration is a CUMULATIVE histogram: two export ticks, same
                // attribute set means the SECOND number is the running total, not an addition.
                // If FromArtifact naively summed every exported data point instead of taking the
                // latest per series, cold starts would come out as (1+1)+(2+2)=6 instead of 3, and
                // reuses as 1+4=5 instead of 4 — this is the exact trap Task 11's brief warns about.
                await writer.WriteAsync(MakeEngineStartMetricsRequest(tick: 1, browserCold: 1, jvmCold: 2, browserReused: 1));
                await writer.WriteAsync(MakeEngineStartMetricsRequest(tick: 2, browserCold: 1, jvmCold: 2, browserReused: 4));

                // harness.resource.count is a cumulative up-down counter: three export ticks, two
                // kinds. Browser peaks at t2 (3) and jvm peaks at t3 (4), but they are NEVER alive
                // simultaneously at that combined level — the true "most alive at once" is at t3
                // (browser=1 + jvm=4 = 5), not the sum of each kind's own independent peak (7).
                await writer.WriteAsync(MakeResourceCountMetricsRequest(timestamp: 1, browser: 2, jvm: 1));
                await writer.WriteAsync(MakeResourceCountMetricsRequest(timestamp: 2, browser: 3, jvm: 1));
                await writer.WriteAsync(MakeResourceCountMetricsRequest(timestamp: 3, browser: 1, jvm: 4));
            }

            var summary = TraceSummary.FromArtifact(artifact);

            Assert.Equal(5, summary.SpecCount);

            // durations sorted: [10, 20, 30, 100, 200]. rank(0.50) = 2.0 -> exact index 2 -> 30.
            Assert.Equal(30.0, summary.P50SpecMs);
            // rank(0.95) = 3.8 -> interpolate between index 3 (100) and 4 (200): 100 + 0.8*100 = 180.
            Assert.Equal(180.0, summary.P95SpecMs, precision: 6);

            // Fallback wall-clock range (no "run" span in this artifact): earliest start (fast, at
            // BaseNanos) to latest end (slowest, at BaseNanos+4s+200ms) = 4.2 seconds.
            Assert.Equal(TimeSpan.FromSeconds(4.2), summary.TotalWall);

            Assert.Equal(3, summary.ColdStarts); // latest-wins: browser 1 + jvm 2, not (1+1)+(2+2)
            Assert.Equal(4, summary.Reuses); // latest-wins: 4, not 1+4

            Assert.Equal(5, summary.PeakLiveResources); // max per-timestamp sum: at t3, browser=1 + jvm=4 = 5
            Assert.Equal(3, summary.PeakLiveResourcesByKind["browser"]);
            Assert.Equal(4, summary.PeakLiveResourcesByKind["jvm"]);

            // Slowest-first ordering, all five present (fewer than the top-10 cap).
            Assert.Equal(5, summary.SlowestSpecs.Count);
            Assert.Equal(["slowest", "slow", "mid2", "medium", "fast"], [.. summary.SlowestSpecs.Select(s => s.Id)]);
            Assert.Equal(200.0, summary.SlowestSpecs[0].DurationMs);
            Assert.Equal("cat-c", summary.SlowestSpecs[0].Category);
        }
        finally
        {
            File.Delete(artifact + ".traces.pb");
            File.Delete(artifact + ".metrics.pb");
            File.Delete(artifact + ".logs.pb");
        }
    }

    [Fact]
    public async Task FromArtifact_PrefersTheRunSpanForTotalWall_OverTheSpecOnlyRange()
    {
        var artifact = Path.Combine(Path.GetTempPath(), $"bsspec-tracesummary-run-{Guid.NewGuid():N}");
        try
        {
            await using (var writer = new OtlpArtifactWriter(artifact))
            {
                // The "run" span wraps a wider interval than any individual spec (it also covers
                // setup/teardown overhead outside any spec span) — TotalWall must reflect that
                // wider interval, not the narrower spec-only range.
                var runSpan = new Span
                {
                    Name = "run",
                    TraceId = ByteString.CopyFrom(new byte[16]),
                    SpanId = ByteString.CopyFrom(new byte[8]),
                    StartTimeUnixNano = BaseNanos,
                    EndTimeUnixNano = BaseNanos + 10_000_000_000, // 10s, well beyond the one spec below
                };
                var specSpan = MakeSpecSpan("only-spec", "cat", BaseNanos + 1_000_000_000, durationMs: 50);

                var scopeSpans = new ScopeSpans();
                scopeSpans.Spans.Add(runSpan);
                scopeSpans.Spans.Add(specSpan);
                var resourceSpans = new ResourceSpans();
                resourceSpans.ScopeSpans.Add(scopeSpans);
                var request = new ExportTraceServiceRequest();
                request.ResourceSpans.Add(resourceSpans);

                await writer.WriteAsync(request);
            }

            var summary = TraceSummary.FromArtifact(artifact);

            Assert.Equal(1, summary.SpecCount);
            Assert.Equal(TimeSpan.FromSeconds(10), summary.TotalWall);
        }
        finally
        {
            File.Delete(artifact + ".traces.pb");
            File.Delete(artifact + ".metrics.pb");
            File.Delete(artifact + ".logs.pb");
        }
    }

    [Fact]
    public void FromArtifact_MissingArtifact_ReturnsEmptySummary()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"bsspec-tracesummary-missing-{Guid.NewGuid():N}");

        var summary = TraceSummary.FromArtifact(missingPath);

        Assert.Same(TraceSummary.Empty, summary);
        Assert.Equal(0, summary.SpecCount);
        Assert.Empty(summary.SlowestSpecs);
    }

    [Fact]
    public async Task FromArtifact_ArtifactWithNoSpecSpans_ReturnsEmptySummary()
    {
        var artifact = Path.Combine(Path.GetTempPath(), $"bsspec-tracesummary-nospecs-{Guid.NewGuid():N}");
        try
        {
            // A writer that never receives anything — as happens when the collector starts but the
            // run itself produces no spec spans (e.g. --matrix, or a filter that matches nothing).
            await using (new OtlpArtifactWriter(artifact))
            {
            }

            var summary = TraceSummary.FromArtifact(artifact);

            Assert.Same(TraceSummary.Empty, summary);
        }
        finally
        {
            File.Delete(artifact + ".traces.pb");
            File.Delete(artifact + ".metrics.pb");
            File.Delete(artifact + ".logs.pb");
        }
    }

    [Fact]
    public void WriteTable_LabelsThePeakAsALowerBound()
    {
        var summary = new TraceSummary(
            SpecCount: 1,
            TotalWall: TimeSpan.FromSeconds(1),
            P50SpecMs: 10,
            P95SpecMs: 10,
            ColdStarts: 1,
            Reuses: 0,
            PeakLiveResources: 2,
            PeakLiveResourcesByKind: new Dictionary<string, long> { ["browser"] = 2 },
            PeakLiveResourcesSampled: true,
            SlowestSpecs: [new TraceSummary.SlowSpec("only", "cat", 10)]);

        using var writer = new StringWriter();
        summary.WriteTable(writer);
        var table = writer.ToString();

        // Task 11's brief is explicit that a peak silently understating itself, presented as
        // exact, is how people make bad tuning decisions — the table text must say so.
        Assert.Contains(">=", table, StringComparison.Ordinal);
        Assert.Contains("lower bound", table, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The bug this guards against: <c>--workers N</c> children are distinguished ONLY by their
    /// OTel <c>Resource</c> (<c>service.instance.id</c>, set per-worker in <c>RunBatch</c>) — the
    /// point attributes on a cold-start data point (<c>harness.resource.kind</c>,
    /// <c>harness.engine.reused</c>) look IDENTICAL across workers doing the same kind of cold
    /// start. A dedup key built from point attributes alone therefore collapses N workers' series
    /// into one, and "latest wins" then keeps only whichever single data point happens to have the
    /// globally-latest timestamp, discarding every other worker's count entirely. This is exactly
    /// the observed live bug: a <c>--workers 2</c> run reporting "1 cold" instead of "2".
    /// </summary>
    [Fact]
    public async Task FromArtifact_ColdStartsSumAcrossWorkerResources_AndLatestWinsPerResourceAcrossTicks()
    {
        var artifact = Path.Combine(Path.GetTempPath(), $"bsspec-tracesummary-multiresource-cold-{Guid.NewGuid():N}");
        try
        {
            await using (var writer = new OtlpArtifactWriter(artifact))
            {
                await writer.WriteAsync(MakeTraceRequest(("only", "cat", BaseNanos, 10)));

                // Worker 0: two export ticks for the SAME series (cumulative growth 2 -> 5). Latest
                // (5) must win for THIS resource, not naively summed with the first tick (which
                // would give 7) and not collapsed with worker 1 (which would lose one of them).
                await writer.WriteAsync(MakeEngineStartMetricsRequestForResource(
                    serviceInstanceId: "0", timeNano: BaseNanos, kind: "jvm", reused: false, count: 2));
                await writer.WriteAsync(MakeEngineStartMetricsRequestForResource(
                    serviceInstanceId: "0", timeNano: BaseNanos + 3_000_000_000, kind: "jvm", reused: false, count: 5));

                // Worker 1: a single tick, same point attributes as worker 0's series
                // (harness.resource.kind=jvm|harness.engine.reused=false) — distinguished from
                // worker 0 ONLY by its Resource's service.instance.id.
                await writer.WriteAsync(MakeEngineStartMetricsRequestForResource(
                    serviceInstanceId: "1", timeNano: BaseNanos + 1_000_000_000, kind: "jvm", reused: false, count: 3));
            }

            var summary = TraceSummary.FromArtifact(artifact);

            // Correct: latest-per-resource (worker 0 -> 5) summed ACROSS resources (5 + 3 = 8).
            // A resource-blind dedup key would instead keep only the single data point with the
            // globally-latest timestamp (worker 0's second tick, at BaseNanos+3s) and report 5 —
            // or, with ties/ordering differences, silently drop whichever worker sorts earlier.
            // Neither wrong answer is 8, so this assertion falsifies both failure modes.
            Assert.Equal(8, summary.ColdStarts);
        }
        finally
        {
            File.Delete(artifact + ".traces.pb");
            File.Delete(artifact + ".metrics.pb");
            File.Delete(artifact + ".logs.pb");
        }
    }

    /// <summary>
    /// The bug this guards against: <see cref="TraceSummary"/> used to bucket
    /// <c>harness.resource.count</c> data points by exact <c>TimeUnixNano</c> equality. Two
    /// independent <c>--workers</c> processes have independent clocks and export schedules, so
    /// their data points essentially never share a bit-identical nanosecond timestamp — even when
    /// the two workers' resources are genuinely alive at the same wall-clock moment. Exact-equality
    /// bucketing then never sums them, so the reported "peak" degenerates into "the largest single
    /// worker's own export tick" rather than true cross-worker concurrency.
    /// </summary>
    [Fact]
    public async Task FromArtifact_PeakLiveResources_SumsGenuinelyOverlappingWorkerResources()
    {
        var artifact = Path.Combine(Path.GetTempPath(), $"bsspec-tracesummary-multiresource-peak-{Guid.NewGuid():N}");
        try
        {
            await using (var writer = new OtlpArtifactWriter(artifact))
            {
                await writer.WriteAsync(MakeTraceRequest(("only", "cat", BaseNanos, 10)));

                // Two workers, each with one jvm alive at genuinely the same moment, exported at
                // slightly different nanosecond timestamps (independent clocks) — both well within
                // one export-interval-wide (1s) bucket.
                await writer.WriteAsync(MakeResourceCountMetricsRequestForResource(
                    serviceInstanceId: "0", timeNano: BaseNanos + 100_000_000, kind: "jvm", value: 1));
                await writer.WriteAsync(MakeResourceCountMetricsRequestForResource(
                    serviceInstanceId: "1", timeNano: BaseNanos + 150_000_000, kind: "jvm", value: 1));

                // A later, non-overlapping instant where only worker 0 still has a jvm alive —
                // must NOT be added to the earlier overlap.
                await writer.WriteAsync(MakeResourceCountMetricsRequestForResource(
                    serviceInstanceId: "0", timeNano: BaseNanos + 5_000_000_000, kind: "jvm", value: 1));
            }

            var summary = TraceSummary.FromArtifact(artifact);

            Assert.True(summary.PeakLiveResourcesSampled);
            // Exact-timestamp bucketing (the pre-fix behaviour) puts 100_000_000 and 150_000_000
            // in separate singleton buckets and reports a peak of 1 — never summing the two
            // workers' genuinely-concurrent jvms.
            Assert.Equal(2, summary.PeakLiveResources);
            Assert.Equal(2, summary.PeakLiveResourcesByKind["jvm"]);
        }
        finally
        {
            File.Delete(artifact + ".traces.pb");
            File.Delete(artifact + ".metrics.pb");
            File.Delete(artifact + ".logs.pb");
        }
    }

    /// <summary>
    /// A run that finishes faster than the metrics export interval never gets a single
    /// <c>harness.resource.count</c> data point exported, even though a resource undeniably
    /// existed. That must render as "not sampled", not as an indistinguishable "0 total" — a human
    /// reading "0" would wrongly conclude no resources were ever live.
    /// </summary>
    [Fact]
    public async Task FromArtifact_NoResourceCountDataPoints_ReportsPeakAsNotSampled()
    {
        var artifact = Path.Combine(Path.GetTempPath(), $"bsspec-tracesummary-notsampled-{Guid.NewGuid():N}");
        try
        {
            await using (var writer = new OtlpArtifactWriter(artifact))
            {
                // A spec span exists (so FromArtifact does not short-circuit to Empty) but no
                // harness.resource.count metric was ever written.
                await writer.WriteAsync(MakeTraceRequest(("only", "cat", BaseNanos, 10)));
            }

            var summary = TraceSummary.FromArtifact(artifact);

            Assert.False(summary.PeakLiveResourcesSampled);
            Assert.Equal(0, summary.PeakLiveResources);

            using var tableWriter = new StringWriter();
            summary.WriteTable(tableWriter);
            var table = tableWriter.ToString();
            Assert.Contains("not sampled", table, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(artifact + ".traces.pb");
            File.Delete(artifact + ".metrics.pb");
            File.Delete(artifact + ".logs.pb");
        }
    }

    private static ExportTraceServiceRequest MakeTraceRequest(
        params (string Id, string Category, ulong StartNano, int DurationMs)[] specs)
    {
        var scopeSpans = new ScopeSpans();
        foreach (var (id, category, startNano, durationMs) in specs)
        {
            scopeSpans.Spans.Add(MakeSpecSpan(id, category, startNano, durationMs));
        }

        var resourceSpans = new ResourceSpans();
        resourceSpans.ScopeSpans.Add(scopeSpans);
        var request = new ExportTraceServiceRequest();
        request.ResourceSpans.Add(resourceSpans);
        return request;
    }

    private static Span MakeSpecSpan(string id, string category, ulong startNano, int durationMs)
    {
        var span = new Span
        {
            Name = id,
            TraceId = ByteString.CopyFrom(new byte[16]),
            SpanId = ByteString.CopyFrom(new byte[8]),
            StartTimeUnixNano = startNano,
            EndTimeUnixNano = startNano + ((ulong)durationMs * 1_000_000UL),
        };
        span.Attributes.Add(StringAttribute("test.case.name", id));
        span.Attributes.Add(StringAttribute("test.suite.name", category));
        return span;
    }

    private static ExportMetricsServiceRequest MakeEngineStartMetricsRequest(
        int tick, long browserCold, long jvmCold, long browserReused)
    {
        var timeNano = BaseNanos + ((ulong)tick * 2_000_000_000UL);
        var histogram = new Histogram { AggregationTemporality = AggregationTemporality.Cumulative };
        histogram.DataPoints.Add(EngineStartDataPoint(timeNano, "browser", reused: false, count: browserCold));
        histogram.DataPoints.Add(EngineStartDataPoint(timeNano, "jvm", reused: false, count: jvmCold));
        histogram.DataPoints.Add(EngineStartDataPoint(timeNano, "browser", reused: true, count: browserReused));

        var metric = new Metric { Name = "harness.engine.start.duration", Unit = "s", Histogram = histogram };
        return WrapMetric(metric);
    }

    private static HistogramDataPoint EngineStartDataPoint(ulong timeNano, string kind, bool reused, long count)
    {
        var dataPoint = new HistogramDataPoint { TimeUnixNano = timeNano, Count = (ulong)count };
        dataPoint.Attributes.Add(StringAttribute("harness.resource.kind", kind));
        dataPoint.Attributes.Add(BoolAttribute("harness.engine.reused", reused));
        return dataPoint;
    }

    private static ExportMetricsServiceRequest MakeResourceCountMetricsRequest(int timestamp, long browser, long jvm)
    {
        var timeNano = BaseNanos + ((ulong)timestamp * 2_000_000_000UL);
        var sum = new Sum { AggregationTemporality = AggregationTemporality.Cumulative, IsMonotonic = false };
        sum.DataPoints.Add(ResourceCountDataPoint(timeNano, "browser", browser));
        sum.DataPoints.Add(ResourceCountDataPoint(timeNano, "jvm", jvm));

        var metric = new Metric { Name = "harness.resource.count", Unit = "{resource}", Sum = sum };
        return WrapMetric(metric);
    }

    private static NumberDataPoint ResourceCountDataPoint(ulong timeNano, string kind, long value)
    {
        var dataPoint = new NumberDataPoint { TimeUnixNano = timeNano, AsInt = value };
        dataPoint.Attributes.Add(StringAttribute("harness.resource.kind", kind));
        return dataPoint;
    }

    /// <summary>
    /// A single-data-point <c>harness.engine.start.duration</c> request tagged with a specific
    /// <c>service.instance.id</c> — i.e. carrying the OTel <c>Resource</c> a <c>--workers</c> child
    /// process attaches to everything it exports (see <c>OTEL_RESOURCE_ATTRIBUTES</c> in
    /// <c>RunBatch</c>). Two calls with different <paramref name="serviceInstanceId"/> values but
    /// otherwise-identical point attributes simulate two workers whose cold-start series are
    /// indistinguishable except by Resource — the exact shape that used to collapse into one.
    /// </summary>
    private static ExportMetricsServiceRequest MakeEngineStartMetricsRequestForResource(
        string serviceInstanceId, ulong timeNano, string kind, bool reused, long count)
    {
        var histogram = new Histogram { AggregationTemporality = AggregationTemporality.Cumulative };
        histogram.DataPoints.Add(EngineStartDataPoint(timeNano, kind, reused, count));
        var metric = new Metric { Name = "harness.engine.start.duration", Unit = "s", Histogram = histogram };
        return WrapMetric(metric, serviceInstanceId);
    }

    /// <summary>
    /// A single-data-point <c>harness.resource.count</c> request tagged with a specific
    /// <c>service.instance.id</c> — see <see cref="MakeEngineStartMetricsRequestForResource"/> for
    /// why this is the shape that models two independent <c>--workers</c> child processes.
    /// </summary>
    private static ExportMetricsServiceRequest MakeResourceCountMetricsRequestForResource(
        string serviceInstanceId, ulong timeNano, string kind, long value)
    {
        var sum = new Sum { AggregationTemporality = AggregationTemporality.Cumulative, IsMonotonic = false };
        sum.DataPoints.Add(ResourceCountDataPoint(timeNano, kind, value));
        var metric = new Metric { Name = "harness.resource.count", Unit = "{resource}", Sum = sum };
        return WrapMetric(metric, serviceInstanceId);
    }

    private static ExportMetricsServiceRequest WrapMetric(Metric metric) => WrapMetric(metric, serviceInstanceId: null);

    private static ExportMetricsServiceRequest WrapMetric(Metric metric, string? serviceInstanceId)
    {
        var scopeMetrics = new ScopeMetrics();
        scopeMetrics.Metrics.Add(metric);
        var resourceMetrics = new ResourceMetrics();
        resourceMetrics.ScopeMetrics.Add(scopeMetrics);
        if (serviceInstanceId is not null)
        {
            var resource = new Resource();
            resource.Attributes.Add(StringAttribute("service.instance.id", serviceInstanceId));
            resourceMetrics.Resource = resource;
        }

        var request = new ExportMetricsServiceRequest();
        request.ResourceMetrics.Add(resourceMetrics);
        return request;
    }

    private static KeyValue StringAttribute(string key, string value) =>
        new() { Key = key, Value = new AnyValue { StringValue = value } };

    private static KeyValue BoolAttribute(string key, bool value) =>
        new() { Key = key, Value = new AnyValue { BoolValue = value } };
}
