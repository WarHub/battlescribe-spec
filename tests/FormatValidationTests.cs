using BattleScribeSpec;
using WarHub.ArmouryModel.Source;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Category 1: Format Validation
/// Tests that BattleScribe XML documents conform to the v2.03 schema.
/// </summary>
[Trait("Category", "Unit")]
public class FormatValidationTests
{
    [Fact]
    public void MinimalGamesystem_HasRequiredAttributes()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();
        Assert.Equal("test-gs-1", gs.Id);
        Assert.Equal("Test Game", gs.Name);
        Assert.Equal("2.03", gs.BattleScribeVersion);
        Assert.Equal(1, gs.Revision);
    }

    [Fact]
    public void MinimalGamesystem_HasCostTypes()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();
        Assert.Single(gs.CostTypes);
        Assert.Equal("pts", gs.CostTypes[0].Name);
        Assert.Equal("pts", gs.CostTypes[0].Id);
    }

    [Fact]
    public void MinimalGamesystem_HasProfileTypes()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();
        Assert.Single(gs.ProfileTypes);
        Assert.Equal("Unit", gs.ProfileTypes[0].Name);
        Assert.Equal(6, gs.ProfileTypes[0].CharacteristicTypes.Count);
    }

    [Fact]
    public void MinimalGamesystem_HasCategoryEntries()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();
        Assert.Equal(3, gs.CategoryEntries.Count);
        Assert.Contains(gs.CategoryEntries, c => c.Name == "HQ");
        Assert.Contains(gs.CategoryEntries, c => c.Name == "Troops");
        Assert.Contains(gs.CategoryEntries, c => c.Name == "Faction");
    }

    [Fact]
    public void MinimalGamesystem_HasForceEntries()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();
        Assert.Single(gs.ForceEntries);
        Assert.Equal("Detachment", gs.ForceEntries[0].Name);
        Assert.Equal(2, gs.ForceEntries[0].CategoryLinks.Count);
    }

    [Fact]
    public void BasicCatalogue_HasRequiredAttributes()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        Assert.Equal("test-cat-1", cat.Id);
        Assert.Equal("Test Catalogue", cat.Name);
        Assert.Equal("test-gs-1", cat.GamesystemId);
        Assert.Equal("2.03", cat.BattleScribeVersion);
    }

    [Fact]
    public void BasicCatalogue_HasSelectionEntries()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        Assert.Equal(2, cat.SelectionEntries.Count);
        Assert.Contains(cat.SelectionEntries, e => e.Name == "Commander");
        Assert.Contains(cat.SelectionEntries, e => e.Name == "Soldier Squad");
    }

    [Fact]
    public void SelectionEntry_HasCorrectType()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        var commander = cat.SelectionEntries.First(e => e.Name == "Commander");
        Assert.Equal(SelectionEntryKind.Unit, commander.Type);

        var soldier = commander.SelectionEntries.First(e => e.Name == "Power Sword");
        Assert.Equal(SelectionEntryKind.Upgrade, soldier.Type);
    }
}
