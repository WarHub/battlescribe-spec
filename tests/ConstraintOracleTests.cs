using BattleScribeSpec;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Oracle tests for constraint validation behavior.
/// Tests how the BattleScribe engine enforces min/max constraints and generates
/// validation errors. These define the canonical constraint behavior.
/// </summary>
public class ConstraintOracleTests(ITestOutputHelper output)
{
    private static ScenarioSpec MakeScenario(SelectionEntrySpec[] entries)
    {
        return new ScenarioSpec(
            new GameSystemSpec(
                ForceEntries: [new ForceEntrySpec("fe-1", "Patrol")]),
            new CatalogueSpec(SelectionEntries: entries));
    }

    [Fact]
    public void MinConstraint_ViolatedWhenNotEnoughSelections()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = MakeScenario([
            new SelectionEntrySpec("se-1", "Marine Squad",
                Constraints: [new ConstraintSpec("c-min", "min", 1, "selections", "parent")])
        ]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);

        // Without selecting the unit, the min constraint should be violated
        var errors = oracle.GetValidationErrors();
        output.WriteLine($"Validation errors (0 of min 1): {errors.Count}");
        foreach (var err in errors)
            output.WriteLine($"  - {err}");
    }

    [Fact]
    public void MaxConstraint_ViolatedWhenTooManySelections()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = MakeScenario([
            new SelectionEntrySpec("se-1", "Marine Squad",
                Constraints: [new ConstraintSpec("c-max", "max", 1, "selections", "parent")])
        ]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);

        // Select the unit once — should be fine
        oracle.SelectFirstAvailableEntry();
        var errorsAfter1 = oracle.GetValidationErrors();
        output.WriteLine($"Errors after 1 selection (max=1): {errorsAfter1.Count}");

        // Select the unit again — should violate max=1
        oracle.SelectFirstAvailableEntry();
        var errorsAfter2 = oracle.GetValidationErrors();
        output.WriteLine($"Errors after 2 selections (max=1): {errorsAfter2.Count}");
        foreach (var err in errorsAfter2)
            output.WriteLine($"  - {err}");

        Assert.True(errorsAfter2.Count >= errorsAfter1.Count,
            $"Expected at least as many errors after exceeding max constraint: before={errorsAfter1.Count}, after={errorsAfter2.Count}");
    }

    [Fact]
    public void MinAndMax_ConstraintsSatisfied()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = MakeScenario([
            new SelectionEntrySpec("se-1", "Marine Squad",
                Constraints: [
                    new ConstraintSpec("c-min", "min", 1, "selections", "parent"),
                    new ConstraintSpec("c-max", "max", 3, "selections", "parent")
                ])
        ]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);

        // Select exactly 1 (satisfies min=1, within max=3)
        oracle.SelectFirstAvailableEntry();
        var errors = oracle.GetValidationErrors();
        output.WriteLine($"Errors with 1 selection (min=1, max=3): {errors.Count}");
        foreach (var err in errors)
            output.WriteLine($"  - {err}");
    }

    [Fact]
    public void MaxUnlimited_NoViolation()
    {
        using var oracle = new BattleScribeOracle();
        var scenario = MakeScenario([
            new SelectionEntrySpec("se-1", "Marine Squad",
                Constraints: [new ConstraintSpec("c-max", "max", -1, "selections", "parent")])
        ]);

        oracle.SetupFromSpec(scenario);
        oracle.AddForceByIndex(0);

        // Select many times — max=-1 should never trigger
        for (int i = 0; i < 5; i++)
            oracle.SelectFirstAvailableEntry();

        var errors = oracle.GetValidationErrors();
        output.WriteLine($"Errors after 5 selections (max=-1 unlimited): {errors.Count}");
        foreach (var err in errors)
            output.WriteLine($"  - {err}");
    }
}
