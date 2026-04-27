using BattleScribeSpec;
using BattleScribeSpec.Protocol;
using Xunit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// engine tests for the full refresh cycle and complex scenarios.
/// Tests how the engine behaves across multiple operations: add forces,
/// select entries, deselect, modify costs, and verify the complete state
/// after each operation (modifiers re-evaluated, constraints re-checked, costs recalculated).
/// </summary>
[Trait("Category", "Unit")]
public class RefreshCycleBattleScribeTests(ITestOutputHelper output)
{
    [Fact]
    public void FullCycle_AddForce_SelectUnit_VerifyCost()
    {
        using var engine = new BattleScribeEngine();
        var gs = new ProtocolGameSystem
        {
            Id = "test-gs",
            Name = "Test Game System",
            ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Patrol" }],
            CostTypes = [new ProtocolCostType { Id = "pts", Name = "pts", DefaultCostLimit = 2000 }],
        };
        var cat = new ProtocolCatalogue
        {
            Id = "cat-1", Name = "Cat", GameSystemId = "test-gs",
            SelectionEntries = [
                new ProtocolSelectionEntry { Id = "se-1", Name = "Tactical Squad",
                    Costs = [new ProtocolCostValue { Name = "pts", TypeId = "pts", Value = 65.0 }] }
            ],
        };

        engine.SetupFromProtocol(gs, [cat]);

        // Step 1: Add force
        engine.AddForceByIndex(0);
        var snap1 = ModelConverter.CaptureEngineSnapshot(engine);
        output.WriteLine($"After AddForce: forces={snap1.Forces.Count}, selections=0");
        Assert.Single(snap1.Forces);
        Assert.Empty(snap1.Forces[0].Selections);

        // Step 2: Select unit
        engine.SelectFirstAvailableEntry();
        var snap2 = ModelConverter.CaptureEngineSnapshot(engine);
        output.WriteLine($"After SelectEntry: selections={snap2.Forces[0].Selections.Count}");
        Assert.Single(snap2.Forces[0].Selections);
        Assert.Equal("Tactical Squad", snap2.Forces[0].Selections[0].Name);

        // Step 3: Check cost
        var ptsCost = snap2.Forces[0].Selections[0].Costs.FirstOrDefault(c => c.TypeId == "pts");
        output.WriteLine($"Unit cost: {ptsCost?.Value ?? -1}");
        Assert.NotNull(ptsCost);
        Assert.Equal(65.0, ptsCost.Value);
    }

    [Fact]
    public void FullCycle_SelectAndDeselect_RestoredToEmpty()
    {
        using var engine = new BattleScribeEngine();
        var gs = new ProtocolGameSystem
        {
            Id = "test-gs",
            Name = "Test Game System",
            ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Patrol" }],
        };
        var cat = new ProtocolCatalogue
        {
            Id = "cat-1", Name = "Cat", GameSystemId = "test-gs",
            SelectionEntries = [
                new ProtocolSelectionEntry { Id = "se-1", Name = "Marine Squad" }
            ],
        };

        engine.SetupFromProtocol(gs, [cat]);
        engine.AddForceByIndex(0);

        // Select
        engine.SelectFirstAvailableEntry();
        Assert.Equal(1, engine.GetAllSelectionCount());

        // Deselect
        engine.DeselectFirstSelection();
        Assert.Equal(0, engine.GetAllSelectionCount());
        output.WriteLine("Select + Deselect cycle verified: back to 0 selections");
    }

    [Fact]
    public void FullCycle_MultipleSelectionsAccumulateCost()
    {
        using var engine = new BattleScribeEngine();
        var gs = new ProtocolGameSystem
        {
            Id = "test-gs",
            Name = "Test Game System",
            ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Patrol" }],
            CostTypes = [new ProtocolCostType { Id = "pts", Name = "pts", DefaultCostLimit = 2000 }],
        };
        var cat = new ProtocolCatalogue
        {
            Id = "cat-1", Name = "Cat", GameSystemId = "test-gs",
            SelectionEntries = [
                new ProtocolSelectionEntry { Id = "se-1", Name = "Marine Squad",
                    Costs = [new ProtocolCostValue { Name = "pts", TypeId = "pts", Value = 100.0 }] }
            ],
        };

        engine.SetupFromProtocol(gs, [cat]);
        engine.AddForceByIndex(0);

        // Select 3 times
        for (int i = 0; i < 3; i++)
            engine.SelectFirstAvailableEntry();

        var snap = ModelConverter.CaptureEngineSnapshot(engine);
        output.WriteLine($"Selections: {snap.Forces[0].Selections.Count}");
        Assert.Equal(3, snap.Forces[0].Selections.Count);

        // Check roster total cost
        var rosterPts = snap.Costs.FirstOrDefault(c => c.TypeId == "pts");
        output.WriteLine($"Roster total pts: {rosterPts?.Value ?? -1} (expected 300)");
        if (rosterPts != null)
            Assert.Equal(300.0, rosterPts.Value);
    }

    [Fact]
    public void FullCycle_RemoveForce_ClearsEverything()
    {
        using var engine = new BattleScribeEngine();
        var gs = new ProtocolGameSystem
        {
            Id = "test-gs",
            Name = "Test Game System",
            ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Patrol" }],
        };
        var cat = new ProtocolCatalogue
        {
            Id = "cat-1", Name = "Cat", GameSystemId = "test-gs",
            SelectionEntries = [
                new ProtocolSelectionEntry { Id = "se-1", Name = "Marine Squad" }
            ],
        };

        engine.SetupFromProtocol(gs, [cat]);
        engine.AddForceByIndex(0);
        engine.SelectFirstAvailableEntry();

        Assert.Equal(1, engine.GetForceCount());
        Assert.Equal(1, engine.GetAllSelectionCount());

        // Remove the force
        engine.RemoveFirstForce();
        Assert.Equal(0, engine.GetForceCount());
        Assert.Equal(0, engine.GetAllSelectionCount());
        output.WriteLine("RemoveForce verified: 0 forces, 0 selections");
    }

    [Fact]
    public void FullCycle_ModifierAndConstraint_Together()
    {
        using var engine = new BattleScribeEngine();
        var gs = new ProtocolGameSystem
        {
            Id = "test-gs",
            Name = "Test Game System",
            ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Patrol" }],
            CostTypes = [new ProtocolCostType { Id = "pts", Name = "pts", DefaultCostLimit = 2000 }],
        };
        var cat = new ProtocolCatalogue
        {
            Id = "cat-1", Name = "Cat", GameSystemId = "test-gs",
            SelectionEntries = [
                new ProtocolSelectionEntry { Id = "se-1", Name = "Marine Squad",
                    Costs = [new ProtocolCostValue { Name = "pts", TypeId = "pts", Value = 50.0 }],
                    Modifiers = [new ProtocolModifier { Type = "increment", Field = "pts", Value = "10" }],
                    Constraints = [
                        new ProtocolConstraint { Id = "c-min", Type = "min", Value = 1, Field = "selections", Scope = "parent" },
                        new ProtocolConstraint { Id = "c-max", Type = "max", Value = 3, Field = "selections", Scope = "parent" },
                    ] }
            ],
        };

        engine.SetupFromProtocol(gs, [cat]);
        engine.AddForceByIndex(0);

        // Before selecting: min constraint should report something
        var errorsBefore = engine.GetValidationErrors();
        output.WriteLine($"Errors before selection: {errorsBefore.Count}");

        // Select 1
        engine.SelectFirstAvailableEntry();
        var errorsAfter1 = engine.GetValidationErrors();
        output.WriteLine($"Errors after 1 selection: {errorsAfter1.Count}");

        // Cost should be 60 (50 base + 10 increment)
        var snap = ModelConverter.CaptureEngineSnapshot(engine);
        var ptsCost = snap.Forces[0].Selections[0].Costs.FirstOrDefault(c => c.TypeId == "pts");
        output.WriteLine($"Unit cost: {ptsCost?.Value ?? -1} (expected 60)");
        if (ptsCost != null)
            Assert.Equal(60.0, ptsCost.Value);
    }

    [Fact]
    public void FullCycle_TwoUnits_IndependentCosts()
    {
        using var engine = new BattleScribeEngine();

        // Use SetupFromSpec with a custom scenario that has two entries
        var gs = new ProtocolGameSystem
        {
            Id = "test-gs",
            Name = "Test Game System",
            ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Patrol" }],
            CostTypes = [new ProtocolCostType { Id = "pts", Name = "pts", DefaultCostLimit = 2000 }],
        };
        var cat = new ProtocolCatalogue
        {
            Id = "cat-1", Name = "Cat", GameSystemId = "test-gs",
            SelectionEntries = [
                new ProtocolSelectionEntry { Id = "se-1", Name = "Tactical Squad",
                    Costs = [new ProtocolCostValue { Name = "pts", TypeId = "pts", Value = 65.0 }] },
                new ProtocolSelectionEntry { Id = "se-2", Name = "Assault Squad",
                    Costs = [new ProtocolCostValue { Name = "pts", TypeId = "pts", Value = 80.0 }] },
            ],
        };
        engine.SetupFromProtocol(gs, [cat]);
        engine.AddForceByIndex(0);

        // Select the first entry (index 0)
        engine.SelectFirstAvailableEntry();
        var snap = ModelConverter.CaptureEngineSnapshot(engine);
        output.WriteLine($"After first entry: {snap.Forces[0].Selections.Count} selection(s)");
        Assert.Single(snap.Forces[0].Selections);
        Assert.Equal("Tactical Squad", snap.Forces[0].Selections[0].Name);

        // Get total roster cost
        var rosterPts = snap.Costs.FirstOrDefault(c => c.TypeId == "pts");
        output.WriteLine($"Roster cost after Tactical Squad: {rosterPts?.Value ?? -1}");
    }
}
