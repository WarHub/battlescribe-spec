using BattleScribeSpec.Concurrency;
using BattleScribeSpec.Engines;

namespace BattleScribeSpec.Tests.Features;

public sealed class EngineHostLocatorTests
{
    private static readonly EngineProfile Profile = new(0, ColdStartCost.Cheap, ReuseSafeRoster: false, ReuseSafeGameData: false);

    private static readonly EngineEntry Builtin =
        new("battlescribe", null, null, ["roster", "gamedata"], Profile, Builtin: true);

    [Fact]
    public void LaunchableEntry_PassesThrough()
    {
        var entry = new EngineEntry("wham", "node", "adapters/wham.js", ["roster"], Profile, Builtin: false);
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
    public void Builtin_InteractiveVerb_ComposesVerbArgsAndQuotesSpaces()
    {
        // probe/discover carry their whole verb tail via verbArgs; the locator prefixes the
        // verb and quotes any element containing whitespace (e.g. a spec path with a space).
        var fake = Path.Combine(Path.GetTempPath(), "fake-host.dll");
        File.WriteAllText(fake, "");
        var priorValue = Environment.GetEnvironmentVariable("BSSPEC_ENGINE_HOST");
        try
        {
            Environment.SetEnvironmentVariable("BSSPEC_ENGINE_HOST", fake);
            var launch = EngineHostLocator.Resolve(
                Builtin,
                verb: "probe",
                verbArgs: ["--engine", "battlescribe-ui", "my spec.yaml", "--roster"]);
            Assert.Equal("dotnet", launch.Executable);
            Assert.Equal($"{fake} probe --engine battlescribe-ui \"my spec.yaml\" --roster", launch.Arguments);
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
        var entry = new EngineEntry("wham", null, null, ["roster"], Profile, Builtin: false);
        var ex = Assert.Throws<InvalidOperationException>(() => EngineHostLocator.Resolve(entry));
        Assert.Contains("wham", ex.Message);
    }
}
