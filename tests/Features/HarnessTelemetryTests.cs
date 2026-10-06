using System.Diagnostics;
using BattleScribeSpec.Telemetry;

namespace BattleScribeSpec.Tests.Features;

[Trait("Category", "Unit")]
public sealed class HarnessTelemetryTests
{
    [Fact]
    public void StartSpec_EmitsTestSemanticConventions()
    {
        using var listener = ListenToHarness();

        using var span = HarnessTelemetry.StartSpec("entry/entry-basic", "entry", "roster");
        Assert.NotNull(span);
        HarnessTelemetry.SetVerdict(span, "expected-failure");
        span.Stop();

        Assert.Equal("entry/entry-basic", span.GetTagItem("test.case.name"));
        Assert.Equal("entry", span.GetTagItem("test.suite.name"));

        // OTel's test.case.result.status admits ONLY "pass" and "fail". Our four-way verdict
        // rides bsspec.verdict; emitting "expected-failure" into the standard attribute would
        // make us unreadable by the backends we adopted OTel to satisfy.
        Assert.Equal("pass", span.GetTagItem("test.case.result.status"));
        Assert.Equal("expected-failure", span.GetTagItem("bsspec.verdict"));
    }

    [Theory]
    [InlineData("passed", "pass", false)]
    [InlineData("expected-failure", "pass", false)]
    [InlineData("failed", "fail", true)]
    [InlineData("unexpected-pass", "fail", true)]
    public void SetVerdict_MapsAllFourVerdicts(string verdict, string expectedStatus, bool shouldHaveError)
    {
        using var listener = ListenToHarness();

        using var span = HarnessTelemetry.StartSpec("test/case", "category", "domain");
        Assert.NotNull(span);
        HarnessTelemetry.SetVerdict(span, verdict);
        span.Stop();

        // Verify the standard OTel attribute is mapped correctly (only "pass" or "fail").
        Assert.Equal(expectedStatus, span.GetTagItem("test.case.result.status"));

        // Verify the original four-way verdict is preserved on the custom attribute.
        Assert.Equal(verdict, span.GetTagItem("bsspec.verdict"));

        // Verify Activity.Status is set to Error only for failing verdicts.
        if (shouldHaveError)
        {
            Assert.Equal(ActivityStatusCode.Error, span.Status);
        }
        else
        {
            Assert.Equal(ActivityStatusCode.Unset, span.Status);
        }
    }

    [Fact]
    public void StartOp_WithTraceparent_NestsUnderTheGivenParent()
    {
        using var listener = ListenToHarness();

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
        using var listener = ListenToHarness();

        using var parent = HarnessTelemetry.StartOp("run");
        var traceparent = HarnessTelemetry.CurrentTraceparent();

        Assert.NotNull(traceparent);
        using var child = HarnessTelemetry.StartOp("spec", traceparent);

        Assert.NotNull(child);
        Assert.Equal(parent!.TraceId, child.TraceId);
        Assert.Equal(parent.SpanId, child.ParentSpanId);
    }

    /// <summary>
    /// Makes the harness source sample, so its spans exist. It collects nothing: each test asserts on
    /// the span it started. An <see cref="ActivityListener"/> is process-wide, so a list it fills also
    /// receives every harness span the other test classes stop on their own threads — reading one is
    /// what failed with "Collection was modified" under load (#525).
    /// </summary>
    private static ActivityListener ListenToHarness()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static s => s.Name == HarnessTelemetry.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
