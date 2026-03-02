using BattleScribeSpec;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

public class MultiCatalogueProtocolTests(ITestOutputHelper output)
{
    [Fact]
    public void Direct_MultiCatalogue_Diagnose()
    {
        // Run the exact multi-catalogue scenario through OracleRosterEngine
        var gs = new GameSystemSpec("test-gs", "Test Game System",
            ForceEntries: [new ForceEntrySpec("fe-patrol", "Patrol")]);
        var catalogues = new CatalogueSpec[] {
            new("cat-a", "Faction A", "test-gs",
                SelectionEntries: [new SelectionEntrySpec("se-a1", "Alpha Unit", "unit")]),
            new("cat-b", "Faction B", "test-gs",
                SelectionEntries: [new SelectionEntrySpec("se-b1", "Beta Unit", "unit")])
        };

        using var engine = new OracleRosterEngine();
        var errors = engine.Setup(gs, catalogues);
        output.WriteLine($"Setup errors: {string.Join(", ", errors)}");
        Assert.Empty(errors);

        engine.AddForce(0, 0);
        output.WriteLine("AddForce(0,0) done");

        engine.AddForce(0, 1);
        output.WriteLine("AddForce(0,1) done");

        var state1 = engine.GetRosterState();
        output.WriteLine($"After AddForce: forceCount={state1.Forces.Count}");
        for (int i = 0; i < state1.Forces.Count; i++)
            output.WriteLine($"  Force[{i}]: name={state1.Forces[i].Name}");

        try
        {
            engine.SelectEntry(0, 0);
            output.WriteLine("SelectEntry(0,0) OK");
        }
        catch (Exception ex)
        {
            output.WriteLine($"SelectEntry(0,0) FAILED: {ex.Message}");
            throw;
        }

        try
        {
            engine.SelectEntry(1, 0);
            output.WriteLine("SelectEntry(1,0) OK");
        }
        catch (Exception ex)
        {
            output.WriteLine($"SelectEntry(1,0) FAILED: {ex.Message}");
            throw;
        }
    }
}
