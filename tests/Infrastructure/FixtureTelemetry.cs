using System.Diagnostics;
using BattleScribeSpec.Telemetry;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Instrumentation helpers shared by the 11 collection fixtures in this directory (#271 Task 9).
/// </summary>
/// <remarks>
/// <para>
/// Each fixture owns exactly one expensive resource (a JVM, a browser, or a browser-context
/// pool). <see cref="ResourceMetrics.Acquired"/>/<see cref="ResourceMetrics.Released"/> already
/// fire from inside the drivers and pools themselves (Task 8), so <c>harness.resource.count</c>
/// is live for free the moment ANY collector is listening — that is the whole point of
/// instrumenting at the resource owner rather than at the call site. What the fixtures add on
/// top is span-shaped: when a pool came up and how big it is, and how long a spec waited to get
/// an engine out of it.
/// </para>
/// <para>
/// Deliberately does not call <see cref="ResourceMetrics.Acquired"/>/<see cref="ResourceMetrics.Released"/>
/// itself — doing so here on top of the drivers/pools would double-count.
/// </para>
/// </remarks>
internal static class FixtureTelemetry
{
    /// <summary>Start a span covering one fixture's <c>InitializeAsync</c> resource bring-up.</summary>
    /// <param name="fixtureName">The fixture's own type name (<c>nameof(...)</c> at the call site).</param>
    public static Activity? StartInit(string fixtureName) =>
        HarnessTelemetry.StartOp($"fixture.init.{fixtureName}");

    /// <summary>Tag <paramref name="span"/> with the pool size, once a pool exists for a fixture that has one.</summary>
    public static void SetPoolSize(Activity? span, int size) =>
        span?.SetTag("harness.pool.size", size);

    /// <summary>
    /// Wrap one pooled <c>AcquireAsync</c> call in a short span, so the time a spec spends
    /// blocked waiting for a free engine (every engine in the pool busy) is visible as its own
    /// span rather than invisibly folded into whatever the caller does next.
    /// </summary>
    /// <param name="fixtureName">The owning fixture's own type name.</param>
    /// <param name="acquire">The pool's own <c>AcquireAsync</c> method.</param>
    /// <param name="ct">Cancellation token forwarded to <paramref name="acquire"/>.</param>
    public static async ValueTask<T> AcquireAsync<T>(
        string fixtureName,
        Func<CancellationToken, ValueTask<T>> acquire,
        CancellationToken ct)
    {
        using var span = HarnessTelemetry.StartOp($"fixture.acquire.{fixtureName}");
        return await acquire(ct);
    }
}
