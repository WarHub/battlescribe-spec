using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.Concurrency;
using BattleScribeSpec.EngineHost;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture for the BS Roster UI driver conformance lane.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="BsGameDataUiFixture"/>: one BattleScribe desktop instance, driven through the
/// Java agent, shared sequentially across specs.
/// </para>
/// <para>
/// <b>This lane did not exist.</b> CI's <c>thorough-ui-bs</c> job filtered on
/// <c>Engine=BsGameDataUi</c> and nothing else, so <c>BsUiRosterEngine</c> and the whole of
/// <c>RosterActions.java</c> — roster creation, addForce, selectEntry, removeForce, the
/// customisation dialogs — had no conformance coverage at all. They were reachable only through
/// <c>bs-spec serve</c> and one teardown test.
/// </para>
/// <para>
/// That gap is why a fixed <c>sleep(300)</c> between choosing a catalogue and choosing a force
/// entry could sit in the agent indefinitely: when it lost the race, the force-entry lookup ran
/// against the PREVIOUS catalogue's combo contents and its <c>toString().contains(id)</c> fallback
/// could match a stale entry, building the wrong roster and reporting success. Nothing was
/// positioned to notice.
/// </para>
/// <para>
/// <b>Reuse is not a knob.</b> Whether the JVM survives between specs is
/// <see cref="ConcurrencyPlan.ReuseRoster"/>, resolved from <see cref="FixtureConcurrency"/> —
/// the same single decision the CLI path takes, for the same reason it is single there.
/// </para>
/// <para>
/// Skipped when <c>BS_UI_SKIP=true</c>, or when the BattleScribe artifacts / agent JAR are absent
/// (run <c>setup.ps1</c>, which provisions both).
/// </para>
/// </remarks>
public sealed class BsRosterUiFixture : IAsyncLifetime
{
    public BsUiRosterEngine? Engine { get; private set; }

    public bool Available => Engine is not null;

    public ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("BS_UI_SKIP") == "true")
        {
            return ValueTask.CompletedTask;
        }

        BsUiOptions options;
        try
        {
            // Throws when the app or agent JAR is missing, which is every machine that has not run
            // setup.ps1 — and every CI job except the ones that build the agent. Treated as "not
            // available" rather than a failure, exactly as the gamedata fixture treats it.
            options = HostEngineFactory.ResolveBsUiOptions();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bs-roster-ui-fixture] artifacts unavailable: {ex.Message}");
            return ValueTask.CompletedTask;
        }

        AnchorDiagnosticsAtRepoRoot();

        var keepAlive = FixtureConcurrency.Resolve("battlescribe-ui", LoadTarget.Local).ReuseRoster;

        using var span = FixtureTelemetry.StartInit(nameof(BsRosterUiFixture));
        Engine = new BsUiRosterEngine(options) { KeepAlive = keepAlive };
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Points <see cref="BsUiDiagnostics"/> at the repo root's <c>artifacts/</c>, not the test
    /// host's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The driver defaults to <c>Directory.GetCurrentDirectory()/artifacts/bs-ui-diagnostics</c>,
    /// which is right for the CLI and wrong here: VSTest runs the test host with its working
    /// directory set to the test assembly's own output folder, so a failing spec in this lane writes
    /// its dump to <c>artifacts/bin/BattleScribeSpec.Tests/debug/artifacts/bs-ui-diagnostics/</c> —
    /// measured, 19 dumps there against 1 at the repo root — and CI's "Upload diagnostics" step
    /// looks only at the latter. The artifact would be empty for exactly the failures it exists to
    /// explain, and <c>if-no-files-found: ignore</c> would keep that quiet.
    /// </para>
    /// <para>
    /// The same trap, with the same cause and the same fix, is written down in
    /// <see cref="TelemetryAssemblyFixture"/> for the telemetry artifact. It is done here rather
    /// than in the driver because <c>TestPaths</c> is test-side, and because knowing that this
    /// process is a VSTest host is the fixture's business, not the driver's.
    /// </para>
    /// <para>
    /// An explicit <c>BS_UI_DIAGNOSTICS_DIR</c> still wins — this only replaces the default, and
    /// only when the repo root can be found.
    /// </para>
    /// </remarks>
    private static void AnchorDiagnosticsAtRepoRoot()
    {
        if (Environment.GetEnvironmentVariable("BS_UI_DIAGNOSTICS_DIR") is { Length: > 0 })
        {
            return;
        }

        if (TestPaths.RepoRootDirectory is { } repoRoot)
        {
            BsUiDiagnostics.DiagnosticsDirectory =
                Path.Combine(repoRoot, "artifacts", "bs-ui-diagnostics");
        }
    }

    public ValueTask DisposeAsync()
    {
        Engine?.Dispose();
        Engine = null;
        return ValueTask.CompletedTask;
    }
}

[CollectionDefinition("BsRosterUi")]
public class BsRosterUiCollection : ICollectionFixture<BsRosterUiFixture>
{
}
