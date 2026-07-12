using System.Diagnostics;
using BattleScribeSpec.Batch;
using BattleScribeSpec.Telemetry;

namespace BattleScribeSpec.Tests.Features;

[Trait("Category", "Integration")]
public sealed class SpecSuiteRunnerTelemetryTests
{
    [Fact]
    public async Task RunAsync_EmitsOneSpecSpanPerSpec_WithVerdict()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == HarnessTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) { spans.Add(a); } },
        };
        ActivitySource.AddActivityListener(listener);

        // ActivityListener is process-wide: xUnit runs test collections in parallel, and other
        // SpecSuiteRunner-based tests emit spec spans concurrently. Root this run under its own
        // Activity so RunOneSpec's spans (created with Activity.Current as their implicit parent,
        // since Workers=1 keeps everything on this async call stack) can be picked out from the
        // noise by parentage rather than by name or spec id (both of which collide with other
        // tests using the same "protocol/protocol-kitchen-sink" spec concurrently).
        using var testRoot = HarnessTelemetry.StartOp("test-run");
        Assert.NotNull(testRoot);

        // NOTE: RunAsync's second parameter is a TextWriter progress sink, NOT a CancellationToken.
        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            AdapterFactory = AdapterTestHost.StartReferenceAdapter,
            Domains = ["roster"],
            FilterPatterns = ["protocol/protocol-kitchen-sink"],
            Workers = 1,
        });

        // HarnessTelemetry.StartSpec names each span for its spec ID (e.g. "protocol-kitchen-sink"),
        // not a fixed literal — "the span is named for the spec so a trace list is readable" (Task 2).
        // So "the spec span" is identified by parentage plus the verdict tag SetVerdict attaches,
        // not by name.
        var specSpans = spans
            .Where(s => s.ParentSpanId == testRoot.SpanId && s.GetTagItem("test.case.result.status") is not null)
            .ToList();
        Assert.Equal(result.Results.Count, specSpans.Count);
        Assert.All(specSpans, s => Assert.NotNull(s.GetTagItem("test.case.result.status")));
        Assert.All(specSpans, s => Assert.True(s.Duration > TimeSpan.Zero));
    }
}
