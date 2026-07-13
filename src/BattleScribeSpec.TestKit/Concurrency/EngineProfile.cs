namespace BattleScribeSpec.Concurrency;

/// <summary>Is an engine's cold start expensive enough that reusing it could pay for itself?</summary>
public enum ColdStartCost
{
    /// <summary>Cheap to construct — reuse buys nothing. A headless Chromium relaunches in ~1.6s.</summary>
    Cheap,

    /// <summary>Expensive to construct — reuse is where the win is. A JVM + JavaFX launch, per spec.</summary>
    Expensive,
}

/// <summary>
/// What an engine declares about itself. The policy derives every number from this plus a
/// <see cref="MachineProfile"/>; nothing string-matches an engine's name.
/// </summary>
/// <param name="MaxParallel">Hard ceiling on concurrent instances; 0 = unlimited.</param>
/// <param name="ColdStartCost">Whether reuse can pay for itself at all.</param>
/// <param name="ReuseSafeRoster">May the roster engine be reused across setups without changing verdicts?</param>
/// <param name="ReuseSafeGameData">May the gamedata engine be reused across setups without changing verdicts?</param>
/// <param name="MemPerInstanceBytes">Measured memory cost of one concurrent instance; 0 = unknown/negligible.</param>
/// <param name="OversubscriptionFactor">
/// The `k` in `workers ≈ cpuCount × k`. MEASURED per engine, never guessed — the engines
/// demonstrably disagree, and on the same 4-vCPU runner one workload degrades past P=6 while
/// another merely plateaus.
/// </param>
/// <remarks>
/// <b><see cref="ReuseSafeRoster"/> and <see cref="ReuseSafeGameData"/> are EARNED, not asserted.</b>
/// An engine may only claim reuse-safety for a domain where <c>bs-spec compare</c> has demonstrated
/// verdict-equality against a cold arm. The one time this was claimed without evidence
/// (newrecruit-ui roster) it silently changed six spec verdicts while a stopwatch reported success.
/// <para>
/// Note reuse needs BOTH properties: <c>reuse ⟺ ReuseSafe(domain) ∧ ColdStartCost == Expensive</c>.
/// "Is it correct?" and "is it worth anything?" are different questions. Reusing a NewRecruit
/// browser is perfectly safe and buys 0.92× — i.e. nothing — so enabling it would add a warm-state
/// failure mode for no gain. A bad trade even when it is a correct one.
/// </para>
/// </remarks>
public sealed record EngineProfile(
    int MaxParallel,
    ColdStartCost ColdStartCost,
    bool ReuseSafeRoster,
    bool ReuseSafeGameData,
    long MemPerInstanceBytes = 0,
    double OversubscriptionFactor = 1.0);
