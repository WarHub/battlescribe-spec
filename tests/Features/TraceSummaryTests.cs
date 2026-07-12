using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
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
            SlowestSpecs: [new TraceSummary.SlowSpec("only", "cat", 10)]);

        using var writer = new StringWriter();
        summary.WriteTable(writer);
        var table = writer.ToString();

        // Task 11's brief is explicit that a peak silently understating itself, presented as
        // exact, is how people make bad tuning decisions — the table text must say so.
        Assert.Contains(">=", table, StringComparison.Ordinal);
        Assert.Contains("lower bound", table, StringComparison.OrdinalIgnoreCase);
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

    private static ExportMetricsServiceRequest WrapMetric(Metric metric)
    {
        var scopeMetrics = new ScopeMetrics();
        scopeMetrics.Metrics.Add(metric);
        var resourceMetrics = new ResourceMetrics();
        resourceMetrics.ScopeMetrics.Add(scopeMetrics);
        var request = new ExportMetricsServiceRequest();
        request.ResourceMetrics.Add(resourceMetrics);
        return request;
    }

    private static KeyValue StringAttribute(string key, string value) =>
        new() { Key = key, Value = new AnyValue { StringValue = value } };

    private static KeyValue BoolAttribute(string key, bool value) =>
        new() { Key = key, Value = new AnyValue { BoolValue = value } };
}
