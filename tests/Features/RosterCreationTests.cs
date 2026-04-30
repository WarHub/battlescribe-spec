using WarHub.ArmouryModel.Source;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Category 3 and 4: Roster Creation and Selection Operations.
/// Tests the roster node construction and selection tree structure.
/// </summary>
[Trait("Category", "Unit")]
public class RosterCreationTests
{
    private static CostCore Pts(decimal value) => new() { TypeId = "pts", Name = "pts", Value = value };

    [Fact]
    public void NewRoster_HasRequiredFields()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();
        var roster = NodeFactory.Roster(gs);

        Assert.NotNull(roster.Id);
        Assert.NotEmpty(roster.Id);
        Assert.Equal("test-gs-1", roster.GameSystemId);
        Assert.Equal("Test Game", roster.GameSystemName);
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
        var roster = NodeFactory.Roster(gs).Core with
        {
            CostLimits = [new CostLimitCore { TypeId = "pts", Name = "pts", Value = 2000m }],
        };

        var node = roster.ToNode();
        Assert.Single(node.CostLimits);
        Assert.Equal(2000m, node.CostLimits[0].Value);
    }

    [Fact]
    public void Roster_CanHaveForces()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();
        var cat = TestDataFactory.CreateBasicCatalogue();
        var forceEntry = gs.ForceEntries[0];
        var force = NodeFactory.Force(forceEntry).Core with
        {
            CatalogueId = cat.Id,
            CatalogueName = cat.Name,
            CatalogueRevision = cat.Revision,
        };

        var roster = NodeFactory.Roster(gs).Core with
        {
            Forces = [force],
        };

        var node = roster.ToNode();
        Assert.Single(node.Forces);
        Assert.Equal("test-cat-1", node.Forces[0].CatalogueId);
        Assert.Equal("Test Catalogue", node.Forces[0].CatalogueName);
    }

    [Fact]
    public void Selection_Unit_HasCorrectStructure()
    {
        var selection = new SelectionCore
        {
            Id = "sel-1",
            Name = "Commander",
            EntryId = "entry-commander",
            Type = SelectionEntryKind.Unit,
            Number = 1,
            Costs = [Pts(100)],
        }.ToNode();

        Assert.Equal("Commander", selection.Name);
        Assert.Equal("entry-commander", selection.EntryId);
        Assert.Equal(1, selection.Number);
        Assert.Equal(SelectionEntryKind.Unit, selection.Type);
        Assert.Equal(100m, selection.Costs[0].Value);
    }

    [Fact]
    public void Selection_CanHaveNestedSelections()
    {
        var selection = new SelectionCore
        {
            Id = "sel-cmd",
            Name = "Commander",
            EntryId = "entry-commander",
            Type = SelectionEntryKind.Unit,
            Number = 1,
            Costs = [Pts(100)],
            Selections =
            [
                new SelectionCore
                {
                    Id = "sel-sword",
                    Name = "Power Sword",
                    EntryId = "entry-power-sword",
                    Type = SelectionEntryKind.Upgrade,
                    Number = 1,
                    Costs = [Pts(5)],
                },
            ],
        }.ToNode();

        Assert.Single(selection.Selections);
        Assert.Equal("Power Sword", selection.Selections[0].Name);
        Assert.Equal(SelectionEntryKind.Upgrade, selection.Selections[0].Type);
    }

    [Fact]
    public void Selection_ModelCount_CanVary()
    {
        // Test with 5 models (minimum)
        var selection5 = new SelectionCore
        {
            Id = "sel-5",
            Name = "Soldier",
            EntryId = "entry-soldier-model",
            Type = SelectionEntryKind.Model,
            Number = 5,
            Costs = [Pts(50)],
        }.ToNode();
        Assert.Equal(5, selection5.Number);
        Assert.Equal(50m, selection5.Costs[0].Value);

        // Test with 10 models (maximum)
        var selection10 = new SelectionCore
        {
            Id = "sel-10",
            Name = "Soldier",
            EntryId = "entry-soldier-model",
            Type = SelectionEntryKind.Model,
            Number = 10,
            Costs = [Pts(100)],
        }.ToNode();
        Assert.Equal(10, selection10.Number);
        Assert.Equal(100m, selection10.Costs[0].Value);
    }

    [Fact]
    public void Force_CanHaveCategories()
    {
        var force = new ForceCore
        {
            Id = "force-1",
            Name = "Detachment",
            EntryId = "force-det",
            Categories =
            [
                new CategoryCore { Id = "rc-1", EntryId = "cat-hq", Name = "HQ", Primary = false },
                new CategoryCore { Id = "rc-2", EntryId = "cat-troops", Name = "Troops", Primary = false },
            ],
        }.ToNode();

        Assert.Equal(2, force.Categories.Count);
    }

    [Fact]
    public void Roster_CostAggregation_Concept()
    {
        // Test the concept of cost aggregation: roster costs = sum of all selection costs
        var commander = new SelectionCore
        {
            Id = "sel-cmd",
            Name = "Commander",
            EntryId = "entry-commander",
            Type = SelectionEntryKind.Unit,
            Number = 1,
            Costs = [Pts(100)],
            Selections =
            [
                new SelectionCore
                {
                    Id = "sel-sword",
                    Name = "Power Sword",
                    EntryId = "entry-power-sword",
                    Type = SelectionEntryKind.Upgrade,
                    Number = 1,
                    Costs = [Pts(5)],
                },
            ],
        }.ToNode();

        // Total should be 100 + 5 = 105
        var commanderCost = commander.Costs[0].Value;
        var swordCost = commander.Selections[0].Costs[0].Value;
        Assert.Equal(105m, commanderCost + swordCost);
    }
}
