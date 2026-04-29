using WarHub.ArmouryModel.Source;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Category 2: Data Model Integrity
/// Tests that data model relationships are correctly defined.
/// </summary>
[Trait("Category", "Unit")]
public class DataModelIntegrityTests
{
    [Fact]
    public void SelectionEntry_HasCosts()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        var commander = cat.SelectionEntries.First(e => e.Name == "Commander");
        Assert.Single(commander.Costs);
        Assert.Equal("pts", commander.Costs[0].TypeId);
        Assert.Equal(100m, commander.Costs[0].Value);
    }

    [Fact]
    public void SelectionEntry_HasConstraints()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        var commander = cat.SelectionEntries.First(e => e.Name == "Commander");
        Assert.Equal(2, commander.Constraints.Count);

        var minConstraint = commander.Constraints.First(c => c.Type == ConstraintKind.Minimum);
        Assert.Equal(0m, minConstraint.Value);
        Assert.Equal("force", minConstraint.Scope);
        Assert.Equal("selections", minConstraint.Field);

        var maxConstraint = commander.Constraints.First(c => c.Type == ConstraintKind.Maximum);
        Assert.Equal(3m, maxConstraint.Value);
    }

    [Fact]
    public void NestedSelectionEntry_HasConstraints()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        var commander = cat.SelectionEntries.First(e => e.Name == "Commander");
        var sword = commander.SelectionEntries.First(e => e.Name == "Power Sword");
        Assert.Single(sword.Constraints);
        Assert.Equal(ConstraintKind.Maximum, sword.Constraints[0].Type);
        Assert.Equal(1m, sword.Constraints[0].Value);
        Assert.Equal("parent", sword.Constraints[0].Scope);
    }

    [Fact]
    public void SelectionEntry_HasCategoryLinks()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        var commander = cat.SelectionEntries.First(e => e.Name == "Commander");
        Assert.Single(commander.CategoryLinks);
        Assert.Equal("cat-hq", commander.CategoryLinks[0].TargetId);
    }

    [Fact]
    public void ModelEntry_HasMinMaxConstraints()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        var squad = cat.SelectionEntries.First(e => e.Name == "Soldier Squad");
        var model = squad.SelectionEntries.First(e => e.Name == "Soldier");
        Assert.Equal(SelectionEntryKind.Model, model.Type);

        var min = model.Constraints.First(c => c.Type == ConstraintKind.Minimum);
        var max = model.Constraints.First(c => c.Type == ConstraintKind.Maximum);
        Assert.Equal(5m, min.Value);
        Assert.Equal(10m, max.Value);
    }

    [Fact]
    public void SharedEntry_CanBeReferenced()
    {
        var cat = TestDataFactory.CreateLinkTestCatalogue();
        Assert.Single(cat.SharedSelectionEntries);
        Assert.Equal("shared-weapon-1", cat.SharedSelectionEntries[0].Id);
        Assert.Equal(15m, cat.SharedSelectionEntries[0].Costs[0].Value);
    }

    [Fact]
    public void EntryLink_ReferencesSharedEntry()
    {
        var cat = TestDataFactory.CreateLinkTestCatalogue();
        var unit = cat.SelectionEntries.First(e => e.Name == "Linked Unit");
        Assert.Single(unit.EntryLinks);
        Assert.Equal("shared-weapon-1", unit.EntryLinks[0].TargetId);
        Assert.Equal(EntryLinkKind.SelectionEntry, unit.EntryLinks[0].Type);
    }

    [Fact]
    public void SelectionEntryGroup_HasDefaultAndConstraints()
    {
        var cat = TestDataFactory.CreateSelectionGroupTestCatalogue();
        var unit = cat.SelectionEntries.First(e => e.Name == "Equipped Unit");
        Assert.Single(unit.SelectionEntryGroups);

        var group = unit.SelectionEntryGroups[0];
        Assert.Equal("Weapon Choice", group.Name);
        Assert.Equal("weapon-a", group.DefaultSelectionEntryId);
        Assert.Equal(3, group.SelectionEntries.Count);
        Assert.Equal(2, group.Constraints.Count);
    }
}
