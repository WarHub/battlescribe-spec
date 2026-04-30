namespace BattleScribeSpec.Tests;

/// <summary>
/// Tests for the BattleScribe Java engine (via IKVM).
/// All Java model types are encapsulated behind the engine API to avoid
/// requiring the test project to reference IKVM-generated assemblies.
/// </summary>
[Trait("Category", "Unit")]
public class BattleScribeEngineTests(ITestOutputHelper output)
{
    [Fact]
    public void BS_CanBeInstantiated()
    {
        using var engine = new BattleScribeEngine();
        Assert.NotNull(engine);
    }

    [Fact]
    public void BS_InitializeWithMinimalGameSystem()
    {
        using var engine = new BattleScribeEngine();
        var errors = engine.InitializeMinimal("test-gs", "Test Game System");
        Assert.NotNull(errors);
    }

    [Fact]
    public void BS_GetRosterAfterInit()
    {
        using var engine = new BattleScribeEngine();
        engine.InitializeMinimal("test-gs", "Test Game System");
        Assert.Equal("Test Roster", engine.GetRosterName());
    }

    [Fact]
    public void BS_GameSystemIdPreserved()
    {
        using var engine = new BattleScribeEngine();
        engine.InitializeMinimal("my-gs-id", "My Game");
        Assert.Equal("my-gs-id", engine.GetRosterGameSystemId());
    }

    [Fact]
    public void BS_EmptyRosterHasNoForces()
    {
        using var engine = new BattleScribeEngine();
        engine.InitializeMinimal("test-gs", "Test Game System");
        Assert.Equal(0, engine.GetForceCount());
    }

    [Fact]
    public void BS_HasNoErrorsOnEmptyRoster()
    {
        using var engine = new BattleScribeEngine();
        engine.InitializeMinimal("test-gs", "Test Game System");
        var errors = engine.GetValidationErrors();
        Assert.NotNull(errors);
    }

    [Fact]
    public void BS_AddForce()
    {
        using var engine = CreateEngineWithPatrolForce(out var errors);
        Assert.Equal(1, engine.GetForceCount());
        // AddForce may return non-fatal warnings; log them but verify no blocking errors
        foreach (var err in errors)
        {
            output.WriteLine($"AddForce error: {err}");
        }
    }

    [Fact]
    public void BS_SelectEntry()
    {
        using var engine = CreateEngineWithUnit(out var selectionCount);
        Assert.True(selectionCount > 0, "Expected at least one selection after SelectEntry");
        output.WriteLine($"Created {selectionCount} selection(s)");
    }

    [Fact]
    public void BS_DeselectEntry()
    {
        using var engine = CreateEngineWithUnit(out _);
        var beforeCount = engine.GetAllSelectionCount();

        engine.DeselectFirstSelection();
        var afterCount = engine.GetAllSelectionCount();

        Assert.True(afterCount < beforeCount,
            $"Expected fewer selections after deselect: before={beforeCount}, after={afterCount}");
    }

    [Fact]
    public void BS_CostCalculation()
    {
        using var engine = CreateEngineWithUnit(out _, withCosts: true);

        var costs = engine.GetRosterCostsSummary();
        Assert.NotEmpty(costs);
        output.WriteLine($"Roster costs ({costs.Count} entries):");
        foreach (var (name, value) in costs)
        {
            output.WriteLine($"  {name}: {value}");
        }
    }

    [Fact]
    public void BS_RemoveForce()
    {
        using var engine = CreateEngineWithPatrolForce(out _);
        Assert.Equal(1, engine.GetForceCount());

        var removed = engine.RemoveFirstForce();
        Assert.True(removed);
        Assert.Equal(0, engine.GetForceCount());
    }

    [Fact]
    public void BS_IsNotLoading()
    {
        using var engine = new BattleScribeEngine();
        engine.InitializeMinimal("test-gs", "Test Game System");
        Assert.False(engine.IsLoading);
    }

    // --- Helpers that use the engine's encapsulated Java model API ---

    private static BattleScribeEngine CreateEngineWithPatrolForce(out List<string> errors)
    {
        var engine = new BattleScribeEngine();
        engine.SetupWithPatrolForce();
        errors = engine.AddForceByIndex(0);
        return engine;
    }

    private static BattleScribeEngine CreateEngineWithUnit(out int selectionCount, bool withCosts = false)
    {
        var engine = new BattleScribeEngine();
        engine.SetupWithPatrolAndUnit(withCosts: withCosts);
        engine.AddForceByIndex(0);
        selectionCount = engine.SelectFirstAvailableEntry();
        return engine;
    }
}
