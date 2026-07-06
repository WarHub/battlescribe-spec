using BattleScribeSpec.Protocol;
using BattleScribeSpec.Tests.Infrastructure;

namespace BattleScribeSpec.Tests.Features;

public sealed class AdapterHandlerTests
{
    private static InMemoryAdapterConnection Connect() => new(
        (input, output, ct) => AdapterHandler.RunAsync(
            () => new BattleScribeSpec.BattleScribeRosterEngine(), input, output, ct));

    [Fact]
    public async Task Setup_GetState_Teardown_RoundTrips()
    {
        await using var connection = Connect();

        var ct = TestContext.Current.CancellationToken;

        var setup = await connection.SendCommandAsync(new SetupCommand
        {
            GameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" },
        }, ct);
        Assert.IsType<SetupResult>(setup);

        var state = await connection.SendCommandAsync(new GetStateCommand(), ct);
        Assert.IsType<StateResponse>(state);

        Assert.IsType<TeardownResult>(await connection.SendCommandAsync(new TeardownCommand(), ct));
    }

    [Fact]
    public async Task LegacyHandler_AnswersDescribe_WithError()
    {
        await using var connection = Connect();

        // Task 3 replaces this expectation with a real DescribeResult; today the
        // legacy loop reports an unknown command — which is exactly the legacy
        // adapter behavior the describe fallback must tolerate.
        var response = await connection.SendCommandAsync(new DescribeCommand(), TestContext.Current.CancellationToken);
        Assert.IsType<ProtocolError>(response);
    }
}
