using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BattleScribeSpec.Engines;
using BattleScribeSpec.Tests.Profiles;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Pins <c>maxParallelThreads</c> in both <c>xunit.runner.json</c> files to one declared value, so
/// the two cannot drift apart or be changed without meeting the reasoning below — and, with
/// <see cref="XunitParallelism_IsDeclaredOnce_OnItsCurrentKeys"/>, keeps xUnit's parallelism
/// declared in those two files alone, on the keys xunit.v3 reads.
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
    /// The declared xUnit scheduling mode: test collections run in parallel with each other, the tests
    /// inside one collection one at a time — the mode every collection fixture here is written for.
    /// </summary>
    internal const string XunitParallelMode = "collections";

    /// <summary>
    /// xUnit's parallelism is declared in exactly one place per test assembly — its
    /// <c>xunit.runner.json</c> — on the keys xunit.v3 4 reads, with values it accepts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the value is pinned, not just present.</b> xunit discards a <c>parallelMode</c> it does
    /// not recognise — <c>"off"</c>, <c>"true"</c>, a bare boolean — without a word, and runs the
    /// default instead. The accepted values are <c>none</c>, <c>collections</c> and <c>all</c>; a typo
    /// is therefore a silent change of scheduling, and only an exact pin can see it.
    /// </para>
    /// <para>
    /// <b>Why one place.</b> Each further record of the same setting is a second answer, and which one
    /// wins is a precedence rule nobody here wrote:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>parallelizeTestCollections</c> is the obsolete spelling of <c>parallelMode</c>. xunit 4
    ///   still reads it and will stop in its next major version, at which point a file that relies on
    ///   it changes behaviour on a package bump.</item>
    ///   <item>Under VSTest a runsettings file's <c>xUnit</c> section (and
    ///   <c>RunConfiguration/DisableParallelization</c>) overrides the JSON for whoever passes that
    ///   profile — one lane would schedule differently from the others, and nothing would show it. The
    ///   same elements passed inline (<c>dotnet test -- xUnit.MaxParallelThreads=…</c>, which
    ///   <c>ConcurrencyPolicy</c>'s remarks record as honoured here) do it for one invocation.</item>
    ///   <item>An assembly-level <c>CollectionBehavior</c> or <c>Parallelization</c> attribute is a
    ///   third source, in code.</item>
    /// </list>
    /// <para>
    /// Mutation-checked when written: <c>"parallelMode": "off"</c>, the obsolete key added back next
    /// to the new one, a <c>&lt;MaxParallelThreads&gt;</c> in a runsettings file,
    /// <c>xUnit.MaxParallelThreads=2</c> passed inline by the test step script, and an assembly
    /// attribute in a test file — <c>[assembly: Xunit.v3.Parallelization(...)]</c>,
    /// <c>[assembly: CollectionBehavior(...)]</c>, <c>global::…ParallelizationAttribute</c>, one placed
    /// second in its attribute list, and one applied through a <c>using</c> alias (which only the
    /// reflection check sees) — each turn this red.
    /// </para>
    /// </remarks>
    [Fact]
    public void XunitParallelism_IsDeclaredOnce_OnItsCurrentKeys()
    {
        var violations = new List<string>();

        foreach (var filePath in new[]
        {
            Path.Combine(RepoRoot, "tests", "xunit.runner.json"),
            Path.Combine(RepoRoot, "tests", "BattleScribeSpec.Cli.Tests", "xunit.runner.json"),
        })
        {
            var relPath = Path.GetRelativePath(RepoRoot, filePath);
            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            if (!doc.RootElement.TryGetProperty("parallelMode", out var mode))
            {
                violations.Add($"  {relPath}: no parallelMode — expected \"{XunitParallelMode}\"");
            }
            else if (mode.ValueKind != JsonValueKind.String || mode.GetString() != XunitParallelMode)
            {
                violations.Add(
                    $"  {relPath}: parallelMode is {mode.ValueKind} {mode} — expected the string \"{XunitParallelMode}\" " +
                    "(xunit silently ignores a value it does not know; accepted: none, collections, all)");
            }

            if (doc.RootElement.TryGetProperty("parallelizeTestCollections", out var obsolete))
            {
                violations.Add(
                    $"  {relPath}: parallelizeTestCollections = {obsolete} — the obsolete spelling of parallelMode; " +
                    "say it once, on the current key");
            }
        }

        string[] runsettingsParallelism =
            ["MaxParallelThreads", "ParallelizeTestCollections", "ParallelMode", "ParallelAlgorithm", "DisableParallelization"];
        var runsettings = Directory.GetFiles(Path.Combine(RepoRoot, "tests", "test-profiles"), "*.runsettings");
        Assert.NotEmpty(runsettings);
        foreach (var file in runsettings)
        {
            foreach (var element in XDocument.Load(file).Descendants().Where(e => runsettingsParallelism.Contains(e.Name.LocalName)))
            {
                violations.Add(
                    $"  {Path.GetRelativePath(RepoRoot, file)}: <{element.Name.LocalName}> — overrides xunit.runner.json " +
                    "for this profile only");
            }
        }

        // The same elements passed inline — `dotnet test -- xUnit.MaxParallelThreads=2`, or
        // RunConfiguration.DisableParallelization=true — override the JSON for that one invocation.
        var inline = new Regex(
            $@"\b(?:xUnit|RunConfiguration)\.(?:{string.Join("|", runsettingsParallelism)})\s*=", RegexOptions.IgnoreCase);
        foreach (var file in TestInvocationFiles(RepoRoot))
        {
            if (inline.Match(File.ReadAllText(file)) is { Success: true } match)
            {
                violations.Add(
                    $"  {Path.GetRelativePath(RepoRoot, file)}: passes {match.Value.TrimEnd('=', ' ')} inline — overrides " +
                    "xunit.runner.json for that invocation only");
            }
        }

        // In code: an assembly attribute in any position of its list, with or without the namespace,
        // `global::` or the `Attribute` suffix. The source scan is what sees Cli.Tests; this assembly's
        // own attributes are also read back by reflection, which no alias or custom attribute escapes.
        var assemblyAttribute = new Regex(
            @"(?m)^\s*\[\s*assembly\s*:(?:[^\]]*,)?\s*(?:global::)?(?:Xunit\.)?(?:v3\.)?(?:CollectionBehavior|Parallelization)(?:Attribute)?\b");
        var sources = Directory.EnumerateFiles(Path.Combine(RepoRoot, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "obj" or "bin"))
            .ToList();
        Assert.NotEmpty(sources);
        foreach (var file in sources)
        {
            if (assemblyAttribute.IsMatch(File.ReadAllText(file)))
            {
                violations.Add($"  {Path.GetRelativePath(RepoRoot, file)}: an assembly-level parallelism attribute");
            }
        }

        foreach (var attribute in typeof(ConcurrencyConfigurationDriftTests).Assembly.GetCustomAttributes(inherit: false)
            .Where(a => a is Xunit.v3.ICollectionBehaviorAttribute or Xunit.v3.IParallelizationAttribute))
        {
            violations.Add(
                $"  {typeof(ConcurrencyConfigurationDriftTests).Assembly.GetName().Name}: carries [assembly: {attribute.GetType().FullName}]");
        }

        Assert.True(violations.Count == 0,
            "xUnit's parallelism must be declared once per test assembly, in its xunit.runner.json, as " +
            $"\"parallelMode\": \"{XunitParallelMode}\" and \"maxParallelThreads\": \"{XunitMaxParallelThreads}\":\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// The files that invoke the test runner or feed it settings — CI workflows and actions, scripts,
    /// tools, the docker files, and the repo's and test projects' MSBuild and root files — scanned for
    /// xunit settings passed inline, which override the JSON for one invocation.
    /// </summary>
    /// <remarks>
    /// Refuses to come back without a file that runs <c>dotnet test</c>, so a scan that stopped
    /// reaching the invocations fails here instead of passing on nothing.
    /// </remarks>
    internal static IReadOnlyList<string> TestInvocationFiles(string repoRoot)
    {
        static bool Generated(string path) =>
            path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "bin" or "obj" or "node_modules");

        string[] rootExtensions = [".ps1", ".sh", ".props", ".targets", ".json"];
        string[] msbuildExtensions = [".csproj", ".props", ".targets"];
        string[] directories = [".github", "scripts", "tools", "docker"];

        var files = directories
            .Select(d => Path.Combine(repoRoot, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(repoRoot).Where(f => rootExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
            .Concat(Directory.EnumerateFiles(Path.Combine(repoRoot, "tests"), "*", SearchOption.AllDirectories)
                .Where(f => msbuildExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
            .Where(f => !Generated(Path.GetRelativePath(repoRoot, f)))
            .ToList();

        Assert.True(files.Any(f => File.ReadAllText(f).Contains("dotnet test", StringComparison.Ordinal)),
            $"None of the {files.Count} files scanned for inline xunit settings under '{repoRoot}' runs `dotnet test`, so " +
            "the scan no longer reaches the places tests are invoked from and would pass on nothing.");
        return files;
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
    /// step for it) and this goes red, naming the project. It reads the test steps
    /// <see cref="CiTestInvocations"/> finds in the parsed workflows — by the project each one runs, in
    /// whatever spelling — so it does not care which job runs the project, with what filter, or in what
    /// order, and a <em>mention</em> of the project in a YAML comment is not a step at all. The first
    /// draft of this test scanned the whole file and was defeated by the comment three lines above the
    /// step it was guarding; the line scan that followed was blind to every test run not spelled with
    /// the wrapper script.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryTestProject_IsRunBySomeCiStep()
    {
        var invocations = CiTestInvocations.ClassifiedSteps()
            .Where(static c => c.Invocation.Kind == CiStepKind.TestRun)
            .Select(static c => c.Invocation)
            .ToArray();

        Assert.NotEmpty(invocations);

        // Every xunit project in BattleScribeSpec.slnx — the same list the classifier recognises.
        var testProjects = CiTestInvocations.TestProjects.Select(static p => p.RelativePath).Order(StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(testProjects);

        // A solution-wide sweep (a bare `dotnet test`, or `… BattleScribeSpec.slnx`) would cover every
        // project at once; today every step names one project explicitly, which is what makes an unnamed
        // project invisible.
        var sweepsTheSolution = invocations.Any(static i => i.TargetsSolution);

        var unrun = testProjects
            .Where(rel => !sweepsTheSolution && !invocations.Any(i => i.Projects.Contains(rel)))
            .ToArray();

        Assert.True(
            unrun.Length == 0,
            $"These test projects are never run by any CI step:\n{string.Join("\n", unrun.Select(p => "  " + p))}\n\n" +
            "Every test step in .github/workflows names a project explicitly — there is no " +
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
    private const string TestStepScript = CiTestInvocations.TestStepScript;

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
    /// this goes red naming the step — and so does any other way of running a test project: the test
    /// exe or dll by path, <c>dotnet run --project tests/…</c>, reordered flags, or a repo script that
    /// does any of those. Steps are found by <see cref="CiTestInvocations"/>, which identifies a test
    /// run by the project it runs (verified by mutation: a reordered bare <c>dotnet test</c> line and a
    /// <c>dotnet artifacts/bin/BattleScribeSpec.Tests/…/BattleScribeSpec.Tests.dll</c> line each went
    /// red). The YAML comments above those steps — which necessarily say "dotnet test" — are not steps.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryCiTestStep_ExecutesAtLeastOneTest()
    {
        var testSteps = CiTestInvocations.ClassifiedSteps()
            .Where(static c => c.Invocation.Kind == CiStepKind.TestRun)
            .ToArray();

        Assert.NotEmpty(testSteps);

        var unguarded = testSteps
            .Where(static c => !c.Invocation.ThroughTestStepScript)
            .Select(static c => c.Step)
            .ToArray();

        Assert.True(
            unguarded.Length == 0,
            $"These CI steps run a test project without going through {TestStepScript}:\n" +
            string.Join("\n", unguarded.Select(static s => $"  {s.Where}: {s.Run!.Trim()}")) + "\n\n" +
            "A bare `dotnet test` step exits 0 when its filter selected NOTHING, and exits 0 when the " +
            "only test it selected SKIPPED — both indistinguishable from a step whose tests ran and " +
            "passed. That is how `Engine=FrozenNrUiRoster&DisplayName~kitchen-sink` (0 matched) and " +
            "`Engine=FrozenNrRoster&DisplayName~kitchen-sink` (1 matched, self-skipping) gated every " +
            $"PR while executing zero tests between them. Route the step through {TestStepScript}, " +
            "which reads the TRX counters and fails when passed + failed == 0.");
    }

    /// <summary>
    /// The environment-variable knobs the concurrency model replaced. Each one used to answer a
    /// question <c>ConcurrencyPolicy</c> now owns, from a second place that could disagree with it.
    /// They are the <see cref="KnobKind.Retired"/> rows of <see cref="Knobs"/>, where each one says what
    /// it used to do.
    /// </summary>
    public static readonly string[] RetiredKnobs =
        [.. Knobs.All.Where(static k => k.Kind == KnobKind.Retired).Select(static k => k.Name)];

    /// <summary>
    /// The knobs retired when <c>ConcurrencyPolicy</c> took over their questions, pinned here as well as
    /// in the table. A retirement can arrive through <see cref="Knobs"/> alone; un-retiring one of these
    /// cannot. Reclassifying its row would take it out of the scan below without a line of this test
    /// changing, so it has to be removed here too, deliberately.
    /// </summary>
    private static readonly string[] PinnedRetiredKnobs = ["NR_PARALLEL", "BS_UI_KEEP_ALIVE", "BSSPEC_DISABLE_WARM_REUSE"];

    /// <summary>
    /// The branch's headline claim — "one policy, no environment-variable knobs" — asserted
    /// mechanically instead of in prose. No production code, and no test fixture, may READ any
    /// retired knob; no CI workflow, and no composite action a workflow calls, may set one.
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
    /// <para>
    /// The CI side reads every file <see cref="CiDefinitionFiles"/> lists — the workflows and
    /// <c>.github/actions</c> — and fails if that included no action: the jobs set up through
    /// <c>.github/actions/setup</c>, and an <c>env:</c> there reaches every step after it. Mutation-checked:
    /// a <c>BS_UI_KEEP_ALIVE: 1</c> in that action's env goes red naming the file.
    /// </para>
    /// </remarks>
    [Fact]
    public void RetiredEnvironmentKnobs_AreReadByNoProductionCodeOrFixture_AndSetByNoWorkflow()
    {
        var unretired = PinnedRetiredKnobs.Except(RetiredKnobs, StringComparer.Ordinal).ToList();
        Assert.True(unretired.Count == 0,
            $"tests/TestProfiles/Knobs.cs no longer classifies {string.Join(", ", unretired)} as Retired, so this test has " +
            "stopped scanning for it. Each was retired because ConcurrencyPolicy answers its question from one place, and a " +
            "second reader is how BsGameDataUiFixture came to run cold while the policy said warm. Bringing one back is a " +
            "decision to take out of PinnedRetiredKnobs here, not a side effect of reclassifying a table row.");
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

        var ciFiles = CiDefinitionFiles.All;
        CiDefinitionFiles.AssertReadAnAction(ciFiles.Select(static f => f.Path), nameof(RetiredEnvironmentKnobs_AreReadByNoProductionCodeOrFixture_AndSetByNoWorkflow));
        foreach (var file in ciFiles)
        {
            foreach (var knob in RetiredKnobs)
            {
                if (file.Text.Contains($"{knob}:", StringComparison.Ordinal))
                {
                    offenders.Add($"  {file.Path} sets {knob}");
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
