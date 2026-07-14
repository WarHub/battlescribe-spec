using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using BattleScribeSpec.Engines;

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
/// honest reading of a suite like this one.
/// </para>
/// <para>
/// <b>Yields — and the CI figure is MEASURED now, not assumed.</b> The GitHub runner reports
/// <c>nproc: 2</c> / <c>MemTotal: 7.8 GiB</c> (printed by the <c>Runner profile</c> step in the
/// <c>checks</c> job; docs/concurrency-policy-measurements.md §11.6) — <b>not</b> the "4-vCPU / 16 GiB
/// CI runner" this repo's docs assumed throughout, which was really the local <em>container</em> used
/// to model CI. So: <b>2-vCPU CI runner → 1 thread</b> (was 2 by default, and <b>8</b> under the old
/// literal — which means that literal raised the smallest box in the fleet <em>eightfold</em>, not
/// twofold as previously claimed); <b>32-core dev box → 16 threads</b> (was 32 by default, 8 under the
/// old literal). One thread is a valid, conservative answer for a 2-core box that must leave room for a
/// Chromium tree; it was the <em>literal</em> that was indefensible, and it is gone.
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
    /// <b>EVERY fixture that opens a session on a third party's production website must draw it from
    /// <see cref="LiveLoadBudget"/>.</b> A policy nobody invokes is a policy nobody has — and four of
    /// the five live fixtures did not invoke this one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this test used to assert, and why that was backwards.</b> It required that
    /// <c>LiveNrRosterFixture</c> be the <em>only</em> file in <c>tests/Infrastructure/</c> containing
    /// <c>LoadTarget.ThirdPartyLive)</c>. The intent was sound — a LOCAL fixture that declares
    /// <c>ThirdPartyLive</c> would silently throttle itself to 2 and look, in the diff, like a safety
    /// improvement — but the rule it wrote down was "only one fixture may be bounded", and the four
    /// other fixtures that drive <c>newrecruit.eu</c> and <c>giloushaker.github.io</c> were therefore
    /// <em>forbidden</em> from coming under the limit. The gate was enforcing the gap.
    /// </para>
    /// <para>
    /// The real rule is a biconditional, and it is what this asserts now: a fixture opens live
    /// third-party sessions <b>if and only if</b> it draws them from the budget. "Opens live sessions"
    /// is detected the same way the CLI detects it — the fixture reads an endpoint URL variable
    /// (<c>NR_ENGINE_URL</c>, <c>NR_EDITOR_URL</c>), which is exactly what turns a frozen fixture into a
    /// live one.
    /// </para>
    /// <para>
    /// <b>Falsifiable in both directions</b> (both verified by mutation):
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Delete the <c>LiveLoadBudget.Reserve</c> call from any live fixture — the sessions it opens stop
    /// counting against the site's budget, which is precisely how 2 + 1 = 3 got onto newrecruit.eu — and
    /// this goes red naming that fixture.
    /// </description></item>
    /// <item><description>
    /// Reserve from the budget in a fixture that reads no endpoint variable (throttling a lane nobody
    /// else pays for — the mirror-image mistake the old test was guarding) and this goes red too.
    /// </description></item>
    /// <item><description>
    /// Change <c>LiveNrRosterFixture</c>'s pool argument to <c>LoadTarget.Local</c> — the change that
    /// "restores" it to the frozen lane's measured pool of 4, i.e. the exact regression of #314/edf3b4a
    /// — and the first assertion goes red.
    /// </description></item>
    /// </list>
    /// <para>
    /// This file is excluded from the scan: it necessarily contains every string it searches for.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryLiveFixture_DrawsItsSessionsFromTheThirdPartyLoadBudget()
    {
        var infrastructure = Path.Combine(RepoRoot, "tests", "Infrastructure");

        // The live NR conformance pool — the 363-spec lane that drives newrecruit.eu — must ask the
        // policy for the third-party load limit, by name, with the engine it shares with `nr-frozen`.
        Assert.Contains(
            "PoolSizeFor(\"newrecruit\", LoadTarget.ThirdPartyLive)",
            File.ReadAllText(Path.Combine(infrastructure, "LiveNrRosterFixture.cs")),
            StringComparison.Ordinal);

        // An endpoint URL variable is what makes a fixture live — the same fact the CLI derives its
        // LoadTarget from (EngineEndpoint.FromUrlVariable). A fixture that reads one opens sessions on
        // somebody else's server; a fixture that does not, cannot.
        string[] endpointVariables = ["NR_ENGINE_URL", "NR_EDITOR_URL"];

        var offenders = new List<string>();

        foreach (var file in Directory
            .EnumerateFiles(infrastructure, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals(
                $"{nameof(ConcurrencyConfigurationDriftTests)}.cs", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(RepoRoot, file);

            var drivesALiveSite = endpointVariables.Any(
                v => text.Contains($"GetEnvironmentVariable(\"{v}\")", StringComparison.Ordinal));
            var budgeted = text.Contains($"{nameof(LiveLoadBudget)}.{nameof(LiveLoadBudget.Reserve)}(", StringComparison.Ordinal);

            if (drivesALiveSite && !budgeted)
            {
                offenders.Add(
                    $"  {relative} opens sessions on a third party's live site (it reads an endpoint URL " +
                    $"variable) but never reserves them from {nameof(LiveLoadBudget)}.");
            }
            else if (budgeted && !drivesALiveSite)
            {
                offenders.Add(
                    $"  {relative} reserves from {nameof(LiveLoadBudget)} but drives no third-party site " +
                    $"(it reads no endpoint URL variable) — that throttles a lane nobody else pays for.");
            }
        }

        if (offenders.Count > 0)
        {
            Assert.Fail(
                $"Live-load budget and live fixtures disagree:\n{string.Join("\n", offenders)}\n\n" +
                $"ConcurrencyPolicy.ThirdPartyLiveLoadLimit calls itself \"the only thing standing between a " +
                $"363-spec conformance run and a volunteer-run website\". It can only be that if every fixture " +
                $"that opens a session there draws it from the one budget: `-p:TestProfile=nr-live` selects BOTH " +
                $"live NR roster collections, and the pool's 2 contexts plus the sequential fixture's 1 engine " +
                $"were 3 concurrent sessions on newrecruit.eu — over a limit its own docstring forbids raising " +
                $"by 1 for a measured speed-up. Reserve the sessions, or stop opening them.");
        }
    }

    /// <summary>
    /// <b>The engine that can go live must be the engine that says it can.</b> The CLI derives its
    /// <c>LoadTarget</c> from what an engine declares (<c>EngineEntry.RosterEndpoint</c> /
    /// <c>GameDataEndpoint</c>), and a declaration is only worth what it costs to falsify: if
    /// <c>HostEngineFactory</c> grew a live route the registry did not declare, the parent would plan
    /// <c>ceil(cpuCount × k)</c> browsers against it and nothing would notice. This ties the declaration
    /// to the code that acts on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Falsifiable, in each of the three ways this can rot:</b>
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Move the <c>NR_ENGINE_URL</c> read into <c>CreateGameDataEngineAsync</c> (or add any other
    /// <c>*_URL</c> endpoint to it) — the gamedata assertion goes red, because every built-in declares its
    /// gamedata endpoint as this machine.
    /// </description></item>
    /// <item><description>
    /// Add a live route to a <em>new</em> engine's roster branch without declaring it — the roster
    /// assertion goes red on the undeclared variable.
    /// </description></item>
    /// <item><description>
    /// Delete <c>RosterEndpoint: EngineEndpoint.FromUrlVariable("NR_ENGINE_URL")</c> from the NR engines
    /// while the factory still reads it — the roster assertion goes red on the orphaned read. (That
    /// deletion also fails safe rather than open — an undeclared engine is treated as live — so this
    /// gate is what stops it being a <em>silent</em> 2×-slower frozen lane.)
    /// </description></item>
    /// </list>
    /// <para>
    /// It matches the read shape <c>GetEnvironmentVariable("…_URL")</c>: an endpoint variable, not the
    /// artifact-path variables (<c>BS_UI_APP_DIR</c>, <c>BS_UI_AGENT_JAR</c>) that point at local files
    /// and cannot send traffic anywhere.
    /// </para>
    /// </remarks>
    [Fact]
    public void HostEngineFactory_LiveEndpointRoutes_AreDeclaredByTheRegistry()
    {
        var factory = Path.Combine(
            RepoRoot, "src", "BattleScribeSpec.EngineHost", "HostEngineFactory.cs");
        var source = File.ReadAllText(factory);

        // The file's two engine-construction methods, in source order: everything from the roster factory
        // up to the gamedata factory, and everything after it (the BS-UI path helpers live there and read
        // no *_URL variable).
        var gamedataStart = source.IndexOf("CreateGameDataEngineAsync", StringComparison.Ordinal);
        Assert.True(gamedataStart > 0, $"could not find CreateGameDataEngineAsync in {factory}");

        var rosterStart = source.IndexOf("CreateRosterEngineAsync", StringComparison.Ordinal);
        Assert.True(rosterStart > 0 && rosterStart < gamedataStart, $"unexpected method order in {factory}");

        var registry = EngineRegistry.Load(null);
        var builtins = registry.KnownNames
            .Select(name => registry.Resolve(EngineConnectable.Parse(name)))
            .ToArray();

        AssertEndpointReadsAreDeclared(
            "roster",
            source[rosterStart..gamedataStart],
            builtins.Select(e => e.EndpointFor("roster")));

        AssertEndpointReadsAreDeclared(
            "gamedata",
            source[gamedataStart..],
            builtins.Select(e => e.EndpointFor("gamedata")));
    }

    /// <summary>
    /// The set of endpoint URL variables <paramref name="factorySource"/> reads must equal the set the
    /// built-in engines <b>declare</b> for that domain. Not a subset either way: an undeclared read is a
    /// live route the policy cannot see, and an unread declaration is a throttle nobody is paying for.
    /// </summary>
    private static void AssertEndpointReadsAreDeclared(
        string domain, string factorySource, IEnumerable<EngineEndpoint> declared)
    {
        var read = Regex
            .Matches(factorySource, """GetEnvironmentVariable\("(\w*_URL)"\)""", RegexOptions.None)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var expected = declared
            .Where(e => e.Kind == EngineEndpointKind.UrlVariable)
            .Select(e => e.UrlVariable!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            expected.SequenceEqual(read, StringComparer.Ordinal),
            $"HostEngineFactory's {domain} engines read endpoint URL variables [{string.Join(", ", read)}], " +
            $"but the built-in registry declares [{string.Join(", ", expected)}] for the {domain} domain.\n\n" +
            $"These must be the same set. The CLI derives its LoadTarget from what the registry declares " +
            $"(EngineEntry.RosterEndpoint / GameDataEndpoint), so a live route the registry does not know " +
            $"about is a route the concurrency policy cannot bound: bs-spec run --all would plan " +
            $"ceil(cpuCount x k) adapter processes — each with its own browser — against a third party's " +
            $"production website, which is the regression #317 fixed. Declare the endpoint on the engine " +
            $"(EngineEndpoint.FromUrlVariable) or stop reading the variable.");
    }

    /// <summary>
    /// <b>Every test project must actually be RUN by CI.</b> A gate nobody invokes is a gate nobody has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This repo has two test projects and CI ran <em>one</em> of them: all fifteen <c>dotnet test</c>
    /// steps in <c>ci.yml</c> named <c>tests/BattleScribeSpec.Tests.csproj</c>, and there was no
    /// solution-wide sweep — so <c>tests/BattleScribeSpec.Cli.Tests</c> had <b>never executed in CI</b>.
    /// That is where every gate on the CLI's load target lives (the third-party limit, the fail-safe for
    /// undeclared adapters, the <c>--policy</c> rejections), and where this branch's regression test for
    /// the case-sensitivity defect lives. They all passed locally and CI had never seen one of them.
    /// </para>
    /// <para>
    /// <b>Falsifiable:</b> delete the CLI step from <c>ci.yml</c> (or add a third test project without a
    /// step for it) and this goes red, naming the project. It scans the <c>dotnet test</c> COMMAND LINES
    /// only — not the file text — so it does not care which job runs the project, with what filter, or in
    /// what order, and (verified by mutation) a passing <em>mention</em> of the project in a comment
    /// cannot satisfy it. The first draft of this test scanned the whole file and was defeated by the
    /// comment three lines above the step it was guarding.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryTestProject_IsRunBySomeCiStep()
    {
        var workflows = Path.Combine(RepoRoot, ".github", "workflows");
        var invocations = Directory
            .EnumerateFiles(workflows, "*.yml", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .SelectMany(File.ReadAllLines)
            .Where(line => line.Contains("dotnet test", StringComparison.Ordinal)
                && !line.TrimStart().StartsWith('#'))
            .ToArray();

        var testProjects = Directory
            .EnumerateFiles(Path.Combine(RepoRoot, "tests"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(testProjects);

        // A solution-wide `dotnet test BattleScribeSpec.slnx` would cover every project at once; today
        // every step names one project explicitly, which is what makes an unnamed project invisible.
        var sweepsTheSolution = invocations.Any(line => line.Contains(".slnx", StringComparison.Ordinal));

        // Workflows are authored with forward slashes whatever the developer's OS.
        var unrun = testProjects
            .Select(p => Path.GetRelativePath(RepoRoot, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(rel => !sweepsTheSolution
                && !invocations.Any(line => line.Contains(rel, StringComparison.Ordinal)))
            .ToArray();

        Assert.True(
            unrun.Length == 0,
            $"These test projects are never run by any CI step:\n{string.Join("\n", unrun.Select(p => "  " + p))}\n\n" +
            "Every `dotnet test` invocation in .github/workflows names a project explicitly — there is no " +
            "solution-wide sweep — so a project with no step of its own is a suite that passes on the " +
            "author's machine and has never once been executed by CI. That is how tests/BattleScribeSpec.Cli.Tests " +
            "came to hold every gate on the CLI's third-party load limit while CI ran none of them. Add a " +
            "step, or delete the project.");
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
