using BattleScribeSpec.Concurrency;

namespace BattleScribeSpec.Tests.Features;

[Trait("Category", "Unit")]
public sealed class ConcurrencyPolicyTests
{
    [Fact]
    public void MachineProfile_Current_ReportsARealMachine()
    {
        var machine = MachineProfile.Current();

        Assert.True(machine.CpuCount >= 1, "a machine has at least one CPU");
        Assert.True(machine.AvailableMemoryBytes > 0, "a machine has some memory");
    }

    [Fact]
    public void MachineProfile_IsAValue_SoAPolicyCanBeTestedWithoutTheRealMachine()
    {
        // The whole point: the policy is a pure function of this, so tests can hand it
        // a 4-vCPU CI runner or a 64-core box without owning either.
        var ci = new MachineProfile(CpuCount: 4, AvailableMemoryBytes: 16L * 1024 * 1024 * 1024);
        var big = new MachineProfile(CpuCount: 64, AvailableMemoryBytes: 256L * 1024 * 1024 * 1024);

        Assert.NotEqual(ci, big);
    }
}
