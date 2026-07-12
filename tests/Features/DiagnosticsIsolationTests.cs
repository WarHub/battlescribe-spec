using BattleScribeSpec.BsGameDataUiDriver;
using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.NrGameDataUiDriver;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// Task 10 (#303): with N parallel adapter workers, <c>BS_UI_DIAGNOSTICS_DIR</c>,
/// <c>BS_GAMEDATA_UI_DIAGNOSTICS_DIR</c> and <c>NR_GAMEDATA_UI_DIAGNOSTICS_DIR</c> used to resolve
/// to a single, shared, unsuffixed directory — every worker overwrote the others' dumps. These
/// tests pin that two different <c>BSSPEC_WORKER_INDEX</c> values now resolve to different
/// directories, and that engine-host stderr (previously only readable via
/// <c>AdapterProcess.GetStderrTail</c> after a failure) is forwarded live to the parent's stderr.
/// </summary>
/// <remarks>
/// These tests mutate process-wide environment variables (<c>BSSPEC_WORKER_INDEX</c>,
/// <c>BS_UI_DIAGNOSTICS_DIR</c>, etc.) and restore them in a <c>finally</c>, following the same
/// pattern already used by <see cref="AdapterProcessEnvTests"/>. No other test in the suite reads
/// or writes these specific variables, so this is safe even though xUnit runs collections (and
/// this class has no <c>[Collection]</c>, so it is its own) in parallel by default.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class DiagnosticsIsolationTests
{
    [Fact]
    public void BsUiDiagnostics_ResolveDefaultDirectory_DiffersPerWorkerIndex()
    {
        AssertDiffersPerWorker(BsUiDiagnostics.ResolveDefaultDirectory, "BS_UI_DIAGNOSTICS_DIR");
    }

    [Fact]
    public void BsGameDataUiDiagnostics_ResolveDefaultDirectory_DiffersPerWorkerIndex()
    {
        AssertDiffersPerWorker(BsGameDataUiDiagnostics.ResolveDefaultDirectory, "BS_GAMEDATA_UI_DIAGNOSTICS_DIR");
    }

    [Fact]
    public void NrGameDataUiDiagnostics_DefaultArtifactsDir_DiffersPerWorkerIndex()
    {
        AssertDiffersPerWorker(() => NrGameDataUiDiagnostics.DefaultArtifactsDir, "NR_GAMEDATA_UI_DIAGNOSTICS_DIR");
    }

    /// <summary>
    /// Resolves <paramref name="resolve"/> under no worker index, worker "1", and worker "2":
    /// today (pre-fix) all three collapse to the same path; after the fix, all three differ, and
    /// the worker-index-bearing paths end with the expected <c>-wN</c> suffix.
    /// </summary>
    private static void AssertDiffersPerWorker(Func<string> resolve, string overrideEnvVar)
    {
        var savedWorkerIndex = Environment.GetEnvironmentVariable("BSSPEC_WORKER_INDEX");
        var savedOverride = Environment.GetEnvironmentVariable(overrideEnvVar);
        try
        {
            // Make sure no stray override from a previous test/process short-circuits resolution.
            Environment.SetEnvironmentVariable(overrideEnvVar, null);

            Environment.SetEnvironmentVariable("BSSPEC_WORKER_INDEX", null);
            var unsuffixed = resolve();

            Environment.SetEnvironmentVariable("BSSPEC_WORKER_INDEX", "1");
            var worker1 = resolve();

            Environment.SetEnvironmentVariable("BSSPEC_WORKER_INDEX", "2");
            var worker2 = resolve();

            Assert.NotEqual(unsuffixed, worker1);
            Assert.NotEqual(unsuffixed, worker2);
            Assert.NotEqual(worker1, worker2);

            Assert.EndsWith("-w1", worker1, StringComparison.Ordinal);
            Assert.EndsWith("-w2", worker2, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BSSPEC_WORKER_INDEX", savedWorkerIndex);
            Environment.SetEnvironmentVariable(overrideEnvVar, savedOverride);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AdapterProcess_ForwardsChildStderr_ToParentConsoleError_TaggedWithWorkerIndex()
    {
        var ct = TestContext.Current.CancellationToken;

        // dotnet's own muxer reliably writes a deterministic, cross-platform message to stderr
        // (and only stderr) when asked to exec a dll that doesn't exist — no reliance on a shell
        // (cmd/bash differ) or on a purpose-built helper binary. The marker makes the assertion
        // unambiguous even though other tests may write to Console.Error concurrently.
        var marker = $"bsspec-stderr-marker-{Guid.NewGuid():N}";

        var originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            using var process = AdapterProcess.Start(
                "dotnet",
                $"exec {marker}.dll",
                new Dictionary<string, string> { ["BSSPEC_WORKER_INDEX"] = "7" });

            // The child exits almost immediately after writing to stderr; ErrorDataReceived
            // delivers asynchronously off the process-exit path, so poll briefly rather than
            // assume it has already landed by the time Start() returns.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!captured.ToString().Contains(marker, StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, ct);
            }
        }
        finally
        {
            Console.SetError(originalError);
        }

        var forwarded = captured.ToString();
        Assert.Contains(marker, forwarded, StringComparison.Ordinal);
        Assert.Contains("[host:7] ", forwarded, StringComparison.Ordinal);
    }
}
