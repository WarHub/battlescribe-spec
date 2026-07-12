using System.Diagnostics;
using BattleScribeSpec.Telemetry;

namespace BattleScribeSpec.Tests.Features;

[Trait("Category", "Unit")]
public sealed class HarnessTelemetryTests
{
    [Fact]
    public void StartSpec_EmitsTestSemanticConventions()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == HarnessTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Add,
        };
        ActivitySource.AddActivityListener(listener);

        using (var activity = HarnessTelemetry.StartSpec("entry/entry-basic", "entry", "roster"))
        {
            HarnessTelemetry.SetVerdict(activity, "expected-failure");
        }

        var span = Assert.Single(captured);
        Assert.Equal("entry/entry-basic", span.GetTagItem("test.case.name"));
        Assert.Equal("entry", span.GetTagItem("test.suite.name"));

        // OTel's test.case.result.status admits ONLY "pass" and "fail". Our four-way verdict
        // rides bsspec.verdict; emitting "expected-failure" into the standard attribute would
        // make us unreadable by the backends we adopted OTel to satisfy.
        Assert.Equal("pass", span.GetTagItem("test.case.result.status"));
        Assert.Equal("expected-failure", span.GetTagItem("bsspec.verdict"));
    }

    [Fact]
    public void StartOp_WithTraceparent_NestsUnderTheGivenParent()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == HarnessTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        // A well-formed W3C traceparent: version-traceid-spanid-flags.
        const string Traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

        using var child = HarnessTelemetry.StartOp("setup", Traceparent);

        Assert.NotNull(child);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", child.TraceId.ToHexString());
        Assert.Equal("b7ad6b7169203331", child.ParentSpanId.ToHexString());
    }

    [Fact]
    public void CurrentTraceparent_RoundTripsThroughStartOp()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == HarnessTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using var parent = HarnessTelemetry.StartOp("run");
        var traceparent = HarnessTelemetry.CurrentTraceparent();

        Assert.NotNull(traceparent);
        using var child = HarnessTelemetry.StartOp("spec", traceparent);

        Assert.NotNull(child);
        Assert.Equal(parent!.TraceId, child.TraceId);
        Assert.Equal(parent.SpanId, child.ParentSpanId);
    }
}
