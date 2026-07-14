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
    /// <para>
    /// <b>The obvious assertion here has no teeth, and this test used to be it.</b>
    /// <c>Assert.Equal(ThirdPartyLiveLoadLimit, LiveLoadBudget.PerHostLimit)</c> where
    /// <c>PerHostLimit => ConcurrencyPolicy.ThirdPartyLiveLoadLimit</c> is <c>X == X</c>: hardcoding a
    /// <c>2</c> in <c>LiveLoadBudget</c> — the exact mutation its own docstring promised would go red —
    /// left it <b>green</b>, because both sides are read at the same instant from whatever the code says.
    /// A gate can only see a divergence it is capable of observing, and a compile-time constant folded
    /// into both operands is not one.
    /// </para>
    /// <para>
    /// <b>Falsifiable:</b> replace the expression body of <c>LiveLoadBudget.PerHostLimit</c> with any
    /// literal and the first assertion goes red — it reads the source, which is the only place the two
    /// can actually be seen to differ. Verified by mutation.
    /// </para>
    /// </remarks>
    [Fact]
    public void PerHostLimit_IsTheOnePolicyConstant()
    {
        // The mechanical pin: the budget must DERIVE its limit, in source, not restate it.
        var source = File.ReadAllText(Path.Combine(
            ConcurrencyConfigurationDriftTests.RepoRoot, "tests", "Infrastructure", "LiveLoadBudget.cs"));

        Assert.Contains(
            $"public static int {nameof(LiveLoadBudget.PerHostLimit)} => " +
            $"{nameof(ConcurrencyPolicy)}.{nameof(ConcurrencyPolicy.ThirdPartyLiveLoadLimit)};",
            source,
            StringComparison.Ordinal);

        Assert.Equal(ConcurrencyPolicy.ThirdPartyLiveLoadLimit, LiveLoadBudget.PerHostLimit);

        // And the live pool the policy sizes can never ask for more than the budget can grant: the two
        // agree because they are the same number, applied twice, not two numbers that happen to match.
        Assert.True(
            FixtureConcurrency.PoolSizeFor("newrecruit", LoadTarget.ThirdPartyLive) <= LiveLoadBudget.PerHostLimit,
            "the live pool asks for more sessions than the harness is allowed to hold at one third party");
    }

    /// <summary>
    /// <b>A fixture that fails to open its session must not keep the permit.</b> Engine construction is
    /// exactly the step that throws when the live site is down — and a leaked permit turns that outage
    /// into a <em>skip that blames the load budget</em>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reproduced before it was fixed</b> (loopback, no third-party traffic): with
    /// <c>NR_ENGINE_URL=http://127.0.0.1:1</c>, two smoke tests failed to construct an engine, each
    /// orphaning a permit, and the third test — which would have failed for the same honest reason —
    /// <c>Skipped</c> instead, reporting <i>"SequentialLiveNrRosterFixture ... was granted 0 ... in this
    /// test process SequentialLiveNrRosterFixture holds 1, SequentialLiveNrRosterFixture holds 1"</i>. Two
    /// permits, from a fixture with <b>zero</b> sessions open. Composed under
    /// <c>-p:TestProfile=nr-live</c>, those two starve <see cref="LiveNrRosterFixture"/> and all 363 live
    /// conformance tests skip for a site outage. "A skip that misreports its reason is how a throttled lane
    /// comes to look like an unconfigured one."
    /// </para>
    /// <para>
    /// <b>Falsifiable:</b> remove the <c>catch { Dispose(); throw; }</c> from
    /// <see cref="LiveLoadLease.Open{T}"/> and both assertions go red — the permit stays held and the
    /// second reservation is starved. Forced failure, not a live outage: nothing here opens a browser.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Open_ReturnsThePermit_WhenOpeningTheSessionThrows()
    {
        const string Host = "https://outage.invalid";

        var lease = LiveLoadBudget.Reserve("FailingFixture", Host, ConcurrencyPolicy.ThirdPartyLiveLoadLimit);
        Assert.Equal(ConcurrencyPolicy.ThirdPartyLiveLoadLimit, LiveLoadBudget.HeldAt(LiveLoadBudget.HostOf(Host)));

        // The site is down: the engine never comes up. The failure must PROPAGATE (a broken site is a
        // failure, not a skip)...
        Assert.Throws<HttpRequestException>(
            () => lease.Open<object>(() => throw new HttpRequestException("connection refused")));

        // ...and it must not have cost the site's budget anything: no session was ever opened.
        Assert.Equal(0, LiveLoadBudget.HeldAt(LiveLoadBudget.HostOf(Host)));

        var asyncLease = LiveLoadBudget.Reserve("FailingAsyncFixture", Host, 1);
        await Assert.ThrowsAsync<HttpRequestException>(
            () => asyncLease.OpenAsync<object>(() => throw new HttpRequestException("connection refused")));
        Assert.Equal(0, LiveLoadBudget.HeldAt(LiveLoadBudget.HostOf(Host)));

        // The next fixture — the one that used to be skipped, blaming a budget held by two ghosts — gets
        // the whole budget, because nothing is holding it.
        using var next = LiveLoadBudget.Reserve("NextFixture", Host, ConcurrencyPolicy.ThirdPartyLiveLoadLimit);
        Assert.Equal(ConcurrencyPolicy.ThirdPartyLiveLoadLimit, next.Sessions);
    }

    /// <summary>
    /// One server, one budget: two spellings of the same host must not each get the whole limit. Case,
    /// port, path and scheme already folded; a trailing dot and a unicode/punycode pair did not.
    /// </summary>
    /// <remarks>
    /// Falsifiable: use <c>Uri.Host</c> instead of <c>Uri.IdnHost</c>, or drop the <c>TrimEnd('.')</c>, in
    /// <c>LiveLoadBudget.HostOf</c>, and the corresponding assertion grants a session that doubles the load
    /// on one site.
    /// </remarks>
    [Theory]
    [InlineData("https://one-server.invalid/app", "https://ONE-SERVER.INVALID:443/other")]  // case + port + path
    [InlineData("https://two-server.invalid/app", "https://two-server.invalid./app")]       // fully-qualified trailing dot
    public void HostOf_TwoSpellingsOfOneServer_ShareOneBudget(string first, string second)
    {
        using var held = LiveLoadBudget.Reserve("FirstFixture", first, ConcurrencyPolicy.ThirdPartyLiveLoadLimit);
        Assert.Equal(ConcurrencyPolicy.ThirdPartyLiveLoadLimit, held.Sessions);

        using var sameServer = LiveLoadBudget.Reserve("SecondFixture", second, 1);

        Assert.Equal(0, sameServer.Sessions);
        Assert.Equal(LiveLoadBudget.HostOf(first), LiveLoadBudget.HostOf(second));
    }
}
