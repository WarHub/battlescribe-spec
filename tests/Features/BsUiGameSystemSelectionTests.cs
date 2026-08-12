using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.EngineHost;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests;

/// <summary>
/// The New Roster dialog must build the roster on the game system the spec asked for, and not on one
/// whose id merely CONTAINS it.
/// </summary>
/// <remarks>
/// Builds its own engine rather than taking the collection's <see cref="BsRosterUiFixture"/>: the
/// decoy game system stays in the app's data directory for the life of that JVM, so staging it into
/// the shared instance would hand every later conformance spec an extra system to match against.
/// <para>
/// <c>Engine=BsRosterUi</c> is what keeps this out of <c>pre-push</c>, which does not launch the
/// desktop app. <c>Shard</c> has to be set by hand: CI's <c>thorough-ui-bs</c> matrix filters
/// <c>Engine=…&amp;Shard=…</c>, so a test carrying no <c>Shard</c> trait runs in neither job.
/// </para>
/// </remarks>
[Collection("BsRosterUi")]
[Trait("Engine", "BsRosterUi")]
[Trait("Shard", "0")]
public sealed class BsUiGameSystemSelectionTests
{
    private const string TargetGameSystemId = "collide-target";

    /// <summary>Staged first, so it is in the combo when the target's roster is created.</summary>
    private const string DecoyGameSystemId = "aa-collide-target";

    private const string CatalogueId = "cat-1";

    private const string ForceEntryId = "fe-patrol";

    [Fact]
    public void CreateRoster_ChoosesTheGameSystemById_NotOneWhoseNameContainsIt()
    {
        Assert.Contains(TargetGameSystemId, DecoyGameSystemId, StringComparison.Ordinal);
        Assert.True(
            string.CompareOrdinal(DecoyGameSystemId, TargetGameSystemId) < 0,
            $"'{DecoyGameSystemId}' must sort before '{TargetGameSystemId}', or a first-hit match " +
            "would find the target first and this test would prove nothing.");

        if (Environment.GetEnvironmentVariable("BS_UI_SKIP") == "true")
        {
            Assert.Skip("BS_UI_SKIP=true — skipping BS Roster UI test");
            return;
        }

        BsUiOptions options;
        try
        {
            options = HostEngineFactory.ResolveBsUiOptions();
        }
        catch (Exception ex)
        {
            Assert.Skip($"BS UI artifacts not found (run setup.ps1): {ex.Message}");
            return;
        }

        // KeepAlive is the condition under test, not a speed-up: a cold start per Setup would drop
        // the decoy from the data directory, leaving nothing for the combo to confuse.
        using var engine = new BsUiRosterEngine(options) { KeepAlive = true };

        Assert.Empty(engine.Setup(GameSystem(DecoyGameSystemId), [Catalogue(DecoyGameSystemId)]));
        engine.AddForce(ForceEntryId, CatalogueId);

        Assert.Empty(engine.Setup(GameSystem(TargetGameSystemId), [Catalogue(TargetGameSystemId)]));
        engine.AddForce(ForceEntryId, CatalogueId);

        var state = engine.GetRosterState();

        // Not redundant: a failed read falls back to an empty state carrying the driver's own
        // game system id, which would satisfy the assertion below without the app agreeing.
        Assert.NotEmpty(state.Forces);
        Assert.Equal(TargetGameSystemId, state.GameSystemId);
    }

    /// <summary>
    /// Id and name are both the spec id, as <c>SpecLoader.ApplySetupDefaults</c> leaves every spec.
    /// </summary>
    private static ProtocolGameSystem GameSystem(string id) => new()
    {
        Id = id,
        Name = id,
        ForceEntries = [new ProtocolForceEntry { Id = ForceEntryId, Name = "Patrol" }],
    };

    private static ProtocolCatalogue Catalogue(string gameSystemId) => new()
    {
        Id = CatalogueId,
        Name = gameSystemId,
        GameSystemId = gameSystemId,
        SelectionEntries = [new ProtocolSelectionEntry { Id = "se-1", Name = "Marine" }],
    };
}
