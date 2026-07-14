using BattleScribeSpec.Concurrency;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <see cref="LiveLoadBudget"/> — the one home of "how many browser sessions may this test host have
/// open at a third party's website at once".
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure tests: nothing here opens a browser or sends a request.</b> The budget is arithmetic over a
/// constant, and it has to be, because the thing it bounds is traffic to somebody else's server — a
/// quantity we are not entitled to measure by generating it.
/// </para>
/// <para>
/// Each test uses its own invented host, so the process-wide budget is not shared state between them
/// and <c>parallelizeTestCollections</c> cannot make them flake into each other.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class LiveLoadBudgetTests
{
    /// <summary>
    /// <b>The composed breach, in one test.</b> The pooled live fixture takes the policy's 2; the
    /// sequential one then asks for a 3rd session and is refused. That 2 + 1 = 3 is what
    /// <c>-p:TestProfile=nr-live</c> actually did to newrecruit.eu — both collections carry
    /// <c>Engine=LiveNrRoster</c> and xUnit runs collections in parallel.
    /// </summary>
    /// <remarks>
    /// Falsifiable: drop the <c>Math.Min(wanted, PerHostLimit - alreadyHeld)</c> in
    /// <c>LiveLoadBudget.Reserve</c> (grant whatever is asked) and the third assertion goes red — the
    /// budget hands out the session that used to be taken without asking.
    /// </remarks>
    [Fact]
    public void Reserve_NeverExceedsTheLoadLimit_ForOneHost()
    {
        const string Host = "https://composed-breach.invalid";

        var pool = LiveLoadBudget.Reserve("PooledFixture", Host, ConcurrencyPolicy.ThirdPartyLiveLoadLimit);
        Assert.Equal(ConcurrencyPolicy.ThirdPartyLiveLoadLimit, pool.Sessions);
        Assert.True(pool.Full);

        // The third session. This is the one that was being taken.
        var sequential = LiveLoadBudget.Reserve("SequentialFixture", Host, 1);

        Assert.Equal(0, sequential.Sessions);
        Assert.False(sequential.Full);
        Assert.Equal(
            ConcurrencyPolicy.ThirdPartyLiveLoadLimit,
            LiveLoadBudget.HeldAt(LiveLoadBudget.HostOf(Host)));

        // The refusal names who is holding the budget — "exhausted" is not actionable; "PooledFixture
        // holds 2" is.
        Assert.Contains("PooledFixture holds 2", sequential.Explanation, StringComparison.Ordinal);

        pool.Dispose();
        sequential.Dispose();
        Assert.Equal(0, LiveLoadBudget.HeldAt(LiveLoadBudget.HostOf(Host)));
    }

    /// <summary>
    /// <b>Two third parties are not one third party.</b> A courtesy limit on <c>newrecruit.eu</c> says
    /// nothing about <c>giloushaker.github.io</c>: each host gets the whole limit.
    /// </summary>
    /// <remarks>
    /// Falsifiable: key the budget globally instead of per host and the second reservation returns 0.
    /// </remarks>
    [Fact]
    public void Reserve_BudgetsEachThirdPartySeparately()
    {
        using var a = LiveLoadBudget.Reserve("FixtureA", "https://one-site.invalid/app", ConcurrencyPolicy.ThirdPartyLiveLoadLimit);
        using var b = LiveLoadBudget.Reserve("FixtureB", "https://other-site.invalid/editor", ConcurrencyPolicy.ThirdPartyLiveLoadLimit);

        Assert.Equal(ConcurrencyPolicy.ThirdPartyLiveLoadLimit, a.Sessions);
        Assert.Equal(ConcurrencyPolicy.ThirdPartyLiveLoadLimit, b.Sessions);

        // ...and the same site under a different path is still the same site.
        using var sameSiteAgain = LiveLoadBudget.Reserve("FixtureC", "https://one-site.invalid/somewhere-else", 1);
        Assert.Equal(0, sameSiteAgain.Sessions);
    }

    /// <summary>
    /// A partially-available budget grants what is left rather than nothing — and rather than the whole
    /// ask. The pooled fixture then opens exactly that many contexts.
    /// </summary>
    [Fact]
    public void Reserve_GrantsWhatIsLeft_NotWhatWasAsked()
    {
        const string Host = "https://partial.invalid";

        using var first = LiveLoadBudget.Reserve("FirstFixture", Host, 1);
        Assert.Equal(1, first.Sessions);

        using var pool = LiveLoadBudget.Reserve("PoolFixture", Host, ConcurrencyPolicy.ThirdPartyLiveLoadLimit);

        Assert.Equal(ConcurrencyPolicy.ThirdPartyLiveLoadLimit - 1, pool.Sessions);
        Assert.False(pool.Full);
        Assert.Equal(
            ConcurrencyPolicy.ThirdPartyLiveLoadLimit,
            LiveLoadBudget.HeldAt(LiveLoadBudget.HostOf(Host)));
    }

    /// <summary>
    /// Returning a lease returns the sessions — a fixture that has torn its browsers down is not still
    /// costing the site anything, and the next lane must not be throttled by a ghost.
    /// </summary>
    [Fact]
    public void Dispose_ReturnsTheSessions_AndIsIdempotent()
    {
        const string Host = "https://returned.invalid";

        var lease = LiveLoadBudget.Reserve("Fixture", Host, ConcurrencyPolicy.ThirdPartyLiveLoadLimit);
        Assert.Equal(ConcurrencyPolicy.ThirdPartyLiveLoadLimit, LiveLoadBudget.HeldAt(LiveLoadBudget.HostOf(Host)));

        lease.Dispose();
        lease.Dispose(); // a fixture's DisposeAsync can run twice; it must not credit the budget twice

        Assert.Equal(0, LiveLoadBudget.HeldAt(LiveLoadBudget.HostOf(Host)));

        using var next = LiveLoadBudget.Reserve("NextFixture", Host, ConcurrencyPolicy.ThirdPartyLiveLoadLimit);
        Assert.Equal(ConcurrencyPolicy.ThirdPartyLiveLoadLimit, next.Sessions);
    }

    /// <summary>
    /// <b>The budget IS the policy's constant — not a second copy of it.</b> Two places deciding one
    /// number is the defect this whole branch exists to remove.
    /// </summary>
    /// <remarks>
    /// Falsifiable: hardcode a <c>2</c> in <c>LiveLoadBudget</c> and this goes red the moment
    /// <c>ThirdPartyLiveLoadLimit</c> moves — which is the only time it would matter, and exactly when
    /// nobody would be looking.
    /// </remarks>
    [Fact]
    public void PerHostLimit_IsTheOnePolicyConstant()
    {
        Assert.Equal(ConcurrencyPolicy.ThirdPartyLiveLoadLimit, LiveLoadBudget.PerHostLimit);

        // And the live pool the policy sizes can never ask for more than the budget can grant: the two
        // agree because they are the same number, applied twice, not two numbers that happen to match.
        Assert.True(
            FixtureConcurrency.PoolSizeFor("newrecruit", LoadTarget.ThirdPartyLive) <= LiveLoadBudget.PerHostLimit,
            "the live pool asks for more sessions than the harness is allowed to hold at one third party");
    }
}
