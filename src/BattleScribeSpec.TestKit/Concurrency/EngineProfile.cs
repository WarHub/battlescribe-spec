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
/// <param name="MaxParallel">
/// <b>PROCESS AXIS.</b> Hard ceiling on concurrent adapter <em>processes</em>; 0 = unlimited. This is
/// the quantity the protocol puts on the wire (<c>describe</c> → <c>capabilities.maxParallel</c>,
/// docs/adapter-guide.md) and the one <c>RunBatch.ClampWorkers</c> applies to the worker count.
/// <para>
/// <b>It does NOT bound <see cref="ContextPoolSize"/>, and it used to.</b> The policy clamped the pool
/// by this number too, justified as "battlescribe-ui runs one JVM, and that is as true of a context
/// pool as of a worker process" — true of that engine, and a generalization of one engine's
/// coincidence into a cross-axis rule. The protocol documents this as a ceiling on <em>processes</em>,
/// so an adapter author writing <c>{"maxParallel": 2, "contextPoolSize": 4}</c> means "don't run more
/// than 2 of my processes" and would have silently lost half their measured pool. That is
/// <c>PoolSize: workers</c> again, in the other direction: one number, two axes. The context axis has
/// its own ceiling now — <see cref="MaxContexts"/>.
/// </para>
/// </param>
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
/// <b>CONTEXT AXIS — a MARGINAL SLOPE, not a total charge.</b> Measured memory cost of ONE
/// <em>additional</em> browser context (the least-squares slope across a pool sweep — each context
/// adds exactly one Chromium renderer). ≈215–225 MiB for the NR engines: <b>~6× cheaper than
/// <see cref="MemPerInstanceBytes"/></b>, which is precisely the over-charge the old mirror made.
/// 0 = undeclared → no memory bound on the pool (the small default pool size is then the only bound).
/// <para>
/// <b>It is HALF of a line, and it must be spent with the other half</b>
/// (<see cref="MemPoolBaselineBytes"/>, the intercept of the same regression). Charging
/// <c>slope × N</c> against the machine's memory and calling it the pool's cost is a marginal slope
/// consumed as a total charge — the whole shared browser, driver and test host counted nowhere. It
/// under-stated the real cost by ~1.3 GiB on the CI-class box, and
/// <c>ConcurrencyPolicy.MemoryHeadroomFactor</c> does <b>not</b> absorb that: the margin on the real
/// runner is 20% of 7.8 GiB = 1.56 GiB, and it has its own job (see the constant). The policy charges
/// both terms now. <c>EngineRegistry.Validate</c> rejects a config that declares one without the other.
/// </para>
/// </param>
/// <param name="MemPoolBaselineBytes">
/// <b>CONTEXT AXIS — the pool's FIXED cost, i.e. the INTERCEPT of the regression whose slope is
/// <see cref="MemPerContextBytes"/>.</b> One shared Chromium, one Playwright Node driver, one test
/// host: what the pool costs before its first context exists. Measured, per engine, on the CI-class
/// box, in the same fit as the slope (docs/concurrency-policy-measurements.md §7.7 — <c>newrecruit</c>
/// 1058 MiB, <c>newrecruit-ui</c> 1310 MiB; the 32-core Windows box fits 1220 / 1607 MiB). 0 =
/// undeclared, which is only safe alongside an undeclared slope — declare both or neither.
/// <para>
/// <b>Do not mix fits.</b> Taking the slope from one regression and the intercept from another is not
/// a line, and "take the larger of each" would be exactly that. Both built-ins take the CI-class fit,
/// whole, because CI is the machine that has to survive it.
/// </para>
/// </param>
/// <param name="MaxContexts">
/// <b>CONTEXT AXIS.</b> Hard ceiling on concurrent browser <em>contexts</em> in one in-process pool;
/// 0 = unlimited. The context axis's own <see cref="MaxParallel"/>, and it exists because that one is
/// not it: <see cref="MaxParallel"/> is a <em>process</em> ceiling, on the protocol wire, read by
/// adapter authors from docs/adapter-guide.md, and using it to clamp the pool made a declaration about
/// one axis silently govern the other.
/// <para>
/// Only <c>battlescribe-ui</c> declares it (1): it drives ONE JavaFX desktop app through one Java
/// agent, so a pool of contexts is meaningless for it — the same fact its <c>MaxParallel: 1</c>
/// states, but stated separately, because they are separate facts that happen to coincide. Nothing
/// else needs it: <c>PoolSize</c> is not on the protocol wire, no adapter reads it, and the two
/// measured pools (4 and 16) are already bounded by the machine's memory
/// (<see cref="MemPoolBaselineBytes"/> + N × <see cref="MemPerContextBytes"/>).
/// </para>
/// </param>
/// <remarks>
/// <para>
/// <b>The two axes are separate facts and must stay separate.</b> <see cref="MaxParallel"/> /
/// <see cref="MemPerInstanceBytes"/> / <see cref="OversubscriptionFactor"/> size adapter
/// <em>processes</em> on the CLI path (<c>bs-spec run --all</c>). <see cref="MaxContexts"/> /
/// <see cref="ContextPoolSize"/> / <see cref="MemPerContextBytes"/> / <see cref="MemPoolBaselineBytes"/>
/// size browser <em>contexts</em> on
/// the xUnit path (<c>dotnet test</c> — what every NewRecruit CI lane runs). No number is shared
/// between them, deliberately: the whole bug was two quantities wearing one name. <see cref="MaxParallel"/>
/// was the last one still shared, and it is not any more.
/// </para>
/// <para>
/// <b>The two memory numbers are not measured the same way, and that is why only one axis carries a
/// baseline term.</b> <see cref="MemPerInstanceBytes"/> is a <em>total</em> — peak RSS over a whole run
/// divided by the worker count — and a worker <em>is</em> a whole process family (its own adapter, its
/// own Node driver, its own browser tree: §3/§5), so there is no shared fixed cost left uncharged.
/// <see cref="MemPerContextBytes"/> is a <em>slope</em>: every context in a pool shares ONE browser
/// behind ONE driver, so the pool has a large fixed cost that no per-context number can carry — and
/// that is <see cref="MemPoolBaselineBytes"/>. One phrase ("the memory cost"), two quantities. Charging
/// the slope as if it were the total is the same defect as <c>PoolSize: workers</c>, one field down.
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
    long MemPerContextBytes = 0,
    int MaxContexts = 0,
    long MemPoolBaselineBytes = 0);
