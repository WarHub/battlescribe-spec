using System.Runtime.CompilerServices;
using System.Text.Json;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Pins <c>maxParallelThreads</c> in both <c>xunit.runner.json</c> files to one declared value, so
/// the two cannot drift apart or be changed without meeting the reasoning below.
/// </summary>
/// <remarks>
/// <para>
/// <b>This value is NOT the concurrency policy's.</b> It used to be pinned to
/// <c>ConcurrencyPolicy.UndeclaredMemoryWorkerCap</c> — a memory-safety ceiling for engines that
/// declare no footprint — which meant raising that cap for an engine would silently have re-sized
/// the test host. Two quantities, no shared meaning, one number. The link is cut: xUnit's thread
/// count is a property of the <em>test runner</em>, it is declared here, and it is justified here.
/// The runner reads this JSON before any of our code executes, so no C# function can supply it (see
/// <c>ConcurrencyPolicy</c>'s remarks for the RunSettings alternative, investigated and rejected).
/// </para>
/// <para>
/// <b>Why a multiplier and not a thread count.</b> xUnit's default is
/// <c>Environment.ProcessorCount</c>. A hardcoded literal fights the machine in both directions: the
/// previous <c>8</c> <em>capped</em> this 32-core dev box (32 → 8) but silently <em>doubled</em> the
/// 4-vCPU CI runner (4 → 8) — an unmeasured increase in test-host contention on the smallest, most
/// memory-constrained machine in the fleet, shipped under a commit message that said "bound the
/// xUnit path". xunit.v3 accepts a machine-relative multiplier (<c>"{n}x"</c>, parsed with
/// InvariantCulture, so it is safe on comma-decimal locales), which scales with the box instead.
/// </para>
/// <para>
/// <b>Why 0.5x.</b> It can never raise parallelism above xUnit's own default on any machine — the
/// property the literal <c>8</c> violated. And half is the right half: xUnit's thread accounting
/// covers <em>only its own test threads</em>, while this suite's tests spawn the things that
/// actually consume the box — JVMs, Playwright Node drivers, Chromium trees, adapter processes —
/// none of which xUnit can see. Leaving half the cores to the processes the tests launch is the
/// honest reading of a suite like this one. Yields: <b>4-vCPU CI runner → 2 threads</b> (was 4 by
/// default, 8 under the old literal); <b>32-core dev box → 16 threads</b> (was 32 by default, 8
/// under the old literal).
/// </para>
/// <para>
/// Note what this does <em>not</em> bound, so nobody mistakes it for a solution to #314: the real
/// browser concurrency in a conformance test is <c>Parallel.ForEachAsync</c> inside a single
/// <c>[Fact]</c>, sized by <c>ConcurrencyPlan.PoolSize</c> (the context axis), which xUnit's thread
/// count does not constrain at all. A third quantity, on a third axis — do not pin any of the three
/// to either of the others, which is the mistake this class's own history records.
/// </para>
/// </remarks>
[Trait("Category", "Lint")]
public sealed class ConcurrencyConfigurationDriftTests
{
    /// <summary>
    /// The declared xUnit collection-parallelism setting for this repo's test assemblies. See the
    /// class remarks for the justification — do not change it without meeting them.
    /// </summary>
    internal const string XunitMaxParallelThreads = "0.5x";

    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot([CallerFilePath] string callerFilePath = "")
    {
        var dir = Path.GetDirectoryName(callerFilePath);
        while (dir is not null)
        {
            if (Directory.EnumerateFiles(dir, "*.slnx").Any())
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Could not find repository root (no *.slnx marker found) while traversing parents of '{callerFilePath}'.");
    }

    [Fact]
    public void XunitRunnerJsonMaxParallelThreadsMatchesTheDeclaredValue()
    {
        var xunitFiles = new[]
        {
            Path.Combine(RepoRoot, "tests", "xunit.runner.json"),
            Path.Combine(RepoRoot, "tests", "BattleScribeSpec.Cli.Tests", "xunit.runner.json"),
        };

        var mismatches = new List<string>();

        foreach (var filePath in xunitFiles)
        {
            if (!File.Exists(filePath))
            {
                Assert.Fail($"Expected xunit.runner.json not found: {filePath}");
            }

            var doc = JsonDocument.Parse(File.ReadAllText(filePath));

            if (!doc.RootElement.TryGetProperty("maxParallelThreads", out var maxParallelProp))
            {
                Assert.Fail($"maxParallelThreads property not found in {filePath}");
            }

            // A bare number here would be the regression: it is what silently doubled the CI
            // runner's thread count. The value must be the machine-relative multiplier.
            if (maxParallelProp.ValueKind != JsonValueKind.String)
            {
                mismatches.Add(
                    $"  {Path.GetRelativePath(RepoRoot, filePath)}: maxParallelThreads is " +
                    $"{maxParallelProp.ValueKind} ({maxParallelProp}) — expected the string \"{XunitMaxParallelThreads}\"");
                continue;
            }

            var declared = maxParallelProp.GetString();
            if (declared != XunitMaxParallelThreads)
            {
                mismatches.Add(
                    $"  {Path.GetRelativePath(RepoRoot, filePath)}: " +
                    $"maxParallelThreads = \"{declared}\" (expected \"{XunitMaxParallelThreads}\")");
            }
        }

        if (mismatches.Count > 0)
        {
            Assert.Fail(
                $"xunit.runner.json maxParallelThreads does not match the declared value " +
                $"(\"{XunitMaxParallelThreads}\"):\n{string.Join("\n", mismatches)}\n" +
                $"\n" +
                $"This is the test runner's own thread count — NOT an engine's worker count, and no " +
                $"longer tied to ConcurrencyPolicy. A bare integer here fights the machine: it caps a " +
                $"big dev box and RAISES the 4-vCPU CI runner above its default. Read " +
                $"{nameof(ConcurrencyConfigurationDriftTests)}'s remarks before changing it.");
        }
    }

    /// <summary>
    /// The environment-variable knobs the concurrency model replaced. Each one used to answer a
    /// question <c>ConcurrencyPolicy</c> now owns, from a second place that could disagree with it.
    /// </summary>
    public static readonly string[] RetiredKnobs =
    [
        "NR_PARALLEL",           // browser-context count; three fixtures, three different defaults
        "BS_UI_KEEP_ALIVE",      // BS-UI gamedata reuse; unset ⇒ cold, while the policy says warm
        "BSSPEC_DISABLE_WARM_REUSE", // the old reuse ablation channel; now compare --policy-a/--policy-b
    ];

    /// <summary>
    /// The branch's headline claim — "one policy, no environment-variable knobs" — asserted
    /// mechanically instead of in prose. No production code, and no test fixture, may READ any
    /// retired knob; no CI workflow may set one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the gate the model was missing. <c>BsGameDataUiFixture</c> kept reading
    /// <c>BS_UI_KEEP_ALIVE</c> long after the policy claimed to own reuse — the fixture ran cold on
    /// every developer's machine while the policy said warm, and CI set the variable in two jobs to
    /// paper over the difference. Nothing failed, because nothing was watching. Now something is:
    /// re-add the read and this goes red.
    /// </para>
    /// <para>
    /// <c>tests/Features</c> is exempt <b>by design</b> — that is where the proofs that these
    /// variables are ignored live (<c>FixtureConcurrencyTests</c>, <c>ServeCommandPolicyTests</c>),
    /// and they necessarily name the variables to set and restore them. Scanning <c>src/</c> and
    /// <c>tests/Infrastructure/</c> covers every place a knob could actually govern behaviour: the
    /// product, and the fixtures that are the harness's own production code.
    /// </para>
    /// </remarks>
    [Fact]
    public void RetiredEnvironmentKnobs_AreReadByNoProductionCodeOrFixture_AndSetByNoWorkflow()
    {
        var offenders = new List<string>();

        var scanned = new[]
        {
            Path.Combine(RepoRoot, "src"),
            Path.Combine(RepoRoot, "tests", "Infrastructure"),
        };

        foreach (var root in scanned)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                // Build output is not source.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                foreach (var knob in RetiredKnobs)
                {
                    // A mention in a comment is fine (they are explained all over this branch);
                    // a READ is what reintroduces the second mechanism.
                    if (text.Contains($"GetEnvironmentVariable(\"{knob}\")", StringComparison.Ordinal))
                    {
                        offenders.Add($"  {Path.GetRelativePath(RepoRoot, file)} reads {knob}");
                    }
                }
            }
        }

        var workflows = Path.Combine(RepoRoot, ".github", "workflows");
        if (Directory.Exists(workflows))
        {
            foreach (var file in Directory.EnumerateFiles(workflows, "*.yml", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                foreach (var knob in RetiredKnobs)
                {
                    if (text.Contains($"{knob}:", StringComparison.Ordinal))
                    {
                        offenders.Add($"  {Path.GetRelativePath(RepoRoot, file)} sets {knob}");
                    }
                }
            }
        }

        if (offenders.Count > 0)
        {
            Assert.Fail(
                "Retired concurrency/reuse environment knobs are back:\n" +
                string.Join("\n", offenders) + "\n\n" +
                "Each of these answers a question ConcurrencyPolicy owns, from a second place that can " +
                "disagree with it — which is exactly what happened before (BS_UI_KEEP_ALIVE unset ran " +
                "the BS-UI gamedata suite cold while the policy said warm, and CI set the variable to " +
                "hide it). Take the decision from ConcurrencyPolicy.For(machine, engine) instead; if it " +
                "gives the wrong answer, fix the policy — that is the point of having one.");
        }
    }
}
