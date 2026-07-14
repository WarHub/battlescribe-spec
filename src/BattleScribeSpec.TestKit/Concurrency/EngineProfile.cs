namespace BattleScribeSpec.Concurrency;

/// <summary>Is an engine's cold start expensive enough that reusing it could pay for itself?</summary>
public enum ColdStartCost
{
    /// <summary>Cheap to construct — reuse buys nothing. A headless Chromium relaunches in ~1.6s.</summary>
    Cheap,

    /// <summary>Expensive to construct — reuse is where the win is. A JVM + JavaFX launch, per spec.</summary>
    Expensive,
}

/// <summary>
/// What an engine declares about itself. The policy derives every number from this plus a
/// <see cref="MachineProfile"/>; nothing string-matches an engine's name.
/// </summary>
/// <param name="MaxParallel">Hard ceiling on concurrent instances — of EITHER axis; 0 = unlimited.</param>
/// <param name="ColdStartCost">Whether reuse can pay for itself at all.</param>
/// <param name="ReuseSafeRoster">May the roster engine be reused across setups without changing verdicts?</param>
/// <param name="ReuseSafeGameData">May the gamedata engine be reused across setups without changing verdicts?</param>
/// <param name="MemPerInstanceBytes">
/// <b>PROCESS AXIS.</b> Measured memory cost of one whole adapter <em>process family</em> (adapter +
/// its own Node driver + its own browser tree) — 1.22–1.44 GiB for the NR engines. 0 = undeclared,
/// which is what makes <c>ConcurrencyPolicy.UndeclaredMemoryWorkerCap</c> bind. <b>Not</b> the cost
/// of a browser context: see <see cref="MemPerContextBytes"/>, which is ~6× smaller.
/// </param>
/// <param name="OversubscriptionFactor">
/// <b>PROCESS AXIS.</b> The `k` in `workers ≈ cpuCount × k`. MEASURED per engine, never guessed — the
/// engines demonstrably disagree (1.0 vs 0.375 on the same box). <b>It sizes worker PROCESSES only.</b>
/// There is deliberately no equivalent factor on the context axis: the measurements say that optimum
/// does not move with CPU count at all (see <see cref="ContextPoolSize"/>).
/// </param>
/// <param name="ContextPoolSize">
/// <b>CONTEXT AXIS — an ABSOLUTE count, NOT a factor of <c>cpuCount</c>.</b> The measured optimal
/// number of browser <em>contexts</em> in one in-process pool (xUnit fixtures:
/// <c>Parallel.ForEachAsync(MaxDegreeOfParallelism = pool.Size)</c> inside a single <c>[Fact]</c>).
/// 0 = undeclared → <c>ConcurrencyPolicy.UndeclaredContextPoolSize</c>.
/// <para>
/// <b>This axis is CONTENTION-bound, not CPU-bound, and that is measured, not assumed.</b> All the
/// contexts in a pool share ONE Chromium and ONE Playwright Node driver, and every CDP message
/// funnels through that single driver — a hard serialization point that no number of cores relieves.
/// The optimum was swept on a 32-core box AND on a 4-CPU/16 GiB container and came out
/// <b>identical on both</b> (<c>newrecruit</c> 4, <c>newrecruit-ui</c> 16), while as a
/// <em>fraction</em> of cpuCount the two boxes disagree by exactly the 8× core ratio. The cleanest
/// single data point: <c>newrecruit-ui</c> at pool=1 takes 240.05 s on 32 CPUs and 241.17 s on 4
/// CPUs — an 8× CPU cut costs 0.5%.
/// </para>
/// <para>
/// So DO NOT "improve" this into a <c>ceil(cpuCount × k)</c> expression. That shape is what produced
/// the bug this field exists to fix: <c>PoolSize: workers</c> mirrored the process-axis number onto
/// this axis and under-provisioned CI by 2× (docs/concurrency-policy-measurements.md §7).
/// Overshooting hurts too — four to six levels past the optimum costs up to +77% wall-clock.
/// </para>
/// </param>
/// <param name="MemPerContextBytes">
/// <b>CONTEXT AXIS.</b> Measured memory cost of ONE additional browser context (the least-squares
/// slope across a pool sweep — each context adds exactly one Chromium renderer). ≈215–225 MiB for the
/// NR engines: <b>~6× cheaper than <see cref="MemPerInstanceBytes"/></b>, which is precisely the
/// over-charge the old mirror made. 0 = undeclared → no memory bound on the pool (the small default
/// pool size is then the only bound).
/// <para>
/// The slope excludes the pool's fixed baseline (≈1.0–1.6 GiB of shared browser + driver + test
/// host), so on a very small box the memory bound is optimistic by roughly that much;
/// <c>ConcurrencyPolicy.MemoryHeadroomFactor</c> is the margin that absorbs it. Memory does not bind
/// at the measured optima on any machine we run: pool 16 peaks at ≈6.2 GiB of a 16 GiB runner.
/// </para>
/// </param>
/// <remarks>
/// <para>
/// <b>The two axes are separate facts and must stay separate.</b> <see cref="MemPerInstanceBytes"/> /
/// <see cref="OversubscriptionFactor"/> size adapter <em>processes</em> on the CLI path
/// (<c>bs-spec run --all</c>). <see cref="ContextPoolSize"/> / <see cref="MemPerContextBytes"/> size
/// browser <em>contexts</em> on the xUnit path (<c>dotnet test</c> — what every NewRecruit CI lane
/// runs). No number is shared between them, deliberately: the whole bug was two quantities wearing
/// one name.
/// </para>
/// <para>
/// <b><see cref="ReuseSafeRoster"/> and <see cref="ReuseSafeGameData"/> are EARNED, not asserted.</b>
/// An engine may only claim reuse-safety for a domain where <c>bs-spec compare</c> has demonstrated
/// verdict-equality against a cold arm. The one time this was claimed without evidence
/// (newrecruit-ui roster) it silently changed six spec verdicts while a stopwatch reported success.
/// </para>
/// <para>
/// Note reuse needs BOTH properties: <c>reuse ⟺ ReuseSafe(domain) ∧ ColdStartCost == Expensive</c>.
/// "Is it correct?" and "is it worth anything?" are different questions. Reusing a NewRecruit
/// browser is perfectly safe and buys 0.92× — i.e. nothing — so enabling it would add a warm-state
/// failure mode for no gain. A bad trade even when it is a correct one.
/// </para>
/// </remarks>
public sealed record EngineProfile(
    int MaxParallel,
    ColdStartCost ColdStartCost,
    bool ReuseSafeRoster,
    bool ReuseSafeGameData,
    long MemPerInstanceBytes = 0,
    double OversubscriptionFactor = 1.0,
    int ContextPoolSize = 0,
    long MemPerContextBytes = 0);
