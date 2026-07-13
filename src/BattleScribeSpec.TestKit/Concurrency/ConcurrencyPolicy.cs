namespace BattleScribeSpec.Concurrency;

/// <summary>
/// The single source of every concurrency and reuse decision in the harness. A <b>pure function</b>
/// of the machine and what the engine declares about itself — no I/O, no environment variables,
/// no string-matching on engine names.
/// </summary>
public static class ConcurrencyPolicy
{
    /// <summary>
    /// PROVISIONAL safety ceiling on worker count, applied ONLY while an engine's
    /// <see cref="EngineProfile.MemPerInstanceBytes"/> is unmeasured (<c>== 0</c>). This is a
    /// stopgap, not a fitted value — see the comment at its use site and Task 8/9 of
    /// <c>docs/superpowers/plans/2026-07-13-harness-concurrency-model.md</c>.
    /// </summary>
    private const int ProvisionalUnmeasuredMemoryCap = 8;

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
        var byMemory = engine.MemPerInstanceBytes > 0
            ? (int)(machine.AvailableMemoryBytes / engine.MemPerInstanceBytes)
            : int.MaxValue;

        var workers = Math.Max(1, Math.Min(byCpu, byMemory));

        // PROVISIONAL SAFETY CAP — NOT a fitted value, do not tune it. MemPerInstanceBytes == 0
        // means "nobody has measured what one instance of this engine costs yet", which makes
        // byMemory above int.MaxValue, i.e. inactive. Without a guard here, a 32-core box picks
        // 32 workers for an engine with an unmeasured memory footprint — which is exactly what
        // happened and would exhaust memory on a 32-core/16 GB developer laptop running 32
        // concurrent Chromium instances. This cap exists only to prevent that failure while
        // Task 8 of docs/superpowers/plans/2026-07-13-harness-concurrency-model.md measures the
        // real MemPerInstanceBytes per engine. Task 9 writes the measured value into
        // EngineRegistry AND removes this cap — once MemPerInstanceBytes is non-zero for an
        // engine, the real (measured) memory bound above governs and this cap must not further
        // restrict the result.
        if (engine.MemPerInstanceBytes == 0)
        {
            workers = Math.Min(workers, Math.Min(machine.CpuCount, ProvisionalUnmeasuredMemoryCap));
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
