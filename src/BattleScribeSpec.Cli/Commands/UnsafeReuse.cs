namespace BattleScribeSpec.Cli;

/// <summary>
/// Whether a verb may honour <c>--policy reuse=on</c> (or <c>reuse-roster</c>/<c>reuse-gamedata</c>)
/// for a domain the engine's <see cref="BattleScribeSpec.Concurrency.EngineProfile"/> does not declare
/// reuse-safe. Passed explicitly to
/// <see cref="RunCommand.ApplyPolicyOverride(EngineSelection, string?, System.Action{string}, UnsafeReuse)"/>
/// by every call site, with no default, because the answer differs per verb and the wrong one is not
/// visible in the output it produces.
/// </summary>
/// <remarks>
/// <para>
/// The distinction is not caution versus convenience — it is whether the run can detect that it was
/// wrong. Reuse-safety is a claim about verdicts, and the only way to establish it is to run both arms
/// and compare them. A verb that runs one arm cannot do that, so forcing reuse there buys speed at the
/// cost of the result meaning anything.
/// </para>
/// </remarks>
internal enum UnsafeReuse
{
    /// <summary>
    /// Reject it — this verb runs a single arm, so a changed verdict would be indistinguishable from a
    /// real one. Correct for <c>run</c>.
    /// </summary>
    Refuse,

    /// <summary>
    /// Allow it, with a warning. Correct for <c>compare</c>: forcing the unsafe configuration is the
    /// experiment, the other arm is the control, and <c>compare</c> asserts per-spec verdict-equality
    /// before it will report so much as a timing figure. This is how an engine earns a
    /// <c>ReuseSafe*</c> flag in the first place, so it must stay reachable.
    /// </summary>
    AllowForAblation,
}
