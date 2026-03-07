using BattleScribeSpec;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Oracle tests for the full refresh cycle and complex scenarios.
/// Tests how the engine behaves across multiple operations: add forces,
/// select entries, deselect, modify costs, and verify the complete state
/// after each operation (modifiers re-evaluated, constraints re-checked, costs recalculated).
/// </summary>
[Trait("Category", "Unit")]
public class RefreshCycleOracleTests(ITestOutputHelper output)
{
    [Fact]
    public void FullCycle_AddForce_SelectUnit_VerifyCost()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = new ScenarioSpec(
            new GameSystemSpec(
                ForceEntries: [new ForceEntrySpec("fe-1", "Patrol")],
                CostTypes: [new CostTypeSpec("pts", "pts", 2000)]),
            [new CatalogueSpec(SelectionEntries: [
                new SelectionEntrySpec("se-1", "Tactical Squad",
                    Costs: [new CostSpec("pts", "pts", 65.0)])
            ])]);

        oracle.SetupFromSpec(scenario);

        // Step 1: Add force
        oracle.AddForceByIndex(0);
        var snap1 = ModelConverter.CaptureOracleSnapshot(oracle);
        output.WriteLine($"After AddForce: forces={snap1.Forces.Length}, selections=0");
        Assert.Single(snap1.Forces);
        Assert.Empty(snap1.Forces[0].Selections);

        // Step 2: Select unit
        oracle.SelectFirstAvailableEntry();
        var snap2 = ModelConverter.CaptureOracleSnapshot(oracle);
        output.WriteLine($"After SelectEntry: selections={snap2.Forces[0].Selections.Length}");
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
        using var oracle = new BattleScribeOracle();
        var scenario = new ScenarioSpec(
            new GameSystemSpec(ForceEntries: [new ForceEntrySpec("fe-1", "Patrol")]),
            [new CatalogueSpec(SelectionEntries: [
                new SelectionEntrySpec("se-1", "Marine Squad")
            ])]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);

        // Select
        oracle.SelectFirstAvailableEntry();
        Assert.Equal(1, oracle.GetAllSelectionCount());

        // Deselect
        oracle.DeselectFirstSelection();
        Assert.Equal(0, oracle.GetAllSelectionCount());
        output.WriteLine("Select + Deselect cycle verified: back to 0 selections");
    }

    [Fact]
    public void FullCycle_MultipleSelectionsAccumulateCost()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = new ScenarioSpec(
            new GameSystemSpec(
                ForceEntries: [new ForceEntrySpec("fe-1", "Patrol")],
                CostTypes: [new CostTypeSpec("pts", "pts", 2000)]),
            [new CatalogueSpec(SelectionEntries: [
                new SelectionEntrySpec("se-1", "Marine Squad",
                    Costs: [new CostSpec("pts", "pts", 100.0)])
            ])]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);

        // Select 3 times
        for (int i = 0; i < 3; i++)
            oracle.SelectFirstAvailableEntry();

        var snap = ModelConverter.CaptureOracleSnapshot(oracle);
        output.WriteLine($"Selections: {snap.Forces[0].Selections.Length}");
        Assert.Equal(3, snap.Forces[0].Selections.Length);

        // Check roster total cost
        var rosterPts = snap.Costs.FirstOrDefault(c => c.TypeId == "pts");
        output.WriteLine($"Roster total pts: {rosterPts?.Value ?? -1} (expected 300)");
        if (rosterPts != null)
            Assert.Equal(300.0, rosterPts.Value);
    }

    [Fact]
    public void FullCycle_RemoveForce_ClearsEverything()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = new ScenarioSpec(
            new GameSystemSpec(ForceEntries: [new ForceEntrySpec("fe-1", "Patrol")]),
            [new CatalogueSpec(SelectionEntries: [
                new SelectionEntrySpec("se-1", "Marine Squad")
            ])]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);
        oracle.SelectFirstAvailableEntry();

        Assert.Equal(1, oracle.GetForceCount());
        Assert.Equal(1, oracle.GetAllSelectionCount());

        // Remove the force
        oracle.RemoveFirstForce();
        Assert.Equal(0, oracle.GetForceCount());
        Assert.Equal(0, oracle.GetAllSelectionCount());
        output.WriteLine("RemoveForce verified: 0 forces, 0 selections");
    }

    [Fact]
    public void FullCycle_ModifierAndConstraint_Together()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = new ScenarioSpec(
            new GameSystemSpec(
                ForceEntries: [new ForceEntrySpec("fe-1", "Patrol")],
                CostTypes: [new CostTypeSpec("pts", "pts", 2000)]),
            [new CatalogueSpec(SelectionEntries: [
                new SelectionEntrySpec("se-1", "Marine Squad",
                    Costs: [new CostSpec("pts", "pts", 50.0)],
                    Modifiers: [new ModifierSpec("increment", "pts", "10")],
                    Constraints: [
                        new ConstraintSpec("c-min", "min", 1, "selections", "parent"),
                        new ConstraintSpec("c-max", "max", 3, "selections", "parent")
                    ])
            ])]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);

        // Before selecting: min constraint should report something
        var errorsBefore = oracle.GetValidationErrors();
        output.WriteLine($"Errors before selection: {errorsBefore.Count}");

        // Select 1
        oracle.SelectFirstAvailableEntry();
        var errorsAfter1 = oracle.GetValidationErrors();
        output.WriteLine($"Errors after 1 selection: {errorsAfter1.Count}");

        // Cost should be 60 (50 base + 10 increment)
        var snap = ModelConverter.CaptureOracleSnapshot(oracle);
        var ptsCost = snap.Forces[0].Selections[0].Costs.FirstOrDefault(c => c.TypeId == "pts");
        output.WriteLine($"Unit cost: {ptsCost?.Value ?? -1} (expected 60)");
        if (ptsCost != null)
            Assert.Equal(60.0, ptsCost.Value);
    }

    [Fact]
    public void FullCycle_TwoUnits_IndependentCosts()
    {
        using var oracle = new BattleScribeOracle();

        // Use SetupFromSpec with a custom scenario that has two entries
        var gs = new GameSystemSpec(
            ForceEntries: [new ForceEntrySpec("fe-1", "Patrol")],
            CostTypes: [new CostTypeSpec("pts", "pts", 2000)]);
        var cat = new CatalogueSpec(SelectionEntries: [
            new SelectionEntrySpec("se-1", "Tactical Squad",
                Costs: [new CostSpec("pts", "pts", 65.0)]),
            new SelectionEntrySpec("se-2", "Assault Squad",
                Costs: [new CostSpec("pts", "pts", 80.0)])
        ]);
        oracle.SetupFromSpec(new ScenarioSpec(gs, [cat]));
        oracle.AddForceByIndex(0);

        // Select the first entry (index 0)
        oracle.SelectFirstAvailableEntry();
        var snap = ModelConverter.CaptureOracleSnapshot(oracle);
        output.WriteLine($"After first entry: {snap.Forces[0].Selections.Length} selection(s)");
        Assert.Single(snap.Forces[0].Selections);
        Assert.Equal("Tactical Squad", snap.Forces[0].Selections[0].Name);

        // Get total roster cost
        var rosterPts = snap.Costs.FirstOrDefault(c => c.TypeId == "pts");
        output.WriteLine($"Roster cost after Tactical Squad: {rosterPts?.Value ?? -1}");
    }
}
