using BattleScribeSpec;
using WarHub.ArmouryModel.Source;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Category 5: Modifier Evaluation
/// Tests that modifiers are correctly defined with appropriate types,
/// fields, values, and conditions.
/// </summary>
public class ModifierEvaluationTests
{
    private readonly CatalogueNode _catalogue = TestDataFactory.CreateModifierTestCatalogue();

    [Fact]
    public void AppendModifier_HasCorrectTypeAndField()
    {
        var entry = _catalogue.SelectionEntries.First(e => e.Id == "entry-name-mod");
        Assert.Single(entry.Modifiers);
        var mod = entry.Modifiers[0];
        Assert.Equal(ModifierKind.Append, mod.Type);
        Assert.Equal("name", mod.Field);
        Assert.Equal("(Modified)", mod.Value);
    }

    [Fact]
    public void AppendModifier_IsUnconditional()
    {
        var entry = _catalogue.SelectionEntries.First(e => e.Id == "entry-name-mod");
        var mod = entry.Modifiers[0];
        Assert.Empty(mod.Conditions);
        Assert.Empty(mod.ConditionGroups);
    }

    [Fact]
    public void IncrementModifier_HasCondition()
    {
        var entry = _catalogue.SelectionEntries.First(e => e.Id == "entry-var-cost");
        Assert.Single(entry.Modifiers);
        var mod = entry.Modifiers[0];
        Assert.Equal(ModifierKind.Increment, mod.Type);
        Assert.Equal("pts", mod.Field);
        Assert.Equal("10", mod.Value);
        Assert.Single(mod.Conditions);
    }

    [Fact]
    public void IncrementModifier_ConditionUsesAtLeast()
    {
        var entry = _catalogue.SelectionEntries.First(e => e.Id == "entry-var-cost");
        var condition = entry.Modifiers[0].Conditions[0];
        Assert.Equal(ConditionKind.AtLeast, condition.Type);
        Assert.Equal(3m, condition.Value);
        Assert.Equal("selections", condition.Field);
        Assert.Equal("force", condition.Scope);
        Assert.Equal("entry-var-cost", condition.ChildId);
    }

    [Fact]
    public void HiddenModifier_SetsVisibility()
    {
        var entry = _catalogue.SelectionEntries.First(e => e.Id == "entry-conditional");
        Assert.True(entry.Hidden); // starts hidden
        Assert.Single(entry.Modifiers);

        var mod = entry.Modifiers[0];
        Assert.Equal(ModifierKind.Set, mod.Type);
        Assert.Equal("hidden", mod.Field);
        Assert.Equal("false", mod.Value);
    }

    [Fact]
    public void HiddenModifier_ConditionChecksOtherEntry()
    {
        var entry = _catalogue.SelectionEntries.First(e => e.Id == "entry-conditional");
        var condition = entry.Modifiers[0].Conditions[0];
        Assert.Equal(ConditionKind.AtLeast, condition.Type);
        Assert.Equal(1m, condition.Value);
        Assert.Equal("entry-commander", condition.ChildId);
    }

    [Fact]
    public void SetPrimaryModifier_HasCategoryField()
    {
        var entry = _catalogue.SelectionEntries.First(e => e.Id == "entry-faction-swap");
        Assert.Single(entry.Modifiers);
        var mod = entry.Modifiers[0];
        Assert.Equal(ModifierKind.SetPrimary, mod.Type);
        Assert.Equal("category", mod.Field);
        Assert.Equal("cat-troops", mod.Value);
    }

    [Fact]
    public void EntryWithModifier_HasInitialCategoryLink()
    {
        var entry = _catalogue.SelectionEntries.First(e => e.Id == "entry-faction-swap");
        Assert.Single(entry.CategoryLinks);
        Assert.Equal("cat-hq", entry.CategoryLinks[0].TargetId);
        Assert.True(entry.CategoryLinks[0].Primary);
    }
}
