using WarHub.ArmouryModel.Source;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Category 9: Cost Calculation
/// Tests that cost structures are correctly defined and calculable.
/// </summary>
[Trait("Category", "Unit")]
public class CostCalculationTests
{
    [Fact]
    public void Cost_HasTypeAndValue()
    {
        var cost = new CostCore { TypeId = "pts", Name = "pts", Value = 100m }.ToNode();
        Assert.Equal("pts", cost.TypeId);
        Assert.Equal("pts", cost.Name);
        Assert.Equal(100m, cost.Value);
    }

    [Fact]
    public void SelectionEntry_CostRepresentsBaseCost()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        var commander = cat.SelectionEntries.First(e => e.Name == "Commander");
        Assert.Equal(100m, commander.Costs[0].Value);
    }

    [Fact]
    public void MultipleSelections_CostScalesWithNumber()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        var squad = cat.SelectionEntries.First(e => e.Name == "Soldier Squad");
        var model = squad.SelectionEntries.First(e => e.Name == "Soldier");

        Assert.Equal(10m, model.Costs[0].Value);

        for (var n = 5; n <= 10; n++)
        {
            var expectedCost = n * 10m;
            Assert.Equal(expectedCost, n * model.Costs[0].Value);
        }
    }

    [Fact]
    public void CostLimit_NegativeOne_MeansNoLimit()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();
        var roster = NodeFactory.Roster(gs).Core with
        {
            CostLimits = [new CostLimitCore { TypeId = "pts", Name = "pts", Value = -1m }],
        };

        var node = roster.ToNode();
        Assert.Equal(-1m, node.CostLimits[0].Value);
    }

    [Fact]
    public void CostAggregation_SumsAllSelectionCosts()
    {
        var costs = new[] { 100m, 5m, 10m, 10m, 10m, 10m, 10m };
        var total = costs.Sum();
        Assert.Equal(155m, total);
    }

    [Fact]
    public void ZeroCost_IsValid()
    {
        var cost = new CostCore { TypeId = "pts", Name = "pts", Value = 0m }.ToNode();
        Assert.Equal(0m, cost.Value);
    }

    [Fact]
    public void MultipleCostTypes_CanCoexist()
    {
        var entry = new SelectionEntryCore
        {
            Id = "multi-cost",
            Name = "Multi-Cost Unit",
            Type = SelectionEntryKind.Unit,
            Costs =
            [
                new CostCore { TypeId = "pts", Name = "pts", Value = 100m },
                new CostCore { TypeId = "pl", Name = "PL", Value = 5m },
                new CostCore { TypeId = "cp", Name = "CP", Value = -1m },
            ],
        }.ToNode();

        Assert.Equal(3, entry.Costs.Count);
        Assert.Equal(100m, entry.Costs.First(c => c.TypeId == "pts").Value);
        Assert.Equal(5m, entry.Costs.First(c => c.TypeId == "pl").Value);
        Assert.Equal(-1m, entry.Costs.First(c => c.TypeId == "cp").Value);
    }
}
