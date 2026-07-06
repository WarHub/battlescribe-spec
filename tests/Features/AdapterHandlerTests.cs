using BattleScribeSpec.Protocol;
using BattleScribeSpec.Tests.Infrastructure;

namespace BattleScribeSpec.Tests.Features;

public sealed class AdapterHandlerTests
{
    private static InMemoryAdapterConnection Connect() => new(
        (input, output, ct) => AdapterHandler.RunAsync(
            () => new BattleScribeSpec.BattleScribeRosterEngine(), input, output, ct));

    private static InMemoryAdapterConnection ConnectV11() => new(
        (input, output, ct) => AdapterHandler.RunAsync(
            new AdapterOptions
            {
                RosterEngineFactory = () => new BattleScribeSpec.BattleScribeRosterEngine(),
                Name = "battlescribe",
                Version = "test",
            },
            input, output, ct));

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
    public async Task Describe_ReturnsIdentityAndDomains()
    {
        await using var connection = ConnectV11();

        var described = Assert.IsType<DescribeResult>(
            await connection.SendCommandAsync(new DescribeCommand(), TestContext.Current.CancellationToken));
        Assert.Equal("battlescribe", described.Name);
        Assert.Equal("1.1", described.ProtocolVersion);
        Assert.Equal(["roster"], described.Domains); // no gamedata factory registered
    }

    [Fact]
    public async Task AdapterDescriber_FallsBack_OnErrorResponse()
    {
        // Simulate a legacy v1.0 adapter: a handler loop that answers everything with an error.
        await using var legacy = new InMemoryAdapterConnection(async (input, output, ct) =>
        {
            while (await input.ReadLineAsync(ct) is { } _)
            {
                await output.WriteLineAsync(ProtocolSerializer.SerializeResponse(
                    new ProtocolError { Message = "Unknown command" }).AsMemory(), ct);
                await output.FlushAsync(ct);
            }
        });

        var described = await AdapterDescriber.DescribeAsync(legacy);
        Assert.Equal("1.0", described.ProtocolVersion);
    }

    [Fact]
    public async Task AdapterDescriber_ReturnsRealDescription_OnV11Adapter()
    {
        await using var connection = ConnectV11();

        var described = await AdapterDescriber.DescribeAsync(connection);
        Assert.Equal("battlescribe", described.Name);
        Assert.Equal("1.1", described.ProtocolVersion);
    }
}
