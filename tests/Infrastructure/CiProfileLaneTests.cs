using System.Text.RegularExpressions;
using BattleScribeSpec.Tests.Profiles;
using YamlDotNet.RepresentationModel;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>CI runs its lanes through the profile registry, and nothing in CI redefines one.</b> A profile is a
/// whole lane (<c>tests/TestProfiles/</c>): what CI runs under a name is what a developer gets from the
/// same name, and every profile the docs name is checked against the same record.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this replaced.</b> CI used to finish defining its lanes itself: eight steps spelled their
/// filters inline, and step <c>env:</c> blocks supplied the switches that narrowed or widened the aggregate
/// lanes. So <c>-p:TestProfile=nr-ui-frozen</c> ran one spec on a laptop and every
/// applicable spec in the thorough job, under one name, and nothing compared the two. Now every
/// Tests-project step names a profile and adds no filter, no switch changes which tests a lane runs
/// (<see cref="Knobs"/>), and the lanes' needs — a display, an upload, the full spec set — are checked
/// against what the registry says each lane is.
/// </para>
/// <para>
/// CI is read through <see cref="CiProfileRuns"/>: the steps <see cref="CiTestInvocations"/> finds,
/// expanded over their job's matrix, with the profile each line names. Every test here was
/// mutation-checked when written; the mutation is named on each.
/// </para>
/// </remarks>
[Trait("Category", "Lint")]
public sealed class CiProfileLaneTests
{
    private const string CiFile = ".github/workflows/ci.yml";

    /// <summary>
    /// <b>Every CI test run names exactly one registry profile that covers what it runs, in the one
    /// spelling its verb takes, and adds nothing to it.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// A profile is the whole lane, so a step that runs a test project runs one — both projects, now that
    /// the test app resolves profiles itself (<c>cli</c> is the CLI tests' lane). The rules:
    /// </para>
    /// <list type="bullet">
    /// <item><description>exactly one profile, which exists and covers every assembly the step runs (the
    /// host would refuse the rest with exit 5; this says so before CI runs);</description></item>
    /// <item><description>on a <c>dotnet test</c> line it is <c>-p:TestProfile=</c>, which MSBuild carries to the
    /// app with the strict policy; anywhere else it is <c>--test-profile</c> after <c>--</c>, which
    /// <c>dotnet run</c> hands the app — one spelling per verb, so a reader sees one shape of step;</description></item>
    /// <item><description>at least one step is <c>dotnet test … -p:TestProfile=</c>, so CI keeps exercising the
    /// MSBuild channel and the host's check that it arrived;</description></item>
    /// <item><description>no <c>--filter</c> (or <c>--filter-*</c>), <c>--settings</c>, <c>--zero-tests-policy</c>,
    /// <c>--logger</c>, <c>--test-modules</c>, nor an option the host refuses alongside a profile
    /// (<see cref="CiProfileRuns.OverridesIn"/>). A filter narrows the profile, so the step would run less
    /// than the lane it names, under that name; the others replace the verdict or drop the channel the
    /// profile travels on.</description></item>
    /// <item><description>a <c>dotnet test</c> step names its project with <c>--project</c>, never as a bare word
    /// (<see cref="CiTestInvocations.PositionalTargets"/>): the SDK takes a bare project only in first
    /// place, and runs a bare <c>.dll</c> as test modules, which drops the MSBuild channel.</description></item>
    /// </list>
    /// <para>
    /// The matrix is expanded, so <c>--test-profile ${{ matrix.suite.profile }}</c> is two checked runs,
    /// and a typo in the key stays an expression that is not a profile. Mutation-checked when written:
    /// the checks job's offline step back on <c>--filter "Category!=Conformance"</c>;
    /// <c>--filter "DisplayName~kitchen-sink"</c> appended to a profiled step; the matrix key misspelt
    /// (<c>${{ matrix.suite.profil }}</c>); <c>-p:TestProfile=core</c> on a <c>dotnet run</c> step;
    /// <c>--test-profile</c> before the <c>--</c>; the CLI step's profile dropped; the CLI step
    /// turned into a <c>dotnet run</c> (no <c>dotnet test</c> step left); the CLI step's <c>--project</c>
    /// dropped, leaving its csproj a bare word after <c>--no-build</c>; and <c>--ignore-exit-code 8</c> on a
    /// lane — each goes red naming the step.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryCiTestRun_NamesAProfile()
    {
        var runs = CiProfileRuns.All;
        Assert.True(runs.Count >= 10, $"Found {runs.Count} CI test runs; the scan has stopped finding the test steps.");

        var problems = new List<string>();
        foreach (var run in runs)
        {
            if (run.ProfileNames.Count != 1)
            {
                problems.Add(run.ProfileNames.Count == 0
                    ? $"  {run.Where}: runs a test project with no profile — `{run.Command}`"
                    : $"  {run.Where}: names {run.ProfileNames.Count} profiles ({string.Join(", ", run.ProfileNames)})");
                continue;
            }

            if (run.Profile is not { } profile)
            {
                problems.Add(run.ProfileNames[0].Contains(CiTestInvocations.Expression, StringComparison.Ordinal)
                    ? $"  {run.Where}: its profile is an expression this leg does not resolve (a matrix key the job does not define?)"
                    : $"  {run.Where}: names profile '{run.ProfileNames[0]}', which tests/TestProfiles/TestProfiles.cs does not have");
                continue;
            }

            var assemblies = CiTestInvocations.TestProjects
                .Where(p => run.Invocation.TargetsSolution || run.Invocation.Projects.Contains(p.RelativePath))
                .Select(static p => p.AssemblyName);
            problems.AddRange(assemblies.Where(a => !profile.Assemblies.Contains(a, StringComparer.Ordinal))
                .Select(a => $"  {run.Where}: runs {a} under profile {profile.Name}, which does not cover it"));

            var dotnetTest = CiTestInvocations.RunsDotnet(run.Command, "test");
            var how = run.Spellings[0].How;
            if (dotnetTest && how != ProfileSpelling.MsBuildProperty)
            {
                problems.Add($"  {run.Where}: a `dotnet test` step names its profile with --test-profile; write -p:TestProfile={profile.Name}");
            }
            else if (!dotnetTest && how != ProfileSpelling.OptionAfterSeparator)
            {
                problems.Add($"  {run.Where}: names its profile {(how == ProfileSpelling.MsBuildProperty ? "with -p:TestProfile" : "with --test-profile before --")}; "
                    + $"write `dotnet run --project <csproj> --no-build -- --test-profile {profile.Name}`");
            }

            if (run.Overrides.Count > 0)
            {
                problems.Add($"  {run.Where}: adds {string.Join(" ", run.Overrides)} to profile {profile.Name}");
            }

            if (CiTestInvocations.PositionalTargets(run.Command) is { Count: > 0 } positional)
            {
                problems.Add($"  {run.Where}: {PositionalTargetProblem(positional)}");
            }
        }

        if (!runs.Any(static r => CiTestInvocations.RunsDotnet(r.Command, "test") && r.Spellings.Any(static s => s.How == ProfileSpelling.MsBuildProperty)))
        {
            problems.Add("  no CI step is `dotnet test … -p:TestProfile=<name>`, so nothing in CI exercises the MSBuild channel the "
                + "profile and the strict policy travel on, or the host's refusal when it is missing");
        }

        Assert.True(problems.Count == 0,
            "CI test steps must each run one registry profile, and only that:\n" + string.Join("\n", problems) + "\n\n"
            + "A lane is defined in tests/TestProfiles/TestProfiles.cs, so that CI and a developer run the same thing under one "
            + "name. Run it as `dotnet run --project tests/BattleScribeSpec.Tests.csproj --no-build -- --test-profile <name>` "
            + "(the CLI tests' step is the `dotnet test --project … -p:TestProfile=cli` one), and put anything the step needs "
            + "into the profile.");
    }

    /// <summary>
    /// <b>Every test assembly is run in CI by a step whose profile covers it.</b> The project-level
    /// coverage lint (<c>ConcurrencyConfigurationDriftTests.EveryTestProject_IsRunBySomeCiStep</c>) asks
    /// whether some step names the project; this asks whether that step runs a lane of it, which is the
    /// only kind of test run CI has now.
    /// </summary>
    /// <remarks>Mutation-checked when written: the CLI step's profile pointed at <c>lint</c> (which covers only <c>BattleScribeSpec.Tests</c>) goes red naming <c>BattleScribeSpec.Cli.Tests</c>.</remarks>
    [Fact]
    public void EveryTestAssembly_IsRunByACiProfile()
    {
        var assemblies = CiTestInvocations.TestProjects.Select(static p => (p.AssemblyName, p.RelativePath)).ToList();
        Assert.NotEmpty(assemblies);

        var unrun = assemblies
            .Where(a => !CiProfileRuns.All.Any(r => r.Profile is { } p && p.Assemblies.Contains(a.AssemblyName, StringComparer.Ordinal)
                && (r.Invocation.TargetsSolution || r.Invocation.Projects.Contains(a.RelativePath))))
            .Select(static a => $"  {a.AssemblyName} ({a.RelativePath})")
            .ToList();

        Assert.True(unrun.Count == 0,
            "These test assemblies are run by no CI step under a profile that covers them:\n" + string.Join("\n", unrun) + "\n\n"
            + "A test project nobody runs is a gate nobody has. Give it a step that runs a profile covering it "
            + "(tests/TestProfiles/TestProfiles.cs lists each profile's assemblies).");
    }

    /// <summary>
    /// <b>Every CI run of a lane that needs the BattleScribe desktop app is under <c>xvfb-run</c></b> — a
    /// profiled test step, or <c>bs-spec run --ui</c> on the BattleScribe engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The app is a JavaFX window and a runner has no display: without the wrapper it cannot start, and
    /// the lane fails at its fixture — or, if a fixture ever reads a dead app as "not provisioned", skips
    /// whole and passes. Which steps need it is read from the registry: a step needs a display when its
    /// profile claims a <see cref="Needs.DesktopApp"/> lane it does not allow to skip
    /// (<c>core</c> claims <c>BsRosterUi</c>, but CI's offline job does not provision the app and the
    /// profile says so in <c>MaySkip</c>).
    /// </para>
    /// <para>
    /// Mutation-checked when written: <c>xvfb-run -a</c> removed from the BS GameData UI smoke step, and
    /// separately from the <c>thorough-ui-bs</c> step (both matrix legs are reported).
    /// </para>
    /// </remarks>
    [Fact]
    public void DesktopAppProfilesRunUnderXvfb()
    {
        var desktop = CiProfileRuns.All.Where(static r => r.Lanes.Any(static l => l.Needs.HasFlag(Needs.DesktopApp))).ToList();
        Assert.True(desktop.Count > 0, "No CI step runs a profile that claims a desktop-app lane; the scan has stopped finding them.");

        var problems = desktop
            .Where(static r => !r.UnderXvfb)
            .Select(static r => $"  {r.Where}: profile {r.Profile!.Name} runs {string.Join(", ", r.Lanes.Where(static l => l.Needs.HasFlag(Needs.DesktopApp)).Select(static l => l.Trait))} without xvfb-run")
            .Concat(CiProfileRuns.CliUi
                .Where(static r => r.Lane.Needs.HasFlag(Needs.DesktopApp) && !r.UnderXvfb)
                .Select(static r => $"  {r.Where}: bs-spec drives {r.Lane.Trait} without xvfb-run"))
            .ToList();

        Assert.True(problems.Count == 0,
            "These CI steps launch the BattleScribe desktop app with no display:\n" + string.Join("\n", problems) + "\n\n"
            + "A runner has no display, so the JavaFX app cannot start. Prefix the command with `xvfb-run -a`, as the other "
            + "desktop-app steps do.");
    }

    /// <summary>
    /// <b>A UI lane's failure diagnostics leave the runner.</b> Every CI job that runs a lane with a
    /// <see cref="EngineLane.DiagnosticsDir"/> uploads that directory, and sets the lane's
    /// <see cref="EngineLane.DiagnosticsSwitch"/> where the lane runs; and nothing under <c>.github/</c>
    /// moves the directory (<see cref="EngineLane.DiagnosticsDirSwitch"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The NR UI drivers capture a screenshot, DOM snapshot, Pinia dump and console log for every failed
    /// action, and the thorough job that ran them over the full spec set once uploaded none of it: what
    /// reached a reader was the exception text, for a Playwright timeout <c>Timeout 20000ms exceeded.</c>
    /// The rows of this check used to be a hand-kept table of (job, directory); they are derived now —
    /// which lanes each job runs, from its steps' profiles and its <c>bs-spec --ui</c> steps; where each
    /// lane's driver writes, from the registry — so a new UI lane in a job brings its upload rule with it.
    /// Each matrix leg is checked with its own values, so an upload path taken from the matrix must name
    /// that leg's directory. The rule is about the default directory, so each driver's directory switch
    /// (<c>NR_UI_DIAGNOSTICS_DIR</c> and its siblings — a caller's value wins over the default) may not
    /// appear anywhere under <c>.github/</c>: set on a job, it would send the dumps to a path no upload
    /// collects while the upload of the default path, <c>if-no-files-found: ignore</c>, kept this check
    /// green. The registry side is checked too: every directory is under <c>artifacts/</c> and named by a
    /// driver under <c>src/</c>; every capture switch is a classified Default switch; and every lane with
    /// a directory names its directory switch, a Default switch for that lane, and every Default
    /// <c>*_DIAGNOSTICS_DIR</c> switch is the directory switch of each lane it is classified for.
    /// </para>
    /// <para>
    /// Mutation-checked when written: <c>artifacts/nr-ui-diagnostics*/</c> removed from
    /// <c>thorough-nr-ui-roster</c>'s upload; the <c>thorough-ui-bs</c> matrix's diagnostics paths
    /// swapped between its legs; <c>NR_GAMEDATA_UI_DIAGNOSTICS</c> dropped from smoke's NR Editor GameData
    /// UI step; a lane's <c>DiagnosticsDir</c> misspelt; smoke's <c>bs-spec</c> step respelt
    /// <c>run --engine battlescribe-ui protocol-kitchen-sink</c> with its upload removed;
    /// <c>NR_UI_DIAGNOSTICS_DIR: ${{ runner.temp }}/nr</c> in <c>thorough-nr-ui-roster</c>'s job
    /// <c>env:</c>; and <c>BsRosterUi</c>'s <c>DiagnosticsDirSwitch</c> removed — each goes red naming it.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryUiLane_UploadsTheDiagnosticsItWrites()
    {
        var problems = new List<string>();
        var sources = TestProfileRegistryTests.SourceFiles(CiWorkflows.Root, "src")
            .Where(static f => f.EndsWith(".cs", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();
        foreach (var lane in EngineLanes.All.Where(static l => l.DiagnosticsDir is not null))
        {
            var dir = lane.DiagnosticsDir!;
            if (!dir.StartsWith("artifacts/", StringComparison.Ordinal) || dir.EndsWith('/'))
            {
                problems.Add($"  EngineLanes: {lane.Trait}'s DiagnosticsDir '{dir}' is not artifacts/<directory>, where the drivers write");
            }
            // The directory name as a whole string literal, or the start of one followed by the drivers'
            // per-worker interpolation or a glob: `$"bs-ui-diagnostics{WorkerSuffix}"`. A prefix is not a match.
            else if (!sources.Any(s => Regex.IsMatch(s, $"\"{Regex.Escape(dir["artifacts/".Length..])}[\"{{*]")))
            {
                problems.Add($"  EngineLanes: {lane.Trait}'s DiagnosticsDir '{dir}' is named by no driver under src/");
            }

            if (lane.DiagnosticsSwitch is { } capture && Knobs.Find(capture)?.Kind != KnobKind.Default)
            {
                problems.Add($"  EngineLanes: {lane.Trait}'s DiagnosticsSwitch {capture} is not a classified Default switch");
            }

            if (lane.DiagnosticsDirSwitch is not { } redirect)
            {
                problems.Add($"  EngineLanes: {lane.Trait} has a DiagnosticsDir but no DiagnosticsDirSwitch, the switch that moves it");
            }
            else if (Knobs.Find(redirect) is not { Kind: KnobKind.Default } knob || !knob.Engines.Contains(lane.Trait, StringComparer.Ordinal))
            {
                problems.Add($"  EngineLanes: {lane.Trait}'s DiagnosticsDirSwitch {redirect} is not a Default switch classified for {lane.Trait} in Knobs.cs");
            }
        }

        foreach (var knob in Knobs.All.Where(static k => k.Kind == KnobKind.Default && k.Name.EndsWith("_DIAGNOSTICS_DIR", StringComparison.Ordinal)))
        {
            problems.AddRange(knob.Engines
                .Where(e => EngineLanes.Find(e)?.DiagnosticsDirSwitch != knob.Name)
                .Select(e => $"  EngineLanes: {knob.Name} moves {e}'s diagnostics (Knobs.cs), but {e}'s DiagnosticsDirSwitch does not name it"));
        }

        var redirects = EngineLanes.All
            .Where(static l => l.DiagnosticsDirSwitch is not null)
            .GroupBy(static l => l.DiagnosticsDirSwitch!, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => string.Join(", ", g.Select(static l => $"{l.Trait} ({l.DiagnosticsDir})")), StringComparer.Ordinal);
        Assert.True(redirects.Count > 0, "No engine lane names a DiagnosticsDirSwitch; the redirect rule checks nothing.");
        problems.AddRange(GithubMentions(redirects.Keys, nameof(EveryUiLane_UploadsTheDiagnosticsItWrites))
            .Select(m => $"  {m.Where}: {m.Name} moves the diagnostics of {redirects[m.Name]} away from the directory the uploads collect"));

        var needs = CiProfileRuns.All
            .SelectMany(static r => r.Lanes.Where(static l => l.DiagnosticsDir is not null).Select(l => (r.Step, r.Matrix, r.Where, Lane: l)))
            .Concat(CiProfileRuns.CliUi.Where(static r => r.Lane.DiagnosticsDir is not null).Select(static r => (r.Step, r.Matrix, r.Where, r.Lane)))
            .ToList();
        Assert.True(needs.Count > 0, "No CI step runs a lane with a diagnostics directory; the scan has stopped finding the UI lanes.");

        foreach (var (step, matrix, where, lane) in needs)
        {
            var dir = lane.DiagnosticsDir!;
            var uploaded = step.Job.Steps
                .Where(static s => s.Uses?.StartsWith("actions/upload-artifact@", StringComparison.Ordinal) == true)
                .SelectMany(s => CiWorkflows.ExpandMatrix(s.With("path") ?? "", matrix).Split('\n'))
                .Select(static p => p.Trim().TrimEnd('/'))
                .ToList();
            if (!uploaded.Any(p => p == dir || p == $"{dir}*"))
            {
                problems.Add($"  {where}: runs {lane.Trait}, whose driver writes {dir}, but job {step.Job.Id} uploads no such path");
            }

            if (lane.DiagnosticsSwitch is { } capture && !SetsVariable(step, capture))
            {
                problems.Add($"  {where}: runs {lane.Trait} without {capture} set on the step, its job or the workflow, so the driver writes nothing to {dir}");
            }
        }

        Assert.True(problems.Count == 0,
            "These CI jobs run a UI driver whose diagnostics never leave the runner:\n" + string.Join("\n", problems.Distinct()) + "\n\n"
            + "A driver that dumps a screenshot, DOM and store state into a runner nobody collects from has diagnosed nothing: "
            + "the reader gets the exception text, which for a Playwright timeout is seven words. Add an "
            + "`actions/upload-artifact` step for the directory (with `*` for the per-worker suffixes), set the driver's "
            + "capture switch where it runs, and leave its directory where the upload looks (no *_DIAGNOSTICS_DIR under "
            + ".github/). The directories and switches come from tests/TestProfiles/EngineLanes.cs.");

        static bool SetsVariable(CiStep step, string name) =>
            step.Env(name) is { Length: > 0 }
            || EnvValue(step.Job.Node, name) is { Length: > 0 }
            || (CiWorkflows.All.SingleOrDefault(w => w.File == step.Job.Workflow) is { } workflow && EnvValue(workflow.Node, name) is { Length: > 0 });

        static string? EnvValue(YamlMappingNode node, string name) =>
            node.Children.TryGetValue(new YamlScalarNode("env"), out var env) && env is YamlMappingNode map ? CiWorkflows.Scalar(map, name) : null;
    }

    /// <summary>
    /// <b>The CI step named "Full frozen NR UI roster" runs the full spec set</b>: its profile selects
    /// <c>FrozenNrUiRosterConformanceTests.OtherSpecs</c>, and neither the every-push smoke lane, nor
    /// <c>pre-push</c>, nor any profile run outside the thorough jobs can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That lane ran <b>one</b> spec for its entire life, on the since-falsified premise that the frozen
    /// HAR supports a single roster-creation flow per run; <c>docs/warm-reuse.md</c> records what that cost:
    /// "CI never caught the original bug because the NR-UI roster lane runs a single spec." It is the whole
    /// applicable suite now, split in two tests because the every-push lane and <c>pre-push</c> must stay
    /// fast — and a full lane whose profile stopped selecting <c>OtherSpecs</c> would be invisible: the step
    /// still passes, still says "Full", and covers one spec of ~378. One executed test is not zero, so the
    /// executed-at-least-one guard cannot see it either.
    /// </para>
    /// <para>
    /// Each profile's filter is evaluated against the test itself (<see cref="FilterReach"/>), so this reads
    /// what a run would select, not a switch that says what it should.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThoroughNrUiRosterStep_RunsTheFullSpecSet()
    {
        var otherSpecs = SuiteTraits.TestMethods.SingleOrDefault(static t =>
            t.FullyQualifiedName == EngineLanes.Find("FrozenNrUiRoster")?.OtherSpecs);
        Assert.True(otherSpecs is not null,
            "EngineLanes' FrozenNrUiRoster.OtherSpecs names no test method of this assembly, so nothing here can tell the full lane "
            + "from its kitchen-sink half.");
        Reach Reaches(TestProfile p) =>
            p.Assemblies.Contains(EngineLanes.Assembly, StringComparer.Ordinal) ? FilterReach.Parse(p.Selection.Filter).Evaluate(otherSpecs) : Reach.No;

        var steps = CiProfileRuns.All.Where(static r => r.Step.Job.Workflow == CiFile && r.Step.Name == "Full frozen NR UI roster").ToList();
        Assert.True(steps.Count == 1,
            $"{CiFile} has {steps.Count} runs in steps named 'Full frozen NR UI roster', not one. If it was renamed, update this guard; "
            + "if it was deleted, the thorough NR UI roster coverage went with it.");

        var problems = new List<string>();
        var step = steps[0];
        if (step.Profile is not { } profile || Reaches(profile) != Reach.Yes)
        {
            problems.Add($"  {step.Where} runs '{string.Join(", ", step.ProfileNames)}', which does not select {otherSpecs.FullyQualifiedName}: "
                + "the lane runs kitchen-sink alone");
        }

        if (TestProfiles.Find("nr-ui-frozen") is not { } fullLane || Reaches(fullLane) != Reach.Yes)
        {
            problems.Add($"  profile nr-ui-frozen does not select {otherSpecs.FullyQualifiedName}, so the name of the full lane runs one spec");
        }

        string[] fastLanes = ["smoke-nr-ui", "pre-push"];
        foreach (var name in fastLanes)
        {
            if (TestProfiles.Find(name) is { } p && Reaches(p) != Reach.No)
            {
                problems.Add($"  profile {name} can select {otherSpecs.FullyQualifiedName}: a fast lane would run every applicable spec (~27 minutes)");
            }
        }

        var thorough = CiGateConfig.Load().ThoroughJobs.ToHashSet(StringComparer.Ordinal);
        problems.AddRange(CiProfileRuns.All
            .Where(r => r.Profile is { } p && Reaches(p) != Reach.No && !(r.Step.Job.Workflow == CiFile && thorough.Contains(r.Step.Job.Id)))
            .Select(static r => $"  {r.Where} runs {r.Profile!.Name}, which selects the full NR UI roster lane, outside the thorough jobs"));

        Assert.True(problems.Count == 0,
            "The full frozen NR UI roster lane must be exactly where it is meant to be:\n" + string.Join("\n", problems) + "\n\n"
            + "FrozenNrUiRosterConformanceTests.OtherSpecs is every applicable spec but kitchen-sink. A lane that silently "
            + "shrinks from ~378 specs to 1 still exits 0, and one that silently grows puts ~27 minutes on every push.");
    }

    /// <summary>
    /// <b>Every engine lane is run by a CI step's profile or carries a <see cref="EngineLane.CiExempt"/>
    /// reason</b> — not both, not neither. A step runs a lane when its profile claims it; a <c>bs-spec</c>
    /// step drives a driver over the specs it names, not the lane's test classes, so it does not count.
    /// </summary>
    [Fact]
    public void EveryEngineLane_IsRunByCi_OrSaysWhyNot()
    {
        var problems = new List<string>();
        foreach (var lane in EngineLanes.All)
        {
            var jobs = CiProfileRuns.All
                .Where(r => r.Step.Job.Workflow == CiFile && r.Lanes.Contains(lane))
                .Select(static r => r.Step.Job.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (jobs.Count > 0 && lane.CiExempt is not null)
            {
                problems.Add($"  {lane.Trait} carries CiExempt, but CI runs it ({string.Join(", ", jobs)}): delete the exemption");
            }
            else if (jobs.Count == 0 && string.IsNullOrWhiteSpace(lane.CiExempt))
            {
                problems.Add($"  {lane.Trait}: no CI step runs a profile that claims it, and it has no CiExempt saying why");
            }
        }

        Assert.True(problems.Count == 0,
            "Which lanes CI runs, and why the others are not, is the registry's to say:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// <b>Every profile a workflow or a document names exists.</b> Read: the CI definition, AGENTS.md,
    /// README.md, <c>docs/**</c> (minus <c>docs/superpowers/</c>, which keeps old plans as they were) and the
    /// skills under <c>.agents/skills/</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A reference is a <c>-p:TestProfile=x</c>, <c>-TestProfile x</c>, <c>--test-profile x</c> or
    /// <c>tests/test-profiles/x.runsettings</c>; its <c>x</c> must be a registry profile. A deleted profile
    /// otherwise lives on in every recipe that names it, and the reader finds out from MSBuild.
    /// Placeholders and patterns (<c>&lt;x&gt;</c>, <c>nr-live*</c>) are not names; what prose puts after a
    /// name — a full stop, <c>**</c>, <c>&lt;br&gt;</c>, a <c>.runsettings</c> extension — is not part of it
    /// (<see cref="CiProfileRuns.AsProfileName"/>).
    /// </para>
    /// <para>
    /// A bare <c>`x`</c> is not a reference: it carries nothing that tells a profile from a spec id or a
    /// job name, so a recipe names a profile the way it is run. Every profile is documented by the test app
    /// itself (<c>--list-test-profiles</c>), so no document has to list them all.
    /// </para>
    /// <para>
    /// Mutation-checked when written: <c>-p:TestProfile=nr-live-visible</c> put back in a doc goes red
    /// naming the file and line, and so do <c>-p:TestProfile=nr-live-visible**</c> and
    /// <c>See tests/test-profiles/nr-ui-live-visible.runsettings.</c> at the end of a sentence.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoDanglingProfileReferences()
    {
        var documents = Documents().Concat(CiDefinitionFiles.All.Select(static f => (f.Path, f.Text))).ToList();
        string[] expectedRoots = ["docs/", ".agents/skills/", ".github/"];
        var unread = expectedRoots.Where(r => !documents.Any(d => d.Path.StartsWith(r, StringComparison.Ordinal))).ToList();
        Assert.True(unread.Count == 0,
            $"Read no file under {string.Join(", ", unread)}: the documents moved, or the paths this lint reads are wrong, so it "
            + "would pass on references it never saw. Update the paths above.");

        var reference = new Regex(@"(?:TestProfile=|-TestProfile[ :]|--test-profile[ =]|test-profiles/)([^\s/|]+?)(?:\.runsettings)?(?=[\s|)\]},;`'""]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        var problems = new List<string>();
        var references = 0;
        foreach (var (path, text) in documents)
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in reference.Matches(lines[i]))
                {
                    if (CiProfileRuns.AsProfileName(m.Groups[1].Value) is not { } name)
                    {
                        continue;
                    }

                    references++;
                    if (TestProfiles.Find(name) is null)
                    {
                        problems.Add($"  {path}:{i + 1}: names profile '{name}', which tests/TestProfiles/TestProfiles.cs does not have");
                    }
                }
            }
        }

        Assert.True(references > 0, "Found no profile reference in the CI definition or the docs; the scan is reading nothing.");
        Assert.True(problems.Count == 0,
            "Profile references and the registry disagree:\n" + string.Join("\n", problems) + "\n\n"
            + "A recipe that names a deleted profile fails when someone follows it. Point the reference at a profile that "
            + "exists (tests/TestProfiles/TestProfiles.cs; --list-test-profiles lists them all).");
    }

    /// <summary>
    /// <b>Every test command a document gives runs as written</b>: one spelling of the profile per verb,
    /// no VSTest option, no second zero-tests policy, no option the host refuses with a profile, no
    /// runsettings file, a project named with <c>--project</c>, and no solution-wide
    /// <c>dotnet test</c> that a test project would refuse or fail. Read: AGENTS.md, README.md,
    /// <c>docs/**</c> (minus <c>docs/superpowers/</c>) and the skills — the same documents as
    /// <see cref="NoDanglingProfileReferences"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The CI rules (<see cref="EveryCiTestRun_NamesAProfile"/>) applied where a reader copies a command
    /// from, except that a document may narrow a profile with <c>--filter</c> — that is what the option
    /// is for on a laptop. A command is a <c>dotnet test</c>, or a <c>dotnet run</c> of a test project,
    /// in a code span or a code-block line. Each rule stands for a recipe that would fail:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>-p:TestProfile=</c> on <c>dotnet test</c>, <c>-- --test-profile</c> on
    /// <c>dotnet run</c>: the spellings CI uses, so a recipe and a CI step read alike;</description></item>
    /// <item><description><c>--settings</c>, <c>--logger</c>, <c>--collect</c>, <c>--blame*</c>,
    /// <c>RunSettingsFilePath</c>, <c>--test-modules</c>, <c>--zero-tests-policy</c>, an inline
    /// <c>TestCaseFilter</c>: refused by the test app (exit 5), or a run that drops the profile;</description></item>
    /// <item><description>with a profile, an option the host refuses alongside one
    /// (<see cref="TestHost.RefusedWithAProfile"/>: <c>--ignore-exit-code</c>, <c>--config-file</c>,
    /// <c>--xunit-config-filename</c>, a response file, <c>--filter-*</c>), read by the host's own parser;</description></item>
    /// <item><description>a <c>dotnet test</c> target given as a bare word rather than <c>--project</c>
    /// (<see cref="CiTestInvocations.PositionalTargets"/>): the SDK takes one only in first place, and a bare
    /// <c>.dll</c> runs as test modules, which the test app refuses;</description></item>
    /// <item><description>a <c>dotnet test</c> naming no project, with a profile that does not cover every
    /// test project or with a <c>--filter</c>: the solution-wide run starts every test project, and the one
    /// outside the profile refuses it — or, under a filter that matches none of its tests, executes nothing
    /// and fails with exit 8;</description></item>
    /// <item><description>any mention of <c>tests/test-profiles/</c> or a <c>.runsettings</c> file: nothing
    /// reads those any more.</description></item>
    /// </list>
    /// <para>
    /// Mutation-checked when written: <c>dotnet test -p:TestProfile=bs-ui-roster</c> back in AGENTS.md;
    /// <c>dotnet test --filter "Tag=cost"</c> in a doc; <c>--logger trx</c> on a documented command;
    /// <c>dotnet run --project tests/BattleScribeSpec.Tests.csproj -p:TestProfile=bs</c>; and
    /// <c>tests/test-profiles/</c> named in a skill — each goes red naming the file and line. Added later
    /// and checked the same way: AGENTS.md's one-spec line with its project moved after the
    /// <c>--filter</c> (<c>dotnet test --filter "DisplayName~my-spec-id" tests/BattleScribeSpec.Tests.csproj</c>,
    /// which the SDK stops on), and <c>--ignore-exit-code 8</c> on a documented profiled command.
    /// </para>
    /// </remarks>
    [Fact]
    public void DocumentedTestCommands_RunAsWritten()
    {
        var everyAssembly = CiTestInvocations.TestProjects.Select(static p => p.AssemblyName).ToList();
        string[] refused = ["--settings", "--logger", "--collect", "--test-modules", "--zero-tests-policy"];
        var problems = new List<string>();
        var commands = 0;
        foreach (var (path, text) in Documents())
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var where = $"{path}:{i + 1}";
                if (lines[i].Contains("test-profiles/", StringComparison.Ordinal) || lines[i].Contains(".runsettings", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"  {where}: names tests/test-profiles/ or a .runsettings file, which nothing reads any more — a lane is a test profile");
                }

                foreach (var command in TestCommandsIn(lines[i]))
                {
                    commands++;
                    var tokens = CiTestInvocations.Tokenize(command).Tokens;
                    var dotnetTest = CiTestInvocations.RunsDotnet(command, "test");
                    var spellings = CiProfileRuns.ProfileSpellings(command);
                    if (dotnetTest && spellings.Any(static s => s.How != ProfileSpelling.MsBuildProperty))
                    {
                        problems.Add($"  {where}: `{command}` names a profile with --test-profile on dotnet test; write -p:TestProfile=<name>");
                    }
                    else if (!dotnetTest && spellings.Any(static s => s.How != ProfileSpelling.OptionAfterSeparator))
                    {
                        problems.Add($"  {where}: `{command}` names a profile without `-- --test-profile <name>`, the dotnet run spelling");
                    }

                    var vstest = tokens.Where(tok => refused.Any(o => tok == o || tok.StartsWith($"{o}=", StringComparison.Ordinal) || tok.StartsWith($"{o}:", StringComparison.Ordinal))
                            || tok.StartsWith("--blame", StringComparison.Ordinal)
                            || tok.Contains("RunSettingsFilePath", StringComparison.OrdinalIgnoreCase)
                            || tok.Contains("TestCaseFilter", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (vstest.Count > 0)
                    {
                        problems.Add($"  {where}: `{command}` passes {string.Join(" ", vstest)}, which the test app refuses or which drops the profile");
                    }

                    if (spellings.Count > 0 && TestHost.OptionsRefusedWithAProfile(tokens) is { Count: > 0 } withProfile)
                    {
                        problems.Add($"  {where}: `{command}` passes {string.Join(" ", withProfile)} with a profile, which the test app refuses (exit 5)");
                    }

                    if (CiTestInvocations.PositionalTargets(command) is { Count: > 0 } positional)
                    {
                        problems.Add($"  {where}: `{command}` {PositionalTargetProblem(positional)}");
                    }

                    var invocation = CiTestInvocations.ClassifyCommand(command);
                    if (dotnetTest && invocation.Projects.Count == 0)
                    {
                        var narrow = spellings
                            .Select(static s => TestProfiles.Find(s.Name))
                            .OfType<TestProfile>()
                            .Where(p => everyAssembly.Any(a => !p.Assemblies.Contains(a, StringComparer.Ordinal)))
                            .Select(static p => p.Name)
                            .ToList();
                        if (narrow.Count > 0)
                        {
                            problems.Add($"  {where}: `{command}` runs the whole solution under {string.Join(", ", narrow)}, which does not cover every "
                                + $"test project; name it: dotnet test --project {TestProfiles.Projects[TestProfiles.Tests]} …");
                        }
                        else if (tokens.Any(static tok => tok == "--filter" || tok.StartsWith("--filter=", StringComparison.Ordinal) || tok.StartsWith("--filter:", StringComparison.Ordinal)))
                        {
                            problems.Add($"  {where}: `{command}` filters the whole solution: a test project the filter matches nothing in executes "
                                + $"nothing and fails (exit 8); name the project: dotnet test --project {TestProfiles.Projects[TestProfiles.Tests]} …");
                        }
                    }
                }
            }
        }

        Assert.True(commands > 0, "Found no dotnet test or dotnet run command in the docs; the scan is reading nothing.");
        Assert.True(problems.Count == 0,
            "These documented test commands would not run as written:\n" + string.Join("\n", problems) + "\n\n"
            + "The suites run on Microsoft.Testing.Platform, and their entry point resolves test profiles itself: "
            + "`dotnet test --project <csproj> -p:TestProfile=<name>`, or `dotnet run --project <csproj> -- --test-profile <name>`, "
            + "narrowed with --filter if need be; `--list-test-profiles` lists the profiles.");
    }

    /// <summary>What is wrong with naming a <c>dotnet test</c> target as a bare word (<see cref="CiTestInvocations.PositionalTargets"/>).</summary>
    private static string PositionalTargetProblem(IReadOnlyList<string> positional) =>
        $"names {string.Join(", ", positional)} as a bare word on `dotnet test`. The SDK takes a bare project, solution or directory "
        + "only before any word it does not know (after one it stops: \"Specifying a project for 'dotnet test' should be via "
        + "'--project'.\"), and runs a bare .dll or .exe as test modules, which drops the arguments MSBuild carries — the profile "
        + "and the strict policy — so the test app refuses it. Write --project <csproj> (or --solution)";

    /// <summary>
    /// The test commands on one line of a document: each code span — or, outside one, the rest of the line,
    /// up to a <c>#</c> comment — holding a <c>dotnet test</c>, or a <c>dotnet run</c> of a test project,
    /// from the word <c>dotnet</c> on.
    /// </summary>
    internal static IEnumerable<string> TestCommandsIn(string line)
    {
        var spans = line.Contains('`', StringComparison.Ordinal)
            ? line.Split('`').Where(static (_, i) => i % 2 == 1)
            : [line];
        foreach (var span in spans)
        {
            var start = Regex.Match(span, @"\bdotnet\s+(?:test|run)\b");
            if (!start.Success)
            {
                continue;
            }

            // A code-block line may end in a shell comment; it is not part of the command.
            var command = Regex.Replace(span[start.Index..], @"\s+#.*$", "").TrimEnd();
            if (Regex.IsMatch(command, @"^dotnet\s+run\b") && CiTestInvocations.ClassifyCommand(command).Kind != CiStepKind.TestRun)
            {
                continue;
            }

            yield return command;
        }
    }

    /// <summary>
    /// The documents the reference and command lints read: AGENTS.md, README.md, <c>docs/**</c> (minus
    /// <c>docs/superpowers/</c>, which keeps old plans as they were) and the skills under <c>.agents/skills/</c>.
    /// </summary>
    private static List<(string Path, string Text)> Documents()
    {
        var root = CiWorkflows.Root;
        var superpowers = Path.Combine(root, "docs", "superpowers") + Path.DirectorySeparatorChar;
        return [.. new[] { Path.Combine(root, "AGENTS.md"), Path.Combine(root, "README.md") }
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
                .Where(f => !f.StartsWith(superpowers, StringComparison.OrdinalIgnoreCase)))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, ".agents", "skills"), "*.md", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal)
            .Select(static f => (Path: CiWorkflows.Relative(f), Text: File.ReadAllText(f)))];
    }

    // ── the helpers' own rules ──

    /// <summary>
    /// <b>A job's matrix expands into its legs</b>, each keyed the way an expression names a value, and a
    /// shape the expansion does not understand is refused rather than read as fewer legs.
    /// </summary>
    [Fact]
    public void CiJob_ExpandsItsMatrixIntoLegs()
    {
        var workflow = CiWorkflows.Parse("inline.yml", """
            jobs:
              two-axes:
                strategy:
                  matrix:
                    suite:
                      - { name: gamedata, profile: bs-ui-gamedata }
                      - { name: roster, profile: bs-ui-roster }
                    os: [linux, windows]
                steps:
                  - run: dotnet run --project tests/BattleScribeSpec.Tests.csproj --no-build -- --test-profile ${{ matrix.suite.profile }}
              none:
                steps:
                  - run: echo
              with-include:
                strategy:
                  matrix:
                    include: [{ a: 1 }]
                steps:
                  - run: echo
            """);

        var legs = workflow.Job("two-axes").MatrixCombinations();
        Assert.Equal(4, legs.Count);
        Assert.Equal("bs-ui-gamedata bs-ui-gamedata bs-ui-roster bs-ui-roster", string.Join(" ", legs.Select(static l => l["matrix.suite.profile"])));
        Assert.Equal("linux windows linux windows", string.Join(" ", legs.Select(static l => l["matrix.os"])));

        var step = workflow.Job("two-axes").Steps[0].Run!;
        Assert.Contains("-- --test-profile bs-ui-roster", CiWorkflows.ExpandMatrix(step, legs[2]), StringComparison.Ordinal);
        Assert.Contains("${{ matrix.suite.profil }}", CiWorkflows.ExpandMatrix("${{ matrix.suite.profil }}", legs[2]), StringComparison.Ordinal);

        Assert.Empty(Assert.Single(workflow.Job("none").MatrixCombinations()));
        Assert.Throws<NotSupportedException>(() => workflow.Job("with-include").MatrixCombinations());
    }

    /// <summary>
    /// <b>A command line's profile, display wrapper and overrides are read in every spelling</b>
    /// <c>dotnet test</c> and the test app accept, with how the profile was spelled; <c>xvfb-run</c>'s own
    /// options are not the runner's.
    /// </summary>
    [Theory]
    [InlineData("dotnet run --project tests/BattleScribeSpec.Tests.csproj --no-build -- --test-profile core", "core", "OptionAfterSeparator", false, "")]
    [InlineData("dotnet run --project x.csproj -- --Test-Profile:core", "core", "OptionAfterSeparator", false, "")]
    [InlineData("artifacts/bin/BattleScribeSpec.Tests/debug/BattleScribeSpec.Tests --test-profile=core", "core", "Option", false, "")]
    [InlineData("dotnet run --project x.csproj --test-profile core --", "core", "Option", false, "")]
    [InlineData("xvfb-run -a -s \"-screen 0 1x1x24\" dotnet run --project x.csproj --no-build -- --test-profile bs-ui-roster", "bs-ui-roster", "OptionAfterSeparator", true, "")]
    [InlineData("dotnet test tests/BattleScribeSpec.Tests.csproj -p:TestProfile=lint --filter \"Engine=X\"", "lint", "MsBuildProperty", false, "--filter")]
    [InlineData("dotnet run --project tests/BattleScribeSpec.Tests.csproj --no-build -- --test-profile=bs --filter-class X", "bs", "OptionAfterSeparator", false, "--filter-class")]
    [InlineData("dotnet test x.csproj -s my.runsettings -- RunConfiguration.TestCaseFilter=A", "", "", false, "-s RunConfiguration.TestCaseFilter=A")]
    [InlineData("dotnet test x.csproj /p:VSTestTestCaseFilter=A -p:TestProfile=core", "core", "MsBuildProperty", false, "-p:VSTestTestCaseFilter=A")]
    [InlineData("dotnet run --project x.csproj -- --test-profile smoke-nr-ui --filter:DisplayName~kitchen-sink --settings:a.runsettings -s:b -s=c --filter-class:X",
        "smoke-nr-ui", "OptionAfterSeparator", false, "--filter:DisplayName~kitchen-sink --settings:a.runsettings -s:b -s=c --filter-class:X")]
    [InlineData("dotnet test x.csproj -p:TestProfile=core --filter=A --settings=b -screenshots", "core", "MsBuildProperty", false, "--filter=A --settings=b")]
    [InlineData("dotnet test --project x.csproj -p:TestProfile=cli --zero-tests-policy none --logger trx --test-modules a.dll", "cli", "MsBuildProperty", false,
        "--zero-tests-policy --logger --test-modules")]
    [InlineData("dotnet run --project x.csproj -- --test-profile bs --ignore-exit-code 8 @more.rsp --xunit-config-filename x.json --Config-File:c.json", "bs", "OptionAfterSeparator", false,
        "--ignore-exit-code @more.rsp --xunit-config-filename --Config-File:c.json")]
    public void CiProfileRuns_ReadTheProfileAndOverridesOffTheLine(string command, string profiles, string spellings, bool xvfb, string overrides)
    {
        Assert.Equal(profiles, string.Join(",", CiProfileRuns.ProfilesNamedBy(command)));
        Assert.Equal(spellings, string.Join(",", CiProfileRuns.ProfileSpellings(command).Select(static s => s.How)));
        Assert.Equal(xvfb, CiProfileRuns.RunsUnderXvfb(command));
        Assert.Equal(overrides, string.Join(" ", CiProfileRuns.OverridesIn(command)));
    }

    /// <summary>
    /// <b>A <c>dotnet test</c> target given as a bare word is found wherever it sits</b> — first or after an
    /// option, a project, solution, directory or built assembly — and the value of <c>--project</c>,
    /// <c>--solution</c> or <c>--test-modules</c>, anything after <c>--</c>, and any other verb are not.
    /// </summary>
    [Theory]
    [InlineData("dotnet test tests/BattleScribeSpec.Tests.csproj --filter \"DisplayName~x\"", "tests/BattleScribeSpec.Tests.csproj")]
    [InlineData("dotnet test --filter \"DisplayName~x\" tests/BattleScribeSpec.Tests.csproj", "tests/BattleScribeSpec.Tests.csproj")]
    [InlineData("dotnet test --no-build .\\tests\\BattleScribeSpec.Cli.Tests\\BattleScribeSpec.Cli.Tests.csproj", "tests/BattleScribeSpec.Cli.Tests/BattleScribeSpec.Cli.Tests.csproj")]
    [InlineData("dotnet test artifacts/bin/BattleScribeSpec.Tests/debug/BattleScribeSpec.Tests.dll", "artifacts/bin/BattleScribeSpec.Tests/debug/BattleScribeSpec.Tests.dll")]
    [InlineData("dotnet test BattleScribeSpec.slnx -p:TestProfile=pre-push", "BattleScribeSpec.slnx")]
    [InlineData("dotnet test tests -p:TestProfile=lint", "tests")]
    [InlineData("dotnet test --project tests/BattleScribeSpec.Tests.csproj --filter \"DisplayName~x\"", "")]
    [InlineData("dotnet test --project=tests/BattleScribeSpec.Tests.csproj", "")]
    [InlineData("dotnet test --solution BattleScribeSpec.slnx", "")]
    [InlineData("dotnet test --test-modules artifacts/bin/BattleScribeSpec.Tests/debug/BattleScribeSpec.Tests.dll", "")]
    [InlineData("dotnet test -p:TestProfile=pre-push", "")]
    [InlineData("dotnet test --project tests/BattleScribeSpec.Tests.csproj -- tests/BattleScribeSpec.Tests.csproj", "")]
    [InlineData("dotnet run --project tests/BattleScribeSpec.Tests.csproj -- --test-profile bs", "")]
    [InlineData("dotnet build tests/BattleScribeSpec.Tests.csproj", "")]
    public void PositionalTargets_AreFoundWhereverTheySit(string command, string expected) =>
        Assert.Equal(expected, string.Join(",", CiTestInvocations.PositionalTargets(command)));

    /// <summary>
    /// <b>A <c>bs-spec</c> command's UI lanes are resolved the way the CLI resolves its engine</b>: the
    /// <c>battlescribe</c> default, every spelling of <c>--engine</c>, a <c>-ui</c> engine name as well as
    /// <c>--ui</c>, the domain from <c>--gamedata</c>/<c>--roster</c> or the spec argument, and
    /// <c>verify</c>'s default engines. Not a UI run: no lane.
    /// </summary>
    [Theory]
    [InlineData("xvfb-run -a dotnet run --project src/BattleScribeSpec.Cli --no-build -- run --engine battlescribe --ui protocol-kitchen-sink", "BsRosterUi")]
    [InlineData("bs-spec run --ui protocol-kitchen-sink", "BsRosterUi")]
    [InlineData("dotnet run --project src/BattleScribeSpec.Cli --no-build -- run --engine battlescribe-ui protocol-kitchen-sink", "BsRosterUi")]
    [InlineData("bs-spec run --engine=newrecruit-ui protocol-kitchen-sink", "FrozenNrUiRoster")]
    [InlineData("bs-spec run --engine:newrecruit --ui=true protocol-kitchen-sink", "FrozenNrUiRoster")]
    [InlineData("dotnet bs-spec.dll run --ui --gamedata --all", "BsGameDataUi")]
    [InlineData("bs-spec run --ui category/category-entry-with-constraint", "BsGameDataUi")]
    [InlineData("bs-spec run --engine newrecruit-ui category-entry-with-constraint", "FrozenNrGameDataUi")]
    [InlineData("bs-spec probe --ui specs/gamedata/category/category-entry-with-constraint.yaml", "BsGameDataUi")]
    [InlineData("bs-spec run --ui --roster category-entry-with-constraint", "BsRosterUi")]
    [InlineData("bs-spec verify category/category-entry-with-constraint", "BsGameDataUi,FrozenNrGameDataUi")]
    [InlineData("bs-spec verify --engines battlescribe,newrecruit-ui category/category-entry-with-constraint", "FrozenNrGameDataUi")]
    [InlineData("bs-spec verify --engines=battlescribe,newrecruit category/category-entry-with-constraint", "")]
    [InlineData("bs-spec run --engine battlescribe protocol-kitchen-sink", "")]
    [InlineData("bs-spec run --ui false protocol-kitchen-sink", "")]
    [InlineData("dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll run --all --engine \"battlescribe=dotnet:artifacts/bin/x/bs-reference-adapter.dll\"", "")]
    [InlineData("bs-spec export-xml cost/cost-hidden-limit-validation ./out/", "")]
    [InlineData("dotnet build src/BattleScribeSpec.Cli", "")]
    public void CiProfileRuns_ResolveTheLanesABsSpecCommandDrives(string command, string lanes) =>
        Assert.Equal(lanes, string.Join(",", CiProfileRuns.CliUiLanesOf(command).Lanes.Select(static l => l.Trait)));

    /// <summary>
    /// <b>A <c>bs-spec</c> UI run the resolver cannot map is refused, not read as no lane</b> — read as no
    /// lane, it would drop out of the display and upload rules.
    /// </summary>
    [Theory]
    [InlineData("bs-spec run --engine mystery-ui protocol-kitchen-sink")]
    [InlineData("bs-spec run --engine ${{…}} --ui protocol-kitchen-sink")]
    [InlineData("bs-spec run --engine ${{…}} protocol-kitchen-sink")]
    public void CiProfileRuns_RefuseABsSpecUiRunTheyCannotMap(string command) =>
        Assert.Throws<NotSupportedException>(() => CiProfileRuns.CliUiLanesOf(command));

    /// <summary>
    /// <b>A profile name is read out of prose</b> with what prose puts after it stripped — punctuation,
    /// emphasis, an HTML tag, a <c>.runsettings</c> extension — and a placeholder or a glob is not a name.
    /// </summary>
    [Theory]
    [InlineData("nr-live", "nr-live")]
    [InlineData("nr-live.", "nr-live")]
    [InlineData("nr-live`),", "nr-live")]
    [InlineData("nr-live-visible**", "nr-live-visible")]
    [InlineData("nr-live-visible__", "nr-live-visible")]
    [InlineData("nr-live-visible<br>", "nr-live-visible")]
    [InlineData("nr-ui-live-visible.runsettings", "nr-ui-live-visible")]
    [InlineData("nr-ui-live-visible.runsettings.", "nr-ui-live-visible")]
    [InlineData("nr-ui-live-visible.runsettings`**.", "nr-ui-live-visible")]
    [InlineData("nr-live*", null)]
    [InlineData("nr-live***", null)]
    [InlineData("<name>", null)]
    [InlineData("smoke-<lane>", null)]
    [InlineData("...", null)]
    [InlineData("${{…}}", null)]
    public void CiProfileRuns_ReadAProfileNameOutOfProse(string token, string? name) =>
        Assert.Equal(name, CiProfileRuns.AsProfileName(token));

    /// <summary>
    /// Every mention of one of <paramref name="names"/> under <c>.github/</c>: in any YAML scalar, keys
    /// included (an <c>env:</c> entry is a key; a <c>$GITHUB_ENV</c> write or an inline assignment is in a
    /// <c>run:</c> value), and on any line of a file that is not YAML. Comments are not scalars, so a
    /// workflow can still explain a rule in words. Fails <paramref name="caller"/> if no composite action
    /// was read.
    /// </summary>
    private static List<(string Where, string Name)> GithubMentions(IEnumerable<string> names, string caller)
    {
        var pattern = new Regex($@"\b(?:{string.Join("|", names.Select(Regex.Escape))})\b", RegexOptions.CultureInvariant);
        var github = Path.Combine(CiWorkflows.Root, ".github");
        var files = Directory.EnumerateFiles(github, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        CiDefinitionFiles.AssertReadAnAction(files.Select(CiWorkflows.Relative), caller);

        var found = new List<(string, string)>();
        foreach (var file in files)
        {
            var relative = CiWorkflows.Relative(file);
            var text = File.ReadAllText(file);
            var values = file.EndsWith(".yml", StringComparison.Ordinal) || file.EndsWith(".yaml", StringComparison.Ordinal)
                ? CiWorkflows.Scalars(CiWorkflows.LoadDocument(relative, text)).Select(static s => (Line: (long)s.Start.Line, Text: s.Value ?? ""))
                : text.Split('\n').Select(static (l, i) => (Line: (long)(i + 1), Text: l));

            foreach (var (line, value) in values)
            {
                found.AddRange(pattern.Matches(value).Select(static m => m.Value).Distinct(StringComparer.Ordinal).Select(n => ($"{relative}:{line}", n)));
            }
        }

        return found;
    }
}
