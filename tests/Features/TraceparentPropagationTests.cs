using System.Diagnostics;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Telemetry;
using BattleScribeSpec.Tests.Infrastructure;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// Verifies the W3C trace-context carrier (<see cref="ProtocolCommand.Traceparent"/> /
/// <see cref="ProtocolCommand.Tracestate"/>) that lets an <see cref="AdapterHandler"/> in a
/// child process nest its spans under the client's spec span. This is the single property
/// that makes the harness open to third-party adapters: any language's stock OTel SDK can
/// parent to <c>traceparent</c> with zero harness-specific code.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TraceparentPropagationTests
{
    [Fact]
    public void Command_CarriesTraceparent_OverTheWire()
    {
        var command = new GetStateCommand { Traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" };

        var json = ProtocolSerializer.SerializeCommand(command);
        var round = ProtocolSerializer.DeserializeCommand(json);

        Assert.Contains("traceparent", json, StringComparison.Ordinal);
        Assert.Equal(command.Traceparent, round!.Traceparent);
    }

    [Fact]
    public void Command_WithoutTraceparent_OmitsItFromTheWire()
    {
        var json = ProtocolSerializer.SerializeCommand(new GetStateCommand());

        // Optional field: adapters that never heard of it must not see it. Same contract as corrId.
        Assert.DoesNotContain("traceparent", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdapterHandler_ParentsItsSpanToTheCommandsTraceparent()
    {
        const string TraceId = "0af7651916cd43dd8448eb211c80319c";
        const string ParentSpanId = "b7ad6b7169203331";

        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == HarnessTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            // Filter by trace id, not just span name: other tests running concurrently in other
            // classes can also dispatch a "getState" command through the same process-wide
            // ActivitySource, and this listener would otherwise capture their spans too.
            ActivityStopped = a =>
            {
                if (a.TraceId.ToHexString() == TraceId)
                {
                    lock (spans)
                    {
                        spans.Add(a);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var ct = TestContext.Current.CancellationToken;

        // Drive one command through the in-process adapter loop.
        await using var connection = new InMemoryAdapterConnection(
            (input, output, innerCt) => AdapterHandler.RunAsync(
                () => new BattleScribeSpec.BattleScribeRosterEngine(), input, output, innerCt));

        await connection.SendCommandAsync(new GetStateCommand
        {
            Traceparent = $"00-{TraceId}-{ParentSpanId}-01",
        }, ct);

        var handled = spans.Single(s => s.OperationName == "getState");
        Assert.Equal(TraceId, handled.TraceId.ToHexString());
        Assert.Equal(ParentSpanId, handled.ParentSpanId.ToHexString());
    }
}
