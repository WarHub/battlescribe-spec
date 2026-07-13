using BattleScribeSpec.Concurrency;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// The one <c>--policy k=v,...</c> parser shared by <c>serve</c>/<c>run</c>/<c>compare</c> (Tasks
/// 4-6 of the concurrency-model plan). Tested in isolation from any command so all three commands
/// can trust the same behavior without duplicating it.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PolicyOverrideTests
{
    private static readonly ConcurrencyPlan Base = new(
        Workers: 2, PoolSize: 2, MaxParallelThreads: 2, ReuseRoster: false, ReuseGameData: false);

    [Fact]
    public void NullOrEmpty_ReturnsBasePlanUnchanged()
    {
        Assert.Equal(Base, PolicyOverride.Apply(null, Base));
        Assert.Equal(Base, PolicyOverride.Apply("", Base));
    }

    [Fact]
    public void Workers_OverridesWorkersPoolSizeAndMaxParallelThreads()
    {
        var plan = PolicyOverride.Apply("workers=5", Base);

        Assert.Equal(5, plan.Workers);
        Assert.Equal(5, plan.PoolSize);
        Assert.Equal(5, plan.MaxParallelThreads);
        // Untouched by this key.
        Assert.Equal(Base.ReuseRoster, plan.ReuseRoster);
        Assert.Equal(Base.ReuseGameData, plan.ReuseGameData);
    }

    [Theory]
    [InlineData("workers=0")]
    [InlineData("workers=-1")]
    [InlineData("workers=abc")]
    public void Workers_RejectsNonPositiveOrNonIntegerValues(string raw)
    {
        Assert.Throws<FormatException>(() => PolicyOverride.Apply(raw, Base));
    }

    [Fact]
    public void Reuse_SetsBothRosterAndGameData()
    {
        var plan = PolicyOverride.Apply("reuse=on", Base);

        Assert.True(plan.ReuseRoster);
        Assert.True(plan.ReuseGameData);
    }

    [Fact]
    public void ReuseRosterAndReuseGameData_AreIndependent()
    {
        var baseAllOn = Base with { ReuseRoster = true, ReuseGameData = true };

        var plan = PolicyOverride.Apply("reuse-roster=off", baseAllOn);

        Assert.False(plan.ReuseRoster);
        Assert.True(plan.ReuseGameData);
    }

    [Fact]
    public void MultipleKeys_AllApply()
    {
        var plan = PolicyOverride.Apply("workers=3,reuse-roster=on,reuse-gamedata=off", Base);

        Assert.Equal(3, plan.Workers);
        Assert.True(plan.ReuseRoster);
        Assert.False(plan.ReuseGameData);
    }

    [Fact]
    public void LaterDuplicateKey_Wins()
    {
        var plan = PolicyOverride.Apply("reuse=on,reuse-roster=off", Base);

        Assert.False(plan.ReuseRoster);
        Assert.True(plan.ReuseGameData);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("workers")]
    [InlineData("=5")]
    public void MalformedEntry_Throws(string raw)
    {
        Assert.Throws<FormatException>(() => PolicyOverride.Apply(raw, Base));
    }

    [Fact]
    public void UnknownKey_Throws()
    {
        var ex = Assert.Throws<FormatException>(() => PolicyOverride.Apply("bogus=on", Base));
        Assert.Contains("bogus", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("reuse=maybe")]
    [InlineData("reuse-roster=true")]
    public void InvalidOnOffValue_Throws(string raw)
    {
        Assert.Throws<FormatException>(() => PolicyOverride.Apply(raw, Base));
    }
}
