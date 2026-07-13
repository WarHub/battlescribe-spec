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
/// and <c>maxParallelThreads</c> capped at a conservative, hardcoded 8 (see
/// <c>tests/xunit.runner.json</c> and Task 7 of
/// <c>docs/superpowers/plans/2026-07-13-harness-concurrency-model.md</c> — that file is static JSON
/// read by the runner before any of our code runs, so it cannot pull the number from
/// <c>ConcurrencyPolicy</c> at runtime). An assembly fixture is the one hook that spans that: xUnit v3
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

    /// <summary>The local artifact path passed to <see cref="HarnessCollector.StartAsync"/>, kept so
    /// <see cref="DisposeAsync"/> can read it back once the collector has flushed. Null when the
    /// collector never started (see the fail-open <c>catch</c> in <see cref="InitializeAsync"/>) or
    /// exported externally (no local artifact to read).</summary>
    private string? _artifactPath;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        try
        {
            Environment.SetEnvironmentVariable("OTEL_METRIC_EXPORT_INTERVAL", "2000");

            var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);

            // Anchored at the repo root, NOT a bare relative path: VSTest runs the test host with
            // its working directory set to the test assembly's own output folder (e.g.
            // artifacts/bin/BattleScribeSpec.Tests/debug/), not the repo root the CLI path uses. A
            // bare "artifacts/telemetry" here would silently write under that nested bin folder —
            // a path CI's "Upload telemetry" step (which looks at the repo-root artifacts/telemetry/)
            // would never find. Falls back to the bare relative path (old behavior) only if the
            // repo root genuinely cannot be located.
            var artifactRoot = TestPaths.RepoRootDirectory is { } repoRoot
                ? Path.Combine(repoRoot, "artifacts", "telemetry")
                : Path.Combine("artifacts", "telemetry");
            var artifactPath = Path.Combine(artifactRoot, $"xunit-{runId}");

            // Bound artifacts/telemetry/'s growth before adding to it — this run's own artifact set
            // doesn't exist yet, so it is never a sweep candidate. See TelemetryRetention for why
            // this isn't wired into HarnessCollector.StartAsync itself (it would also fire for ad
            // hoc unit tests pointed at a shared temp directory, racing sibling tests' artifacts).
            TelemetryRetention.Sweep(artifactRoot, currentArtifactBasePath: artifactPath);

            Collector = await HarnessCollector.StartAsync(artifactPath);
            _artifactPath = Collector.HasLocalArtifact ? artifactPath : null;
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
            // Dispose FIRST: this force-flushes the assembly-wide TracerProvider/MeterProvider, so
            // the artifact TraceSummary.FromArtifact reads below actually has every span/metric the
            // whole dotnet test run produced (mirrors RunBatch's ordering, and for the same reason).
            await Collector.DisposeAsync();
        }

        if (_artifactPath is { } artifactPath)
        {
            var summary = TraceSummary.FromArtifact(artifactPath);

            // Unlike the CLI paths (RunBatch/CompareCommand), this artifact typically has
            // SpecCount == 0 — see the remarks on TraceSummary.FromArtifact — so the print gate is
            // "not the Empty singleton" (any real signal at all), not "ran at least one spec".
            if (!ReferenceEquals(summary, TraceSummary.Empty))
            {
                // AppendToGitHubStepSummary alone means a developer running `dotnet test` locally
                // sees nothing — the peak (this whole fixture's deliverable) would exist only in an
                // unread $GITHUB_STEP_SUMMARY file that is unset outside CI. WriteTable to stderr
                // too, mirroring RunBatch/CompareCommand's own CLI-path printing.
                Console.Error.WriteLine();
                summary.WriteTable(Console.Error);
                summary.AppendToGitHubStepSummary("Trace summary — dotnet test");
            }
        }
    }
}
