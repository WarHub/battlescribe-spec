using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests.Features;

public sealed class EngineHostServeTests
{
    private static string FindHostDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BattleScribeSpec.slnx")))
        {
            dir = dir.Parent!;
        }

        Assert.NotNull(dir);
        var dll = Path.Combine(dir.FullName, "artifacts", "bin",
            "BattleScribeSpec.EngineHost", "debug", "bs-engine-host.dll");
        Assert.True(File.Exists(dll), $"Engine host not built: {dll}");
        return dll;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Serve_Battlescribe_DescribesAndRunsRosterSetup()
    {
        var ct = TestContext.Current.CancellationToken;
        using var process = AdapterProcess.Start("dotnet", $"{FindHostDll()} serve --engine battlescribe");

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
        using var process = AdapterProcess.Start("dotnet", $"{FindHostDll()} serve --engine battlescribe");
        await process.SendCommandAsync(new SetupCommand
        {
            GameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" },
        }, ct);

        var response = await process.SendCommandAsync(new ScreenshotCommand(), ct);
        Assert.IsType<ProtocolError>(response);
    }
}
