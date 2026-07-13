namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// Guards Task 7's deletion of <c>NR_PARALLEL</c>: the NR fixtures' pool size must come from
/// <see cref="NrFixtureConcurrency"/> (which is backed by <c>ConcurrencyPolicy</c>) and must be
/// completely deaf to the now-deleted env var. Regression protection against a future contributor
/// re-adding an <c>NR_PARALLEL</c> read "just to unblock debugging."
/// </summary>
[Trait("Category", "Unit")]
public sealed class NrFixtureConcurrencyTests
{
    [Theory]
    [InlineData("newrecruit")]
    [InlineData("newrecruit-ui")]
    public void Resolve_IgnoresNrParallel_ThePolicyIsTheOnlySource(string engineName)
    {
        var before = NrFixtureConcurrency.Resolve(engineName);

        var previous = Environment.GetEnvironmentVariable("NR_PARALLEL");
        Environment.SetEnvironmentVariable("NR_PARALLEL", "999999");
        try
        {
            var after = NrFixtureConcurrency.Resolve(engineName);

            Assert.Equal(before, after);
            Assert.NotEqual(999999, after.PoolSize);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NR_PARALLEL", previous);
        }
    }

    [Theory]
    [InlineData("newrecruit")]
    [InlineData("newrecruit-ui")]
    public void Resolve_ReturnsAtLeastOneWorker(string engineName)
    {
        var plan = NrFixtureConcurrency.Resolve(engineName);

        Assert.True(plan.PoolSize >= 1, $"pool size must be at least 1; got {plan.PoolSize}");
    }
}
