using BattleScribeSpec;
using BattleScribeSpec.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

[Trait("Category", "Unit")]
public class MultiCatalogueProtocolTests(ITestOutputHelper output)
{
    [Fact]
    public void Direct_MultiCatalogue_Diagnose()
    {
        // Run the exact multi-catalogue scenario through OracleRosterEngine
        var gs = new ProtocolGameSystem
        {
            Id = "test-gs",
            Name = "Test Game System",
            ForceEntries = [new ProtocolForceEntry { Id = "fe-patrol", Name = "Patrol" }],
        };
        var catalogues = new ProtocolCatalogue[]
        {
            new() { Id = "cat-a", Name = "Faction A", GameSystemId = "test-gs",
                SelectionEntries = [new ProtocolSelectionEntry { Id = "se-a1", Name = "Alpha Unit", Type = "unit" }] },
            new() { Id = "cat-b", Name = "Faction B", GameSystemId = "test-gs",
                SelectionEntries = [new ProtocolSelectionEntry { Id = "se-b1", Name = "Beta Unit", Type = "unit" }] },
        };

        using var engine = new OracleRosterEngine();
        var errors = engine.Setup(gs, catalogues);
        output.WriteLine($"Setup errors: {string.Join(", ", errors)}");
        Assert.Empty(errors);

        var force0 = engine.AddForce("fe-patrol", "cat-a");
        output.WriteLine("AddForce(0,0) done");

        var force1 = engine.AddForce("fe-patrol", "cat-b");
        output.WriteLine("AddForce(0,1) done");

        var state1 = engine.GetRosterState();
        output.WriteLine($"After AddForce: forceCount={state1.Forces.Count}");
        for (int i = 0; i < state1.Forces.Count; i++)
            output.WriteLine($"  Force[{i}]: name={state1.Forces[i].Name}");

        try
        {
            engine.SelectEntry(force0.ForceId!, "se-a1");
            output.WriteLine("SelectEntry(0,0) OK");
        }
        catch (Exception ex)
        {
            output.WriteLine($"SelectEntry(0,0) FAILED: {ex.Message}");
            throw;
        }

        try
        {
            engine.SelectEntry(force1.ForceId!, "se-b1");
            output.WriteLine("SelectEntry(1,0) OK");
        }
        catch (Exception ex)
        {
            output.WriteLine($"SelectEntry(1,0) FAILED: {ex.Message}");
            throw;
        }
    }
}
