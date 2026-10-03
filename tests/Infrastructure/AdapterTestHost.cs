using BattleScribeSpec.Protocol;
using BattleScribeSpec.TestSupport;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared helpers for tests that need a live adapter process, and the one place this project finds the
/// sibling binaries it starts (<c>bs-reference-adapter.dll</c>, <c>bs-engine-host.dll</c>). Four suites
/// used to walk up from the binaries to the repository root on their own, each with a hard-coded
/// <c>debug</c> pivot; they resolve through <see cref="TestPaths.Root"/> and <see cref="BuiltBinaries"/>, the
/// rule Cli.Tests applies too, here instead.
/// </summary>
internal static class AdapterTestHost
{
    /// <summary>The built in-repo reference adapter (<c>src/BattleScribeSpec.ReferenceAdapter</c>).</summary>
    public static string ReferenceAdapterDll => BuiltBinaries.Find(TestPaths.Root, "BattleScribeSpec.ReferenceAdapter", "bs-reference-adapter.dll");

    /// <summary>The built engine host (<c>src/BattleScribeSpec.EngineHost</c>), which serves the built-in engines.</summary>
    public static string EngineHostDll => BuiltBinaries.Find(TestPaths.Root, "BattleScribeSpec.EngineHost", "bs-engine-host.dll");

    /// <summary>
    /// Starts the in-repo reference adapter (<c>src/BattleScribeSpec.ReferenceAdapter</c>,
    /// <c>bs-reference-adapter.dll</c>), which advertises unlimited parallelism (<c>MaxParallel = 0</c>
    /// in its engine registration) — it is the standard adapter used for
    /// <see cref="BattleScribeSpec.Batch.SpecSuiteRunner"/> integration tests.
    /// </summary>
    /// <param name="environment">
    /// Extra child-process environment variables, e.g. <c>BSSPEC_TEST_FORCE_KILL</c> to make the
    /// process kill itself on a named spec (see <c>ForceKillHook</c> in the reference adapter) —
    /// used to deterministically exercise <see cref="BattleScribeSpec.Batch.SpecSuiteRunner"/>'s
    /// adapter-death recovery.
    /// </param>
    public static AdapterProcess StartReferenceAdapter(IReadOnlyDictionary<string, string>? environment = null) =>
        AdapterProcess.Start("dotnet", ReferenceAdapterDll, environment);
}
