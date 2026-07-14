namespace BattleScribeSpec.Concurrency;

/// <summary>
/// The single source of every concurrency and reuse decision in the harness. A <b>pure function</b>
/// of the machine, what the engine declares about itself, and <b>who is on the other end</b>
/// (<see cref="LoadTarget"/>) — no I/O, no environment variables, no string-matching on engine names.
/// </summary>
/// <remarks>
/// <para>
/// The third input is not a performance parameter and was not there originally, which is exactly how
/// a courtesy limit on a third party's website got replaced by a constant fitted against a HAR file.
/// See <see cref="ThirdPartyLiveLoadLimit"/>.
/// </para>
/// </remarks>
public static class ConcurrencyPolicy
{
    /// <summary>
    /// The permanent, conservative worker ceiling for any engine that has <b>not declared</b> its
    /// per-instance memory footprint (<see cref="EngineProfile.MemPerInstanceBytes"/> == 0).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not a stopgap.</b> It began life as one (a provisional cap pending the Task 8
    /// measurement campaign), but it is gated on <c>MemPerInstanceBytes == 0</c>, which means it
    /// <em>self-retires per engine</em>: the moment an engine declares a measured footprint, the
    /// real memory bound in <see cref="For"/> governs and this cap stops applying to it — no code
    /// change needed. Its consumers today are <c>battlescribe</c> (the one built-in still unmeasured),
    /// <c>EngineRegistry.DefaultProfile</c>, and every third-party engine registered via
    /// <c>engines.json</c> that omits <c>memPerInstanceBytes</c> — this harness is explicitly open to
    /// other engines, so "every engine is measured" is a state it can never reach. Deleting this cap
    /// would send every such engine straight back to unbounded <c>cpuCount</c> workers on a big box.
    /// </para>
    /// <para>
    /// <b>And an unmeasured engine can hide a cliff.</b> That is not hypothetical: <c>newrecruit</c>
    /// was unmeasured when this cap was written, and the sweep that finally measured it (§5 of
    /// <c>docs/concurrency-policy-measurements.md</c>) found a <b>1.97× wall-clock cliff one worker
    /// past its optimum</b> — 32 workers on the dev box measures <b>58.9 s against 23.1 s</b> at the
    /// capped 8. Had the cap been deleted while that engine still declared nothing, it would have
    /// been handed <c>cpuCount</c> workers and driven straight over it. <c>battlescribe</c> is in
    /// exactly that position today, and nobody knows where its cliff is either.
    /// </para>
    /// <para>
    /// <b>Declaring <see cref="EngineProfile.MemPerInstanceBytes"/> is how an engine opts into full
    /// machine-width parallelism.</b> Until it does, it gets <c>min(cpuCount, 8)</c> — slower than
    /// optimal, but it cannot OOM a laptop. That is the right default for code we did not measure
    /// and did not write.
    /// </para>
    /// <para>
    /// <b>This is an engine worker cap, and nothing else.</b> It used to be borrowed as the value of
    /// <c>maxParallelThreads</c> in the two <c>xunit.runner.json</c> files, mechanically pinned to
    /// it by <c>ConcurrencyConfigurationDriftTests</c> — a coupling between two quantities that share
    /// no meaning (a memory-safety ceiling for unmeasured engines vs the test runner's own thread
    /// count), such that raising this cap would silently have re-sized the test host. That link is
    /// cut: the xUnit value now carries its own constant and its own justification, in
    /// <c>ConcurrencyConfigurationDriftTests</c>. Raising or lowering this cap no longer touches the
    /// test runner.
    /// </para>
    /// <para>
    /// The reason xUnit's thread count cannot simply come from <see cref="For"/>: the runner reads
    /// that static JSON <em>before any of this code executes</em>. The obvious alternative was
    /// investigated and rejected — xunit.v3's VSTest RunSettings override (confirmed live via
    /// <c>Xunit.Runner.VisualStudio.RunSettings.Parse</c>: an
    /// <c>&lt;xUnit&gt;&lt;MaxParallelThreads&gt;</c> element in a <c>.runsettings</c> file, or
    /// <c>dotnet test -- xUnit.MaxParallelThreads=&lt;value&gt;</c>, both genuinely honored by this
    /// repo's adapter) is read by the runner at the same point, so it cannot call <see cref="For"/>
    /// either. It would only move a static literal from one file format to another, at the cost of a
    /// second file to keep in sync. Making the value truly dynamic would need an external wrapper
    /// script that computes it and rewrites the config before invoking the runner — reintroducing the
    /// wrapper-script axis this design exists to remove. The value therefore stays declarative, and
    /// uses xUnit's own machine-relative multiplier syntax instead of a hardcoded thread count.
    /// </para>
    /// </remarks>
    internal const int UndeclaredMemoryWorkerCap = 8;

    /// <summary>
    /// Fraction of <see cref="MachineProfile.AvailableMemoryBytes"/> the engines may claim. Safety
    /// margin is <b>policy</b> and lives here, exactly once — never baked into an engine's
    /// <see cref="EngineProfile.MemPerInstanceBytes"/>, which must stay an honest measured number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without it the memory bound plans to consume <b>100% of available memory</b>, which is unsafe
    /// in two independent ways that point the same direction:
    /// </para>
    /// <para>
    /// 1. <b>A sampled peak is a lower bound.</b> <c>MemPerInstanceBytes</c> was measured by polling
    /// working set on an interval (and <c>ResourceMetrics</c> documents the same limitation about
    /// its own 2 s export interval): a spike shorter than the sampling period is invisible. The true
    /// peak is <em>at least</em> what we recorded, never less.
    /// </para>
    /// <para>
    /// 2. <b>"Available" is not "spare".</b> <see cref="MachineProfile.AvailableMemoryBytes"/> is
    /// <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c> — <em>total</em> physical memory (or a
    /// cgroup limit), not currently-free memory. The OS, the page cache, the parent CLI and the test
    /// host all need room inside that number. A 16 GiB box never has 16 GiB free.
    /// </para>
    /// <para>
    /// And OOM is a <b>cliff, not a gradient</b>: one worker too few costs a little wall-clock; one
    /// too many kills the whole run. The asymmetry is why the margin is generous rather than tight.
    /// </para>
    /// <para>
    /// <b>Worked examples</b> (with <c>newrecruit-ui</c>'s measured
    /// <c>MemPerInstanceBytes = 1,548,969,984</c> and <c>k = 1.0</c>):
    /// </para>
    /// <para>
    /// • <i>32-core / 93.6 GiB dev box</i> (the machine the knee was measured on):
    /// <c>byMemory = floor(100,451,844,096 × 0.8 / 1,548,969,984) = 51</c>;
    /// <c>byCpu = 32</c> → <b>32 workers</b>. CPU still binds with room to spare (51 ≫ 32), so the
    /// headroom factor costs nothing here — the policy reproduces the empirically measured knee of
    /// P=32 exactly, and the harness is not slowed below measured-optimal.
    /// </para>
    /// <para>
    /// • <i>16 GiB laptop</i>: <c>byMemory = floor(17,179,869,184 × 0.8 / 1,548,969,984) = 8</c>
    /// → <b>8 workers</b> (memory binds on any laptop with ≥ 8 cores). Those 8 claim ≈11.5 GiB of a
    /// nominal 16 GiB, leaving ≈4.5 GiB for the OS and everything else — which is the point: without
    /// the factor the same box would plan <c>floor(17,179,869,184 / 1,548,969,984) = 11</c> workers,
    /// ≈15.9 GiB, i.e. the entire machine, on a peak figure that is itself a lower bound.
    /// </para>
    /// </remarks>
    internal const double MemoryHeadroomFactor = 0.8;

    /// <summary>
    /// The conservative context-pool size for any engine that has <b>not declared</b> a measured one
    /// (<see cref="EngineProfile.ContextPoolSize"/> == 0). An <b>absolute count</b> — like the axis
    /// itself, it does not scale with <see cref="MachineProfile.CpuCount"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why 4, and why an absolute number is the honest default here.</b> This axis is bound by
    /// contention on the ONE Playwright driver every context in a pool shares, not by CPU — the two
    /// engines that were swept have the same optimum on a 32-core box and on a 4-CPU container
    /// (docs/concurrency-policy-measurements.md §7.4). 4 is the <em>smaller</em> of the two measured
    /// optima (<c>newrecruit</c> 4, <c>newrecruit-ui</c> 16), which makes it the safe end of the only
    /// evidence that exists: it is exactly optimal for one measured engine and merely slower — never
    /// degraded — for the other.
    /// </para>
    /// <para>
    /// <b>Undershooting and overshooting are not symmetric, and the asymmetry points down.</b> Past
    /// the optimum this axis degrades hard and monotonically: <c>newrecruit</c> at pool 32 is
    /// <b>+77%</b> wall-clock against its optimum of 4, over six consecutive worsening levels. Below
    /// the optimum you merely leave throughput on the table. For an engine nobody has swept, sitting
    /// at the low end of the measured band is the cheap mistake to make.
    /// </para>
    /// <para>
    /// It is also memory-trivial (4 × ≈225 MiB ≈ 0.9 GiB at the measured slope), so an engine that
    /// declares no <see cref="EngineProfile.MemPerContextBytes"/> — and therefore gets no memory
    /// bound on its pool — still cannot hurt a small box with it.
    /// </para>
    /// </remarks>
    internal const int UndeclaredContextPoolSize = 4;

    /// <summary>
    /// <b>A LOAD LIMIT ON A THIRD PARTY'S LIVE WEBSITE. NOT A THROUGHPUT KNOB.</b> The most concurrent
    /// browser sessions this harness may point at someone else's production service
    /// (<see cref="LoadTarget.ThirdPartyLive"/>) — on <em>either</em> axis, because what the remote host
    /// feels is requests in flight, and it does not care whether we spawned them as worker processes or
    /// as browser contexts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THIS NUMBER MUST NEVER BE "OPTIMIZED" BY A SWEEP. RAISING IT INCREASES LOAD ON SOMEONE ELSE'S
    /// WEBSITE.</b> If you are reading this because a 2 looks small next to
    /// <see cref="EngineProfile.ContextPoolSize"/> = 4 (or <c>newrecruit-ui</c>'s 16) and you would like
    /// to bring them into line: <b>stop.</b> That is not a tidy-up, it is a decision to send twice the
    /// traffic to a website we do not own, and it has already been made once by accident. Read the next
    /// two paragraphs before you touch this line.
    /// </para>
    /// <para>
    /// <b>Where the 2 comes from — verbatim, from the commit that set it</b> (<c>7e65836</c>,
    /// 2026-07-12, <i>"ci: NR_PARALLEL 4 -&gt; 6 on the frozen NR lanes (measured optimum on real
    /// runners)"</i>):
    /// </para>
    /// <para>
    /// <i>"The live nr-conformance lane stays at 2 — it drives the real newrecruit.eu, so parallelism
    /// there is a load question, not a throughput one."</i>
    /// </para>
    /// <para>
    /// Note what that commit <b>is</b>: a sweep result. It raised the frozen lanes to their measured
    /// optimum and <b>deliberately declined to apply itself to the live lane</b>. So this 2 is not an
    /// unmeasured number waiting for someone to measure it — it is <em>deliberately</em> not measured,
    /// because the quantity it bounds is not ours to optimize. A sweep can tell you how fast
    /// newrecruit.eu will answer 8 concurrent sessions. It cannot tell you whether we are entitled to
    /// ask.
    /// </para>
    /// <para>
    /// <b>How it was lost, so that it cannot be lost the same way twice.</b> The 2 lived in a
    /// <c>NR_PARALLEL: 2</c> environment variable in <c>ci.yml</c>. The concurrency model deleted that
    /// variable — correctly: it was a second place to decide a question the policy owns — but the
    /// <em>constraint</em> it carried had nowhere in the model to live, and was deleted along with it.
    /// It then survived by <b>coincidence</b>: the mirrored policy computed the live pool as
    /// <c>ceil(4 × 0.375) = 2</c>, the same 2 by accident. The axis separation (#314) broke the
    /// coincidence, and the live lane silently became <b>4</b> — fitted by sweeping <c>nr-frozen</c>
    /// (HAR replay, no network) on a 4-CPU container, a measurement that never touched newrecruit.eu.
    /// That was the first change to this lane's concurrency in the repo's history, and nobody chose it.
    /// Related: issue #318.
    /// </para>
    /// <para>
    /// <b>Nothing else bounds this load.</b> Not politeness, not the network, not the engine: a search
    /// of <c>src/BattleScribeSpec.NewRecruit/</c> for
    /// <c>retry|backoff|throttl|rate.?limit|429|Task.Delay|Thread.Sleep</c> returns <b>zero hits</b> —
    /// no pause between specs, no retry, no backoff, no 429 handling. This constant is the only thing
    /// standing between a 363-spec conformance run and a volunteer-run website.
    /// </para>
    /// <para>
    /// <b>The price is known and accepted.</b> Holding the live lane at 2 costs real wall-clock: CI
    /// measured that lane at ≈145 s with a pool of 4 against ≈230 s at 2 (§8.8 of
    /// docs/concurrency-policy-measurements.md). We pay those 85 s out of our own CI budget rather than
    /// out of someone else's bandwidth. If this lane must get faster, make it send <em>fewer</em>
    /// requests — not more of them at once.
    /// </para>
    /// <para>
    /// <b>Public because the harness has to be able to say the number out loud.</b> <c>bs-spec run --all</c>
    /// tells the user, on stderr, that it has held their run to this limit and why; the CLI's tests state
    /// the expected worker count in terms of this constant rather than duplicating a literal <c>2</c> that
    /// could drift away from it. It is exposed to be <em>read and reported</em>, not to be minimum'd into
    /// by hand: <see cref="ClampToLoadTarget"/> is the one place that applies it.
    /// </para>
    /// </remarks>
    public const int ThirdPartyLiveLoadLimit = 2;

    /// <summary>Derive the plan. Deterministic: the same machine and engine always give the same plan.</summary>
    /// <remarks>
    /// <para>
    /// <b>Two axes, computed independently. They must never be mirrored onto each other again.</b>
    /// <see cref="ConcurrencyPlan.Workers"/> counts adapter <em>processes</em> and scales with
    /// <see cref="MachineProfile.CpuCount"/>; <see cref="ConcurrencyPlan.PoolSize"/> counts browser
    /// <em>contexts</em> and <b>does not</b>. This method used to end with <c>PoolSize: workers</c> —
    /// one integer feeding two consumers that share no mechanism — which handed a constant fitted by
    /// sweeping processes to a pool of contexts and cost CI up to 2× on the lanes it governs. The
    /// measurements behind each axis are in docs/concurrency-policy-measurements.md (§1–§6 processes,
    /// §7 contexts).
    /// </para>
    /// </remarks>
    /// <param name="machine">The machine the run is happening on.</param>
    /// <param name="engine">What the engine declares about itself.</param>
    /// <param name="loadTarget">
    /// <b>Where the load lands.</b> <see cref="LoadTarget.ThirdPartyLive"/> clamps <em>both</em> axes to
    /// <see cref="ThirdPartyLiveLoadLimit"/>. This is a limit on someone else's website, not a tuning
    /// input — see the constant. It defaults to <see cref="LoadTarget.Local"/> (the "nothing leaves this
    /// machine" case, which is what every measurement in this repo was taken on); a caller that can
    /// reach a live third-party service <b>must</b> say so, and <c>FixtureConcurrency</c> — the xUnit
    /// path, which is where the live lane lives — gives it no default at all.
    /// <para>
    /// <b>Both paths now answer it.</b> The xUnit path answers per fixture (<c>FixtureConcurrency</c>).
    /// The CLI path answers per engine, at engine-resolution time: an engine declares where its service
    /// lives (<see cref="Engines.EngineEndpoint"/>) and <c>EngineSelection.LoadTarget</c> derives this
    /// value from that declaration plus the environment the child will see — which is how
    /// <c>bs-spec run --all --engine newrecruit</c> can tell a HAR file on local disk from
    /// <c>newrecruit.eu</c> when both resolve the same engine and the same
    /// <see cref="EngineProfile"/>. It could not, and it planned 12 browsers at the live site; the
    /// derivation lives in the caller because <b>this method must never string-match an engine name</b>.
    /// (Closed the §9.4 gap in docs/concurrency-policy-measurements.md.)
    /// </para>
    /// </param>
    /// <returns>The concurrency and reuse decisions for this machine/engine pair.</returns>
    public static ConcurrencyPlan For(
        MachineProfile machine, EngineProfile engine, LoadTarget loadTarget = LoadTarget.Local)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(engine);

        // ===== PROCESS AXIS (CLI: `bs-spec run --all` spawns this many adapter processes) =====

        // Scale with the machine...
        var byCpu = (int)Math.Ceiling(machine.CpuCount * engine.OversubscriptionFactor);

        // ...but memory binds before CPU on a big box with hungry instances. Without this a
        // 64-core machine launches 64 browsers and exhausts memory long before it saturates CPU.
        // Only MemoryHeadroomFactor of the machine's memory is on offer — see the constant for why
        // consuming 100% of "available" memory is not a safe plan.
        var claimableMemory = (long)(machine.AvailableMemoryBytes * MemoryHeadroomFactor);
        var byMemory = engine.MemPerInstanceBytes > 0
            ? (int)Math.Min(int.MaxValue, claimableMemory / engine.MemPerInstanceBytes)
            : int.MaxValue;

        var workers = Math.Max(1, Math.Min(byCpu, byMemory));

        // An engine that has not declared what one instance costs does not get the whole machine.
        // "Undeclared" is MemPerInstanceBytes <= 0, and the two halves of that condition must agree
        // with the memory bound above, which is gated on `> 0`. They did not: `== 0` here left a
        // NEGATIVE value escaping BOTH gates — byMemory became int.MaxValue (no bound) and this cap
        // did not fire — so a single minus sign in engines.json bought unbounded ceil(cpu × k)
        // workers of an engine nobody measured, which is the exact failure this cap exists to
        // prevent. EngineRegistry.Load now rejects a negative value outright; this gate is the
        // second line of defence, for a profile constructed in code rather than parsed from config.
        //
        // Undeclared is the PERMANENT conservative default (two built-ins and every third-party
        // engine that omits the field are in that position): declaring MemPerInstanceBytes is how an
        // engine opts into full machine-width parallelism, and doing so retires this cap for that
        // engine automatically — the gate simply stops firing. See UndeclaredMemoryWorkerCap.
        if (engine.MemPerInstanceBytes <= 0)
        {
            workers = Math.Min(workers, Math.Min(machine.CpuCount, UndeclaredMemoryWorkerCap));
        }

        // The engine's hard ceiling wins over everything. 0 = unlimited.
        if (engine.MaxParallel > 0)
        {
            workers = Math.Min(workers, engine.MaxParallel);
        }

        // ===== CONTEXT AXIS (xUnit: the fixture pool's browser contexts). =====
        //
        // NOTE WHAT IS ABSENT: machine.CpuCount. That is not an oversight and it is not laziness —
        // it is the measurement. The optimal pool is IDENTICAL on a 32-core box and on a 4-CPU
        // container for both engines that were swept (newrecruit 4, newrecruit-ui 16), because every
        // context in a pool talks to the SAME Chromium through the SAME Playwright Node driver: the
        // binding constraint is contention on that one driver, not cores. newrecruit-ui at pool=1
        // takes 240.05s on 32 CPUs and 241.17s on 4 CPUs — an 8x CPU cut costs 0.5%.
        //
        // The previous line here was `PoolSize: workers`, which made this axis a function of
        // ceil(cpuCount × k) — a shape the data refutes — and cost the CI lanes it governs up to 2x.
        // If you are about to reintroduce a cpuCount term here, read §7 of
        // docs/concurrency-policy-measurements.md first; Policy_PoolSize_IsIndependentOfCpuCount
        // exists to stop you.
        var declaredPool = engine.ContextPoolSize > 0 ? engine.ContextPoolSize : UndeclaredContextPoolSize;

        // Memory still bounds it — a context is ~6x cheaper than a worker process (≈225 MiB vs
        // ≈1.4 GiB), so this rarely binds (a 16 GiB runner affords ~58 contexts against a measured
        // optimum of 16), but "rarely" is not "never": a 4 GiB container is a real thing. Same
        // headroom factor as the process axis — the reasons for it (a sampled peak is a lower bound;
        // "available" is not "spare") are properties of the machine, not of the axis.
        var poolByMemory = engine.MemPerContextBytes > 0
            ? (int)Math.Min(int.MaxValue, claimableMemory / engine.MemPerContextBytes)
            : int.MaxValue;

        var poolSize = Math.Max(1, Math.Min(declaredPool, poolByMemory));

        // The engine's hard ceiling is a ceiling on EITHER axis: battlescribe-ui runs one JVM, and
        // that is as true of a context pool as of a worker process.
        if (engine.MaxParallel > 0)
        {
            poolSize = Math.Min(poolSize, engine.MaxParallel);
        }

        // Reuse needs BOTH: correct AND worth it. Reusing a cheap-to-start engine is safe and
        // buys nothing (measured: 0.92x for NewRecruit) — it would add a warm-state failure mode
        // for no gain, which is a bad trade even when it is a correct one.
        var worthReusing = engine.ColdStartCost == ColdStartCost.Expensive;

        // ===== THE LOAD LIMIT. NOT A THIRD AXIS — THE OTHER PARTY'S CONSTRAINT ON BOTH OF THEM. =====
        // Every line above this one asks "how fast can THIS MACHINE go?". ClampToLoadTarget asks the one
        // question no measurement of ours can answer. It is the LAST thing that happens to a plan, here
        // and everywhere else — see its own remarks.
        return ClampToLoadTarget(
            new ConcurrencyPlan(
                Workers: workers,
                PoolSize: poolSize,
                ReuseRoster: worthReusing && engine.ReuseSafeRoster,
                ReuseGameData: worthReusing && engine.ReuseSafeGameData),
            loadTarget);
    }

    /// <summary>
    /// Hold <paramref name="plan"/> to <see cref="ThirdPartyLiveLoadLimit"/> on <b>both</b> axes when it
    /// points at someone else's live production service. A no-op for <see cref="LoadTarget.Local"/>, and
    /// idempotent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is separable from <see cref="For"/> at all: because a plan can arrive from somewhere
    /// else.</b> <see cref="For"/> applies it as its last step, so the policy's own answer is always
    /// bounded. But <c>--policy workers=32</c> (<c>PolicyOverride</c>) <em>replaces</em> that answer
    /// wholesale — and a user override is still not a mandate to put 32 browsers on a stranger's website.
    /// The CLI rejects such an override outright, loudly, rather than dropping it silently (#305), and
    /// then passes the plan through here anyway: <b>a ceiling that only holds when nobody is pushing on
    /// it is not a ceiling.</b>
    /// </para>
    /// <para>
    /// It clamps both axes for the reason the constant gives: the remote host feels requests in flight
    /// and cannot see whether we spawned them as worker processes or as browser contexts. It does not
    /// touch the reuse decisions — reuse is a correctness property of the engine, and it does not change
    /// with who is serving it.
    /// </para>
    /// <para>
    /// Do not inline <c>Math.Min(x, ThirdPartyLiveLoadLimit)</c> at a call site instead. The limit is a
    /// policy decision and it stays in the policy, applied in one place, so that "where does the live
    /// bound come from?" has exactly one answer — the failure this whole design exists to prevent is two
    /// places deciding one thing.
    /// </para>
    /// </remarks>
    /// <param name="plan">The plan the machine and the engine justify.</param>
    /// <param name="loadTarget">Where the load lands.</param>
    /// <returns><paramref name="plan"/>, bounded by the load limit when it is aimed at a third party.</returns>
    public static ConcurrencyPlan ClampToLoadTarget(ConcurrencyPlan plan, LoadTarget loadTarget)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return loadTarget == LoadTarget.ThirdPartyLive
            ? plan with
            {
                Workers = Math.Min(plan.Workers, ThirdPartyLiveLoadLimit),
                PoolSize = Math.Min(plan.PoolSize, ThirdPartyLiveLoadLimit),
            }
            : plan;
    }
}
