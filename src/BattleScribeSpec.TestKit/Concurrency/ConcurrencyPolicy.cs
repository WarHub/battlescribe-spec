namespace BattleScribeSpec.Concurrency;

/// <summary>
/// The single source of every concurrency and reuse decision in the harness. A <b>pure function</b>
/// of the machine and what the engine declares about itself — no I/O, no environment variables,
/// no string-matching on engine names.
/// </summary>
public static class ConcurrencyPolicy
{
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
