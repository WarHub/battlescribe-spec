using System.Linq;
using BattleScribeSpec;
using Xunit;

namespace BattleScribeSpec.Tests;

public class ConstraintOracleTests
{
    private static ScenarioSpec MakeUncategorisedScenario(SelectionEntrySpec[] entries)
    {
        return new ScenarioSpec(
            new GameSystemSpec(
                ForceEntries: [new ForceEntrySpec("fe-1", "Patrol")]),
            [new CatalogueSpec(SelectionEntries: entries)]);
    }

    private static ScenarioSpec MakeCategorisedScenario(SelectionEntrySpec[] entries)
    {
        const string categoryId = "cat-troops";
        var withCategories = entries
            .Select(e => e with
            {
                CategoryLinks = e.CategoryLinks is { Length: > 0 }
                    ? e.CategoryLinks
                    : [new CategoryLinkSpec($"cl-{e.Id}-troops", categoryId, "Troops", Primary: true)]
            })
            .ToArray();

        return new ScenarioSpec(
            new GameSystemSpec(
                ForceEntries: [
                    new ForceEntrySpec(
                        "fe-1",
                        "Patrol",
                        CategoryLinks: [new CategoryLinkSpec("cl-fe-troops", categoryId, "Troops", Primary: false)])
                ],
                CategoryEntries: [new CategoryEntrySpec(categoryId, "Troops")]),
            [new CatalogueSpec(SelectionEntries: withCategories)]);
    }

    [Fact]
    public void MinConstraint_ViolatedWhenNotEnoughSelections()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = MakeCategorisedScenario([
            new SelectionEntrySpec("se-1", "Marine Squad",
                Constraints: [new ConstraintSpec("c-min", "min", 1, "selections", "parent")])
        ]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);

        var errors = oracle.GetValidationErrors();
        Assert.True(oracle.HasValidationErrors(), "Expected a min-constraint validation error.");
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void MaxConstraint_ViolatedWhenTooManySelections()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = MakeCategorisedScenario([
            new SelectionEntrySpec("se-1", "Marine Squad",
                Constraints: [new ConstraintSpec("c-max", "max", 1, "selections", "parent")])
        ]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);

        oracle.SelectFirstAvailableEntry();
        var errorsAfter1 = oracle.GetValidationErrors();
        Assert.False(oracle.HasValidationErrors());
        Assert.Empty(errorsAfter1);

        oracle.SelectFirstAvailableEntry();
        var errorsAfter2 = oracle.GetValidationErrors();
        Assert.True(oracle.HasValidationErrors(), "Expected a max-constraint validation error after selecting twice.");
        Assert.NotEmpty(errorsAfter2);
        Assert.True(errorsAfter2.Count >= errorsAfter1.Count,
            $"Expected error count to stay the same or increase after exceeding max (before={errorsAfter1.Count}, after={errorsAfter2.Count}).");
    }

    [Fact]
    public void MinAndMax_ConstraintsSatisfied()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = MakeCategorisedScenario([
            new SelectionEntrySpec("se-1", "Marine Squad",
                Constraints: [
                    new ConstraintSpec("c-min", "min", 1, "selections", "parent"),
                    new ConstraintSpec("c-max", "max", 3, "selections", "parent")
                ])
        ]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);

        oracle.SelectFirstAvailableEntry();
        var errors = oracle.GetValidationErrors();
        Assert.False(oracle.HasValidationErrors());
        Assert.Empty(errors);
    }

    [Fact]
    public void MaxUnlimited_NoViolation()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = MakeCategorisedScenario([
            new SelectionEntrySpec("se-1", "Marine Squad",
                Constraints: [new ConstraintSpec("c-max", "max", -1, "selections", "parent")])
        ]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);

        for (int i = 0; i < 5; i++)
            oracle.SelectFirstAvailableEntry();

        var errors = oracle.GetValidationErrors();
        Assert.False(oracle.HasValidationErrors());
        Assert.Empty(errors);
    }

    [Fact]
    public void MinConstraint_UncategorisedParentScope_IsSkipped()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = MakeUncategorisedScenario([
            new SelectionEntrySpec("se-1", "Marine Squad",
                Constraints: [new ConstraintSpec("c-min", "min", 1, "selections", "parent")])
        ]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);

        var errors = oracle.GetValidationErrors();
        Assert.False(oracle.HasValidationErrors());
        Assert.Empty(errors);
    }
}
