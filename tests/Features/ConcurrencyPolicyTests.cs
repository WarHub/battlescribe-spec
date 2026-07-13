using BattleScribeSpec.Concurrency;

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
}
