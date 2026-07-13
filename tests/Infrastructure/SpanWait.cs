using System.Diagnostics;

namespace BattleScribeSpec.Tests.Infrastructure;

/// <summary>
/// Bounded poll for a span to appear in a captured-spans list built by an
/// <see cref="ActivityListener.ActivityStopped"/> callback.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS: <c>AdapterHandler.RunAsync</c> declares its SERVER activity outside the
/// per-command try/catch and disposes it in a <c>finally</c> that runs AFTER the response is
/// written and flushed to the client. That ordering is deliberate — it lets the span capture the
/// response write and carry an error status set by the catch/switch above it — but it means
/// <c>SendCommandAsync</c> returning does NOT mean <c>ActivityStopped</c> has fired yet. Asserting
/// on a captured-spans list immediately after awaiting a command races that callback: the
/// assertion passes only when the scheduler happens to run the adapter's <c>finally</c> block
/// before the test's continuation, which is exactly the kind of race that is green in a full
/// suite (where scheduling noise from other tests tends to let the callback win) and red in
/// isolation. Do not "simplify" this back into a bare <c>Assert.Single(captured, ...)</c> —
/// that reintroduces the race. Poll instead of asserting synchronously.
/// </para>
/// <para>
/// One case where the race does NOT apply: if the test drains the adapter loop to completion
/// first (e.g. <c>await connection.DisposeAsync()</c> on <see cref="InMemoryAdapterConnection"/>,
/// which awaits the handler loop's <see cref="Task"/> to finish), then by the time that await
/// returns, every command's <c>finally</c> block — including the activity dispose — has already
/// run, because it is sequenced on the same async call stack before the loop task can complete.
/// Using this helper there is still safe (and kept, for one consistent idiom across the suite) —
/// it will simply find the span on its first check.
/// </para>
/// </remarks>
public static class SpanWait
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Waits until <paramref name="predicate"/> matches an entry in <paramref name="capturedSpans"/>,
    /// polling under the same lock the <c>ActivityStopped</c> callback uses to append to it. Fails
    /// the test with a clear message if no match appears within <paramref name="timeout"/>
    /// (default 5s).
    /// </summary>
    /// <param name="capturedSpans">
    /// The list an <see cref="ActivityListener.ActivityStopped"/> callback appends to. Must be the
    /// exact list instance used as the lock object by that callback.
    /// </param>
    /// <param name="predicate">Matches the span being waited for, e.g. by operation name.</param>
    /// <param name="ct">Cancellation token for the poll delay.</param>
    /// <param name="because">Optional context included in the failure message.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 5 seconds.</param>
    /// <returns>The first matching span.</returns>
    public static async Task<Activity> ForAsync(
        List<Activity> capturedSpans,
        Func<Activity, bool> predicate,
        CancellationToken ct,
        string? because = null,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);

        while (true)
        {
            lock (capturedSpans)
            {
                var match = capturedSpans.Find(a => predicate(a));
                if (match is not null)
                {
                    return match;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                var suffix = because is null ? string.Empty : $" ({because})";
                var seen = SnapshotOperationNames(capturedSpans);
                Assert.Fail(
                    $"SpanWait timed out after {(timeout ?? DefaultTimeout).TotalSeconds}s waiting for a " +
                    $"matching span{suffix}. Spans captured so far: [{seen}].");
                throw new InvalidOperationException("unreachable"); // Assert.Fail always throws.
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    private static string SnapshotOperationNames(List<Activity> capturedSpans)
    {
        lock (capturedSpans)
        {
            return string.Join(", ", capturedSpans.Select(a => a.OperationName));
        }
    }
}
