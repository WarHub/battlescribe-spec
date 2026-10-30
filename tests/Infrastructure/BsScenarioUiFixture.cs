using BattleScribeSpec.BsGameDataUiDriver;
using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.Concurrency;
using BattleScribeSpec.EngineHost;

namespace BattleScribeSpec.Tests;

/// <summary>
/// The BattleScribe desktop pair for scenario specs: a <see cref="BsGameDataUiEngine"/> driving the real Data
/// Editor as the data editor, and a <see cref="BsUiRosterEngine"/> driving the real Roster Editor as the roster
/// editor, both through the Java agent. One pair, shared by every scenario row in turn.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two apps at once, each in its own isolated home.</b> BattleScribe is two programs sharing nothing but the
/// data folder; a scenario holds both, so this fixture runs a Data Editor JVM and a Roster Editor JVM side by
/// side, each in the temp home its engine creates, and the runner carries the Data Editor's saved files to the
/// Roster Editor (<c>docs/adr/002-scenario-specs.md</c>, "Negative"). Neither app is launched here: each starts
/// on its engine's first setup, as in <see cref="BsGameDataUiFixture"/> and <see cref="BsRosterUiFixture"/>.
/// </para>
/// <para>
/// <b>Its own collection, like each of those two.</b> No collection keeps desktop-app lanes from running
/// together: the profiles do. <c>bs-ui-gamedata</c>, <c>bs-ui-roster</c> and <c>bs-ui-scenario</c> each claim
/// one lane, and CI's <c>thorough-ui-bs</c> runs them as separate matrix legs.
/// </para>
/// <para>
/// <b>Reuse is not a knob</b>: whether each JVM survives between specs is <see cref="ConcurrencyPlan.ReuseGameData"/>
/// and <see cref="ConcurrencyPlan.ReuseRoster"/>, the decisions both single-app fixtures take. Every
/// <c>newRoster</c>/<c>openRoster</c> restarts the Roster Editor anyway, because the data under it changed.
/// </para>
/// <para>
/// Skipped when the BattleScribe app, the JavaFX-capable JDK or the agent jar is missing (run
/// <c>setup.ps1</c>); resolved in the checkout these binaries were built in, as both single-app fixtures
/// resolve theirs.
/// </para>
/// </remarks>
public sealed class BsScenarioUiFixture : IAsyncLifetime
{
    public BsGameDataUiEngine? Data { get; private set; }

    public BsUiRosterEngine? Roster { get; private set; }

    /// <summary>Why the pair is not available on this machine, or <see langword="null"/> when it is.</summary>
    public string? Unavailable { get; private set; }

    public ValueTask InitializeAsync()
    {
        var dataOptions = BsGameDataUiEngine.FindOptions();
        BsUiOptions rosterOptions;
        try
        {
            rosterOptions = HostEngineFactory.ResolveBsUiOptions(RepoRoot.FromBinaries);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bs-scenario-ui-fixture] artifacts unavailable: {ex.Message}");
            Unavailable = $"BS UI artifacts not found (run setup.ps1): {ex.Message} — skipping BS scenario UI tests";
            return ValueTask.CompletedTask;
        }

        if (dataOptions is null)
        {
            Unavailable = "BS UI artifacts not found (run setup.ps1): the Data Editor, the JDK or the agent jar is missing "
                + "— skipping BS scenario UI tests";
            return ValueTask.CompletedTask;
        }

        // Local: two desktop apps on this machine (FixtureConcurrency states who every fixture talks to).
        var plan = FixtureConcurrency.Resolve("battlescribe-ui", LoadTarget.Local);

        using var span = FixtureTelemetry.StartInit(nameof(BsScenarioUiFixture));
        Data = new BsGameDataUiEngine(dataOptions) { KeepAlive = plan.ReuseGameData };
        Roster = new BsUiRosterEngine(rosterOptions) { KeepAlive = plan.ReuseRoster };
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Roster?.Dispose();
        }
        finally
        {
            Data?.Dispose();
            Roster = null;
            Data = null;
        }

        return ValueTask.CompletedTask;
    }
}

[CollectionDefinition("BsScenarioUi")]
public class BsScenarioUiCollection : ICollectionFixture<BsScenarioUiFixture>
{
}
