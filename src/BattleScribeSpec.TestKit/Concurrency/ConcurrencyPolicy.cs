namespace BattleScribeSpec.Concurrency;

/// <summary>
/// The single source of every concurrency and reuse decision in the harness. A <b>pure function</b>
/// of the machine and what the engine declares about itself — no I/O, no environment variables,
/// no string-matching on engine names.
/// </summary>
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
    /// <c>tests/xunit.runner.json</c> and <c>tests/BattleScribeSpec.Cli.Tests/xunit.runner.json</c>
    /// hardcode <c>maxParallelThreads: 8</c> to this same number (Task 7), mechanically pinned by
    /// <c>ConcurrencyConfigurationDriftTests</c>. That file is static JSON read by the xUnit runner
    /// before any of our code runs, so it cannot call this policy at runtime — the literal is a
    /// deliberate, honest stand-in for it, not a coincidence. Note it governs the <em>test suite's
    /// own</em> xUnit thread count, which is a different quantity from an engine's worker count;
    /// they share a value, not a meaning.
    /// </para>
    /// <para>
    /// Investigated and rejected as an alternative: xunit.v3's VSTest RunSettings override
    /// (confirmed live via <c>Xunit.Runner.VisualStudio.RunSettings.Parse</c> — an
    /// <c>&lt;xUnit&gt;&lt;MaxParallelThreads&gt;</c> element in a <c>.runsettings</c> file, or
    /// <c>dotnet test -- xUnit.MaxParallelThreads=&lt;value&gt;</c>, both of which this repo's
    /// <c>xunit.runner.visualstudio</c> adapter genuinely honors). It shares the exact same
    /// limitation as this JSON file: the RunSettings XML is also read by the runner before this
    /// process's code executes, so it cannot call <see cref="For"/> either — using it would only
    /// move the hardcoded <c>8</c> from one static file format to another, at the cost of a second
    /// static file to keep in sync (this repo's <c>.runsettings</c> files are already spoken for,
    /// encoding <c>TestCaseFilter</c> per profile). Making the value truly dynamic would need an
    /// external wrapper script that computes it and writes/overrides the RunSettings before invoking
    /// the runner — reintroducing the wrapper-script axis this design exists to avoid. Not pursued.
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

    /// <summary>Derive the plan. Deterministic: the same machine and engine always give the same plan.</summary>
    /// <param name="machine">The machine the run is happening on.</param>
    /// <param name="engine">What the engine declares about itself.</param>
    /// <returns>The concurrency and reuse decisions for this machine/engine pair.</returns>
    public static ConcurrencyPlan For(MachineProfile machine, EngineProfile engine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(engine);

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
        // MemPerInstanceBytes == 0 means "undeclared", which makes byMemory above int.MaxValue —
        // i.e. inactive — so without this guard a 32-core box would launch 32 instances of an engine
        // whose footprint nobody knows, and exhaust memory on a laptop. This is the PERMANENT
        // conservative default for such engines (two built-ins and every third-party engine that
        // omits the field are in exactly this position); declaring MemPerInstanceBytes is how an
        // engine opts into full machine-width parallelism, and doing so retires this cap for that
        // engine automatically — the gate below simply stops firing. See UndeclaredMemoryWorkerCap.
        if (engine.MemPerInstanceBytes == 0)
        {
            workers = Math.Min(workers, Math.Min(machine.CpuCount, UndeclaredMemoryWorkerCap));
        }

        // The engine's hard ceiling wins over everything. 0 = unlimited.
        if (engine.MaxParallel > 0)
        {
            workers = Math.Min(workers, engine.MaxParallel);
        }

        // Reuse needs BOTH: correct AND worth it. Reusing a cheap-to-start engine is safe and
        // buys nothing (measured: 0.92x for NewRecruit) — it would add a warm-state failure mode
        // for no gain, which is a bad trade even when it is a correct one.
        var worthReusing = engine.ColdStartCost == ColdStartCost.Expensive;

        return new ConcurrencyPlan(
            Workers: workers,
            PoolSize: workers,
            MaxParallelThreads: workers,
            ReuseRoster: worthReusing && engine.ReuseSafeRoster,
            ReuseGameData: worthReusing && engine.ReuseSafeGameData);
    }
}
