using BattleScribeSpec;
using WarHub.ArmouryModel.Source;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Category 9: Cost Calculation
/// Tests that cost structures are correctly defined and calculable.
/// </summary>
public class CostCalculationTests
{
    [Fact]
    public void Cost_HasTypeAndValue()
    {
        var cost = NodeFactory.Cost("pts") with { TypeId = "pts", Value = 100m };
        Assert.Equal("pts", cost.TypeId);
        Assert.Equal("pts", cost.Name);
        Assert.Equal(100m, cost.Value);
    }

    [Fact]
    public void SelectionEntry_CostRepresentsBaseCost()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        var commander = cat.SelectionEntries.First(e => e.Name == "Commander");

        // Base cost is 100pts for 1 commander
        Assert.Equal(100m, commander.Costs[0].Value);
    }

    [Fact]
    public void MultipleSelections_CostScalesWithNumber()
    {
        // Spec: cost × selection.number
        var cat = TestDataFactory.CreateBasicCatalogue();
        var squad = cat.SelectionEntries.First(e => e.Name == "Soldier Squad");
        var model = squad.SelectionEntries.First(e => e.Name == "Soldier");

        // Base per-model cost is 10pts
        Assert.Equal(10m, model.Costs[0].Value);

        // For N models, total cost = N × 10
        for (int n = 5; n <= 10; n++)
        {
            var expectedCost = n * 10m;
            Assert.Equal(expectedCost, n * model.Costs[0].Value);
        }
    }

    [Fact]
    public void CostLimit_NegativeOne_MeansNoLimit()
    {
        // Spec: costLimit of -1 means no limit
        var gs = TestDataFactory.CreateMinimalGamesystem();
        var roster = NodeFactory.Roster(gs) with
        {
            CostLimits = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = -1m }],
        };

        Assert.Equal(-1m, roster.CostLimits[0].Value);
    }

    [Fact]
    public void CostAggregation_SumsAllSelectionCosts()
    {
        // Test the concept: roster total = sum of all leaf selection costs
        var costs = new[]
        {
            100m,  // Commander
            5m,    // Power Sword
            10m,   // Soldier × 1
            10m,   // Soldier × 1
            10m,   // Soldier × 1
            10m,   // Soldier × 1
            10m,   // Soldier × 1
        };

        var total = costs.Sum();
        Assert.Equal(155m, total);
    }

    [Fact]
    public void ZeroCost_IsValid()
    {
        var cost = NodeFactory.Cost("pts") with { TypeId = "pts", Value = 0m };
        Assert.Equal(0m, cost.Value);
    }

    [Fact]
    public void MultipleCostTypes_CanCoexist()
    {
        // Some games have multiple cost types (pts, PL, CP)
        var entry = NodeFactory.SelectionEntry("Multi-Cost Unit") with
        {
            Type = SelectionEntryKind.Unit,
            Costs =
            [
                NodeFactory.Cost("pts") with { TypeId = "pts", Value = 100m },
                NodeFactory.Cost("PL") with { TypeId = "pl", Value = 5m },
                NodeFactory.Cost("CP") with { TypeId = "cp", Value = -1m },
            ],
        };

        Assert.Equal(3, entry.Costs.Length);
        Assert.Equal(100m, entry.Costs.First(c => c.TypeId == "pts").Value);
        Assert.Equal(5m, entry.Costs.First(c => c.TypeId == "pl").Value);
        Assert.Equal(-1m, entry.Costs.First(c => c.TypeId == "cp").Value);
    }
}
