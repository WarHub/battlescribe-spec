using BattleScribeSpec.Engines;

namespace BattleScribeSpec.Tests.Features;

public sealed class EngineHostLocatorTests
{
    private static readonly EngineEntry Builtin =
        new("battlescribe", null, null, ["roster", "gamedata"], 0, Builtin: true);

    [Fact]
    public void LaunchableEntry_PassesThrough()
    {
        var entry = new EngineEntry("wham", "node", "adapters/wham.js", ["roster"], 0, Builtin: false);
        var launch = EngineHostLocator.Resolve(entry);
        Assert.Equal("node", launch.Executable);
        Assert.Equal("adapters/wham.js", launch.Arguments);
    }

    [Fact]
    public void Builtin_UsesEnvOverride_WithServeArgs()
    {
        var fake = Path.Combine(Path.GetTempPath(), "fake-host.dll");
        File.WriteAllText(fake, "");
        var priorValue = Environment.GetEnvironmentVariable("BSSPEC_ENGINE_HOST");
        try
        {
            Environment.SetEnvironmentVariable("BSSPEC_ENGINE_HOST", fake);
            var launch = EngineHostLocator.Resolve(Builtin, headed: true, keepAlive: true);
            Assert.Equal("dotnet", launch.Executable);
            Assert.Equal($"{fake} serve --engine battlescribe --headed --keep-alive", launch.Arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BSSPEC_ENGINE_HOST", priorValue);
            File.Delete(fake);
        }
    }

    [Fact]
    public void Builtin_FindsHostInArtifacts()
    {
        // Running from the repo, probe 3 (artifacts walk) must find the built host.
        var launch = EngineHostLocator.Resolve(Builtin);
        Assert.Contains("bs-engine-host", launch.Arguments + launch.Executable);
        Assert.StartsWith("dotnet", launch.Executable);
        Assert.Contains("serve --engine battlescribe", launch.Arguments);
    }

    [Fact]
    public void ConfiguredEntryWithoutExec_Throws()
    {
        var entry = new EngineEntry("wham", null, null, ["roster"], 0, Builtin: false);
        var ex = Assert.Throws<InvalidOperationException>(() => EngineHostLocator.Resolve(entry));
        Assert.Contains("wham", ex.Message);
    }
}
