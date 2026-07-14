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
    // PoolSize deliberately differs from Workers here: they are different axes, and this base plan
    // would hide a mirror if they matched.
    private static readonly ConcurrencyPlan Base = new(
        Workers: 2, PoolSize: 16, ReuseRoster: false, ReuseGameData: false);

    [Fact]
    public void NullOrEmpty_ReturnsBasePlanUnchanged()
    {
        Assert.Equal(Base, PolicyOverride.Apply(null, Base));
        Assert.Equal(Base, PolicyOverride.Apply("", Base));
    }

    /// <summary>
    /// <c>workers=N</c> sets the process axis and <b>only</b> the process axis. It used to also
    /// assign <see cref="ConcurrencyPlan.PoolSize"/> — the same mirror the policy has now dropped,
    /// and for the same reason: <c>--policy</c> is parsed only by CLI commands, and the CLI path has
    /// no context pool at all (<c>PoolSize</c> is not even on the protocol wire).
    /// </summary>
    /// <remarks>
    /// Falsifiable: restore <c>poolSize = parsedWorkers</c> in <c>PolicyOverride.Apply</c> and the
    /// <c>PoolSize</c> assertion below reads 5 instead of the base plan's 16.
    /// </remarks>
    [Fact]
    public void Workers_OverridesTheProcessAxisOnly_NotThePool()
    {
        var plan = PolicyOverride.Apply("workers=5", Base);

        Assert.Equal(5, plan.Workers);
        // Untouched by this key — a different axis, sized by the engine's measured constant.
        Assert.Equal(Base.PoolSize, plan.PoolSize);
        Assert.Equal(Base.ReuseRoster, plan.ReuseRoster);
        Assert.Equal(Base.ReuseGameData, plan.ReuseGameData);
    }

    /// <summary>
    /// There is no <c>pool=</c> key, and adding one would be a silently-dropped flag (#305): no CLI
    /// command has a pool to size. Unknown keys are rejected, so this is a real gate rather than a
    /// comment.
    /// </summary>
    [Theory]
    [InlineData("pool=16")]
    [InlineData("poolsize=16")]
    [InlineData("contexts=16")]
    public void PoolKeys_AreNotAccepted_TheCliHasNoPool(string raw)
    {
        Assert.Throws<FormatException>(() => PolicyOverride.Apply(raw, Base));
        Assert.Throws<FormatException>(() => PolicyOverride.Keys(raw));
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
