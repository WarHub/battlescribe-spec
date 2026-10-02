using System.Text;
using System.Text.RegularExpressions;
using BattleScribeSpec.Tests.Profiles;
using YamlDotNet.RepresentationModel;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>CI runs its lanes through the profile registry, and nothing in CI redefines one.</b> A profile is a
/// whole lane (<c>tests/TestProfiles/</c>): what CI runs under a name is what a developer gets from the
/// same name, and what the docs say about a lane is generated from — or checked against — the same
/// record.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this replaced.</b> CI used to finish defining its lanes itself: eight steps spelled their
/// filters inline, and step <c>env:</c> blocks supplied <c>NR_FROZEN_SMOKE</c>, <c>NR_UI_SMOKE</c> and
/// <c>NR_UI_ROSTER_FULL</c>. So <c>-p:TestProfile=nr-ui-frozen</c> ran one spec on a laptop and every
/// applicable spec in the thorough job, under one name, and nothing compared the two. Now every
/// Tests-project step names a profile and adds no filter, no lane-defining or profile-owned switch
/// appears anywhere under <c>.github/</c>, and the lanes' needs — a display, an upload, the full spec
/// set — are checked against what the registry says each lane is.
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

    /// <summary>The marker lines around the generated block in AGENTS.md.</summary>
    internal const string AgentsBlockBegin =
        "<!-- BEGIN GENERATED: lanes outside pre-push. Rendered from tests/TestProfiles/ and ci.yml by "
        + "CiProfileLaneTests.AgentsMd_LanesOutsidePrePush_AreGeneratedFromTheRegistry; edit those, not this. -->";

    internal const string AgentsBlockEnd = "<!-- END GENERATED: lanes outside pre-push -->";

    /// <summary>The marker lines around the generated profile list in AGENTS.md.</summary>
    internal const string AgentsProfilesBegin =
        "<!-- BEGIN GENERATED: profiles. Rendered from tests/TestProfiles/TestProfiles.cs by "
        + "CiProfileLaneTests.AgentsMd_ProfileList_IsGeneratedFromTheRegistry; edit that, not this. -->";

    internal const string AgentsProfilesEnd = "<!-- END GENERATED: profiles -->";

    /// <summary>
    /// <b>Every CI step that runs <c>BattleScribeSpec.Tests</c> names exactly one registry profile that
    /// covers it, and adds nothing to its selection.</b> A step that runs a profile names one that exists
    /// and covers the project it runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An inline <c>--filter</c> was a lane only CI knew: eight of them, two of which once selected
    /// nothing or only a self-skipping row and stayed green on every PR. On VSTest a command-line filter or
    /// settings file does not narrow a profile, it <b>replaces</b> it, so a profile plus a filter is the
    /// same unrecorded lane under a recorded name. The matrix is expanded, so
    /// <c>-TestProfile ${{ matrix.suite.profile }}</c> is two checked runs, and a typo in the key stays an
    /// expression that is not a profile.
    /// </para>
    /// <para>
    /// Mutation-checked when written: the checks job's offline step back on
    /// <c>--filter "Category!=Conformance"</c>; <c>--filter "DisplayName~kitchen-sink"</c> appended to a
    /// profiled step; and the matrix key misspelt (<c>${{ matrix.suite.profil }}</c>) each go red naming
    /// the step.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryCiTestStep_OnTheTestsProject_RunsAProfile()
    {
        var testsProject = CiTestInvocations.TestProjects.Single(static p => p.AssemblyName == EngineLanes.Assembly).RelativePath;
        var runs = CiProfileRuns.All;
        var onTests = runs.Where(r => r.Invocation.TargetsSolution || r.Invocation.Projects.Contains(testsProject)).ToList();

        // No separate canary that some run came from a matrix leg: a matrix step the expansion failed to
        // resolve still names `${{ matrix.… }}`, which is reported below as an unresolved profile, and
        // CiJob_ExpandsItsMatrixIntoLegs holds the expansion itself.
        Assert.True(onTests.Count >= 10, $"Found {onTests.Count} CI runs of {testsProject}; the scan has stopped finding the test steps.");

        var problems = new List<string>();
        foreach (var run in runs)
        {
            var runsTests = onTests.Contains(run);
            if (run.ProfileNames.Count == 0)
            {
                if (runsTests)
                {
                    problems.Add($"  {run.Where}: runs {EngineLanes.Assembly} with no profile — `{run.Command}`");
                }

                continue;
            }

            if (run.ProfileNames.Count > 1)
            {
                problems.Add($"  {run.Where}: names {run.ProfileNames.Count} profiles ({string.Join(", ", run.ProfileNames)})");
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

            if (run.SelectionOverrides.Count > 0)
            {
                problems.Add($"  {run.Where}: adds {string.Join(" ", run.SelectionOverrides)} to profile {profile.Name}");
            }
        }

        Assert.True(problems.Count == 0,
            "CI steps that run the test suite must run a registry profile, and only that:\n" + string.Join("\n", problems) + "\n\n"
            + $"A lane is defined in tests/TestProfiles/TestProfiles.cs, so that CI and `dotnet test -p:TestProfile=<name>` run the "
            + "same thing. On VSTest a command-line --filter or --settings REPLACES the profile's selection rather than narrowing "
            + "it, so a step that adds one runs a lane no profile records. Add a profile for what the step means, and run it with "
            + $"`pwsh {CiTestInvocations.TestStepScript} -TestProfile <name> {testsProject} --no-build …`.");
    }

    /// <summary>
    /// <b>No lane-defining switch, and no switch a profile sets, appears anywhere under <c>.github/</c></b>
    /// — not in a workflow's, a job's or a step's <c>env:</c>, not in a <c>$GITHUB_ENV</c> write, not on a
    /// command line, not in a composite action.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A lane-defining switch (<see cref="KnobKind.LaneDefining"/>) changes which tests run or whether a
    /// lane runs at all; set from a workflow, it defines the lane there instead of in the profile, which
    /// is how <c>nr-ui-frozen</c> meant 378 specs in CI and one everywhere else. A switch a profile sets is
    /// the profile's: a second value in CI is at best inert (on VSTest the profile's runsettings value
    /// wins) and at worst the one that counts once precedence changes. Every YAML scalar is read, keys
    /// included — an <c>env:</c> entry is a key, a <c>$GITHUB_ENV</c> write or an inline assignment is in a
    /// <c>run:</c> value — and comments are not scalars, so the workflows can still explain the rule.
    /// Default switches the registry leaves to CI (the NR Editor UI driver's diagnostics capture) are not
    /// covered.
    /// </para>
    /// <para>
    /// Mutation-checked when written: <c>NR_UI_ROSTER_FULL: 0</c> in a job-level <c>env:</c>;
    /// <c>echo NR_FROZEN_SMOKE=1 &gt;&gt; $GITHUB_ENV</c> in a step; and <c>NR_ENGINE_URL</c> set in the
    /// setup action's environment each go red naming the file and line.
    /// </para>
    /// </remarks>
    [Fact]
    public void LaneDefiningKnobs_AppearNowhereInGithub()
    {
        var banned = Knobs.All.Where(static k => k.Kind == KnobKind.LaneDefining).Select(static k => k.Name)
            .Concat(TestProfiles.All.SelectMany(static p => p.Env.Keys))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(static name => name, static name => string.Join("; ", new[]
            {
                Knobs.Find(name) is { Kind: KnobKind.LaneDefining } knob ? $"lane-defining: {knob.Why}" : null,
                TestProfiles.All.Where(p => p.Env.ContainsKey(name)).Select(static p => p.Name).ToList() is { Count: > 0 } owners
                    ? $"set by {(owners.Count == 1 ? "profile" : "profiles")} {string.Join(", ", owners)}"
                    : null,
            }.OfType<string>()), StringComparer.Ordinal);

        Assert.True(banned.Keys.Any(static k => Knobs.Find(k)?.Kind == KnobKind.LaneDefining),
            "No lane-defining switch is banned: tests/TestProfiles/Knobs.cs classifies none as LaneDefining, so this lint checks nothing.");

        var problems = GithubMentions(banned.Keys, nameof(LaneDefiningKnobs_AppearNowhereInGithub))
            .Select(m => $"  {m.Where}: {m.Name} ({banned[m.Name]})")
            .ToList();

        Assert.True(problems.Count == 0,
            "These switches belong to the test-profile registry, and CI sets or names them:\n" + string.Join("\n", problems) + "\n\n"
            + "A lane is its profile: tests/TestProfiles/TestProfiles.cs states every switch the lane depends on, and every "
            + "lane-defining switch a profile does not list must be unset in it. Set from CI, a switch makes the CI lane differ "
            + "from the profile of the same name — the full NR UI roster lane was one spec everywhere but CI for exactly this "
            + "reason. Put the value in the profile the step runs (or a new profile), and delete it here.");
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
    /// <b>The CI step named "Full frozen NR UI roster" runs the full spec set</b>: its profile sets
    /// <c>NR_UI_ROSTER_FULL</c>, and neither the every-push smoke lane nor <c>pre-push</c> does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That lane ran <b>one</b> spec for its entire life, on the since-falsified premise that the frozen
    /// HAR supports a single roster-creation flow per run; <c>docs/warm-reuse.md</c> records what that cost:
    /// "CI never caught the original bug because the NR-UI roster lane runs a single spec." It is the whole
    /// applicable suite now, but only when the switch is set, because the every-push lane and
    /// <c>pre-push</c> must stay fast — and an opt-in that is dropped is invisible: the step still passes,
    /// still says "Full", and covers one spec of ~378. One executed test is not zero, so the
    /// executed-at-least-one guard cannot see it either.
    /// </para>
    /// <para>
    /// The switch used to be the step's <c>env:</c>, so the profile of the same name ran one spec
    /// everywhere else. It is the profile's now (<c>nr-ui-frozen</c>), and a profile run in any job outside
    /// the thorough set may not set it.
    /// </para>
    /// <para>
    /// Mutation-checked when written: <c>NR_UI_ROSTER_FULL</c> removed from <c>nr-ui-frozen</c>; set on
    /// <c>smoke-nr-ui</c>; and the step pointed at <c>smoke-nr-ui</c> — each goes red.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThoroughNrUiRosterStep_RunsTheFullSpecSet()
    {
        const string full = FrozenNrUiRosterConformanceTests.FullVariable;
        static bool SetsFull(TestProfile p) => p.Env.GetValueOrDefault(full) is "1" or "true";

        var steps = CiProfileRuns.All.Where(static r => r.Step.Job.Workflow == CiFile && r.Step.Name == "Full frozen NR UI roster").ToList();
        Assert.True(steps.Count == 1,
            $"{CiFile} has {steps.Count} runs in steps named 'Full frozen NR UI roster', not one. If it was renamed, update this guard; "
            + "if it was deleted, the thorough NR UI roster coverage went with it.");

        var problems = new List<string>();
        var step = steps[0];
        if (step.Profile is not { } profile || !step.Lanes.Any(static l => l.Trait == "FrozenNrUiRoster"))
        {
            problems.Add($"  {step.Where} runs '{string.Join(", ", step.ProfileNames)}', which is not a profile that runs FrozenNrUiRoster");
        }
        else if (!SetsFull(profile))
        {
            problems.Add($"  {step.Where} runs profile {profile.Name}, which does not set {full}=1: the lane runs kitchen-sink alone");
        }

        if (TestProfiles.Find("nr-ui-frozen") is not { } fullLane || !SetsFull(fullLane))
        {
            problems.Add($"  profile nr-ui-frozen does not set {full}=1, so the name of the full lane runs one spec");
        }

        string[] fastLanes = ["smoke-nr-ui", "pre-push"];
        foreach (var name in fastLanes)
        {
            if (TestProfiles.Find(name) is { } p && p.Env.GetValueOrDefault(full) is not null)
            {
                problems.Add($"  profile {name} sets {full}: a fast lane would run every applicable spec (~27 minutes)");
            }
        }

        var thorough = CiGateConfig.Load().ThoroughJobs.ToHashSet(StringComparer.Ordinal);
        problems.AddRange(CiProfileRuns.All
            .Where(r => r.Profile is { } p && SetsFull(p) && !(r.Step.Job.Workflow == CiFile && thorough.Contains(r.Step.Job.Id)))
            .Select(static r => $"  {r.Where} runs {r.Profile!.Name}, the full NR UI roster lane, outside the thorough jobs"));

        Assert.True(problems.Count == 0,
            "The full frozen NR UI roster lane must be exactly where it is meant to be:\n" + string.Join("\n", problems) + "\n\n"
            + $"{full} turns FrozenNrUiRosterConformanceTests from kitchen-sink into every applicable spec. A lane that "
            + "silently shrinks from ~378 specs to 1 still exits 0, and one that silently grows puts ~27 minutes on every push.");
    }

    /// <summary>
    /// <b>AGENTS.md's table of lanes outside <c>pre-push</c> is generated</b> from the registry and the
    /// workflow, and every engine lane is either run by a CI step's profile or carries a
    /// <see cref="EngineLane.CiExempt"/> reason — not both, not neither.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hand-written table it replaces claimed coverage it did not check: <c>LiveNr*</c>, "covered in CI
    /// by <c>nr-conformance</c>", when that job runs one of the four live lanes. The rows are the lanes not
    /// marked <see cref="EngineLane.InPrePush"/>, in registry order; each says why it is out, the profiles
    /// that run it, and the CI jobs that run it (with the gate output that turns the job on, from
    /// <c>scripts/ci-gate.json</c>) — or the lane's reason for not being in CI. A job runs a lane when a
    /// step runs a profile that claims it, and the cell also names the <c>bs-spec</c> steps that drive the
    /// lane's driver (<see cref="CiProfileRuns.CliUi"/>): smoke drives the BattleScribe roster UI driver on
    /// every push, and a table that said only "thorough" would tell a reader the opposite. The
    /// run-XOR-exempt rule counts profile runs alone: a <c>bs-spec</c> step drives the driver over the
    /// specs it names, not the lane's test classes, so it does not excuse a lane from CI. On a mismatch
    /// the message carries the block to paste between the markers.
    /// </para>
    /// <para>
    /// Mutation-checked when written: a row edited by hand in AGENTS.md; a <c>CiExempt</c> added to
    /// <c>LiveNrRoster</c>, which CI runs; and <c>LiveNrUiRoster</c>'s removed — each goes red.
    /// </para>
    /// </remarks>
    [Fact]
    public void AgentsMd_LanesOutsidePrePush_AreGeneratedFromTheRegistry()
    {
        var problems = new List<string>();
        foreach (var lane in EngineLanes.All)
        {
            var jobs = CiJobsRunning(lane);
            if (jobs.Count > 0 && lane.CiExempt is not null)
            {
                problems.Add($"  {lane.Trait} carries CiExempt, but CI runs it ({string.Join("; ", jobs.Select(static j => j.Job))}): delete the exemption");
            }
            else if (jobs.Count == 0 && string.IsNullOrWhiteSpace(lane.CiExempt))
            {
                problems.Add($"  {lane.Trait}: no CI step runs a profile that claims it, and it has no CiExempt saying why");
            }
        }

        problems.AddRange(GeneratedBlockProblems(AgentsBlockBegin, AgentsBlockEnd, RenderLanesOutsidePrePush(), "where the table belongs"));

        Assert.True(problems.Count == 0,
            "Which lanes pre-push leaves out, and who runs them instead, is the registry's to say:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// <b>AGENTS.md's list of profiles is generated from the registry</b>: every profile, in registry order,
    /// with the test assemblies it covers when that is not <c>BattleScribeSpec.Tests</c> alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It was a hand-kept sentence of backticked names, and that is the one place a deleted profile is
    /// most likely to linger and a reference lint is least able to see: a bare <c>`nr-live-visible`</c>
    /// carries no <c>TestProfile=</c> to recognise it by, and this layer had to take two names out of it
    /// by hand. Generated, the list cannot name a profile the registry lacks, and it cannot leave one out —
    /// which is also the orphan rule: a profile nothing documents is a lane nobody can find, and this list
    /// documents every one where AGENTS.md says they are listed.
    /// </para>
    /// <para>
    /// Mutation-checked when written: a deleted profile's name put back in the list; and a new profile
    /// added to the registry (an orphan) — each goes red, printing the block to paste.
    /// </para>
    /// </remarks>
    [Fact]
    public void AgentsMd_ProfileList_IsGeneratedFromTheRegistry()
    {
        var problems = GeneratedBlockProblems(AgentsProfilesBegin, AgentsProfilesEnd, RenderProfileList(), "where the profiles are listed");
        Assert.True(problems.Count == 0,
            "Which profiles exist is the registry's to say (tests/TestProfiles/TestProfiles.cs):\n" + string.Join("\n", problems));
    }

    /// <summary>What is wrong with the generated block between <paramref name="begin"/> and <paramref name="end"/> in AGENTS.md: absent, or not <paramref name="expected"/>.</summary>
    private static List<string> GeneratedBlockProblems(string begin, string end, string expected, string where)
    {
        var agents = File.ReadAllText(Path.Combine(CiWorkflows.Root, "AGENTS.md")).ReplaceLineEndings("\n");
        var from = agents.IndexOf(begin, StringComparison.Ordinal);
        var to = agents.IndexOf(end, StringComparison.Ordinal);
        if (from < 0 || to < from)
        {
            return [$"  AGENTS.md lacks a generated block, or one of its markers; put these lines {where}:\n{begin}\n{expected}{end}"];
        }

        return agents[(from + begin.Length + 1)..to] == expected
            ? []
            : [$"  AGENTS.md's generated block differs from the registry; replace the lines between the markers with:\n{expected}"];
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
    /// job name, so a recipe names a profile the way it is run. The one place that lists profiles by bare
    /// name is AGENTS.md's list, which is generated (<see cref="AgentsMd_ProfileList_IsGeneratedFromTheRegistry"/>)
    /// and so can neither name a deleted profile nor leave a new one out — the orphan rule this lint once
    /// carried, a profile nothing documents being a lane nobody can find.
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
        var root = CiWorkflows.Root;
        var superpowers = Path.Combine(root, "docs", "superpowers") + Path.DirectorySeparatorChar;
        var documents = new[] { Path.Combine(root, "AGENTS.md"), Path.Combine(root, "README.md") }
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
                .Where(f => !f.StartsWith(superpowers, StringComparison.OrdinalIgnoreCase)))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, ".agents", "skills"), "*.md", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal)
            .Select(f => (Path: CiWorkflows.Relative(f), Text: File.ReadAllText(f)))
            .Concat(CiDefinitionFiles.All.Select(static f => (f.Path, f.Text)))
            .ToList();
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
            + "exists (tests/TestProfiles/TestProfiles.cs; AGENTS.md lists them all).");
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
                  - run: pwsh scripts/dotnet-test-step.ps1 -TestProfile ${{ matrix.suite.profile }} tests/BattleScribeSpec.Tests.csproj
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
        Assert.Contains("-TestProfile bs-ui-roster tests/", CiWorkflows.ExpandMatrix(step, legs[2]), StringComparison.Ordinal);
        Assert.Contains("${{ matrix.suite.profil }}", CiWorkflows.ExpandMatrix("${{ matrix.suite.profil }}", legs[2]), StringComparison.Ordinal);

        Assert.Empty(Assert.Single(workflow.Job("none").MatrixCombinations()));
        Assert.Throws<NotSupportedException>(() => workflow.Job("with-include").MatrixCombinations());
    }

    /// <summary>
    /// <b>A command line's profile, display wrapper and selection overrides are read in every spelling</b>
    /// the step script, <c>dotnet test</c> and the test app accept; <c>xvfb-run</c>'s own options are not
    /// the runner's.
    /// </summary>
    [Theory]
    [InlineData("pwsh scripts/dotnet-test-step.ps1 -TestProfile core tests/BattleScribeSpec.Tests.csproj --no-build", "core", false, "")]
    [InlineData("pwsh scripts/dotnet-test-step.ps1 -testprofile:core tests/BattleScribeSpec.Tests.csproj", "core", false, "")]
    [InlineData("xvfb-run -a -s \"-screen 0 1x1x24\" pwsh scripts/dotnet-test-step.ps1 -TestProfile bs-ui-roster x.csproj", "bs-ui-roster", true, "")]
    [InlineData("dotnet test tests/BattleScribeSpec.Tests.csproj -p:TestProfile=lint --filter \"Engine=X\"", "lint", false, "--filter")]
    [InlineData("dotnet run --project tests/BattleScribeSpec.Tests.csproj --no-build -- --test-profile=bs --filter-class X", "bs", false, "--filter-class")]
    [InlineData("dotnet test x.csproj -s my.runsettings -- RunConfiguration.TestCaseFilter=A", "", false, "-s RunConfiguration.TestCaseFilter=A")]
    [InlineData("dotnet test x.csproj /p:VSTestTestCaseFilter=A -p:TestProfile=core", "core", false, "-p:VSTestTestCaseFilter=A")]
    [InlineData("pwsh scripts/dotnet-test-step.ps1 -TestProfile smoke-nr-ui x.csproj --filter:DisplayName~kitchen-sink --settings:a.runsettings -s:b -s=c --filter-class:X",
        "smoke-nr-ui", false, "--filter:DisplayName~kitchen-sink --settings:a.runsettings -s:b -s=c --filter-class:X")]
    [InlineData("dotnet test x.csproj -p:TestProfile=core --filter=A --settings=b -screenshots", "core", false, "--filter=A --settings=b")]
    public void CiProfileRuns_ReadTheProfileAndOverridesOffTheLine(string command, string profiles, bool xvfb, string overrides)
    {
        Assert.Equal(profiles, string.Join(",", CiProfileRuns.ProfilesNamedBy(command)));
        Assert.Equal(xvfb, CiProfileRuns.RunsUnderXvfb(command));
        Assert.Equal(overrides, string.Join(" ", CiProfileRuns.SelectionOverridesIn(command)));
    }

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

    /// <summary>The CI jobs in ci.yml whose steps run a profile that claims <paramref name="lane"/>, in job order, with those profiles.</summary>
    private static List<(string Job, IReadOnlyList<string> Profiles)> CiJobsRunning(EngineLane lane)
    {
        var order = CiWorkflows.Ci.Jobs.Select(static (j, i) => (j.Id, i)).ToDictionary(static x => x.Id, static x => x.i, StringComparer.Ordinal);
        var jobs = CiProfileRuns.All
            .Where(r => r.Step.Job.Workflow == CiFile && r.Lanes.Contains(lane))
            .GroupBy(static r => r.Step.Job.Id)
            .OrderBy(g => order[g.Key]);
        return [.. jobs.Select(static g => (g.Key, (IReadOnlyList<string>)[.. g.Select(static r => r.Profile!.Name).Distinct(StringComparer.Ordinal)]))];
    }

    /// <summary>
    /// The generated table: one row per lane not in pre-push, each line ending in a newline. The CI cell
    /// names every job that runs the lane — through a profile, or through a <c>bs-spec</c> step that drives
    /// its driver — in job order, with what each job runs and the gate output that turns it on.
    /// </summary>
    internal static string RenderLanesOutsidePrePush()
    {
        var gate = CiGateConfig.Load();
        var order = CiWorkflows.Ci.Jobs.Select(static (j, i) => (j.Id, i)).ToDictionary(static x => x.Id, static x => x.i, StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.Append("| Not in `pre-push` | Why | Run it with | Run in CI by |\n");
        sb.Append("|---|---|---|---|\n");
        foreach (var lane in EngineLanes.All.Where(static l => !l.InPrePush))
        {
            var profiles = TestProfiles.All
                .Where(p => p.Selection.Claims.Contains(lane.Trait, StringComparer.Ordinal) && !p.MaySkip.Any(m => m.Engine == lane.Trait))
                .Select(static p => $"`-p:TestProfile={p.Name}`");
            var driving = CiJobsRunning(lane)
                .Select(static j => (j.Job, Runs: j.Profiles.Select(static p => $"`{p}`")))
                .Concat(CiProfileRuns.CliUi
                    .Where(r => r.Step.Job.Workflow == CiFile && r.Lane == lane)
                    .GroupBy(static r => r.Step.Job.Id)
                    .Select(static g => (Job: g.Key, Runs: g.Select(static r => $"`bs-spec {string.Join(" ", r.Arguments)}`").Distinct(StringComparer.Ordinal))))
                .GroupBy(static j => j.Job)
                .OrderBy(g => order[g.Key])
                .Select(g => $"`{g.Key}` ({string.Join(", ", g.SelectMany(static j => j.Runs))}; "
                    + (gate.ThoroughJobs.Contains(g.Key) ? "thorough" : gate.LiveJobs.Contains(g.Key) ? "live" : "every push") + ")")
                .ToList();
            var ci = (lane.CiExempt, driving.Count) switch
            {
                (null, _) => string.Join("; ", driving),
                (var exempt, 0) => $"not in CI: {exempt}",
                (var exempt, _) => $"not in CI: {exempt} Its driver alone: {string.Join("; ", driving)}",
            };
            sb.Append($"| `{lane.Trait}` | {Cell(lane.Why)} | {string.Join(", ", profiles)} | {Cell(ci)} |\n");
        }

        return sb.ToString();

        static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
    }

    /// <summary>
    /// The generated profile list: every profile in registry order, with the assemblies it covers when
    /// that is not <see cref="TestProfiles.Tests"/> alone, wrapped as AGENTS.md wraps its prose; each line
    /// ends in a newline.
    /// </summary>
    internal static string RenderProfileList()
    {
        var items = TestProfiles.All.Select(static p => p.Assemblies is [TestProfiles.Tests]
            ? $"`{p.Name}`"
            : $"`{p.Name}` ({string.Join(" and ", p.Assemblies)}{(p.Assemblies.Count == 1 ? " only" : "")})");
        var words = $"Every profile ({TestProfiles.All.Count}), in registry order: {string.Join(", ", items)}.".Split(' ');

        var sb = new StringBuilder();
        var line = new StringBuilder();
        foreach (var word in words)
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > 100)
            {
                sb.Append(line).Append('\n');
                line.Clear();
            }

            line.Append(line.Length > 0 ? " " : "").Append(word);
        }

        return sb.Append(line).Append('\n').ToString();
    }

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
