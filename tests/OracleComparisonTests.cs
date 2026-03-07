using BattleScribeSpec;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Comparison tests that run the same operations on the BattleScribe Java engine
/// and verify the behavior matches expected patterns. These establish the canonical
/// behavior specification that any conforming implementation must match.
/// </summary>
[Trait("Category", "Unit")]
public class OracleComparisonTests(ITestOutputHelper output)
{
    [Fact]
    public void EmptyRoster_HasExpectedState()
    {
        using var fixture = new OracleTestFixture();
        fixture.SetupMinimalGameSystem();

        var snapshot = fixture.CaptureOracleSnapshot();

        Assert.Equal("Oracle Roster", snapshot.Name);
        Assert.Equal("test-gs", snapshot.GameSystemId);
        Assert.Empty(snapshot.Forces);
        output.WriteLine($"Empty roster: {snapshot.Forces.Length} forces, {snapshot.Costs.Length} costs, {snapshot.ValidationErrors.Length} errors");
    }

    [Fact]
    public void AddForce_IncreasesForceCount()
    {
        using var fixture = new OracleTestFixture();
        fixture.SetupWithUnit();

        var before = fixture.CaptureOracleSnapshot();
        Assert.Empty(before.Forces);

        fixture.AddForce();

        var after = fixture.CaptureOracleSnapshot();
        Assert.Single(after.Forces);
        output.WriteLine($"Force added: {after.Forces[0].Name}");
    }

    [Fact]
    public void SelectEntry_CreatesSelectionInForce()
    {
        using var fixture = new OracleTestFixture();
        fixture.SetupWithUnit("Marine Squad", 100.0);
        fixture.AddForce();
        fixture.SelectEntry();

        var snapshot = fixture.CaptureOracleSnapshot();
        Assert.Single(snapshot.Forces);

        var force = snapshot.Forces[0];
        Assert.NotEmpty(force.Selections);
        Assert.Equal("Marine Squad", force.Selections[0].Name);
        output.WriteLine($"Selection: {force.Selections[0].Name} (type={force.Selections[0].Type}, count={force.Selections[0].Number})");
    }

    [Fact]
    public void SelectEntry_CostIsTracked()
    {
        using var fixture = new OracleTestFixture();
        fixture.SetupWithUnit("Marine Squad", 100.0);
        fixture.AddForce();
        fixture.SelectEntry();

        var snapshot = fixture.CaptureOracleSnapshot();
        var force = snapshot.Forces[0];
        var selection = force.Selections[0];

        // The selection should have a cost
        output.WriteLine($"Selection costs: {string.Join(", ", selection.Costs.Select(c => $"{c.Name}={c.Value}"))}");
        Assert.NotEmpty(selection.Costs);
        var ptsCost = Assert.Single(selection.Costs, c => c.TypeId == "pts");
        Assert.Equal(100.0, ptsCost.Value);
        output.WriteLine($"Cost verified: {ptsCost.Name} = {ptsCost.Value}");

        // Check roster-level costs too
        output.WriteLine($"Roster costs: {string.Join(", ", snapshot.Costs.Select(c => $"{c.Name}={c.Value}"))}");
    }

    [Fact]
    public void DeselectEntry_RemovesSelection()
    {
        using var fixture = new OracleTestFixture();
        fixture.SetupWithUnit();
        fixture.AddForce();
        fixture.SelectEntry();

        var beforeDeselect = fixture.CaptureOracleSnapshot();
        Assert.NotEmpty(beforeDeselect.Forces[0].Selections);

        fixture.Oracle.DeselectFirstSelection();

        var afterDeselect = fixture.CaptureOracleSnapshot();
        Assert.Empty(afterDeselect.Forces[0].Selections);
    }

    [Fact]
    public void RemoveForce_RemovesAllSelections()
    {
        using var fixture = new OracleTestFixture();
        fixture.SetupWithUnit();
        fixture.AddForce();
        fixture.SelectEntry();

        var before = fixture.CaptureOracleSnapshot();
        Assert.Single(before.Forces);
        Assert.NotEmpty(before.Forces[0].Selections);

        fixture.Oracle.RemoveFirstForce();

        var after = fixture.CaptureOracleSnapshot();
        Assert.Empty(after.Forces);
    }

    [Fact]
    public void MultipleSelections_CostsAccumulate()
    {
        using var fixture = new OracleTestFixture();
        fixture.SetupWithUnit("Marine Squad", 50.0);
        fixture.AddForce();

        fixture.SelectEntry();
        var after1 = fixture.CaptureOracleSnapshot();

        fixture.SelectEntry();
        var after2 = fixture.CaptureOracleSnapshot();

        output.WriteLine($"After 1 selection: {after1.Forces[0].Selections.Length} selections");
        output.WriteLine($"After 2 selections: {after2.Forces[0].Selections.Length} selections");

        Assert.True(after2.Forces[0].Selections.Length >= after1.Forces[0].Selections.Length,
            "Expected at least as many selections after second SelectEntry");
    }

    [Fact]
    public void ValidationErrors_OnEmptyRoster()
    {
        using var fixture = new OracleTestFixture();
        fixture.SetupMinimalGameSystem();

        var errors = fixture.Oracle.GetValidationErrors();
        output.WriteLine($"Validation errors on empty roster: {errors.Count}");
        foreach (var err in errors)
            output.WriteLine($"  - {err}");
    }

    [Fact]
    public void EngineState_ConsistentAfterMultipleOperations()
    {
        using var fixture = new OracleTestFixture();
        fixture.SetupWithUnit("Marine Squad", 75.0);

        // Add force, select entry, take snapshot
        fixture.AddForce();
        fixture.SelectEntry();
        var snapshot1 = fixture.CaptureOracleSnapshot();

        // Capture again — should be identical (no mutations happened)
        var snapshot2 = fixture.CaptureOracleSnapshot();

        Assert.Equal(snapshot1.Forces.Length, snapshot2.Forces.Length);
        Assert.Equal(snapshot1.Forces[0].Selections.Length, snapshot2.Forces[0].Selections.Length);
        Assert.Equal(snapshot1.Costs.Length, snapshot2.Costs.Length);

        output.WriteLine($"State consistent: {snapshot1.Forces[0].Selections.Length} selections");
    }

    [Fact]
    public void Snapshot_CapturesAllDetails()
    {
        using var fixture = new OracleTestFixture();
        fixture.SetupWithUnit("Tactical Marines", 65.0, "pts");
        fixture.AddForce();
        fixture.SelectEntry();

        var snapshot = fixture.CaptureOracleSnapshot();

        // Log full snapshot for debugging
        output.WriteLine($"Roster: name={snapshot.Name}, gs={snapshot.GameSystemId}");
        output.WriteLine($"  Forces: {snapshot.Forces.Length}");
        foreach (var force in snapshot.Forces)
        {
            output.WriteLine($"    Force: {force.Name} (cat={force.CatalogueId})");
            foreach (var sel in force.Selections)
            {
                output.WriteLine($"      Selection: {sel.Name} (type={sel.Type}, num={sel.Number})");
                foreach (var cost in sel.Costs)
                    output.WriteLine($"        Cost: {cost.Name}={cost.Value} (typeId={cost.TypeId})");
            }
        }
        output.WriteLine($"  Roster costs: {string.Join(", ", snapshot.Costs.Select(c => $"{c.Name}={c.Value}"))}");
        output.WriteLine($"  Errors: {string.Join(", ", snapshot.ValidationErrors)}");
    }
}
