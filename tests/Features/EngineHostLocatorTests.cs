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
    public void Builtin_NoOverrides_ComposesPlainServeArgs()
    {
        // When no plan is given, no --policy flag is composed at all, and the
        // child falls back to ServeCommand.NoPolicyPlan — a hardcoded, deliberately conservative
        // (1, 1, no-reuse). The child does NOT compute a policy of its own: the parent decides, the
        // child is told. In practice the harness always passes --policy, so this path is reached
        // only by a hand-run `bs-engine-host serve`.
        var fake = Path.Combine(Path.GetTempPath(), "fake-host.dll");
        File.WriteAllText(fake, "");
        var priorValue = Environment.GetEnvironmentVariable("BSSPEC_ENGINE_HOST");
        try
        {
            Environment.SetEnvironmentVariable("BSSPEC_ENGINE_HOST", fake);
            var launch = EngineHostLocator.Resolve(Builtin, headed: true);
            Assert.Equal("dotnet", launch.Executable);
            Assert.Equal($"{fake} serve --engine battlescribe --headed", launch.Arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BSSPEC_ENGINE_HOST", priorValue);
            File.Delete(fake);
        }
    }

    // Builtin_KeepAlive_ComposesPolicyReuseOn lived here. `EngineHostLocator.Resolve`'s `keepAlive`
    // parameter composed `--policy reuse=on` when no plan was given, and its docstring called it
    // "legacy sugar ... e.g. interactive debugging via `run --keep-alive`" — a flag this branch had
    // already deleted from the CLI. Both EngineSelection construction sites passed `KeepAlive: false`,
    // so no production path could reach the branch: only this test could, by calling the parameter
    // directly. Reuse is ConcurrencyPolicy's ONE decision and it travels in the plan.

    /// <summary>
    /// A plan composes the full <c>--policy</c> string: the child is TOLD every decision, including the
    /// negative ones (<c>reuse-gamedata=off</c>), so it never has to infer one from an absence.
    /// </summary>
    [Fact]
    public void Builtin_Plan_ComposesFullPolicyString()
    {
        var fake = Path.Combine(Path.GetTempPath(), "fake-host.dll");
        File.WriteAllText(fake, "");
        var priorValue = Environment.GetEnvironmentVariable("BSSPEC_ENGINE_HOST");
        try
        {
            Environment.SetEnvironmentVariable("BSSPEC_ENGINE_HOST", fake);
            var plan = new ConcurrencyPlan(Workers: 3, PoolSize: 3, ReuseRoster: true, ReuseGameData: false);

            var launch = EngineHostLocator.Resolve(Builtin, plan: plan);

            Assert.Equal(
                $"{fake} serve --engine battlescribe --policy workers=3,reuse-roster=on,reuse-gamedata=off",
                launch.Arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BSSPEC_ENGINE_HOST", priorValue);
            File.Delete(fake);
        }
    }

    [Fact]
    public void LaunchableEntry_WithPlan_ThrowsRatherThanSilentlyDroppingIt()
    {
        // #305 (headed/keep-alive silently dropped for exec:/dotnet: adapters) stays open — but a
        // policy override must never suffer the same silent drop. There is no channel to convey it
        // to a launchable adapter, so this must fail loudly instead of quietly ignoring the plan.
        var entry = new EngineEntry("wham", "node", "adapters/wham.js", ["roster"], Profile, Builtin: false);
        var plan = new ConcurrencyPlan(Workers: 1, PoolSize: 1, ReuseRoster: false, ReuseGameData: false);

        var ex = Assert.Throws<InvalidOperationException>(() => EngineHostLocator.Resolve(entry, plan: plan));
        Assert.Contains("wham", ex.Message, StringComparison.Ordinal);
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
