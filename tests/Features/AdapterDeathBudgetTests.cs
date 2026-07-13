using BattleScribeSpec.Batch;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// Fast, pure unit tests for the cap math <see cref="SpecSuiteRunner"/>'s adapter-death recovery
/// relies on — complements the slower reference-adapter integration tests in
/// <c>SpecSuiteRunnerAdapterDeathTests</c>, which prove the cap end-to-end but can't cheaply cover
/// every boundary value.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AdapterDeathBudgetTests
{
    [Fact]
    public void NotExceeded_WhileCountIsAtOrBelowMax()
    {
        var budget = new AdapterDeathBudget(maxDeaths: 3);

        Assert.False(budget.IsExceeded);
        Assert.Equal(1, budget.Increment());
        Assert.False(budget.IsExceeded);
        Assert.Equal(2, budget.Increment());
        Assert.False(budget.IsExceeded);
        Assert.Equal(3, budget.Increment());
        Assert.False(budget.IsExceeded);
    }

    [Fact]
    public void Exceeded_OnceCountPassesMax()
    {
        var budget = new AdapterDeathBudget(maxDeaths: 1);

        Assert.Equal(1, budget.Increment());
        Assert.False(budget.IsExceeded);

        Assert.Equal(2, budget.Increment());
        Assert.True(budget.IsExceeded);
    }

    [Fact]
    public void ZeroCap_ExceededOnTheVeryFirstDeath()
    {
        var budget = new AdapterDeathBudget(maxDeaths: 0);

        Assert.False(budget.IsExceeded); // no deaths yet
        budget.Increment();
        Assert.True(budget.IsExceeded);
    }

    [Fact]
    public async Task Increment_IsThreadSafeAcrossConcurrentCallers()
    {
        var budget = new AdapterDeathBudget(maxDeaths: 1000);
        var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(() => budget.Increment())).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(100, budget.Count);
    }
}
