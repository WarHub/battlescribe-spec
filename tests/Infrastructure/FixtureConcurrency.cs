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
    /// Ceiling on any single fixture's in-process pool, applied on top of the policy's
    /// <see cref="ConcurrencyPlan.PoolSize"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A defensive bound over an unmeasured path, not a fitted constant.</b> Every sweep in the
    /// measurement campaign (<c>docs/concurrency-policy-measurements.md</c>) drove the <b>CLI</b>
    /// path, where a worker is a whole adapter <em>process family</em> (adapter + Node driver +
    /// browser tree) and exactly one pool exists per process. The xUnit path is a different shape,
    /// and it was never measured:
    /// </para>
    /// <para>
    /// 1. <b>The unit differs.</b> <see cref="ConcurrencyPlan.PoolSize"/> mirrors the worker count,
    /// but a fixture pool's elements are in-process browser <em>contexts</em> sharing one browser and
    /// one Node driver. <c>MemPerInstanceBytes</c> over-charges for those, so the sizing errs
    /// conservatively — it can over-provision, not OOM.
    /// </para>
    /// <para>
    /// 2. <b>The product across collections is unbounded.</b> Real concurrency inside a conformance
    /// test is <c>Parallel.ForEachAsync(MaxDegreeOfParallelism = pool.Size)</c> within a single
    /// <c>[Fact]</c> — which xUnit's <c>maxParallelThreads</c> does not constrain at all. Collection
    /// fixtures live for the whole collection, so several pools can be alive simultaneously and
    /// nothing bounds the sum. Tracked in <b>issue #314</b>; a shared budget the pools draw from is
    /// the real fix. This cap is the interim guard, not that fix.
    /// </para>
    /// <para>
    /// The value keeps the worst-case composed context count on a big box in the region the machine
    /// already held before the policy existed. The three NR fixtures previously defaulted to 5 / 5 /
    /// 10 contexts (≤ 20 live at once); the policy uncapped asks this 32-core box for 32 / 12 / 12
    /// (≤ 56). Capped at 8: ≤ 24 — no lower than the old frozen defaults, and no step-change upward.
    /// It does not bind on the 4-vCPU CI runner (which the policy sizes at 2–4), so CI is unchanged.
    /// </para>
    /// </remarks>
    internal const int FixturePoolCap = 8;

    /// <summary>
    /// The <see cref="ConcurrencyPlan"/> for the named built-in engine (e.g. <c>"newrecruit"</c>,
    /// <c>"battlescribe-ui"</c>) on the real machine this process is running on.
    /// </summary>
    /// <param name="engineName">Built-in engine identity.</param>
    public static ConcurrencyPlan Resolve(string engineName)
    {
        var profile = EngineRegistry.LoadDefault().Resolve(EngineConnectable.Parse(engineName)).Profile;
        return ConcurrencyPolicy.For(MachineProfile.Current(), profile);
    }

    /// <summary>
    /// The pool size a fixture may use for <paramref name="engineName"/>: the policy's answer,
    /// bounded by <see cref="FixturePoolCap"/>. Fixtures call this rather than reading
    /// <see cref="ConcurrencyPlan.PoolSize"/> directly.
    /// </summary>
    /// <param name="engineName">Built-in engine identity.</param>
    public static int PoolSizeFor(string engineName) => CapPoolSize(Resolve(engineName).PoolSize);

    /// <summary>The pure half of <see cref="PoolSizeFor"/>, so the cap is testable on any machine.</summary>
    /// <param name="planPoolSize">The policy's unbounded answer for this machine and engine.</param>
    internal static int CapPoolSize(int planPoolSize) => Math.Min(planPoolSize, FixturePoolCap);
}
