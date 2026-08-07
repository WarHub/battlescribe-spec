namespace BattleScribeSpec.NrRosterUiDriver;

/// <summary>
/// Ceilings for the NR UI drivers' condition waits, named by what a wait MEANS rather than by how
/// long it happens to be.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every value here is a CEILING, not a cost.</b> These drivers contain no fixed sleeps: each
/// wait returns the moment its condition holds — typically 10-40ms — so the number only decides how
/// long a genuinely stuck run takes to report. Nothing waits on these when things are healthy.
/// </para>
/// <para>
/// <b>Why this type exists.</b> The two NR drivers previously carried 105 timeout literals across
/// ten distinct values, chosen one site at a time. Nothing forced them to be considered as a set,
/// and the result was exactly what you would predict: 5s sat beside 30s with no principle
/// distinguishing them. That cost a CI run — <c>ordering/ordering-categories</c> and
/// <c>modifier/modifier-conditional-set-name</c> failed with "Setup failed: Timeout 10000ms
/// exceeded" on a Linux/headless runner after the same lane had passed 363 specs twice on a
/// developer machine. The conditions were right; the bound was picked without asking what it was
/// bounding.
/// </para>
/// <para>
/// <b>The distinction that matters is not the duration — it is whether the condition MUST hold.</b>
/// A wait for something that must become true should be generous, because a tight bound there can
/// only ever turn a slow-but-correct run into a failure. A probe whose failure is acceptable must
/// be short, because its cost is paid on every run that legitimately has nothing to find. Getting
/// those two backwards is the bug this type is shaped to prevent, so pick by meaning and let the
/// number follow.
/// </para>
/// </remarks>
internal static class NrUiTimeouts
{
    /// <summary>
    /// A condition that MUST become true for the operation to be correct — a store value settling,
    /// an element the next step needs, a route arriving.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. Exceeding it is a real failure worth reporting loudly, and being
    /// generous costs nothing when the condition holds in milliseconds.
    /// </remarks>
    internal const int Condition = 30_000;

    /// <summary>
    /// An interaction with an element that is expected to be present and actionable — a click, a
    /// select, filling a field.
    /// </summary>
    /// <remarks>
    /// Slightly tighter than <see cref="Condition"/> because Playwright's actionability check is
    /// already retrying underneath, so a long stall here usually means the element is wrong rather
    /// than slow.
    /// </remarks>
    internal const int Interaction = 20_000;

    /// <summary>
    /// Work whose duration NR does not bound — a catalogue reload that re-parses and recurses
    /// through every catalogue referencing it.
    /// </summary>
    internal const int UnboundedWork = 60_000;

    /// <summary>
    /// A probe whose failure is ACCEPTABLE and handled — "is this popup still open?", "did the
    /// overlay close?". The caller tolerates a timeout and proceeds.
    /// </summary>
    /// <remarks>
    /// Must stay short. Unlike the ceilings above, this one is paid in full on every run where the
    /// thing legitimately is not there, so a generous value here is a pure tax.
    /// </remarks>
    internal const int OptionalProbe = 3_000;

    /// <summary>
    /// Concluding that something is ABSENT, where absence is a legitimate result rather than a
    /// failure — enumerating a widget that may genuinely offer no options.
    /// </summary>
    /// <remarks>
    /// Shortest of all, and the only one where a larger number makes the tool WORSE: waiting longer
    /// for something that is not coming turns "this widget is empty" into a timeout, and the caller
    /// then drops the widget from its report instead of recording it as empty.
    /// </remarks>
    internal const int AbsenceProbe = 750;
}
