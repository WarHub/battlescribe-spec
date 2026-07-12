using System.Globalization;
using BattleScribeSpec.Telemetry.Collector;

[assembly: AssemblyFixture(typeof(BattleScribeSpec.Tests.TelemetryAssemblyFixture))]

namespace BattleScribeSpec.Tests;

/// <summary>
/// Hosts one <see cref="HarnessCollector"/> for the entire xUnit test assembly (#271 Task 9).
/// </summary>
/// <remarks>
/// <para>
/// The CLI path (<c>bs-spec run --all</c>) is instrumented per-run by <c>RunBatch</c>, which
/// starts a <see cref="HarnessCollector"/> around the whole batch and hands each child
/// <c>bs-engine-host</c> process its own OTLP exporter env. The <c>dotnet test</c> path has no
/// such entry point to hook — there are 11 independent collection fixtures, each free to spin up
/// whenever xUnit decides to run their collection, with <c>parallelizeTestCollections: true</c>
/// and <c>maxParallelThreads</c> left at its CPU-count default (see
/// <c>tests/xunit.runner.json</c>). An assembly fixture is the one hook that spans that: xUnit v3
/// constructs it once before any test runs and disposes it once after the last one finishes,
/// regardless of how many collections ran, or how many of them overlapped.
/// </para>
/// <para>
/// <b>Why <c>OTEL_METRIC_EXPORT_INTERVAL</c> is set here, before <see cref="HarnessCollector.StartAsync"/>
/// builds the provider:</b> in the CLI path, the process holding a pool/JVM is always a CHILD
/// (<c>bs-engine-host</c>), and <see cref="HarnessCollector.ChildEnvironment"/> already sets a
/// short export interval on it so a short-lived child still gets to report before it exits. In
/// the xUnit path there is no child — THIS process is the one holding every browser-context pool
/// and the JVM — and its own <c>MeterProvider</c> only flushes on <c>Dispose</c> by default: one
/// snapshot, at the very end of the whole run, by which point every pool has already torn down
/// and every counter reads zero. Without a short interval, <c>harness.resource.count</c> would
/// still appear in the artifact, but its only value would be 0 — the one value that can never be
/// the peak. A short interval turns that single dead reading into a real time series.
/// </para>
/// <para>
/// <b>Fail-open</b>: a run must never fail, slow down meaningfully, or behave differently because
/// telemetry could not start. <see cref="HarnessCollector.StartAsync"/> already fails open
/// internally (a bind failure yields a disabled collector rather than an exception); the extra
/// <c>try</c>/<c>catch</c> here is defense against anything failing before that call so this
/// fixture can never itself fail an entire <c>dotnet test</c> run.
/// </para>
/// </remarks>
public sealed class TelemetryAssemblyFixture : IAsyncLifetime
{
    /// <summary>
    /// The assembly-wide collector, or null if it could not be started at all (extremely
    /// unlikely — see the fail-open remarks above). Callers should treat a null or
    /// <see cref="HarnessCollector.Enabled"/> <c>false</c> collector the same way: telemetry
    /// simply isn't flowing for this run.
    /// </summary>
    public HarnessCollector? Collector { get; private set; }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        try
        {
            Environment.SetEnvironmentVariable("OTEL_METRIC_EXPORT_INTERVAL", "2000");

            var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);
            var artifactPath = Path.Combine("artifacts", "telemetry", $"xunit-{runId}");

            Collector = await HarnessCollector.StartAsync(artifactPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[telemetry] assembly fixture disabled: {ex.Message}");
            Collector = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Collector is not null)
        {
            await Collector.DisposeAsync();
        }
    }
}
