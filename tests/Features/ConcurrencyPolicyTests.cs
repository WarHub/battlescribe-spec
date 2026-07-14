using BattleScribeSpec.Concurrency;
using BattleScribeSpec.Engines;

namespace BattleScribeSpec.Tests.Features;

[Trait("Category", "Unit")]
public sealed class ConcurrencyPolicyTests
{
    [Fact]
    public void MachineProfile_Current_ReportsARealMachine()
    {
        var machine = MachineProfile.Current();

        // Assert that CpuCount is actually reading Environment.ProcessorCount, not AvailableMemoryBytes.
        // If these were swapped, CpuCount would be in the billions (the byte count) and this assertion
        // would fail. A simple >= 1 check would pass either way.
        Assert.Equal(Environment.ProcessorCount, machine.CpuCount);

        // Assert that AvailableMemoryBytes is in a sane range. 64 MiB is the absolute floor for any
        // real machine running .NET — if AvailableMemoryBytes were set to the CPU count (e.g., 8),
        // this assertion would fail. A simple > 0 check would pass either way.
        const long minMemoryBytes = 64L * 1024 * 1024; // 64 MiB minimum
        Assert.True(
            machine.AvailableMemoryBytes >= minMemoryBytes,
            $"available memory ({machine.AvailableMemoryBytes} bytes) must be at least 64 MiB");
    }

    [Fact]
    public void MachineProfile_IsAValue_SoAPolicyCanBeTestedWithoutTheRealMachine()
    {
        // The whole point: the policy is a pure function of this, so tests can hand it
        // a 4-vCPU CI runner or a 64-core box without owning either.
        const long ci_Memory = 16L * 1024 * 1024 * 1024;
        const long big_Memory = 256L * 1024 * 1024 * 1024;

        var ci = new MachineProfile(CpuCount: 4, AvailableMemoryBytes: ci_Memory);
        var big = new MachineProfile(CpuCount: 64, AvailableMemoryBytes: big_Memory);

        // Assert that the positional record parameters bind in the correct order: the first
        // argument (4) goes to CpuCount, and the second (16 GiB) goes to AvailableMemoryBytes.
        // This proves the policy will see the values it expects, not that two different records
        // are unequal (which the compiler already guarantees).
        Assert.Equal(4, ci.CpuCount);
        Assert.Equal(ci_Memory, ci.AvailableMemoryBytes);

        Assert.Equal(64, big.CpuCount);
        Assert.Equal(big_Memory, big.AvailableMemoryBytes);
    }

    [Fact]
    public void EngineProfiles_EncodeWhatWasMeasured_NotWhatWasAssumed()
    {
        var registry = EngineRegistry.LoadDefault();

        // battlescribe-ui: expensive cold start (JVM + JavaFX), cannot be parallelized,
        // and reuse is verdict-neutral in BOTH domains (measured: 2.20x gamedata / 1.79x roster).
        var bsUi = registry.Resolve(EngineConnectable.Parse("battlescribe-ui")).Profile;
        Assert.Equal(1, bsUi.MaxParallel);
        Assert.Equal(ColdStartCost.Expensive, bsUi.ColdStartCost);
        Assert.True(bsUi.ReuseSafeRoster);
        Assert.True(bsUi.ReuseSafeGameData);

        // newrecruit-ui: cheap cold start (~1.6s Chromium), parallelizes freely — and roster reuse
        // is NOT verdict-safe. It was once enabled on a plausible assumption and silently changed
        // six spec verdicts. That is why ReuseSafe is a declared property and not a guess.
        var nrUi = registry.Resolve(EngineConnectable.Parse("newrecruit-ui")).Profile;
        Assert.Equal(0, nrUi.MaxParallel);
        Assert.Equal(ColdStartCost.Cheap, nrUi.ColdStartCost);
        Assert.False(nrUi.ReuseSafeRoster);
    }

    [Theory]
    // engine, MaxParallel, ColdStartCost, ReuseSafeRoster, ReuseSafeGameData,
    //   PROCESS axis: MemPerInstanceBytes, k | CONTEXT axis: ContextPoolSize, MemPerContextBytes
    [InlineData("battlescribe", 0, ColdStartCost.Cheap, false, false, 0L, 1.0, 0, 0L)]
    [InlineData("battlescribe-ui", 1, ColdStartCost.Expensive, true, true, 1_055_391_744L, 1.0, 0, 0L)]
    [InlineData("newrecruit", 0, ColdStartCost.Cheap, false, false, 1_313_420_083L, 0.375, 4, 225_863_270L)]
    [InlineData("newrecruit-ui", 0, ColdStartCost.Cheap, false, false, 1_548_969_984L, 1.0, 16, 235_824_742L)]
    public void EngineProfiles_AllBuiltins_PinAllFields(
        string engineName,
        int expectedMaxParallel,
        ColdStartCost expectedColdStartCost,
        bool expectedReuseSafeRoster,
        bool expectedReuseSafeGameData,
        long expectedMemPerInstanceBytes,
        double expectedOversubscriptionFactor,
        int expectedContextPoolSize,
        long expectedMemPerContextBytes)
    {
        var registry = EngineRegistry.LoadDefault();
        var profile = registry.Resolve(EngineConnectable.Parse(engineName)).Profile;

        // Pin the four measured/declared fields that affect policy decisions.
        Assert.Equal(expectedMaxParallel, profile.MaxParallel);
        Assert.Equal(expectedColdStartCost, profile.ColdStartCost);
        Assert.Equal(expectedReuseSafeRoster, profile.ReuseSafeRoster);
        Assert.Equal(expectedReuseSafeGameData, profile.ReuseSafeGameData);

        // PROCESS axis. Pin the §1–§6-measured numbers, and pin the one still-UNMEASURED engine
        // (battlescribe) at 0 so nobody can slip a guessed footprint into it without turning this
        // test red and having the conversation. A 0 here is not "negligible" — it is "undeclared",
        // and it is what makes UndeclaredMemoryWorkerCap bind.
        //
        // newrecruit's k = 0.375 is deliberately BELOW its measured optimum of 0.47 (a 1.97x cliff
        // sits one worker to the right of the peak). Do not "correct" it upward — read §5 first.
        Assert.Equal(expectedMemPerInstanceBytes, profile.MemPerInstanceBytes);
        Assert.Equal(expectedOversubscriptionFactor, profile.OversubscriptionFactor);

        // CONTEXT axis (§7). These are ABSOLUTE pool sizes, not factors of cpuCount — the sweep found
        // the same optimum on a 32-core box and a 4-CPU container (newrecruit 4, newrecruit-ui 16).
        // The per-context memory is ~6x smaller than the per-process figure above, which is the whole
        // reason these are separate fields.
        Assert.Equal(expectedContextPoolSize, profile.ContextPoolSize);
        Assert.Equal(expectedMemPerContextBytes, profile.MemPerContextBytes);
    }

    [Fact]
    public void Policy_ScalesWithTheMachine()
    {
        var engine = new EngineProfile(MaxParallel: 0, ColdStartCost.Cheap,
            ReuseSafeRoster: false, ReuseSafeGameData: false,
            MemPerInstanceBytes: 512L * 1024 * 1024, OversubscriptionFactor: 1.0);

        var small = ConcurrencyPolicy.For(new MachineProfile(4, 64L << 30), engine);
        var big = ConcurrencyPolicy.For(new MachineProfile(32, 256L << 30), engine);

        Assert.True(big.Workers > small.Workers, "a bigger box must get a bigger plan");
    }

    [Fact]
    public void Policy_NeverExceedsTheEnginesCeiling()
    {
        // battlescribe-ui cannot be parallelized at all — a 64-core box changes nothing.
        var bsUi = new EngineProfile(MaxParallel: 1, ColdStartCost.Expensive,
            ReuseSafeRoster: true, ReuseSafeGameData: true);

        var plan = ConcurrencyPolicy.For(new MachineProfile(64, 256L << 30), bsUi);

        Assert.Equal(1, plan.Workers);
    }

    [Fact]
    public void Policy_IsBoundedByMemory_NotJustCpu()
    {
        // 64 cores but only 2 GB free, and each instance costs 512 MB: memory binds, not CPU.
        // Without this bound a big box launches 64 browsers and dies before it saturates CPU.
        var engine = new EngineProfile(MaxParallel: 0, ColdStartCost.Cheap,
            ReuseSafeRoster: false, ReuseSafeGameData: false,
            MemPerInstanceBytes: 512L * 1024 * 1024, OversubscriptionFactor: 1.0);

        var plan = ConcurrencyPolicy.For(new MachineProfile(64, 2L << 30), engine);

        Assert.True(plan.Workers <= 4, $"memory must bind before CPU does; got {plan.Workers}");
        Assert.True(plan.Workers >= 1, "always at least one worker");
    }

    [Theory]
    [InlineData(ColdStartCost.Expensive, true, true, true, true)]    // BS-UI: safe AND worth it
    [InlineData(ColdStartCost.Cheap, true, true, false, false)]      // safe but WORTHLESS (NR: 0.92x)
    [InlineData(ColdStartCost.Expensive, false, false, false, false)] // worth it but NOT SAFE
    public void Policy_EnablesReuse_OnlyWhenSafeAndWorthIt(
        ColdStartCost cost, bool safeRoster, bool safeGameData, bool expectRoster, bool expectGameData)
    {
        var engine = new EngineProfile(MaxParallel: 0, cost, safeRoster, safeGameData);

        var plan = ConcurrencyPolicy.For(new MachineProfile(8, 32L << 30), engine);

        Assert.Equal(expectRoster, plan.ReuseRoster);
        Assert.Equal(expectGameData, plan.ReuseGameData);
    }

    [Fact]
    public void Policy_CapsWorkers_WhenMemPerInstanceIsUndeclared()
    {
        // Big box (64 CPU, 256 GiB) but the engine declares NO MemPerInstanceBytes (0 = undeclared).
        // Without the cap this picks 64 workers — exactly what shipped once, and what would exhaust
        // memory on a laptop running that many Chromium instances. The cap is PERMANENT, not
        // provisional: two built-ins are still unmeasured, EngineRegistry.DefaultProfile declares
        // nothing, and a third-party engines.json entry may omit the field entirely.
        var engine = new EngineProfile(MaxParallel: 0, ColdStartCost.Cheap,
            ReuseSafeRoster: false, ReuseSafeGameData: false,
            MemPerInstanceBytes: 0, OversubscriptionFactor: 1.0);

        var plan = ConcurrencyPolicy.For(new MachineProfile(64, 256L << 30), engine);

        Assert.Equal(8, plan.Workers);
    }

    [Fact]
    public void Policy_DoesNotApplyTheUndeclaredCap_OnceMemPerInstanceIsDeclared()
    {
        // Same big box — but this time the engine HAS declared MemPerInstanceBytes. The real,
        // measured memory bound must govern; the undeclared-memory cap must not additionally
        // restrict the result below what the measured bound already allows. Declaring a footprint
        // IS the opt-in to full machine-width parallelism.
        var engine = new EngineProfile(MaxParallel: 0, ColdStartCost.Cheap,
            ReuseSafeRoster: false, ReuseSafeGameData: false,
            MemPerInstanceBytes: 1L * 1024 * 1024 * 1024, OversubscriptionFactor: 1.0);

        var plan = ConcurrencyPolicy.For(new MachineProfile(64, 256L << 30), engine);

        // byCpu = 64; byMemory = floor(256 GiB * 0.8 / 1 GiB) = 204 -> workers bound by CPU at 64.
        // If the undeclared-memory cap wrongly applied here, this would be 8 instead of 64.
        Assert.Equal(64, plan.Workers);
    }

    // ===== Task 9: the measured constants, and what the policy does with them =====

    /// <summary>The dev box the Task 8 campaign measured the knee on: 32 logical processors, 93.6 GiB.</summary>
    private static readonly MachineProfile DevBox = new(CpuCount: 32, AvailableMemoryBytes: 100_451_844_096L);

    /// <summary>A 16 GiB laptop. Note "available" is TOTAL memory — such a box never has 16 GiB free.</summary>
    private static readonly MachineProfile Laptop16Gib = new(CpuCount: 16, AvailableMemoryBytes: 16L << 30);

    /// <summary>The 4-vCPU / 16 GB GitHub-hosted runner CI actually runs on.</summary>
    private static readonly MachineProfile CiRunner = new(CpuCount: 4, AvailableMemoryBytes: 16L << 30);

    private static EngineProfile Builtin(string name) =>
        EngineRegistry.LoadDefault().Resolve(EngineConnectable.Parse(name)).Profile;

    [Fact]
    public void Policy_MeasuredEngine_IsBoundByTheRealMemoryBound_NotTheUndeclaredCap()
    {
        // newrecruit-ui now DECLARES a measured footprint (1,548,969,984 B) and a measured k (1.0),
        // so it must get the machine's full width — not the 8-worker cap that binds undeclared
        // engines. On this box the cap was costing 2.65x (149.5s at P=8 vs 56.4s at P=32).
        //
        // byCpu    = ceil(32 * 1.0)                                        = 32
        // byMemory = floor(100,451,844,096 * 0.8 / 1,548,969,984)          = 51
        // workers  = min(32, 51)                                           = 32   <- the measured knee
        var plan = ConcurrencyPolicy.For(DevBox, Builtin("newrecruit-ui"));

        Assert.Equal(32, plan.Workers);
        Assert.True(plan.Workers > ConcurrencyPolicy.UndeclaredMemoryWorkerCap,
            $"a measured engine must escape the undeclared-memory cap; got {plan.Workers}");
    }

    [Fact]
    public void Policy_MemoryHeadroom_ActuallyReducesWorkers_OnAMemoryConstrainedBox()
    {
        // Falsifiable by construction: with MemoryHeadroomFactor = 1.0 this box would plan
        // floor(17,179,869,184 / 1,548,969,984) = 11 workers (~15.9 GiB — the ENTIRE machine, on a
        // sampled peak that is itself a lower bound). With the 0.8 factor it plans 8 (~11.5 GiB),
        // leaving ~4.5 GiB for the OS, the page cache, the parent CLI and the test host.
        // A test asserting only "<= 11" would pass at headroom = 1.0 and prove nothing.
        var nrUi = Builtin("newrecruit-ui");

        var plan = ConcurrencyPolicy.For(Laptop16Gib, nrUi);

        var withoutHeadroom = (int)(Laptop16Gib.AvailableMemoryBytes / nrUi.MemPerInstanceBytes);
        Assert.Equal(11, withoutHeadroom);
        Assert.Equal(8, plan.Workers);
        Assert.True(plan.Workers < withoutHeadroom,
            "the headroom factor must actually cost workers on a memory-bound box, or it is not doing anything");

        // And it must leave real room: the plan's own claim must fit inside the headroom budget.
        var claimed = (long)plan.Workers * nrUi.MemPerInstanceBytes;
        Assert.True(claimed <= (long)(Laptop16Gib.AvailableMemoryBytes * 0.8),
            $"planned claim {claimed} B exceeds the 80% headroom budget");
    }

    [Fact]
    public void Policy_MeasuredEngine_OnTheCiRunner_IsCpuBound()
    {
        // The 4-vCPU runner: byCpu = ceil(4 * 1.0) = 4; byMemory = floor(16 GiB * 0.8 / 1.44 GiB) = 8.
        // CPU binds -> 4 workers, replacing the hand-set NR_PARALLEL: 6 / NR_PARALLEL: 2 in ci.yml.
        // k = 1.0 was fitted on the 32-core dev box and is NOT measured here — this is the number we
        // ship watching, not one we claim.
        var plan = ConcurrencyPolicy.For(CiRunner, Builtin("newrecruit-ui"));

        Assert.Equal(4, plan.Workers);
    }

    [Theory]
    [InlineData(4, 16L)]    // CI runner
    [InlineData(16, 16L)]   // laptop
    [InlineData(32, 93L)]   // dev box
    [InlineData(64, 256L)]  // a box far bigger than anything we own
    public void Policy_BattlescribeUi_StaysAtOneWorker_OnEveryProfile(int cpuCount, long memoryGib)
    {
        // MaxParallel = 1 is applied AFTER the cpu/memory computation, so it wins on every box.
        // Its measured MemPerInstanceBytes (1,055,391,744 B) exists to PROVE memory never binds
        // here, not to change the answer — the answer was always 1 and must stay 1.
        var plan = ConcurrencyPolicy.For(
            new MachineProfile(cpuCount, memoryGib << 30), Builtin("battlescribe-ui"));

        Assert.Equal(1, plan.Workers);

        // ...and MaxParallel is a ceiling on BOTH axes, so the context pool is 1 too. This engine
        // declares no ContextPoolSize (it drives a JavaFX desktop app; it has no browser contexts to
        // pool), so without the MaxParallel clamp on the pool it would take
        // UndeclaredContextPoolSize = 4 here. Falsifiable: drop that clamp and this reads 4.
        Assert.Equal(1, plan.PoolSize);
        Assert.Equal(0, Builtin("battlescribe-ui").ContextPoolSize);
    }

    [Fact]
    public void Policy_UndeclaredBuiltin_StillGetsTheUndeclaredCap()
    {
        // battlescribe is the one built-in still declaring MemPerInstanceBytes = 0, so the cap still
        // binds it — which is why deleting the cap would have been a regression, not a cleanup. The
        // newrecruit sweep is the evidence for why that matters: it was unmeasured too, and turned
        // out to hide a 1.97x cliff one worker past its optimum. Nobody knows where battlescribe's is.
        var profile = Builtin("battlescribe");

        Assert.Equal(0L, profile.MemPerInstanceBytes);
        Assert.Equal(
            ConcurrencyPolicy.UndeclaredMemoryWorkerCap,
            ConcurrencyPolicy.For(DevBox, profile).Workers);
    }

    [Theory]
    // The engines DISAGREE, and the policy must reproduce the disagreement rather than average it.
    // newrecruit is CPU-bound (p50 2.4s/spec): k = 0.375, optimum P=15, 1.97x cliff at P=16.
    // newrecruit-ui is I/O-bound (p50 17.1s/spec): k = 1.0, knee at P=32. Same box, same specs.
    [InlineData("newrecruit", 32, 100_451_844_096L, 12)]   // dev box: ceil(32 x 0.375) = 12, well left of the cliff at 16
    [InlineData("newrecruit-ui", 32, 100_451_844_096L, 32)] // dev box: ceil(32 x 1.0) = 32 — the measured knee
    [InlineData("newrecruit", 16, 17_179_869_184L, 6)]      // 16 GiB laptop: ceil(16 x 0.375) = 6 (CPU binds; memory allows 10)
    [InlineData("newrecruit-ui", 16, 17_179_869_184L, 8)]   // 16 GiB laptop: memory binds at 8 (CPU would allow 16)
    [InlineData("newrecruit", 4, 17_179_869_184L, 2)]       // 4-vCPU CI runner: ceil(4 x 0.375) = 2
    [InlineData("newrecruit-ui", 4, 17_179_869_184L, 4)]    // 4-vCPU CI runner: CPU binds at 4
    public void Policy_TheTwoNrEngines_GetDifferentWorkerCounts_OnTheSameBox(
        string engineName, int cpuCount, long availableMemoryBytes, int expectedWorkers)
    {
        var plan = ConcurrencyPolicy.For(
            new MachineProfile(cpuCount, availableMemoryBytes), Builtin(engineName));

        Assert.Equal(expectedWorkers, plan.Workers);

        // And neither is bound by the undeclared-memory cap any more — both declare a footprint.
        // (A 12 or a 32 here cannot come from min(cpuCount, 8) by construction; the 2/4/6/8 cases
        // could numerically coincide with it, so assert the mechanism directly.)
        Assert.NotEqual(0L, Builtin(engineName).MemPerInstanceBytes);
    }

    /// <summary>
    /// A non-positive <c>MemPerInstanceBytes</c> must be treated as UNDECLARED, i.e. capped — not as
    /// a licence to take the whole machine.
    /// </summary>
    /// <remarks>
    /// The two guards used to disagree about what "undeclared" meant: the memory bound was gated on
    /// <c>&gt; 0</c> and the cap on <c>== 0</c>, so a negative slipped between them — no memory bound
    /// (byMemory became int.MaxValue) and no cap, yielding ceil(cpu × k) workers of an engine nobody
    /// measured. Falsifiable: restore the gate to <c>== 0</c> and this returns 32 on the dev box
    /// instead of 8. <c>EngineRegistry</c> now also rejects such a config at load; this is the
    /// in-code second line of defence, for a profile built in C# rather than parsed from JSON.
    /// </remarks>
    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Policy_NegativeMemPerInstance_IsTreatedAsUndeclared_AndStillCapped(long memPerInstanceBytes)
    {
        var hostile = new EngineProfile(
            MaxParallel: 0, ColdStartCost.Cheap, ReuseSafeRoster: false, ReuseSafeGameData: false,
            MemPerInstanceBytes: memPerInstanceBytes, OversubscriptionFactor: 1.0);

        var plan = ConcurrencyPolicy.For(DevBox, hostile);

        Assert.Equal(ConcurrencyPolicy.UndeclaredMemoryWorkerCap, plan.Workers);
    }

    [Fact]
    public void Policy_NewRecruit_StaysLeftOfItsMeasuredCliff_OnTheBoxItWasMeasuredOn()
    {
        // The single most important property of k = 0.375: on the box where the cliff was measured,
        // the policy must land BELOW P=16, where the wall-clock doubles (15.8s at P=15 -> 31.0s at
        // P=16). 12 < 16 with three workers of margin. If a future change to k or to the headroom
        // factor pushes this to >= 16, this test fails and the harness would have shipped a 1.97x
        // regression that no unit test would otherwise have caught.
        const int measuredCliff = 16;

        var plan = ConcurrencyPolicy.For(DevBox, Builtin("newrecruit"));

        Assert.True(plan.Workers < measuredCliff,
            $"newrecruit must stay left of its measured 1.97x cliff at P={measuredCliff}; got {plan.Workers}");
    }

    // ===== The CONTEXT axis (#314): PoolSize is NOT Workers, and it is NOT a function of CpuCount ===
    //
    // Everything above this line sizes adapter PROCESSES for the CLI path. Everything below sizes
    // browser CONTEXTS for the xUnit path — which is what every NewRecruit CI conformance lane runs.
    // The policy used to return `PoolSize: workers`, feeding a constant fitted by sweeping processes
    // into a pool of contexts. The tests below are the ones that would have caught it.

    /// <summary>
    /// <b>The assertion that would have caught the original bug.</b> The context-axis optimum is
    /// CPU-INDEPENDENT — measured, not assumed: <c>newrecruit-ui</c> at pool=1 runs the same 112 specs
    /// in 240.05 s on 32 CPUs and 241.17 s on 4 CPUs (an 8× CPU cut costs 0.5%), and the optimal pool
    /// came out identical on both boxes (4 / 16). Contexts share one Chromium and one Playwright
    /// driver; they contend on that driver, not on cores.
    /// </summary>
    /// <remarks>
    /// Falsifiable, and precisely aimed: restore <c>PoolSize: workers</c> in
    /// <see cref="ConcurrencyPolicy.For"/> and this goes red on the first non-4-CPU row
    /// (<c>newrecruit-ui</c> would yield 2 / 4 / 8 / 16 / 32 across these boxes instead of a flat 16).
    /// Any other reintroduction of a <c>CpuCount</c> term into the pool computation fails it too,
    /// which is the point: the shape is what was wrong, not just the constant.
    /// </remarks>
    private static readonly int[] CpuCountsSpanningEveryBoxWeRunOn = [1, 2, 4, 8, 16, 32, 64];

    [Theory]
    [InlineData("newrecruit", 4)]
    [InlineData("newrecruit-ui", 16)]
    public void Policy_PoolSize_IsIndependentOfCpuCount(string engineName, int measuredOptimalPool)
    {
        var engine = Builtin(engineName);

        // Memory is held CONSTANT (96 GiB — enough that the memory bound never binds) across every
        // row, so the only thing varying is CpuCount. A test that let memory vary too could not
        // attribute a difference to either input.
        const long fixedMemory = 96L << 30;
        var pools = CpuCountsSpanningEveryBoxWeRunOn
            .Select(cpu => ConcurrencyPolicy.For(new MachineProfile(cpu, fixedMemory), engine).PoolSize)
            .ToArray();

        Assert.All(pools, pool => Assert.Equal(measuredOptimalPool, pool));
    }

    /// <summary>
    /// The two axes move independently on the same machine — which is what "they are different
    /// quantities" means operationally. <see cref="ConcurrencyPlan.Workers"/> still scales with CPU
    /// (the process axis is unchanged and still correct); <see cref="ConcurrencyPlan.PoolSize"/> does
    /// not move at all.
    /// </summary>
    /// <remarks>
    /// Falsifiable in both directions. Restore the mirror and the pool assertions fail. Delete the
    /// <c>CpuCount</c> term from the <em>worker</em> computation — "for symmetry", the plausible
    /// over-correction — and the worker assertions fail. This test is why the fix cannot regress the
    /// axis that was never broken.
    /// </remarks>
    [Fact]
    public void Policy_TheTwoAxes_MoveIndependently_ProcessWithCpu_ContextNotAtAll()
    {
        var nrUi = Builtin("newrecruit-ui");

        var onCi = ConcurrencyPolicy.For(CiRunner, nrUi);       // 4 vCPU / 16 GiB
        var onDevBox = ConcurrencyPolicy.For(DevBox, nrUi);     // 32 cpu / 93.6 GiB

        // PROCESS axis: 8x the CPUs, 8x the workers. Unchanged behaviour, deliberately.
        Assert.Equal(4, onCi.Workers);
        Assert.Equal(32, onDevBox.Workers);
        Assert.True(onDevBox.Workers > onCi.Workers, "the process axis must still scale with the machine");

        // CONTEXT axis: the same 16 on both, because that is what was measured on both.
        Assert.Equal(16, onCi.PoolSize);
        Assert.Equal(16, onDevBox.PoolSize);

        // And the mirror is gone: on neither box is the pool the worker count. If a future change
        // makes these equal for this engine, it has re-fused the two axes.
        Assert.NotEqual(onCi.Workers, onCi.PoolSize);
        Assert.NotEqual(onDevBox.Workers, onDevBox.PoolSize);
    }

    /// <summary>
    /// What CI actually gets on its 4-vCPU / 16 GiB runner — the numbers this change exists to
    /// deliver, pinned so a regression is a red test rather than a slow lane nobody attributes.
    /// </summary>
    /// <remarks>
    /// Falsifiable: the mirror gave 2 and 4 here (measured cost: +11.6% on <c>nr-frozen</c>, +99.8%
    /// on <c>nr-editor-ui-frozen</c>). The retired <c>NR_PARALLEL: 6</c> gave 6 and 6 (still 4% and
    /// 50% off). Only 4 and 16 pass, and both are measured optima on this exact hardware class.
    /// </remarks>
    [Theory]
    [InlineData("newrecruit", 4)]        // nr-frozen / nr-live-conformance
    [InlineData("newrecruit-ui", 16)]    // nr-editor-ui-frozen
    public void Policy_PoolSize_OnTheCiRunner_IsTheMeasuredOptimum(string engineName, int expectedPool)
    {
        var plan = ConcurrencyPolicy.For(CiRunner, Builtin(engineName));

        Assert.Equal(expectedPool, plan.PoolSize);

        // Memory does not bind at the optimum on this axis — contention does. (Measured: pool 16
        // peaks at 6.16 GiB of the runner's 16 GiB.) If memory were what produced these numbers, the
        // test above would be pinning an accident.
        var affordable = (long)(CiRunner.AvailableMemoryBytes * 0.8) / Builtin(engineName).MemPerContextBytes;
        Assert.True(affordable > expectedPool,
            $"the memory bound ({affordable}) must not be what produces the pool of {expectedPool}");
    }

    /// <summary>
    /// The memory bound really does bind the pool on a small box. <c>newrecruit-ui</c> declares a pool
    /// of 16 and ≈225 MiB per context; a 2 GiB container cannot afford 16 of them.
    /// </summary>
    /// <remarks>
    /// Falsifiable by construction, and specifically against the two ways to break it: delete the
    /// memory bound from the pool computation and this box plans the full declared 16 (asserted
    /// directly, so a test that merely said "&lt;= 16" cannot hide it); delete the headroom factor and
    /// it plans 9 instead of 7. Both are red.
    /// </remarks>
    [Fact]
    public void Policy_PoolSize_IsBoundedByMemory_OnASmallMemoryBox()
    {
        var nrUi = Builtin("newrecruit-ui");
        var tiny = new MachineProfile(CpuCount: 32, AvailableMemoryBytes: 2L << 30);

        var plan = ConcurrencyPolicy.For(tiny, nrUi);

        // floor(2 GiB * 0.8 / 235,824,742 B) = 7 — memory binds, well below the declared 16.
        Assert.Equal(7, plan.PoolSize);
        Assert.True(plan.PoolSize < nrUi.ContextPoolSize,
            "the memory bound must actually cost contexts on a small box, or it is not doing anything");

        // Without the headroom factor the same box would plan 9 (~2.0 GiB — the entire machine, on a
        // slope that excludes the pool's ~1.3 GiB fixed baseline). The margin is not decorative.
        var withoutHeadroom = (int)(tiny.AvailableMemoryBytes / nrUi.MemPerContextBytes);
        Assert.Equal(9, withoutHeadroom);
        Assert.True(plan.PoolSize < withoutHeadroom);
    }

    /// <summary>
    /// An engine that declares no context-pool size gets the documented conservative default — an
    /// absolute 4, the low end of the measured band — on every box, not <c>cpuCount</c> contexts.
    /// </summary>
    /// <remarks>
    /// Falsifiable: if <see cref="ConcurrencyPolicy.For"/> fell back to the worker count for an
    /// undeclared engine (the old behaviour), the 64-core box below would plan 64 contexts and the
    /// 4-CPU box 4 — the constant would not even be constant. Both rows assert 4.
    /// </remarks>
    [Theory]
    [InlineData(4, 16L)]
    [InlineData(64, 256L)]
    public void Policy_UndeclaredContextPoolSize_GetsTheConservativeDefault(int cpuCount, long memoryGib)
    {
        // Declares a process-axis footprint (so it is NOT an "undeclared engine" in the old sense),
        // but says nothing about contexts — exactly the position every third-party engine, and
        // battlescribe, is in.
        var engine = new EngineProfile(
            MaxParallel: 0, ColdStartCost.Cheap, ReuseSafeRoster: false, ReuseSafeGameData: false,
            MemPerInstanceBytes: 1L << 30, OversubscriptionFactor: 1.0);

        var plan = ConcurrencyPolicy.For(new MachineProfile(cpuCount, memoryGib << 30), engine);

        Assert.Equal(ConcurrencyPolicy.UndeclaredContextPoolSize, plan.PoolSize);
        Assert.Equal(4, plan.PoolSize);
    }

    /// <summary>
    /// A negative <c>ContextPoolSize</c>/<c>MemPerContextBytes</c> is treated as undeclared, not as a
    /// licence. Same class of hole as the negative <c>MemPerInstanceBytes</c> that once escaped both
    /// worker guards; <c>EngineRegistry.Load</c> rejects it at the config boundary, and this is the
    /// second line of defence for a profile built in C#.
    /// </summary>
    /// <remarks>
    /// Falsifiable: gate the pool on <c>!= 0</c> instead of <c>&gt; 0</c> and a pool size of -1 would
    /// flow through <c>Math.Max(1, ...)</c> as 1 (a silently serial lane), while a negative
    /// per-context cost would make the memory bound negative and clamp every pool to 1.
    /// </remarks>
    [Theory]
    [InlineData(-1, 0L)]
    [InlineData(0, -1L)]
    [InlineData(int.MinValue, long.MinValue)]
    public void Policy_NegativeContextDeclarations_AreTreatedAsUndeclared(
        int contextPoolSize, long memPerContextBytes)
    {
        var hostile = new EngineProfile(
            MaxParallel: 0, ColdStartCost.Cheap, ReuseSafeRoster: false, ReuseSafeGameData: false,
            MemPerInstanceBytes: 1L << 30, OversubscriptionFactor: 1.0,
            ContextPoolSize: contextPoolSize, MemPerContextBytes: memPerContextBytes);

        var plan = ConcurrencyPolicy.For(DevBox, hostile);

        Assert.Equal(ConcurrencyPolicy.UndeclaredContextPoolSize, plan.PoolSize);
    }

    [Fact]
    public void Policy_IsPure_SameInputsSamePlan()
    {
        // Reproducibility is not a nicety: `compare` holds everything constant except one variable,
        // and a policy that wanders between runs makes that comparison meaningless.
        var machine = new MachineProfile(8, 32L << 30);
        var engine = new EngineProfile(MaxParallel: 0, ColdStartCost.Cheap, false, false);

        Assert.Equal(ConcurrencyPolicy.For(machine, engine), ConcurrencyPolicy.For(machine, engine));
    }
}
