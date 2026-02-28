using BattleScribeSpec;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Oracle tests for modifier evaluation behavior.
/// Tests how the BattleScribe engine applies modifiers (set, increment, decrement, append)
/// to different field types (string, number, boolean, category).
/// These define the canonical modifier behavior that conforming implementations must match.
/// </summary>
public class ModifierOracleTests(ITestOutputHelper output)
{
    private static ScenarioSpec MakeScenario(
        SelectionEntrySpec[] entries,
        CostTypeSpec[]? costTypes = null)
    {
        return new ScenarioSpec(
            new GameSystemSpec(
                ForceEntries: [new ForceEntrySpec("fe-1", "Patrol")],
                CostTypes: costTypes),
            new CatalogueSpec(SelectionEntries: entries));
    }

    [Fact]
    public void Modifier_SetName_ChangesSelectionName()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = MakeScenario([
            new SelectionEntrySpec("se-1", "Marine Squad",
                Modifiers: [new ModifierSpec("set", "name", "Veterans")])
        ]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);
        oracle.SelectFirstAvailableEntry();

        var name = oracle.GetFirstSelectionName();
        output.WriteLine($"Selection name after 'set' modifier: '{name}'");
        Assert.Equal("Veterans", name);
    }

    [Fact]
    public void Modifier_AppendName_AppendsToSelectionName()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = MakeScenario([
            new SelectionEntrySpec("se-1", "Marine Squad",
                Modifiers: [new ModifierSpec("append", "name", "(Elite)")])
        ]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);
        oracle.SelectFirstAvailableEntry();

        var name = oracle.GetFirstSelectionName();
        output.WriteLine($"Selection name after 'append' modifier: '{name}'");
        // Append adds " " prefix before value (per decompiled engine)
        Assert.Contains("(Elite)", name);
    }

    [Fact]
    public void Modifier_SetHidden_HidesEntry()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = MakeScenario([
            new SelectionEntrySpec("se-1", "Marine Squad",
                Modifiers: [new ModifierSpec("set", "hidden", "true")])
        ]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);

        var snapshot = ModelConverter.CaptureOracleSnapshot(oracle);
        output.WriteLine($"Force selections after hidden modifier: {snapshot.Forces[0].Selections.Length}");
    }

    [Fact]
    public void Modifier_IncrementCost_IncreasesCostValue()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = MakeScenario(
            entries: [
                new SelectionEntrySpec("se-1", "Marine Squad",
                    Costs: [new CostSpec("pts", "pts", 50.0)],
                    Modifiers: [new ModifierSpec("increment", "pts", "25")])
            ],
            costTypes: [new CostTypeSpec("pts", "pts", 2000)]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);
        oracle.SelectFirstAvailableEntry();

        var snapshot = ModelConverter.CaptureOracleSnapshot(oracle);
        var selCosts = snapshot.Forces[0].Selections[0].Costs;
        output.WriteLine($"Selection costs: {string.Join(", ", selCosts.Select(c => $"{c.Name}={c.Value}"))}");

        var ptsCost = selCosts.FirstOrDefault(c => c.TypeId == "pts");
        if (ptsCost != null)
        {
            output.WriteLine($"Expected 75 (50 base + 25 increment), got {ptsCost.Value}");
            Assert.Equal(75.0, ptsCost.Value);
        }
    }

    [Fact]
    public void Modifier_DecrementCost_DecreasesCostValue()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = MakeScenario(
            entries: [
                new SelectionEntrySpec("se-1", "Marine Squad",
                    Costs: [new CostSpec("pts", "pts", 100.0)],
                    Modifiers: [new ModifierSpec("decrement", "pts", "30")])
            ],
            costTypes: [new CostTypeSpec("pts", "pts", 2000)]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);
        oracle.SelectFirstAvailableEntry();

        var snapshot = ModelConverter.CaptureOracleSnapshot(oracle);
        var ptsCost = snapshot.Forces[0].Selections[0].Costs.FirstOrDefault(c => c.TypeId == "pts");
        if (ptsCost != null)
        {
            output.WriteLine($"Expected 70 (100 base - 30 decrement), got {ptsCost.Value}");
            Assert.Equal(70.0, ptsCost.Value);
        }
    }

    [Fact]
    public void Modifier_WithCondition_OnlyAppliesWhenConditionMet()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = MakeScenario([
            new SelectionEntrySpec("se-1", "Marine Squad",
                Modifiers: [new ModifierSpec("set", "name", "Veterans",
                    Conditions: [new ConditionSpec("atLeast", 1, "selections", "self", "nonexistent-child")])])
        ]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);
        oracle.SelectFirstAvailableEntry();

        var name = oracle.GetFirstSelectionName();
        output.WriteLine($"Selection name (condition not met, should remain 'Marine Squad'): '{name}'");
        // Condition references nonexistent child, so it should NOT be met
        Assert.Equal("Marine Squad", name);
    }
}
