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
    [InlineData("battlescribe", 0, ColdStartCost.Cheap, false, false)]
    [InlineData("battlescribe-ui", 1, ColdStartCost.Expensive, true, true)]
    [InlineData("newrecruit", 0, ColdStartCost.Cheap, false, false)]
    [InlineData("newrecruit-ui", 0, ColdStartCost.Cheap, false, false)]
    public void EngineProfiles_AllBuiltins_PinAllFields(
        string engineName,
        int expectedMaxParallel,
        ColdStartCost expectedColdStartCost,
        bool expectedReuseSafeRoster,
        bool expectedReuseSafeGameData)
    {
        var registry = EngineRegistry.LoadDefault();
        var profile = registry.Resolve(EngineConnectable.Parse(engineName)).Profile;

        // Pin the four measured/declared fields that affect policy decisions.
        Assert.Equal(expectedMaxParallel, profile.MaxParallel);
        Assert.Equal(expectedColdStartCost, profile.ColdStartCost);
        Assert.Equal(expectedReuseSafeRoster, profile.ReuseSafeRoster);
        Assert.Equal(expectedReuseSafeGameData, profile.ReuseSafeGameData);

        // Pin the unmeasured placeholders. These are deliberately 0 and 1.0 (no measured limit,
        // no oversubscription strategy yet) — they get refined when measurements become available.
        // A test pinning them at defaults means nobody can slip in a guessed number without
        // forcing a conversation: the test turns red and demands an evidence-based decision.
        Assert.Equal(0L, profile.MemPerInstanceBytes);
        Assert.Equal(1.0, profile.OversubscriptionFactor);
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
    public void Policy_CapsWorkers_WhenMemPerInstanceIsUnmeasured()
    {
        // Big box (64 CPU, 256 GiB) but the engine declares NO MemPerInstanceBytes (0 = unknown).
        // Without a provisional cap this picks 64 workers — exactly what shipped and would
        // exhaust memory on a 32-core/16 GB developer laptop running that many Chromium instances.
        var engine = new EngineProfile(MaxParallel: 0, ColdStartCost.Cheap,
            ReuseSafeRoster: false, ReuseSafeGameData: false,
            MemPerInstanceBytes: 0, OversubscriptionFactor: 1.0);

        var plan = ConcurrencyPolicy.For(new MachineProfile(64, 256L << 30), engine);

        Assert.True(
            plan.Workers <= 8,
            $"an engine with no measured memory footprint must be capped provisionally; got {plan.Workers}");
    }

    [Fact]
    public void Policy_DoesNotApplyTheProvisionalCap_OnceMemPerInstanceIsMeasured()
    {
        // Same big box — but this time the engine HAS declared MemPerInstanceBytes (post-Task-9).
        // The real, measured memory bound must govern; the provisional cap must not additionally
        // restrict the result below what the measured bound already allows.
        var engine = new EngineProfile(MaxParallel: 0, ColdStartCost.Cheap,
            ReuseSafeRoster: false, ReuseSafeGameData: false,
            MemPerInstanceBytes: 1L * 1024 * 1024 * 1024, OversubscriptionFactor: 1.0);

        var plan = ConcurrencyPolicy.For(new MachineProfile(64, 256L << 30), engine);

        // byCpu = 64, byMemory = 256 GiB / 1 GiB = 256 -> workers bound by CPU at 64.
        // If the provisional cap wrongly applied here, this would be <= 8 instead of 64.
        Assert.Equal(64, plan.Workers);
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
