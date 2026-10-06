namespace BattleScribeSpec.Cli.Tests;

/// <summary>
/// Covers <c>bs-spec compare</c> (#271 Task 12): the verdict-equality rail a future auto-tuner
/// must pass. The critical case is RED-on-divergence — two configurations that produce different
/// per-spec verdicts must fail the comparison with a non-zero exit code, never just a slower/faster
/// number. The reference adapter's <c>BSSPEC_TEST_FORCE_FAIL</c> hook (see
/// <c>src/BattleScribeSpec.ReferenceAdapter/ForceFailEngines.cs</c>) is what lets a test make one
/// arm diverge deterministically without touching any real engine.
/// </summary>
public sealed class CompareCommandTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Compare_ExitsNonZero_WhenAVerdictDiverges()
    {
        // A configuration change that alters conformance results is not an optimization, it is a
        // regression — and this assertion is the only thing in the harness that catches it.
        var adapterDll = CliProcess.ReferenceAdapterDll;

        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
            "compare",
            "--engine", $"battlescribe=dotnet:{adapterDll}",
            "--filter", "protocol/protocol-kitchen-sink",
            "--config-a", "",
            "--config-b", "BSSPEC_TEST_FORCE_FAIL=1");

        Assert.NotEqual(0, exitCode);
        var combined = stdOut + stdErr;
        Assert.Contains("DIVERGENCE", combined, StringComparison.Ordinal);
        Assert.Contains("A=passed", combined, StringComparison.Ordinal);
        Assert.Contains("B=failed", combined, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Compare_IdenticalConfigs_ExitsZero_AndTimesNeitherArmCold()
    {
        var adapterDll = CliProcess.ReferenceAdapterDll;

        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
            "compare",
            "--engine", $"battlescribe=dotnet:{adapterDll}",
            "--filter", "protocol/protocol-kitchen-sink",
            "--config-a", "",
            "--config-b", "");

        var combined = stdOut + stdErr;
        Assert.Equal(0, exitCode);
        Assert.Contains("Verdicts identical", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("DIVERGENCE", combined, StringComparison.Ordinal);

        Assert.Matches(@"speedup \(B/A\):\s*[0-9]+\.[0-9]+x", combined);

        // Neither timed arm runs cold. `compare` used to time arm A first with no warm-up, so arm A
        // ate the process's first-run costs (JIT, cold OS file cache, first AV scan of freshly built
        // DLLs) that arm B then got for free: two IDENTICAL arms measured a speedup as low as 0.29,
        // about one run in six. The fix is a discarded warm-up pass over the same spec set before
        // either arm is timed, so this asserts that pass, in order, rather than the ratio that found
        // the bug. That ratio was asserted in [0.6, 1.6], and a wall-clock ratio of two arms a second
        // or two long is a property of a quiet machine: pre-push runs this module beside the other
        // one, and identical arms read 1.69 and 1.7 there with the warm-up in place (#525).
        var warmUp = combined.IndexOf("Warm-up (untimed, discarded)", StringComparison.Ordinal);
        var warmUpRan = combined.IndexOf("Running 1 specs", warmUp + 1, StringComparison.Ordinal);
        var armA = combined.IndexOf("Arm A", StringComparison.Ordinal);
        var armB = combined.IndexOf("Arm B", StringComparison.Ordinal);
        Assert.True(warmUp >= 0 && warmUp < warmUpRan && warmUpRan < armA && armA < armB,
            $"Expected a warm-up pass that runs the spec before arm A and arm B are timed:\n{combined}");
    }

    /// <summary>
    /// <b>Identical verdicts over specs that never ran are not "verdict-neutral".</b> Two arms that
    /// both executed nothing agree perfectly and prove nothing, so <c>compare</c> exits 8 there, as an
    /// empty <c>run --all</c> does, instead of printing "Verdicts identical across 0 spec(s)" and 0.
    /// </summary>
    /// <remarks>
    /// Falsifiable: delete the <c>executed == 0</c> check in <c>CompareCommand</c> and this exits 0; print
    /// the message through <c>Ui.Error</c> and the last stderr line is only its wrapped tail.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Compare_FilterMatchingNothing_ExitsEight_NotIdentical()
    {
        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
            "compare",
            "--engine", $"battlescribe=dotnet:{CliProcess.ReferenceAdapterDll}",
            "--filter", "no-such-spec",
            "--config-a", "",
            "--config-b", "");

        var combined = stdOut + stdErr;
        Assert.True(exitCode == 8, $"exit code {exitCode}; output:\n{combined}");
        var lastLine = CliProcess.LastLine(stdErr);
        Assert.True(
            lastLine.StartsWith("error: compared ", StringComparison.Ordinal)
                && lastLine.Contains("executed 0 in either arm", StringComparison.Ordinal)
                && lastLine.EndsWith("(exit 8).", StringComparison.Ordinal),
            $"the last stderr line is not the whole nothing-executed message: \"{lastLine}\"; output:\n{combined}");
        Assert.DoesNotContain("Verdicts identical", combined, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Compare_ExpectedFailures_ThreadsIntoBothArms_ReportsExpectedFailureNotFailed()
    {
        // #271 review IMPORTANT 1: compare never set SpecSuiteOptions.ExpectedFailuresEngine, so a
        // spec annotated `engines: { <name>: fail }` was always labeled "failed"/"passed" rather
        // than "expected-failure"/"unexpected-pass". This fixture spec is annotated fail for a
        // synthetic engine identity; --config-b force-fails it via the reference adapter's test-only
        // hook while --config-a leaves it passing, so the two arms deliberately diverge on status —
        // proving --expected-failures reached BOTH arms: arm A must read "unexpected-pass" (it
        // actually passed but was annotated to fail) and arm B must read "expected-failure" (it
        // actually failed and was annotated to fail), never the plain "passed"/"failed" that
        // omitting the flag would produce.
        var adapterDll = CliProcess.ReferenceAdapterDll;
        var fixturesDir = WriteExpectedFailureFixture();

        try
        {
            var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
                "compare",
                "--engine", $"cmp-xfail-engine=dotnet:{adapterDll}",
                "--specs", fixturesDir,
                "--expected-failures", "cmp-xfail-engine",
                "--config-a", "",
                "--config-b", "BSSPEC_TEST_FORCE_FAIL=1");

            var combined = stdOut + stdErr;
            Assert.NotEqual(0, exitCode);
            Assert.Contains("DIVERGENCE", combined, StringComparison.Ordinal);
            Assert.Contains("A=unexpected-pass", combined, StringComparison.Ordinal);
            Assert.Contains("B=expected-failure", combined, StringComparison.Ordinal);
            Assert.DoesNotContain("B=failed", combined, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturesDir, recursive: true);
        }
    }

    /// <summary>
    /// Writes a single-spec fixture directory (category "xfail") annotated
    /// <c>engines: { cmp-xfail-engine: fail }</c>: normal setup/steps that pass on the reference
    /// adapter unless <c>BSSPEC_TEST_FORCE_FAIL</c> is set. No repo spec carries a top-level "fail"
    /// annotation (grepped for one), so a private fixture is needed to exercise the
    /// expected-failure/unexpected-pass classification without touching the shared specs tree.
    /// </summary>
    private static string WriteExpectedFailureFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bsspec-compare-xfail-" + Guid.NewGuid().ToString("N")[..8], "xfail");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "xfail-demo.yaml"), """
            id: xfail-demo
            category: xfail
            description: Fixture for bs-spec compare's --expected-failures threading test.
            engines:
              cmp-xfail-engine: fail

            setup:
              gameSystem:
                id: gs-1
                name: GS
                costTypes:
                  - id: ct-pts
                    name: pts
                forceEntries:
                  - id: fe-1
                    name: Force
                    categoryLinks:
                      - id: cl-1
                        targetId: cat-1
                        name: Cat
                categoryEntries:
                  - id: cat-1
                    name: Cat

              catalogues:
                - id: cat-file-1
                  gameSystemId: gs-1
                  selectionEntries:
                    - id: se-1
                      name: Unit
                      type: unit
                      costs:
                        - name: pts
                          typeId: ct-pts
                          value: 10
                      categoryLinks:
                        - id: cl-se-1
                          targetId: cat-1
                          name: Cat
                          primary: true

            steps:
              - action: addForce
                id: add-1
                forceEntryId: fe-1

              - expectedState:
                  forceCount: 1
            """);
        return Path.GetDirectoryName(dir)!;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Compare_RejectsMalformedConfig()
    {
        var (exitCode, _, stdErr) = await CliProcess.RunAsync(
            "compare", "--engine", "battlescribe",
            "--config-a", "NOT_KEY_VALUE",
            "--config-b", "");

        Assert.Equal(1, exitCode);
        Assert.Contains("--config-a", stdErr, StringComparison.Ordinal);
        Assert.Contains("KEY=VALUE", stdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", stdErr, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Compare_PolicyA_InvalidValue_ReportsCliError()
    {
        // --workers was deleted from `compare` (#271 Task 6) — it becomes --policy-a/--policy-b,
        // the same vocabulary as `run --policy` and `serve --policy`, parsed by the shared
        // PolicyOverride.Apply. This asserts a malformed --policy-a is a clean CLI error, not an
        // unhandled exception, mirroring Compare_RejectsMalformedConfig for --config-a.
        var (exitCode, _, stdErr) = await CliProcess.RunAsync(
            "compare", "--engine", "battlescribe",
            "--policy-a", "workers=0",
            "--config-a", "",
            "--config-b", "");

        Assert.Equal(1, exitCode);
        // "positive integer", not just "workers": before --policy-a existed, System.CommandLine's
        // own "unrecognized argument 'workers=0'" error also happens to contain the substring
        // "workers" — asserting the actual PolicyOverride.Apply message keeps this falsifiable.
        Assert.Contains("positive integer", stdErr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unhandled exception", stdErr, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Compare_ConfigAB_AreOptional_WhenOnlyPolicyVaries()
    {
        // --config-a/--config-b used to be Required=true, which forced every --policy-a/--policy-b
        // caller to also type two empty strings for an axis they weren't using. Now that
        // --policy-a/--policy-b is the primary axis (this is the exact recipe documented in
        // docs/warm-reuse.md and the #271 plan's Task 6 gate), --config-a/--config-b must be
        // omittable — this is the literal command shape from that recipe, minus the real UI
        // engine (uses "battlescribe" so it runs hermetically, no JVM/browser needed).
        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
            "compare",
            "--engine", "battlescribe",
            "--filter", "protocol/protocol-kitchen-sink",
            "--policy-a", "reuse=on",
            "--policy-b", "reuse=off");

        var combined = stdOut + stdErr;
        Assert.Equal(0, exitCode);
        Assert.Contains("Verdicts identical", combined, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Compare_PolicyAB_RunsArmsWithDifferentReuseDecisions_VerdictsStillAsserted()
    {
        // The verdict-equality rail's whole reconnection: BSSPEC_DISABLE_WARM_REUSE (the old
        // ablation channel) was deleted along with warm-reuse moving to a ConcurrencyPolicy the
        // parent computes and sends. --policy-a/--policy-b is the replacement — a per-arm
        // ConcurrencyPlan override, using the exact vocabulary `run --policy`/`serve --policy`
        // share (PolicyOverride.Apply), NOT a second parser. A launchable (dotnet:) adapter has no
        // channel to receive a plan override at all (EngineHostLocator.Resolve throws), so this
        // uses the "battlescribe" BUILT-IN (in-process, no JVM/browser dependency) — forcing
        // reuse=on/off on it genuinely changes whether AdapterHandler recreates the engine per
        // spec or calls Cleanup() and keeps it alive, even though the engine's own profile
        // declares neither domain reuse-safe (an explicit override is allowed — it is exactly the
        // ablation this rail exists to run — just warned).
        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
            "compare",
            "--engine", "battlescribe",
            "--filter", "protocol/protocol-kitchen-sink",
            "--config-a", "",
            "--config-b", "",
            "--policy-a", "reuse=on",
            "--policy-b", "reuse=off");

        var combined = stdOut + stdErr;
        Assert.Equal(0, exitCode);
        Assert.Contains("Verdicts identical", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("DIVERGENCE", combined, StringComparison.Ordinal);
    }
}
