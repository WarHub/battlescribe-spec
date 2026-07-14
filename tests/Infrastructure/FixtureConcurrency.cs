using BattleScribeSpec.Concurrency;
using BattleScribeSpec.Engines;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Resolves every xUnit fixture's concurrency and reuse decision from <see cref="ConcurrencyPolicy"/>
/// — the single source of every such decision in the harness. No fixture reads an environment
/// variable to decide how parallel it is, or whether it reuses its engine.
/// </summary>
/// <remarks>
/// <para>
/// Before this, the NR fixtures each read <c>NR_PARALLEL</c> with a <em>different</em> hardcoded
/// default (5 / 10 / 5, plus a fourth copy of those defaults in the pool factories' signatures),
/// and <c>BsGameDataUiFixture</c> read <c>BS_UI_KEEP_ALIVE</c> to decide reuse — a knob that
/// answered the same question as <see cref="ConcurrencyPlan.ReuseGameData"/> and <em>disagreed</em>
/// with it by default (unset ⇒ cold; the policy says warm), which CI papered over by setting the
/// variable in two jobs. Both variables are now read nowhere in this repo.
/// </para>
/// </remarks>
internal static class FixtureConcurrency
{
    /// <summary>
    /// The <see cref="ConcurrencyPlan"/> for the named built-in engine (e.g. <c>"newrecruit"</c>,
    /// <c>"battlescribe-ui"</c>) on the real machine this process is running on, pointed at
    /// <paramref name="loadTarget"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><paramref name="loadTarget"/> deliberately has no default.</b> The engine name cannot answer
    /// it: <c>FrozenNrRosterFixture</c> and <c>LiveNrRosterFixture</c> both resolve <c>"newrecruit"</c>,
    /// and one replays a HAR file from local disk while the other drives a third party's production
    /// website. Every fixture must say which it is — that a fixture had no way to say so is precisely
    /// how the live lane's <c>NR_PARALLEL: 2</c> courtesy limit was deleted with the env var that
    /// carried it, and replaced by a pool size fitted against a HAR file
    /// (<c>ConcurrencyPolicy.ThirdPartyLiveLoadLimit</c>).
    /// </para>
    /// </remarks>
    /// <param name="engineName">Built-in engine identity.</param>
    /// <param name="loadTarget">Whether this fixture's engine is served by this machine or by a third party's live site.</param>
    public static ConcurrencyPlan Resolve(string engineName, LoadTarget loadTarget)
    {
        var profile = EngineRegistry.LoadDefault().Resolve(EngineConnectable.Parse(engineName)).Profile;
        return ConcurrencyPolicy.For(MachineProfile.Current(), profile, loadTarget);
    }

    /// <summary>
    /// The pool size a fixture uses for <paramref name="engineName"/>: <b>the policy's answer,
    /// unmodified</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There is deliberately no second cap here any more.</b> A blanket
    /// <c>FixturePoolCap = 8</c> used to sit on this line, justified as a defensive bound over "an
    /// unmeasured path" whose sizing "can over-provision, not OOM". The path has now been measured
    /// (docs/concurrency-policy-measurements.md §7) and the docstring was half right in the worst
    /// way: true of memory, false of time. <c>newrecruit-ui</c>'s measured optimum is a pool of
    /// <b>16</b>, so the 8 was silently halving it and costing that lane <b>31%</b> on this box —
    /// a defensive constant that cost more than the thing it defended against.
    /// </para>
    /// <para>
    /// What replaces it is not "nothing": <see cref="ConcurrencyPolicy.For"/> now bounds the pool by
    /// what the pool actually costs on this machine — its fixed baseline
    /// (<c>EngineProfile.MemPoolBaselineBytes</c>: the shared browser, driver and test host) plus the
    /// measured per-context slope (<c>MemPerContextBytes</c>), against <c>MemoryHeadroomFactor</c> of
    /// the machine's memory — and by <c>MaxContexts</c>, this axis's own ceiling. <b>Not</b> by
    /// <c>MaxParallel</c>: that one bounds adapter <em>processes</em>, and clamping the pool with it was
    /// one number governing two axes. A real bound derived from what a context actually costs, rather
    /// than a round number — and unlike the round number, it gets tighter on a small box instead of on a
    /// big one.
    /// </para>
    /// <para>
    /// The worst-case composed count across simultaneously-live collection fixtures (<b>issue
    /// #314</b>, still open) is <em>not</em> made worse by removing the cap: the three NR pools now
    /// ask for 2 + 4 + 16 = <b>22</b> contexts (the live pool being held at
    /// <c>ConcurrencyPolicy.ThirdPartyLiveLoadLimit</c>), against the 8-cap's 24 and the pre-policy
    /// defaults' 20. The composed bound was never this cap's to give; a shared budget the pools draw
    /// from is #314's business.
    /// </para>
    /// <para>
    /// <b>The one bound that is NOT the throughput answer</b> is <paramref name="loadTarget"/>: a pool
    /// pointed at <see cref="LoadTarget.ThirdPartyLive"/> is clamped to a load limit that no sweep may
    /// raise. It is not a fixture-level cap sneaking back in — it lives in the policy, with the rest of
    /// the decisions, and it bounds a quantity (traffic to a stranger's server) that no measurement of
    /// ours is entitled to fit.
    /// </para>
    /// </remarks>
    /// <param name="engineName">Built-in engine identity.</param>
    /// <param name="loadTarget">Whether this pool's contexts are served by this machine or by a third party's live site.</param>
    public static int PoolSizeFor(string engineName, LoadTarget loadTarget) =>
        Resolve(engineName, loadTarget).PoolSize;
}
