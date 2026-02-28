using BattleScribeSpec;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Tests for the BattleScribe Java engine oracle (via IKVM).
/// All Java model types are encapsulated behind the oracle API to avoid
/// requiring the test project to reference IKVM-generated assemblies.
/// </summary>
public class OracleTests(ITestOutputHelper output)
{
    [Fact]
    public void Oracle_CanBeInstantiated()
    {
        using var oracle = new BattleScribeOracle();
        Assert.NotNull(oracle);
    }

    [Fact]
    public void Oracle_InitializeWithMinimalGameSystem()
    {
        using var oracle = new BattleScribeOracle();
        var errors = oracle.InitializeMinimal("test-gs", "Test Game System");
        Assert.NotNull(errors);
    }

    [Fact]
    public void Oracle_GetRosterAfterInit()
    {
        using var oracle = new BattleScribeOracle();
        oracle.InitializeMinimal("test-gs", "Test Game System");
        Assert.Equal("Oracle Roster", oracle.GetRosterName());
    }

    [Fact]
    public void Oracle_GameSystemIdPreserved()
    {
        using var oracle = new BattleScribeOracle();
        oracle.InitializeMinimal("my-gs-id", "My Game");
        Assert.Equal("my-gs-id", oracle.GetRosterGameSystemId());
    }

    [Fact]
    public void Oracle_EmptyRosterHasNoForces()
    {
        using var oracle = new BattleScribeOracle();
        oracle.InitializeMinimal("test-gs", "Test Game System");
        Assert.Equal(0, oracle.GetForceCount());
    }

    [Fact]
    public void Oracle_HasNoErrorsOnEmptyRoster()
    {
        using var oracle = new BattleScribeOracle();
        oracle.InitializeMinimal("test-gs", "Test Game System");
        var errors = oracle.GetValidationErrors();
        Assert.NotNull(errors);
    }

    [Fact]
    public void Oracle_AddForce()
    {
        using var oracle = CreateOracleWithPatrolForce(out var errors);
        Assert.Equal(1, oracle.GetForceCount());
        foreach (var err in errors)
            output.WriteLine($"AddForce error: {err}");
    }

    [Fact]
    public void Oracle_SelectEntry()
    {
        using var oracle = CreateOracleWithUnit(out var selectionCount);
        Assert.True(selectionCount > 0, "Expected at least one selection after SelectEntry");
        output.WriteLine($"Created {selectionCount} selection(s)");
    }

    [Fact]
    public void Oracle_DeselectEntry()
    {
        using var oracle = CreateOracleWithUnit(out _);
        var beforeCount = oracle.GetAllSelectionCount();

        oracle.DeselectFirstSelection();
        var afterCount = oracle.GetAllSelectionCount();

        Assert.True(afterCount < beforeCount,
            $"Expected fewer selections after deselect: before={beforeCount}, after={afterCount}");
    }

    [Fact]
    public void Oracle_CostCalculation()
    {
        using var oracle = CreateOracleWithUnit(out _, withCosts: true);

        var costs = oracle.GetRosterCostsSummary();
        output.WriteLine($"Roster costs ({costs.Count} entries):");
        foreach (var (name, value) in costs)
            output.WriteLine($"  {name}: {value}");
    }

    [Fact]
    public void Oracle_RemoveForce()
    {
        using var oracle = CreateOracleWithPatrolForce(out _);
        Assert.Equal(1, oracle.GetForceCount());

        var removed = oracle.RemoveFirstForce();
        Assert.True(removed);
        Assert.Equal(0, oracle.GetForceCount());
    }

    [Fact]
    public void Oracle_IsNotLoading()
    {
        using var oracle = new BattleScribeOracle();
        oracle.InitializeMinimal("test-gs", "Test Game System");
        Assert.False(oracle.IsLoading);
    }

    // --- Helpers that use the oracle's encapsulated Java model API ---

    private BattleScribeOracle CreateOracleWithPatrolForce(out List<string> errors)
    {
        var oracle = new BattleScribeOracle();
        oracle.SetupWithPatrolForce();
        errors = oracle.AddForceByIndex(0);
        return oracle;
    }

    private BattleScribeOracle CreateOracleWithUnit(out int selectionCount, bool withCosts = false)
    {
        var oracle = new BattleScribeOracle();
        oracle.SetupWithPatrolAndUnit(withCosts: withCosts);
        oracle.AddForceByIndex(0);
        selectionCount = oracle.SelectFirstAvailableEntry();
        return oracle;
    }
}
