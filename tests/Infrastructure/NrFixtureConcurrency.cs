using BattleScribeSpec.Concurrency;
using BattleScribeSpec.Engines;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Resolves the in-process browser/context pool size for the NR fixtures from
/// <see cref="ConcurrencyPolicy"/> — the single source of every concurrency decision in the harness.
/// </summary>
/// <remarks>
/// Before this, three fixtures each read a env var called <c>NR_PARALLEL</c> with a <em>different</em>
/// hardcoded default (5 / 10 / 5), and the pool factories carried a fourth copy of the same defaults
/// in their signatures. <c>NR_PARALLEL</c> is now read nowhere in this repo; every NR fixture pool
/// size comes from here instead, so there is exactly one place to be wrong.
/// </remarks>
internal static class NrFixtureConcurrency
{
    /// <summary>
    /// The <see cref="ConcurrencyPlan"/> for the named built-in engine (e.g. <c>"newrecruit"</c>,
    /// <c>"newrecruit-ui"</c>) on the real machine this process is running on.
    /// </summary>
    public static ConcurrencyPlan Resolve(string engineName)
    {
        var profile = EngineRegistry.LoadDefault().Resolve(EngineConnectable.Parse(engineName)).Profile;
        return ConcurrencyPolicy.For(MachineProfile.Current(), profile);
    }
}
