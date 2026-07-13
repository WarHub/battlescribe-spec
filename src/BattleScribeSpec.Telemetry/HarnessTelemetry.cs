using System.Diagnostics;

namespace BattleScribeSpec.Telemetry;

/// <summary>
/// The harness's instrumentation API. Uses only BCL primitives (<see cref="ActivitySource"/>,
/// <see cref="System.Diagnostics.Metrics.Meter"/>) so it stays AOT-safe and can be called from
/// the trim-analyzed Cli and TestKit projects. Emitting is free when nothing is listening.
/// </summary>
public static class HarnessTelemetry
{
    /// <summary>Name of the harness <see cref="ActivitySource"/>; listeners subscribe by this.</summary>
    public const string SourceName = "BattleScribeSpec.Harness";

    /// <summary>Name of the harness meter.</summary>
    public const string MeterName = "BattleScribeSpec.Harness";

    private static readonly ActivitySource Source = new(
        SourceName,
        typeof(HarnessTelemetry).Assembly.GetName().Version?.ToString());

    /// <summary>
    /// Start the span for one spec execution, tagged with OpenTelemetry's test semantic
    /// conventions (stability: Development) so off-the-shelf backends render conformance runs
    /// without an adapter. The span is named for the spec so a trace list is readable — OTel
    /// publishes no span-name convention for tests, so this is our choice, not a standard.
    /// </summary>
    public static Activity? StartSpec(string specId, string category, string domain)
    {
        var activity = Source.StartActivity(specId, ActivityKind.Internal);
        activity?.SetTag("test.case.name", specId);
        activity?.SetTag("test.suite.name", category);
        activity?.SetTag("bsspec.domain", domain);
        return activity;
    }

    /// <summary>
    /// Start an operation span. When <paramref name="traceparent"/> is a valid W3C trace-context
    /// header the span is parented to it — this is how a child process nests its work under the
    /// parent's spec span.
    /// </summary>
    /// <param name="name">The operation's span name.</param>
    /// <param name="traceparent">
    /// A W3C <c>traceparent</c> header to parent this span to, or null to start unparented
    /// (or parented to <see cref="Activity.Current"/>, per normal <see cref="ActivitySource"/> rules).
    /// </param>
    /// <param name="kind">
    /// An adapter command is a remote call, so the sending side passes <see cref="ActivityKind.Client"/>
    /// and the handling side passes <see cref="ActivityKind.Server"/>. Jaeger's dependency graph and
    /// Tempo's servicegraph processor derive edges EXCLUSIVELY from CLIENT→SERVER pairs; with
    /// Internal on both sides there is no edge between bs-spec and bs-engine-host at all.
    /// </param>
    /// <param name="tracestate">The W3C <c>tracestate</c> header accompanying <paramref name="traceparent"/>, if any.</param>
    public static Activity? StartOp(
        string name,
        string? traceparent = null,
        ActivityKind kind = ActivityKind.Internal,
        string? tracestate = null)
    {
        if (traceparent is not null && ActivityContext.TryParse(traceparent, tracestate, out var parent))
        {
            return Source.StartActivity(name, kind, parent);
        }

        return Source.StartActivity(name, kind);
    }

    /// <summary>
    /// Record a spec's verdict: one of "passed", "failed", "expected-failure", "unexpected-pass".
    /// </summary>
    /// <remarks>
    /// OTel's <c>test.case.result.status</c> admits ONLY the values <c>pass</c> and <c>fail</c>, so the
    /// harness's four-way verdict lives on <c>bsspec.verdict</c> and is mapped down for the standard
    /// attribute. Emitting our richer vocabulary into the convention would make conformance runs
    /// unreadable to the backends we adopted OpenTelemetry in order to satisfy.
    /// </remarks>
    public static void SetVerdict(Activity? activity, string status)
    {
        activity?.SetTag("bsspec.verdict", status);
        activity?.SetTag("test.case.result.status", status is "passed" or "expected-failure" ? "pass" : "fail");

        if (status is "failed" or "unexpected-pass")
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }
    }

    /// <summary>
    /// Tag the current spec's span with a distinguishable adapter-death signal — an event plus an
    /// error status, not merely a normal assertion failure (which <see cref="SetVerdict"/> already
    /// covers via <c>bsspec.verdict</c>/<c>test.case.result.status</c>). Called by the harness's
    /// spec-suite runner immediately after it detects the underlying adapter process exited during
    /// this spec's attempt, so the crash is visible in any OTel backend rendering this span even
    /// when a subsequent retry rescues the spec's verdict.
    /// </summary>
    public static void SetAdapterDeath(Activity? activity)
    {
        activity?.SetTag("bsspec.adapter_death", true);
        activity?.AddEvent(new ActivityEvent("adapter.process.died"));
        activity?.SetStatus(ActivityStatusCode.Error, "adapter process died while running this spec");
    }

    /// <summary>
    /// The W3C <c>traceparent</c> for <see cref="Activity.Current"/>, or null when untraced.
    /// Send this over the adapter protocol so the child parents its spans correctly.
    /// </summary>
    public static string? CurrentTraceparent() => Activity.Current?.Id;
}
