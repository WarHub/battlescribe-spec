using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests;

/// <summary>
/// engine tests for modifier evaluation behavior.
/// Tests how the BattleScribe engine applies modifiers (set, increment, decrement, append)
/// to different field types (string, number, boolean, category).
/// These define the canonical modifier behavior that conforming implementations must match.
/// </summary>
[Trait("Category", "Unit")]
public class ModifierBattleScribeTests(ITestOutputHelper output)
{
    private static (ProtocolGameSystem gs, ProtocolCatalogue[] cats) MakeScenario(
        ProtocolSelectionEntry[] entries,
        List<ProtocolCostType>? costTypes = null)
    {
        return (
            new ProtocolGameSystem
            {
                Id = "test-gs",
                Name = "Test Game System",
                ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Patrol" }],
                CostTypes = costTypes,
            },
            [new ProtocolCatalogue { Id = "cat-1", Name = "Cat", GameSystemId = "test-gs", SelectionEntries = [.. entries] }]);
    }

    [Fact]
    public void Modifier_SetName_ChangesSelectionName()
    {
        using var engine = new BattleScribeEngine();
        var (gs, cats) = MakeScenario([
            new ProtocolSelectionEntry { Id = "se-1", Name = "Marine Squad",
                Modifiers = [new ProtocolModifier { Type = "set", Field = "name", Value = "Veterans" }] }
        ]);

        engine.SetupFromProtocol(gs, cats);
        engine.AddForceByIndex(0);
        engine.SelectFirstAvailableEntry();

        var name = engine.GetFirstSelectionName();
        output.WriteLine($"Selection name after 'set' modifier: '{name}'");
        Assert.Equal("Veterans", name);
    }

    [Fact]
    public void Modifier_AppendName_AppendsToSelectionName()
    {
        using var engine = new BattleScribeEngine();
        var (gs, cats) = MakeScenario([
            new ProtocolSelectionEntry { Id = "se-1", Name = "Marine Squad",
                Modifiers = [new ProtocolModifier { Type = "append", Field = "name", Value = "(Elite)" }] }
        ]);

        engine.SetupFromProtocol(gs, cats);
        engine.AddForceByIndex(0);
        engine.SelectFirstAvailableEntry();

        var name = engine.GetFirstSelectionName();
        output.WriteLine($"Selection name after 'append' modifier: '{name}'");
        // Append adds " " prefix before value (per decompiled engine)
        Assert.Contains("(Elite)", name);
    }

    [Fact]
    public void Modifier_SetHidden_HidesEntry()
    {
        using var engine = new BattleScribeEngine();
        var (gs, cats) = MakeScenario([
            new ProtocolSelectionEntry { Id = "se-1", Name = "Marine Squad",
                Modifiers = [new ProtocolModifier { Type = "set", Field = "hidden", Value = "true" }] }
        ]);

        engine.SetupFromProtocol(gs, cats);
        engine.AddForceByIndex(0);

        var snapshot = ModelConverter.CaptureEngineSnapshot(engine);
        output.WriteLine($"Force selections after hidden modifier: {snapshot.Forces[0].Selections.Count}");
    }

    [Fact]
    public void Modifier_IncrementCost_IncreasesCostValue()
    {
        using var engine = new BattleScribeEngine();
        var (gs, cats) = MakeScenario(
            entries: [
                new ProtocolSelectionEntry { Id = "se-1", Name = "Marine Squad",
                    Costs = [new ProtocolCostValue { Name = "pts", TypeId = "pts", Value = 50.0m }],
                    Modifiers = [new ProtocolModifier { Type = "increment", Field = "pts", Value = "25" }] }
            ],
            costTypes: [new ProtocolCostType { Id = "pts", Name = "pts", DefaultCostLimit = 2000 }]);

        engine.SetupFromProtocol(gs, cats);
        engine.AddForceByIndex(0);
        engine.SelectFirstAvailableEntry();

        var snapshot = ModelConverter.CaptureEngineSnapshot(engine);
        var selCosts = snapshot.Forces[0].Selections[0].Costs;
        output.WriteLine($"Selection costs: {string.Join(", ", selCosts.Select(c => $"{c.Name}={c.Value}"))}");

        var ptsCost = selCosts.FirstOrDefault(c => c.TypeId == "pts");
        Assert.NotNull(ptsCost);
        output.WriteLine($"Expected 75 (50 base + 25 increment), got {ptsCost.Value}");
        Assert.Equal(75.0m, ptsCost.Value);
    }

    [Fact]
    public void Modifier_DecrementCost_DecreasesCostValue()
    {
        using var engine = new BattleScribeEngine();
        var (gs, cats) = MakeScenario(
            entries: [
                new ProtocolSelectionEntry { Id = "se-1", Name = "Marine Squad",
                    Costs = [new ProtocolCostValue { Name = "pts", TypeId = "pts", Value = 100.0m }],
                    Modifiers = [new ProtocolModifier { Type = "decrement", Field = "pts", Value = "30" }] }
            ],
            costTypes: [new ProtocolCostType { Id = "pts", Name = "pts", DefaultCostLimit = 2000 }]);

        engine.SetupFromProtocol(gs, cats);
        engine.AddForceByIndex(0);
        engine.SelectFirstAvailableEntry();

        var snapshot = ModelConverter.CaptureEngineSnapshot(engine);
        var ptsCost = snapshot.Forces[0].Selections[0].Costs.FirstOrDefault(c => c.TypeId == "pts");
        Assert.NotNull(ptsCost);
        output.WriteLine($"Expected 70 (100 base - 30 decrement), got {ptsCost.Value}");
        Assert.Equal(70.0m, ptsCost.Value);
    }

    [Fact]
    public void Modifier_SetCharacteristicValue_ChangesProfileCharacteristic()
    {
        using var engine = new BattleScribeRosterEngine();
        var gs = new ProtocolGameSystem
        {
            Id = "test-gs",
            Name = "Test Game System",
            ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Patrol" }],
            ProfileTypes = [new ProtocolProfileType { Id = "stat-type", Name = "Unit Stats",
                CharacteristicTypes = [new ProtocolCharacteristicType { Id = "char-wounds", Name = "Wounds" }] }],
        };
        var cat = new ProtocolCatalogue
        {
            Id = "cat-1",
            Name = "Cat",
            GameSystemId = "test-gs",
            SelectionEntries = [
                new ProtocolSelectionEntry { Id = "se-1", Name = "Marine",
                    Profiles = [new ProtocolProfile { Id = "prof-1", Name = "Marine Stats", TypeId = "stat-type", TypeName = "Unit Stats",
                        Characteristics = [new ProtocolCharacteristic { Name = "Wounds", TypeId = "char-wounds", Value = "2" }],
                        Modifiers = [new ProtocolModifier { Type = "set", Field = "char-wounds", Value = "3" }] }] }
            ],
        };
        engine.Setup(gs, [cat]);
        var addForceOut = engine.AddForce("fe-1", "cat-1");
        engine.SelectEntry(addForceOut.ForceId!, "se-1");
        var state = engine.GetRosterState();

        var sel = state.Forces[0].Selections[0];
        Assert.NotNull(sel.Profiles);
        Assert.Single(sel.Profiles);
        Assert.Equal("Marine Stats", sel.Profiles[0].Name);
        Assert.Equal("3", sel.Profiles[0].Characteristics[0].Value);
    }

    [Fact]
    public void Modifier_RuleDescription_ChangesRuleOnSelection()
    {
        using var engine = new BattleScribeRosterEngine();
        var gs = new ProtocolGameSystem
        {
            Id = "test-gs",
            Name = "Test Game System",
            ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Patrol" }],
        };
        var cat = new ProtocolCatalogue
        {
            Id = "cat-1",
            Name = "Cat",
            GameSystemId = "test-gs",
            SelectionEntries = [
                new ProtocolSelectionEntry { Id = "se-1", Name = "Marine",
                    Rules = [new ProtocolRule { Id = "rule-1", Name = "Combat Doctrine", Description = "Original description",
                        Modifiers = [new ProtocolModifier { Type = "set", Field = "description", Value = "Modified description" }] }] }
            ],
        };
        engine.Setup(gs, [cat]);
        var addForceOut2 = engine.AddForce("fe-1", "cat-1");
        engine.SelectEntry(addForceOut2.ForceId!, "se-1");
        var state = engine.GetRosterState();

        var sel = state.Forces[0].Selections[0];
        Assert.NotNull(sel.Rules);
        Assert.Single(sel.Rules);
        Assert.Equal("Modified description", sel.Rules[0].Description);
    }

    [Fact]
    public void Modifier_WithCondition_OnlyAppliesWhenConditionMet()
    {
        using var engine = new BattleScribeEngine();
        var (gs, cats) = MakeScenario([
            new ProtocolSelectionEntry { Id = "se-1", Name = "Marine Squad",
                Modifiers = [new ProtocolModifier { Type = "set", Field = "name", Value = "Veterans",
                    Conditions = [new ProtocolCondition { Type = "atLeast", Value = 1, Field = "selections", Scope = "self", ChildId = "nonexistent-child" }] }] }
        ]);

        engine.SetupFromProtocol(gs, cats);
        engine.AddForceByIndex(0);
        engine.SelectFirstAvailableEntry();

        var name = engine.GetFirstSelectionName();
        output.WriteLine($"Selection name (condition not met, should remain 'Marine Squad'): '{name}'");
        // Condition references nonexistent child, so it should NOT be met
        Assert.Equal("Marine Squad", name);
    }
}
