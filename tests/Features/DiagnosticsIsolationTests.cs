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
/// <para>
/// These tests mutate process-wide environment variables (<c>BSSPEC_WORKER_INDEX</c>,
/// <c>BS_UI_DIAGNOSTICS_DIR</c>, etc.) and restore them in a <c>finally</c>, following the same
/// pattern already used by <see cref="AdapterProcessEnvTests"/>. No other test in the suite reads
/// or writes these specific variables.
/// </para>
/// <para>
/// <b>Code-review follow-up (final whole-branch review):</b> the last test in this class
/// (<see cref="AdapterProcess_ForwardsChildStderr_ToParentConsoleError_TaggedWithWorkerIndex"/>)
/// hijacks the process-wide <c>Console.Error</c> via <c>Console.SetError</c> for the duration of
/// spawning a child process. <see cref="AdapterProcess.Start"/>'s stderr forwarding (#303, this
/// branch) writes to <c>Console.Error</c> from a BACKGROUND <c>ErrorDataReceived</c> callback
/// thread — so any concurrently-running test elsewhere in the suite that also spawns an adapter
/// process (spanning many collections: e.g. <c>EndToEndTraceTests</c> in THIS collection, plus
/// every conformance fixture that owns a pooled adapter process) can now write into the
/// <see cref="StringWriter"/> this test installed as <c>Console.Error</c> WHILE this test is also
/// writing to it — and <see cref="StringWriter"/>'s underlying <c>StringBuilder</c> is not
/// thread-safe for concurrent writers, so that's a real (if rare) source of corruption/exceptions,
/// not just misdirected log lines. This interaction did not exist before #303 added the live
/// forwarding.
/// </para>
/// <para>
/// Fix: share the <c>HarnessCollectorEnv</c> collection with <see cref="EndToEndTraceTests"/> —
/// xUnit serializes tests WITHIN one collection while running different collections in parallel,
/// so this stops the one adapter-spawning test class known to run in the same assembly-fixture
/// neighborhood as this one from ever overlapping it. This is a partial mitigation, not a complete
/// one: it does not serialize against the many Conformance-collection fixtures that also own
/// pooled adapter processes (BsGameDataUi, FrozenNrRoster, LiveNrGameData, ...) in their own
/// collections — collection membership only pairs two SPECIFIC classes, and putting this test in
/// the same collection as literally every adapter-owning fixture in the suite would serialize away
/// most of the suite's parallelism, which the "harness reuse and parallelism" branch's whole point
/// is to avoid trading away. The residual race is accepted as low-probability (the hijack window is
/// a single child-process spawn) and structurally identical to the already-accepted residual in
/// <c>BrowserResourceRaceGate</c>'s remarks (a per-resource semaphore, not blanket collection
/// membership, is the tool for a suite-wide guarantee — not applicable here since this isn't a
/// resource-count peak but a shared-writer race).
/// </para>
/// </remarks>
[Collection("HarnessCollectorEnv")]
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
