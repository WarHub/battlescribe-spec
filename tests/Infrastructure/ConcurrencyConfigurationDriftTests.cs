using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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

    /// <summary>The repo root, for the source-scanning gates (this class's, and LiveLoadBudgetTests').</summary>
    internal static readonly string RepoRoot = FindRepoRoot();

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

            // ...and reserving is only half of it: the permit must come BACK when the session fails to
            // open. Constructing the engine is exactly the step that throws when the site is down, and a
            // fixture that kept the permit turned that outage into a SKIP that blamed the load budget —
            // after two of them, every later test in the process was granted 0. LiveLoadLease.Open /
            // OpenAsync is the one exception-safe path; a hand-rolled try/catch in a sixth fixture is the
            // one the sixth author forgets.
            if (budgeted
                && !text.Contains($".{nameof(LiveLoadLease.Open)}(", StringComparison.Ordinal)
                && !text.Contains($".{nameof(LiveLoadLease.OpenAsync)}(", StringComparison.Ordinal))
            {
                offenders.Add(
                    $"  {relative} reserves from {nameof(LiveLoadBudget)} but does not open its session " +
                    $"through {nameof(LiveLoadLease)}.{nameof(LiveLoadLease.Open)}/" +
                    $"{nameof(LiveLoadLease.OpenAsync)} — if the engine fails to construct, the permit " +
                    $"leaks and a site outage becomes a skip that blames the budget.");
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
    /// This repo has two test projects and CI ran <em>one</em> of them: all fifteen test
    /// steps in <c>ci.yml</c> named <c>tests/BattleScribeSpec.Tests.csproj</c>, and there was no
    /// solution-wide sweep — so <c>tests/BattleScribeSpec.Cli.Tests</c> had <b>never executed in CI</b>.
    /// That is where every gate on the CLI's load target lives (the third-party limit, the fail-safe for
    /// undeclared adapters, the <c>--policy</c> rejections), and where this branch's regression test for
    /// the case-sensitivity defect lives. They all passed locally and CI had never seen one of them.
    /// </para>
    /// <para>
    /// <b>Falsifiable:</b> delete the CLI step from <c>ci.yml</c> (or add a third test project without a
    /// step for it) and this goes red, naming the project. It scans the test-step COMMAND LINES
    /// only — not the file text — so it does not care which job runs the project, with what filter, or in
    /// what order, and (verified by mutation) a passing <em>mention</em> of the project in a comment
    /// cannot satisfy it. The first draft of this test scanned the whole file and was defeated by the
    /// comment three lines above the step it was guarding.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryTestProject_IsRunBySomeCiStep()
    {
        // Test steps run through the guard wrapper, not `dotnet test` directly — see
        // EveryCiTestStep_ExecutesAtLeastOneTest, which is what enforces that.
        var invocations = WorkflowCommandLines()
            .Where(line => line.Contains(TestStepScript, StringComparison.Ordinal)
                && !line.TrimStart().StartsWith('#'))
            .ToArray();

        Assert.NotEmpty(invocations);

        var testProjects = Directory
            .EnumerateFiles(Path.Combine(RepoRoot, "tests"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(testProjects);

        // A solution-wide sweep (`… BattleScribeSpec.slnx`) would cover every project at once; today
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
            "Every test-step invocation in .github/workflows names a project explicitly — there is no " +
            "solution-wide sweep — so a project with no step of its own is a suite that passes on the " +
            "author's machine and has never once been executed by CI. That is how tests/BattleScribeSpec.Cli.Tests " +
            "came to hold every gate on the CLI's third-party load limit while CI ran none of them. Add a " +
            "step, or delete the project.");
    }

    /// <summary>
    /// The wrapper every CI test step must go through. It fails the step when the step EXECUTED no
    /// tests — see its own header for the two ways that happens and why neither is detectable from
    /// a `dotnet test` exit code.
    /// </summary>
    private const string TestStepScript = "scripts/dotnet-test-step.ps1";

    /// <summary>
    /// <b>A CI test step that EXECUTED NO TESTS must fail, not pass.</b> Every test step in
    /// <c>.github/workflows</c> must run through <see cref="TestStepScript"/>; bare <c>dotnet test</c>
    /// is forbidden there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The invariant is <b>passed + failed ≥ 1</b>, not "the filter matched something" — because the
    /// two real defects this replaces failed in different ways and only one of them was empty:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>Engine=FrozenNrUiRoster&amp;DisplayName~kitchen-sink</c> matched <b>zero</b> tests. That
    /// class is a single <c>[Fact] AllSpecs()</c>, so no test's display name carries a spec id and a
    /// DisplayName clause can never match. VSTest printed "No test matches the given testcase
    /// filter", exited 0, green.
    /// </description></item>
    /// <item><description>
    /// <c>Engine=FrozenNrRoster&amp;DisplayName~kitchen-sink</c> matched <b>exactly one</b> — the
    /// <c>Mode=Sequential</c> variant of the class, gated behind <c>NR_SEQUENTIAL</c>, which
    /// self-skips in CI. Measured: <c>Skipped! - Failed: 0, Passed: 0, Skipped: 1, Total: 1</c>,
    /// exit 0, green. <b>A non-empty-selection check does not catch this one</b>, which is why the
    /// guard counts executions rather than matches; it is also the more insidious of the two,
    /// because a non-zero test count looks like a real run.
    /// </description></item>
    /// </list>
    /// <para>
    /// Between them, both frozen NR roster suites had zero per-PR coverage — which is how a HAR bump
    /// merged green and then broke two suites the smoke job claimed to guard.
    /// </para>
    /// <para>
    /// The guard is a per-invocation wrapper rather than a runsettings setting <b>on purpose</b>: a
    /// runsettings would also bind <c>dotnet test -p:TestProfile=&lt;x&gt;</c> run against the
    /// <em>solution</em> — the form AGENTS.md documents — where the profile's engine filter genuinely
    /// matches nothing in <c>BattleScribeSpec.Cli.Tests</c> (measured: <c>-p:TestProfile=lint</c> at
    /// solution level would start failing). The gate belongs where a silent zero is a lie (CI), not
    /// where it is expected (a developer's solution-wide profile run).
    /// </para>
    /// <para>
    /// <b>Falsifiable:</b> change any step back to a bare <c>dotnet test</c> (or add a new one) and
    /// this goes red naming the line. Like <see cref="EveryTestProject_IsRunBySomeCiStep"/> it scans
    /// COMMAND LINES only, so the prose above those steps — which necessarily says "dotnet test" —
    /// cannot satisfy or trip it.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryCiTestStep_ExecutesAtLeastOneTest()
    {
        var unguarded = WorkflowCommandLines()
            .Where(line => line.Contains("dotnet test", StringComparison.Ordinal)
                && !line.TrimStart().StartsWith('#')
                && !line.Contains(TestStepScript, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            unguarded.Length == 0,
            $"These CI steps invoke `dotnet test` directly instead of through {TestStepScript}:\n" +
            string.Join("\n", unguarded.Select(l => "  " + l.Trim())) + "\n\n" +
            "A bare `dotnet test` step exits 0 when its filter selected NOTHING, and exits 0 when the " +
            "only test it selected SKIPPED — both indistinguishable from a step whose tests ran and " +
            "passed. That is how `Engine=FrozenNrUiRoster&DisplayName~kitchen-sink` (0 matched) and " +
            "`Engine=FrozenNrRoster&DisplayName~kitchen-sink` (1 matched, self-skipping) gated every " +
            $"PR while executing zero tests between them. Route the step through {TestStepScript}, " +
            "which reads the TRX counters and fails when passed + failed == 0.");
    }

    /// <summary>
    /// The CI step named "Full frozen NR UI roster" must actually run the full spec set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That lane ran <b>one</b> spec for its entire life — `TargetSpecs = ["protocol/protocol-kitchen-sink"]`
    /// — on the since-falsified premise that "the frozen HAR supports a single roster-creation flow
    /// per run". <c>docs/warm-reuse.md</c> records what that cost: "CI never caught the original bug
    /// because the NR-UI roster lane runs a single spec." It is now the whole applicable suite, but
    /// only when <c>NR_UI_ROSTER_FULL</c> is set, because the every-push lane and <c>pre-push</c>
    /// must stay fast.
    /// </para>
    /// <para>
    /// An opt-in that gets dropped is invisible: the step still passes, still says "Full", and
    /// quietly covers one spec instead of ~49. <c>dotnet-test-step.ps1</c> cannot catch it either —
    /// one executed test is not zero executed tests. Hence this.
    /// </para>
    /// <para>
    /// <b>Falsifiable:</b> delete the <c>NR_UI_ROSTER_FULL</c> env entry from that step and this goes
    /// red.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Lint")]
    public void ThoroughNrUiRosterStep_RunsTheFullSpecSet()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot, ".github", "workflows", "ci.yml"));
        var stepIndex = Array.FindIndex(lines, l => l.Contains("name: Full frozen NR UI roster", StringComparison.Ordinal));

        Assert.True(stepIndex >= 0,
            "ci.yml no longer has a step named 'Full frozen NR UI roster'. If it was renamed, update "
            + "this guard; if it was deleted, the thorough NR-UI roster coverage went with it.");

        // Scan this step only — up to the next step's `- name:`.
        var end = Array.FindIndex(lines, stepIndex + 1, l => l.TrimStart().StartsWith("- name:", StringComparison.Ordinal));
        var body = string.Join("\n", lines[stepIndex..(end < 0 ? lines.Length : end)]);

        Assert.Contains("NR_UI_ROSTER_FULL", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where each UI driver dumps its post-mortem, and the CI job that runs that driver and must
    /// therefore upload it.
    /// </summary>
    /// <remarks>
    /// Paths as the drivers resolve them (repo-root <c>artifacts/</c>, anchored there for the test
    /// host by <see cref="TestPaths.AnchorDiagnosticsAtRepoRoot"/>); the workflow may match them with
    /// a trailing wildcard for the drivers' per-worker suffixes.
    /// </remarks>
    private static readonly (string Job, string Directory)[] UiDiagnosticsUploads =
    [
        ("thorough-conformance", "artifacts/nr-ui-diagnostics"),
        ("thorough-conformance", "artifacts/nr-gamedata-ui-diagnostics"),
        ("thorough-ui-bs", "artifacts/bs-ui-diagnostics"),
        ("thorough-ui-bs", "artifacts/bs-gamedata-ui-diagnostics"),
    ];

    /// <summary>
    /// <b>A UI lane's failure diagnostics must leave the runner.</b> Every directory in
    /// <see cref="UiDiagnosticsUploads"/> must be named inside its job in <c>ci.yml</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The NR UI drivers capture a screenshot, DOM snapshot, Pinia dump and console log for every
    /// failed action, and <c>thorough-conformance</c> — the only job that runs either of them over
    /// the full spec set — uploaded none of it. What reached a reader was the exception text, and
    /// for a Playwright timeout that is <c>Timeout 20000ms exceeded.</c> and nothing else. The
    /// artifacts existed, on a machine that was about to be deleted.
    /// </para>
    /// <para>
    /// <c>thorough-ui-bs</c> is in the table because it already does this correctly and is the
    /// reason the gap was visible at all: two jobs writing dumps, one uploading them.
    /// </para>
    /// <para>
    /// <b>Falsifiable:</b> delete either path from the "Upload NR UI diagnostics" step and this goes
    /// red naming it. It matches within the job block, so an upload wired to the wrong job does not
    /// satisfy it.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Lint")]
    public void EveryUiLane_UploadsTheDiagnosticsItWrites()
    {
        var ci = File.ReadAllLines(Path.Combine(RepoRoot, ".github", "workflows", "ci.yml"));

        var missing = UiDiagnosticsUploads
            .Where(u => !JobBlock(ci, u.Job).Contains(u.Directory, StringComparison.Ordinal))
            .Select(u => $"  {u.Job} does not upload {u.Directory}")
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "These CI jobs run a UI driver whose diagnostics they never upload:\n"
            + string.Join("\n", missing) + "\n\n"
            + "A driver that dumps a screenshot, DOM and store state into a runner nobody collects "
            + "from has diagnosed nothing: the reader gets the exception text, which for a Playwright "
            + "timeout is seven words. Add an `actions/upload-artifact` step for the directory, or — "
            + "if the job genuinely no longer runs that driver — delete its row from "
            + nameof(UiDiagnosticsUploads) + ".");
    }

    /// <summary>
    /// The lines of one top-level job in a workflow file: from <c>  &lt;job&gt;:</c> to the next
    /// two-space-indented key, so a match cannot come from a neighbouring job.
    /// </summary>
    private static string JobBlock(string[] lines, string job)
    {
        var start = Array.FindIndex(lines, l => l.StartsWith($"  {job}:", StringComparison.Ordinal));
        Assert.True(start >= 0, $"ci.yml has no job named '{job}'. If it was renamed, update the table.");

        var end = Array.FindIndex(lines, start + 1, l =>
            l.Length > 2 && l[0] == ' ' && l[1] == ' ' && l[2] != ' ' && l.TrimEnd().EndsWith(':'));

        return string.Join("\n", lines[start..(end < 0 ? lines.Length : end)]);
    }

    /// <summary>The profile AGENTS.md tells every contributor to run before pushing.</summary>
    private const string PrePushProfile = "pre-push";

    /// <summary>
    /// <b>What <c>pre-push</c> does with every engine lane, and why.</b> One row per <c>Engine</c>
    /// trait value in the suite; <see cref="EveryEngineLane_IsADeliberateDecisionInThePrePushProfile"/>
    /// requires this table and the profile's own filter to say the same thing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The costs are MEASURED on this repo's 32-core dev box, 2026-08-12, from the TRX of one
    /// <c>pre-push</c> run each side of the change. Per lane they are <b>summed test time</b>, which
    /// is unambiguous; the lanes overlap heavily, so they add to more than the run.
    /// </para>
    /// <para>
    /// <b>They refute the rule everybody reaches for first.</b> "Keep UI lanes out of a fast gate"
    /// would also have excluded the two frozen Playwright lanes — 22.6s and 51.8s — from a run whose
    /// critical path is <c>BsRoster</c>: an in-process engine, no UI at all, 266.8s across 367 specs
    /// and effectively the whole 267.9s wall clock. Excluding them would have bought nothing
    /// measurable and cost the NR Editor UI driver its only local signal. What was expensive was
    /// never "a UI"; it was a desktop application. <c>BsRosterUi</c> spanned <b>688.8s of the 689.2s
    /// run it was in</b> — it WAS the run.
    /// </para>
    /// </remarks>
    private static readonly (string Engine, bool RunsInPrePush, string Why)[] PrePushEngineLaneDecisions =
    [
        // ── Runs. Offline, no app, and cheap against a 267.9s profile.
        ("BsRoster", true, "in-process IKVM reference engine; 266.8s — the critical path, and not a UI"),
        ("BsGameData", true, "in-process IKVM reference engine; 0.8s"),
        ("FrozenNrRoster", true, "offline HAR replay, no network; 70.3s"),
        ("FrozenNrGameData", true, "offline static-file serving, no network; 133.9s"),
        ("FrozenNrUiRoster", true, "Playwright over the frozen HAR, kitchen-sink only; 22.6s"),
        ("FrozenNrGameDataUi", true, "Playwright over the frozen NR Editor snapshot; 51.8s"),

        // ── Excluded: needs the BattleScribe desktop app (setup.ps1 artifacts + Java agent + a
        //    display). CI's `thorough-ui-bs` job runs both halves, sharded.
        ("BsRosterUi", false, "launches the BattleScribe desktop app; 687.8s, 367 specs, sequential"),
        ("BsGameDataUi", false, "launches the BattleScribe desktop app"),

        // ── Excluded: opens sessions on somebody else's production website. A pre-push gate is run
        //    on every push by every contributor; that is the last traffic profile these sites should
        //    see. See EveryLiveFixture_DrawsItsSessionsFromTheThirdPartyLoadBudget.
        ("LiveNrRoster", false, "opens sessions on newrecruit.eu"),
        ("LiveNrUiRoster", false, "opens sessions on newrecruit.eu"),
        ("LiveNrGameData", false, "opens sessions on the NR Editor deployment"),
        ("LiveNrGameDataUi", false, "opens sessions on the NR Editor deployment"),
    ];

    /// <summary>
    /// <b>An engine lane joins <c>pre-push</c> by DEFAULT, which is how the gate came to cost 2.6x
    /// what it needed to, running a desktop application nobody chose to put there.</b> Every
    /// <c>Engine</c> trait value in the suite must appear in
    /// <see cref="PrePushEngineLaneDecisions"/>, and the profile's filter must agree with it — so the
    /// next lane cannot arrive without somebody deciding, in writing, whether it belongs there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> <c>pre-push.runsettings</c> excluded <c>BsGameDataUi</c> and said, in its own
    /// comment, that it ran "all tests except those requiring live NR, sequential mode, or the
    /// BattleScribe desktop Data Editor". Then <c>BsRosterUi</c> was added (#353) — the OTHER half of
    /// the same desktop app — and no exclusion followed it, because nothing required one to. The
    /// filter is a deny-list, so the new lane was opted in by silence: measured
    /// <c>Failed: 1, Passed: 2936, Skipped: 368, Total: 3305, Duration: 11 m 29 s</c> for a profile
    /// AGENTS.md advertised at <c>~40s</c>. <c>LiveNrUiRoster</c> had drifted in the same way and was
    /// only invisible because it self-skips without <c>NR_ENGINE_URL</c> — 368 tests that would have
    /// opened sessions on newrecruit.eu from a pre-push hook the moment that variable was set.
    /// </para>
    /// <para>
    /// <b>Why a table and not a rule.</b> "Exclude anything with Ui in the name" would have caught
    /// <c>BsRosterUi</c> and been wrong about the two frozen Playwright lanes, which are UI drivers
    /// too and are not the cost — see the measurements on the table. No predicate over the name
    /// separates them; only a measurement does, and a measurement is a decision somebody made. So
    /// the table records the decision, and this test records that one was made at all.
    /// </para>
    /// <para>
    /// <b>Falsifiable, in each of the three ways this can rot</b> (all verified by mutation):
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Add a <c>[Trait("Engine", "…")]</c> lane to the suite without a row here — the arriving-lane
    /// case, which is #405 itself — and this goes red naming the new value.
    /// </description></item>
    /// <item><description>
    /// Delete <c>Engine!=BsRosterUi</c> from the filter while the row still says excluded — the exact
    /// state <c>origin/main</c> was in — and this goes red naming the lane and the profile.
    /// </description></item>
    /// <item><description>
    /// Add an <c>Engine!=</c> clause for a lane the table says runs (silently narrowing the gate, the
    /// mirror-image mistake) and this goes red too.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>This file is excluded from the scan, and the first run is why.</b> The draft was not
    /// excluded, on the reasoning that the table is tuple syntax and the scan matches attribute
    /// syntax — true of the table, and false of the prose, which spelled the attribute out as an
    /// example and thereby invented a thirteenth lane. The gate went red naming it. That is the
    /// mistake <see cref="EveryTestProject_IsRunBySomeCiStep"/> records having made once already
    /// ("defeated by the comment three lines above the step it was guarding"), so it is now
    /// prevented the same way the sibling gates prevent it: a file that documents what it searches
    /// for cannot also be searched. Nothing is lost — no engine lane lives here.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Lint")]
    public void EveryEngineLane_IsADeliberateDecisionInThePrePushProfile()
    {
        var declared = PrePushEngineLaneDecisions
            .Select(d => d.Engine)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            declared.Count == PrePushEngineLaneDecisions.Length,
            $"{nameof(PrePushEngineLaneDecisions)} lists an engine twice — two rows can say opposite " +
            "things and only one of them can be true.");

        // This file is skipped: it spells the attribute out in prose to explain itself, and that
        // example is not a lane. See the remarks — the first draft learned this the hard way.
        var thisFile = $"{nameof(ConcurrencyConfigurationDriftTests)}.cs";

        var discovered = Directory
            .EnumerateFiles(Path.Combine(RepoRoot, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !Path.GetFileName(p).Equals(thisFile, StringComparison.Ordinal))
            .SelectMany(p => Regex.Matches(File.ReadAllText(p), """\[Trait\("Engine",\s*"(\w+)"\)\]"""))
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(discovered);

        var undecided = discovered.Except(declared).Order(StringComparer.Ordinal).ToArray();
        var phantom = declared.Except(discovered).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            undecided.Length == 0 && phantom.Length == 0,
            $"{nameof(PrePushEngineLaneDecisions)} and the suite's Engine traits disagree.\n" +
            (undecided.Length > 0
                ? $"  Lanes with no decision recorded: {string.Join(", ", undecided)}\n"
                : string.Empty) +
            (phantom.Length > 0
                ? $"  Decisions for lanes that no longer exist: {string.Join(", ", phantom)}\n"
                : string.Empty) +
            $"\n{PrePushProfile} is a DENY-list: a lane with no row here is a lane that joined the gate " +
            "every contributor is told to run before every push, without anyone choosing that. It is how " +
            "BsRosterUi came to spend 688.8s of a 689.2s run launching the BattleScribe desktop app in a " +
            "profile documented as offline and fast. Add a row saying whether the new lane runs there, " +
            "and put the cost that justifies the answer in it — measured, not guessed.");

        var profilePath = Path.Combine(RepoRoot, "tests", "test-profiles", $"{PrePushProfile}.runsettings");
        var filter = XDocument.Load(profilePath).Descendants("TestCaseFilter").Single().Value;

        var excludedByFilter = Regex
            .Matches(filter, """Engine!=(\w+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var excludedByTable = PrePushEngineLaneDecisions
            .Where(d => !d.RunsInPrePush)
            .Select(d => d.Engine)
            .ToHashSet(StringComparer.Ordinal);

        var runsButShouldNot = excludedByTable
            .Except(excludedByFilter)
            .Order(StringComparer.Ordinal)
            .Select(e => $"  {e} — the table says it must not run in {PrePushProfile} " +
                $"({PrePushEngineLaneDecisions.First(d => d.Engine == e).Why}), but the filter has no " +
                $"Engine!={e} clause, so it does.")
            .ToArray();

        var excludedButShould = excludedByFilter
            .Except(excludedByTable)
            .Order(StringComparer.Ordinal)
            .Select(e => $"  {e} — the filter excludes it from {PrePushProfile}, but the table does not " +
                "say it should be excluded. Either it is not a real Engine trait value, or the gate was " +
                "narrowed without recording why.")
            .ToArray();

        Assert.True(
            runsButShouldNot.Length == 0 && excludedButShould.Length == 0,
            $"{PrePushProfile}.runsettings and {nameof(PrePushEngineLaneDecisions)} disagree:\n" +
            string.Join("\n", runsButShouldNot.Concat(excludedButShould)) + "\n\n" +
            "The filter is the thing that actually runs; this table is the thing that explains it. When " +
            "they differ, the profile is doing something nobody wrote down — which is the whole of #405.");
    }

    /// <summary>
    /// Every line of every workflow file, with backslash-continued shell lines joined into the single
    /// command line they actually form — so a multi-line <c>run:</c> block is scanned as one
    /// invocation rather than as fragments that individually look flagless.
    /// </summary>
    private static List<string> WorkflowCommandLines()
    {
        var workflows = Path.Combine(RepoRoot, ".github", "workflows");
        var lines = new List<string>();

        foreach (var file in Directory
            .EnumerateFiles(workflows, "*.yml", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal))
        {
            var pending = new StringBuilder();
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.TrimEnd();
                if (line.EndsWith('\\'))
                {
                    pending.Append(line, 0, line.Length - 1).Append(' ');
                    continue;
                }

                lines.Add(pending.Append(line).ToString());
                pending.Clear();
            }

            if (pending.Length > 0)
            {
                lines.Add(pending.ToString());
            }
        }

        return lines;
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
