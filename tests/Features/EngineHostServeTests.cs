using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests.Features;

public sealed class EngineHostServeTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Serve_Battlescribe_DescribesAndRunsRosterSetup()
    {
        var ct = TestContext.Current.CancellationToken;
        using var process = AdapterProcess.Start("dotnet", $"{AdapterTestHost.EngineHostDll} serve --engine battlescribe");

        var described = await AdapterDescriber.DescribeAsync(process);
        Assert.Equal("battlescribe", described.Name);
        Assert.Equal(["roster", "gamedata"], described.Domains);
        Assert.False(described.Capabilities.Screenshot);

        var setup = await process.SendCommandAsync(new SetupCommand
        {
            GameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" },
        }, ct);
        Assert.IsType<SetupResult>(setup);
        Assert.IsType<StateResponse>(await process.SendCommandAsync(new GetStateCommand(), ct));
        Assert.IsType<TeardownResult>(await process.SendCommandAsync(new TeardownCommand(), ct));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Serve_Battlescribe_ScreenshotAnswersNotSupported()
    {
        var ct = TestContext.Current.CancellationToken;
        using var process = AdapterProcess.Start("dotnet", $"{AdapterTestHost.EngineHostDll} serve --engine battlescribe");
        await process.SendCommandAsync(new SetupCommand
        {
            GameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" },
        }, ct);

        var response = await process.SendCommandAsync(new ScreenshotCommand(), ct);
        Assert.IsType<ProtocolError>(response);
    }
}
