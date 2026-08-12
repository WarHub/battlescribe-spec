using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.EngineHost;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.XmlGen;

namespace BattleScribeSpec.Tests;

/// <summary>
/// The New Roster dialog must build the roster on the game system the spec asked for, and not on one
/// whose name merely CONTAINS it.
/// </summary>
/// <remarks>
/// The decoy is staged by hand, through a stager this test owns. An engine retires the game system
/// it staged last, so arranging the decoy through a first <c>Setup</c> on the engine would have the
/// second <c>Setup</c> delete it again, leaving a combo with one entry and a test that proves
/// nothing.
/// <para>
/// Its own home, never the collection's <see cref="BsRosterUiFixture"/>: nothing retires the decoy,
/// so a shared home would hand every later conformance spec an extra system to match against.
/// </para>
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

    /// <summary>
    /// Staged first, so it is in the combo when the target's roster is created. Its id contains the
    /// target's and sorts ahead of it — the two conditions a first-hit substring match needs to pick
    /// the wrong one.
    /// </summary>
    private const string DecoyGameSystemId = "aa-collide-target";

    private const string CatalogueId = "cat-1";

    private const string ForceEntryId = "fe-patrol";

    [Fact]
    public async Task CreateRoster_ChoosesTheGameSystemById_NotOneWhoseNameContainsIt()
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

        var home = Path.Combine(Path.GetTempPath(), $"bsspec-bs-ui-collide-{Guid.NewGuid():N}");
        try
        {
            // A stager of this test's own: the engine retires what IT staged last, so a decoy it
            // never staged is one it will never take away.
            await new BsUiDataStaging().StageDataFilesAsync(
                Path.Combine(home, "BattleScribe", "data"),
                DecoyGameSystemId,
                DecoyFiles());

            using var engine = new BsUiRosterEngine(options with { IsolatedHomePath = home });

            Assert.Empty(engine.Setup(GameSystem(TargetGameSystemId), [Catalogue(TargetGameSystemId)]));
            engine.AddForce(ForceEntryId, CatalogueId);

            var state = engine.GetRosterState();

            // Not redundant: a failed read falls back to an empty state carrying the driver's own
            // game system id, which would satisfy the assertion below without the app agreeing.
            Assert.NotEmpty(state.Forces);
            Assert.Equal(TargetGameSystemId, state.GameSystemId);
        }
        finally
        {
            // The app deletes an isolated home only when it made one, and this one was handed in.
            if (Directory.Exists(home))
            {
                try
                {
                    Directory.Delete(home, recursive: true);
                }
                catch (Exception)
                {
                    // A JVM still shutting down holds its data files open and Windows refuses the
                    // delete. Not worth failing a passing test over a leaked temp directory.
                }
            }
        }
    }

    /// <summary>
    /// Built the way the engine builds a spec's, so BattleScribe indexes the decoy — an unindexed
    /// one never reaches the combo and the test passes for the wrong reason.
    /// </summary>
    private static IReadOnlyList<(string FileName, string Content)> DecoyFiles()
    {
        var gameSystem = GameSystem(DecoyGameSystemId);
        var files = new List<(string FileName, string Content)>
        {
            ($"{DecoyGameSystemId}.gst", CatXmlGenerator.GenerateGameSystemXml(gameSystem)),
        };

        foreach (var (fileName, xml) in CatXmlGenerator.GenerateAllCatalogueXml(
            gameSystem, [Catalogue(DecoyGameSystemId)]))
        {
            files.Add((fileName, xml));
        }

        return files;
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
