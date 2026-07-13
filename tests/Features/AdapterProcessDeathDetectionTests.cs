using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// #308 CI regression: all six <c>SpecSuiteRunnerAdapterDeathTests</c> passed on Windows and failed
/// on Linux CI. Root cause confirmed via code trace (not guessed): <see cref="AdapterProcess.HasExited"/>
/// is <c>_disposed || _process.HasExited</c>, and <c>SpecSuiteRunner.RunOneSpec</c> reads it
/// immediately after a spec attempt to decide whether to retry/replace. On Windows, a child's
/// <c>Process.HasExited</c> flips essentially the instant it dies. On Linux, a just-exited child is
/// not reaped synchronously — its stdio pipe closing (which is what makes
/// <see cref="NdjsonLineConnection"/>'s read loop fault, and <see cref="AdapterProcess.SendCommandAsync"/>
/// throw) can race ahead of <c>Process.HasExited</c> flipping true, so a genuine death gets
/// misclassified as an ordinary transport error and the entire #304 recovery mechanism never
/// engages.
/// </summary>
/// <remarks>
/// This box cannot reproduce Linux's reaping timing, so instead of asserting on wall-clock races,
/// these tests drive <see cref="AdapterProcess.IsDeadAfterTransportFailure"/> — the pure
/// classification helper the production fix extracted — directly with both possible shapes of that
/// race, via a fake <c>waitForExitShort</c> delegate. This is deterministic and fails on BOTH
/// platforms if the classification regresses (e.g. someone reverts to trusting only the immediate
/// snapshot), rather than only failing on Linux under unlucky timing.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class AdapterProcessDeathDetectionTests
{
    [Fact]
    public void AlreadyReapedExited_IsDead_WithoutConsultingTheBoundedWait()
    {
        var waitWasCalled = false;

        var result = AdapterProcess.IsDeadAfterTransportFailure(
            hasExitedNow: true,
            waitForExitShort: () =>
            {
                waitWasCalled = true;
                return false; // must not matter: short-circuited by hasExitedNow
            });

        Assert.True(result);
        Assert.False(waitWasCalled, "hasExitedNow == true must short-circuit; the bounded wait is only for the unreaped case.");
    }

    [Fact]
    public void NotYetReaped_ButExitsWithinTheBoundedWait_IsDead()
    {
        // This is the Linux race itself: HasExited reads false at the moment the transport failed
        // (the child hasn't been reaped yet), but the process is genuinely dying/dead and reports
        // its exit once actually waited for. Before the fix, SpecSuiteRunner would have read
        // HasExited immediately afterward and seen `false` here — misclassifying a real death as an
        // ordinary failure, which is exactly what made all six #308 tests fail only on Linux CI.
        var result = AdapterProcess.IsDeadAfterTransportFailure(
            hasExitedNow: false,
            waitForExitShort: () => true);

        Assert.True(result);
    }

    [Fact]
    public void NotYetReaped_AndStaysAliveThroughTheBoundedWait_IsNotDead()
    {
        // The distinction the design relies on: a transport error against a process that is
        // genuinely still running (a bad response, a hung call unrelated to a crash) must NOT be
        // classified as a death — no spurious retry/replacement should be spent on a live process.
        var result = AdapterProcess.IsDeadAfterTransportFailure(
            hasExitedNow: false,
            waitForExitShort: () => false);

        Assert.False(result);
    }
}
