namespace BattleScribeSpec.Concurrency;

/// <summary>
/// The machine a run is happening on. The <b>only</b> place the harness reads the environment
/// to decide concurrency — everything downstream is a pure function of this value.
/// </summary>
/// <remarks>
/// Being a plain value (not a static reader) is the point: a test can hand the policy a 4-vCPU
/// CI runner or a 64-core workstation without owning either. The two disagree violently about
/// optimal parallelism, and the policy must be testable against both.
/// </remarks>
/// <param name="CpuCount">Logical processors available to this process.</param>
/// <param name="AvailableMemoryBytes">
/// Memory the process may reasonably use. Load-bearing: CPU count alone is not a safe input,
/// because a 64-core box will happily launch 64 Chromium contexts and exhaust memory long
/// before it saturates CPU.
/// </param>
public sealed record MachineProfile(int CpuCount, long AvailableMemoryBytes)
{
    /// <summary>Read the real machine.</summary>
    public static MachineProfile Current()
    {
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return new MachineProfile(
            CpuCount: Environment.ProcessorCount,
            AvailableMemoryBytes: memory > 0 ? memory : 4L * 1024 * 1024 * 1024);
    }
}
