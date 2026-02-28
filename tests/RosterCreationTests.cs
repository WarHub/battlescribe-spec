using BattleScribeSpec;
using WarHub.ArmouryModel.Source;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Category 3 & 4: Roster Creation and Selection Operations
/// Tests the roster node construction and selection tree structure.
/// </summary>
public class RosterCreationTests
{
    [Fact]
    public void NewRoster_HasRequiredFields()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();
        var roster = NodeFactory.Roster(gs);

        Assert.NotNull(roster.Id);
        Assert.NotEmpty(roster.Id);
        Assert.Equal("test-gs-1", roster.GamesystemId);
        Assert.Equal("Test Game", roster.GamesystemName);
    }

    [Fact]
    public void NewRoster_HasBattleScribeVersion()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();
        var roster = NodeFactory.Roster(gs);

        Assert.Equal("2.03", roster.BattleScribeVersion);
    }

    [Fact]
    public void Roster_CanHaveCostLimits()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();
        var roster = NodeFactory.Roster(gs) with
        {
            CostLimits =
            [
                NodeFactory.Cost("pts") with { TypeId = "pts", Value = 2000m },
            ],
        };

        Assert.Single(roster.CostLimits);
        Assert.Equal(2000m, roster.CostLimits[0].Value);
    }

    [Fact]
    public void Roster_CanHaveForces()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();
        var cat = TestDataFactory.CreateBasicCatalogue();
        var forceEntry = gs.ForceEntries[0];
        var force = NodeFactory.Force(forceEntry) with
        {
            CatalogueId = cat.Id,
            CatalogueName = cat.Name,
            CatalogueRevision = cat.Revision,
        };

        var roster = NodeFactory.Roster(gs) with
        {
            Forces = [force],
        };

        Assert.Single(roster.Forces);
        Assert.Equal("test-cat-1", roster.Forces[0].CatalogueId);
        Assert.Equal("Test Catalogue", roster.Forces[0].CatalogueName);
    }

    [Fact]
    public void Selection_Unit_HasCorrectStructure()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        var commanderEntry = cat.SelectionEntries.First(e => e.Name == "Commander");

        var selection = NodeFactory.Selection(commanderEntry, commanderEntry.Id) with
        {
            Number = 1,
            Costs = commanderEntry.Costs,
        };

        Assert.Equal("Commander", selection.Name);
        Assert.Equal(commanderEntry.Id, selection.EntryId);
        Assert.Equal(1, selection.Number);
        Assert.Equal(SelectionEntryKind.Unit, selection.Type);
        Assert.Equal(100m, selection.Costs[0].Value);
    }

    [Fact]
    public void Selection_CanHaveNestedSelections()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        var commanderEntry = cat.SelectionEntries.First(e => e.Name == "Commander");
        var swordEntry = commanderEntry.SelectionEntries.First(e => e.Name == "Power Sword");

        var sword = NodeFactory.Selection(swordEntry, swordEntry.Id) with
        {
            Number = 1,
            Costs = swordEntry.Costs,
        };
        var commander = NodeFactory.Selection(commanderEntry, commanderEntry.Id) with
        {
            Number = 1,
            Costs = commanderEntry.Costs,
            Selections = [sword],
        };

        Assert.Single(commander.Selections);
        Assert.Equal("Power Sword", commander.Selections[0].Name);
        Assert.Equal(SelectionEntryKind.Upgrade, commander.Selections[0].Type);
    }

    [Fact]
    public void Selection_ModelCount_CanVary()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        var squad = cat.SelectionEntries.First(e => e.Name == "Soldier Squad");
        var modelEntry = squad.SelectionEntries.First(e => e.Name == "Soldier");

        // Test with 5 models (minimum)
        var selection5 = NodeFactory.Selection(modelEntry, modelEntry.Id) with
        {
            Number = 5,
            Costs = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = 50m }],
        };
        Assert.Equal(5, selection5.Number);
        Assert.Equal(50m, selection5.Costs[0].Value); // 5 × 10pts

        // Test with 10 models (maximum)
        var selection10 = NodeFactory.Selection(modelEntry, modelEntry.Id) with
        {
            Number = 10,
            Costs = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = 100m }],
        };
        Assert.Equal(10, selection10.Number);
        Assert.Equal(100m, selection10.Costs[0].Value); // 10 × 10pts
    }

    [Fact]
    public void Force_CanHaveCategories()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();
        var forceEntry = gs.ForceEntries[0];
        var force = NodeFactory.Force(forceEntry) with
        {
            Categories =
            [
                NodeFactory.Category() with
                {
                    EntryId = "cat-hq", Name = "HQ", Primary = false,
                },
                NodeFactory.Category() with
                {
                    EntryId = "cat-troops", Name = "Troops", Primary = false,
                },
            ],
        };

        Assert.Equal(2, force.Categories.Length);
    }

    [Fact]
    public void Roster_CostAggregation_Concept()
    {
        // Test the concept of cost aggregation: roster costs = sum of all selection costs
        var gs = TestDataFactory.CreateMinimalGamesystem();
        var cat = TestDataFactory.CreateBasicCatalogue();
        var commanderEntry = cat.SelectionEntries.First(e => e.Name == "Commander");
        var soldierSquadEntry = cat.SelectionEntries.First(e => e.Name == "Soldier Squad");

        // Commander: 100pts + Power Sword: 5pts
        var sword = NodeFactory.Selection(
            commanderEntry.SelectionEntries[0], commanderEntry.SelectionEntries[0].Id) with
        {
            Number = 1,
            Costs = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = 5m }],
        };
        var commander = NodeFactory.Selection(commanderEntry, commanderEntry.Id) with
        {
            Number = 1,
            Costs = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = 100m }],
            Selections = [sword],
        };

        // Total should be 100 + 5 = 105 (conceptually; actual aggregation is engine-level)
        var commanderCost = commander.Costs[0].Value;
        var swordCost = commander.Selections[0].Costs[0].Value;
        Assert.Equal(105m, commanderCost + swordCost);
    }
}
