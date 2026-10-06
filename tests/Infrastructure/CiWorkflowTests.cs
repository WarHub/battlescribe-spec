using System.Globalization;
using System.Text.RegularExpressions;
using BattleScribeSpec.Tests.Profiles;
using YamlDotNet.RepresentationModel;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>A CI step that runs a test project or <c>bs-spec</c> is one of four fixed lines, and what those
/// lines run is checked against the profile registry.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>Shapes, not a shell parser.</b> These lints used to find the test steps by classifying arbitrary
/// shell — every verb and flag order, <c>pwsh -c</c>/<c>bash -c</c> wrappers, working directories, scripts
/// a step called, a matrix expanded leg by leg — and the classifier and the rules on it grew to ~3,900
/// lines (#533).
/// Now any <c>run:</c> the <see cref="Marker"/> matches, in a workflow or a composite action, must be
/// exactly one of the allowed lines (<see cref="EveryVerdictStep_HasAnAllowedShape"/>), and anything else
/// is rejected outright. The marker over-matches on purpose: a false red costs one rewrite to an allowed
/// shape, and a wrapper (<c>pwsh -c "dotnet run …"</c>) contains the marker and matches no shape.
/// </para>
/// <para>
/// Each test here is backed by a failure this repository has had, named on the test, that nothing else
/// catches. What already fails loudly at run time — <c>TESTINGPLATFORM_EXITCODE_IGNORE</c> (the host refuses
/// it), a lane that executed nothing (exit 8), a desktop-app lane with no display — is left to run time.
/// </para>
/// </remarks>
[Trait("Category", "Lint")]
public sealed class CiWorkflowTests
{
    private const string CiFile = ".github/workflows/ci.yml";

    /// <summary>What makes a <c>run:</c> a verdict step that must have an allowed shape. Broad on purpose.</summary>
    private static readonly Regex Marker = new(
        @"\bdotnet\s+test\b|test-?profile|BattleScribeSpec\.(?:Tests|Cli)\b|bs-spec\.dll", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>T1, and T1x under <c>xvfb-run -a</c>: a profile run through the test app.</summary>
    private static readonly Regex ProfileRun = new(
        @"^(?:xvfb-run -a )?dotnet run --project (?<p>\S+) --no-build -- --test-profile (?<n>[a-z0-9]+(?:-[a-z0-9]+)*|\$\{\{ matrix\.suite\.profile \}\})$",
        RegexOptions.CultureInvariant);

    /// <summary>T2: the one <c>dotnet test</c> line, kept so CI exercises the MSBuild channel (the <c>cli</c> profile's Purpose).</summary>
    private static readonly Regex DotnetTest = new(
        @"^dotnet test --project (?<p>\S+) --no-build -p:TestProfile=(?<n>[a-z0-9]+(?:-[a-z0-9]+)*) --output Detailed$", RegexOptions.CultureInvariant);

    /// <summary>C: a <c>bs-spec</c> verdict, by its built dll or under <c>xvfb-run</c>; nothing that chains a second command.</summary>
    private static readonly Regex CliRun = new(
        @"^(?:dotnet artifacts/bin/BattleScribeSpec\.Cli/debug/bs-spec\.dll|xvfb-run -a dotnet run --project src/BattleScribeSpec\.Cli --no-build --) [^|;&`\n]+$",
        RegexOptions.CultureInvariant);

    private static readonly string[] StepKeys = ["name", "if", "timeout-minutes", "run", "env"];

    private const string Independent = "${{ !cancelled() && steps.build.outcome == 'success' }}";

    /// <summary>The job whose steps are sequential on purpose: it drives newrecruit.eu, and a lane that found the site down must not be followed by more.</summary>
    private const string SequentialJob = "nr-conformance";

    private const double GitHubDefaultTimeoutMinutes = 360;

    private const string Shapes =
        "  dotnet run --project <csproj> --no-build -- --test-profile <profile>   (optionally after `xvfb-run -a `)\n"
        + "  dotnet test --project <csproj> --no-build -p:TestProfile=<profile> --output Detailed\n"
        + "  dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll <args>\n"
        + "  xvfb-run -a dotnet run --project src/BattleScribeSpec.Cli --no-build -- <args>\n"
        + "where <csproj> is a value of TestProfiles.Projects, <profile> a profile that covers it (or ${{ matrix.suite.profile }}), "
        + "and <args> holds no | ; & or backtick. The step's keys are name, if, timeout-minutes, run and env only; its "
        + "timeout-minutes is below its job's; and its condition is `if: " + Independent + "` (outside " + SequentialJob + ").";

    /// <summary>
    /// <b>Every verdict step has an allowed shape</b>, the keys <c>name</c>, <c>if</c>,
    /// <c>timeout-minutes</c>, <c>run</c> and <c>env</c> only, a <c>timeout-minutes</c> below its job's, and
    /// <c>if: ${{ !cancelled() &amp;&amp; steps.build.outcome == 'success' }}</c> outside
    /// <see cref="SequentialJob"/>; a test run's project is a test project and its profile covers it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The anchored lines leave no room for a <c>--filter</c> (#330: two CI lanes ran 0 and 1 tests under
    /// inline filters and passed), a pipe or a <c>|| true</c>, an expression for the project, or a second
    /// command; the key list leaves none for <c>continue-on-error</c>, <c>working-directory</c> or
    /// <c>shell</c> — which a composite action's <c>run</c> step must carry, so no verdict step fits in one.
    /// </para>
    /// <para>
    /// The step bound is for a hang: with none, "Full frozen NR UI roster" ran into its job's bound six
    /// times (runs 32334364162 … 35079735082), and the cancellation skipped the <c>failure()</c> upload of
    /// its diagnostics. The condition is for a red lane: under the default <c>success()</c>, a failed NR UI
    /// roster lane skipped both NR Editor lanes of PR #338, which was reported red without their verdicts.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryVerdictStep_HasAnAllowedShape()
    {
        var steps = CiWorkflows.AllSteps.ToList();
        CiDefinitionFiles.AssertReadAnAction(steps.Select(static s => s.Job.Workflow), nameof(EveryVerdictStep_HasAnAllowedShape));

        var problems = Read(steps).Problems;
        Assert.True(problems.Count == 0,
            "These CI steps run tests or bs-spec in a shape the workflow lints do not allow:\n" + string.Join("\n", problems) + "\n\n"
            + "A verdict step is exactly one of:\n" + Shapes);
    }

    /// <summary>
    /// <b><c>ci-gate</c> needs every other job in <c>ci.yml</c>.</b> It is the one required check, and a
    /// job it does not need is a job whose failure nobody is told about.
    /// </summary>
    /// <remarks>
    /// The issue the weekly run opens on failure used to list its jobs by hand and had dropped
    /// <c>docker</c>; the verdict table had once dropped <c>gate</c> itself, so a failed gate skipped the
    /// thorough lanes and the aggregate read green. Mutation-checked: delete <c>docker</c> from
    /// <c>ci-gate.needs</c> and this goes red naming it.
    /// </remarks>
    [Fact]
    public void CiGate_NeedsEveryOtherJob()
    {
        var ci = CiWorkflows.Ci;
        var needs = ci.Job("ci-gate").Needs.ToHashSet(StringComparer.Ordinal);
        var others = ci.Jobs.Select(static j => j.Id).Where(static id => id != "ci-gate").ToHashSet(StringComparer.Ordinal);

        Assert.True(others.Count >= 2, $"ci.yml has {others.Count} job(s) besides ci-gate — the parse found nothing to check.");

        var missing = others.Except(needs).Order(StringComparer.Ordinal).ToArray();
        var phantom = needs.Except(others).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            missing.Length == 0 && phantom.Length == 0,
            $"ci-gate's `needs:` and ci.yml's jobs disagree.\n" +
            (missing.Length > 0 ? $"  Not needed by ci-gate (their result is never checked): {string.Join(", ", missing)}\n" : "") +
            (phantom.Length > 0 ? $"  Needed but not a job in ci.yml: {string.Join(", ", phantom)}\n" : "") +
            "\nci-gate is the single required check; scripts/ci-gate.mjs can only judge the jobs it is given. " +
            "Add the job to `needs:` (and to scripts/ci-gate.json if the gate turns it on or off).");
    }

    /// <summary><b>Every test project is run by a CI step</b>: each <see cref="TestProfiles.Projects"/> value is some verdict step's project.</summary>
    /// <remarks>
    /// <c>tests/BattleScribeSpec.Cli.Tests</c> was added in #262 and never ran in CI until #314, while it held
    /// every gate on the CLI's third-party load limit. A gate nobody invokes is a gate nobody has.
    /// </remarks>
    [Fact]
    public void EveryTestProject_IsRunByCi()
    {
        var run = Read(CiWorkflows.AllSteps).Verdicts.Select(static v => v.Assembly).ToHashSet(StringComparer.Ordinal);
        var unrun = TestProfiles.Projects.Where(p => !run.Contains(p.Key)).Select(static p => $"  {p.Value}").ToList();

        Assert.True(unrun.Count == 0,
            "These test projects are run by no CI step:\n" + string.Join("\n", unrun) + "\n\n"
            + "A test project nobody runs is a suite that passes on its author's machine and has never been executed by CI. "
            + "Give it a step that runs a profile covering it, or delete the project.");
    }

    /// <summary>
    /// <b>Every engine lane is run by a <c>ci.yml</c> step's profile or carries a
    /// <see cref="EngineLane.CiExempt"/> reason.</b> A profile runs the lanes it claims, less those it lets
    /// skip (<see cref="TestProfile.MaySkip"/>); a <c>bs-spec</c> step drives a driver, not the lane's tests.
    /// </summary>
    /// <remarks>
    /// <c>BsRosterUiConformanceTests</c> ran nowhere (#355, fixed in #378), so every change to
    /// <c>BsUiRosterEngine</c> and <c>RosterActions.java</c> reached main with only unit tests behind it.
    /// </remarks>
    [Fact]
    public void EveryEngineLane_IsRunByCi_OrSaysWhyNot()
    {
        var run = Read(CiWorkflows.AllSteps).Verdicts
            .Where(static v => v.Step.Job.Workflow == CiFile)
            .SelectMany(Lanes)
            .ToHashSet();
        var problems = EngineLanes.All
            .Where(l => !run.Contains(l) && string.IsNullOrWhiteSpace(l.CiExempt))
            .Select(static l => $"  {l.Trait}: no CI step runs a profile that claims it, and it has no CiExempt saying why")
            .ToList();

        Assert.True(problems.Count == 0,
            "Which lanes CI runs, and why the others are not, is the registry's to say:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// <b>A UI lane's failure diagnostics leave the runner.</b> Every job with a step whose profile runs a
    /// lane with a <see cref="EngineLane.DiagnosticsDir"/> uploads that directory, and sets the lane's
    /// <see cref="EngineLane.DiagnosticsSwitch"/> on the step, the job or the workflow.
    /// </summary>
    /// <remarks>
    /// The thorough job that ran the NR UI drivers over the full spec set uploaded none of their dumps
    /// (#414), so <c>Timeout 20000ms exceeded.</c> was the whole record of a failure; and <c>smoke</c>
    /// uploaded nothing, with no step setting <c>NR_GAMEDATA_UI_DIAGNOSTICS</c>, so even an upload would
    /// have collected nothing (#509).
    /// </remarks>
    [Fact]
    public void EveryUiLane_UploadsTheDiagnosticsItWrites()
    {
        var problems = new List<string>();
        foreach (var verdict in Read(CiWorkflows.AllSteps).Verdicts)
        {
            var (step, job) = (verdict.Step, verdict.Step.Job);
            var workflow = CiWorkflows.All.SingleOrDefault(w => w.File == job.Workflow);
            var uploaded = job.Steps
                .Where(static s => s.Uses?.StartsWith("actions/upload-artifact@", StringComparison.Ordinal) == true)
                .SelectMany(static s => (s.With("path") ?? "").Split('\n'))
                .Select(static p => p.Trim().TrimEnd('/'))
                .ToList();
            foreach (var lane in Lanes(verdict).Where(static l => l.DiagnosticsDir is not null))
            {
                var dir = lane.DiagnosticsDir!;
                if (!uploaded.Any(p => p == dir || p == $"{dir}*"))
                {
                    problems.Add($"  {step.Where}: runs {lane.Trait}, whose driver writes {dir}, but job {job.Id} uploads no such path");
                }

                if (lane.DiagnosticsSwitch is { } capture
                    && CiWorkflows.Env(step.Node, capture) is not { Length: > 0 }
                    && CiWorkflows.Env(job.Node, capture) is not { Length: > 0 }
                    && (workflow is null || CiWorkflows.Env(workflow.Node, capture) is not { Length: > 0 }))
                {
                    problems.Add($"  {step.Where}: runs {lane.Trait} without {capture} set on the step, its job or the workflow, so the driver writes nothing to {dir}");
                }
            }
        }

        Assert.True(problems.Count == 0,
            "These CI jobs run a UI driver whose diagnostics never leave the runner:\n" + string.Join("\n", problems.Distinct()) + "\n\n"
            + "Add an `actions/upload-artifact` step for the directory (with `*` for the per-worker suffixes), and set the driver's "
            + "capture switch where it runs. The directories and switches come from tests/TestProfiles/EngineLanes.cs.");
    }

    /// <summary>
    /// <b>No switch a profile sets appears anywhere under <c>.github/</c></b> — not in a workflow's, a
    /// job's or a step's <c>env:</c>, not in a <c>$GITHUB_ENV</c> write, not on a command line, not in a
    /// composite action.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A switch a profile sets is the profile's — the live lanes' <c>NR_ENGINE_URL</c> and
    /// <c>NR_EDITOR_URL</c>. Set in CI, it wins over the profile's value, as a caller's value does; set
    /// where the <c>checks</c> job's <c>non-conformance</c> step sees it, it sends the live smoke and
    /// integration tests that lane selects to newrecruit.eu on every push. Every YAML scalar is read, keys
    /// included, and comments are not scalars, so the workflows can still explain the rule.
    /// </para>
    /// <para>
    /// Mutation-checked when written: <c>NR_ENGINE_URL</c> set in the setup action's environment goes
    /// red naming the file and line.
    /// </para>
    /// </remarks>
    [Fact]
    public void ProfileSwitches_AppearNowhereInGithub()
    {
        var owners = TestProfiles.All
            .SelectMany(static p => p.Env.Keys.Select(k => (Key: k, p.Name)))
            .GroupBy(static e => e.Key, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => string.Join(", ", g.Select(static e => e.Name)), StringComparer.Ordinal);
        Assert.True(owners.Count > 0, "No profile sets a switch; this lint checks nothing.");

        var problems = GithubMentions(owners.Keys, nameof(ProfileSwitches_AppearNowhereInGithub))
            .Select(m => $"  {m.Where}: {m.Name} (set by {owners[m.Name]})")
            .ToList();

        Assert.True(problems.Count == 0,
            "These switches belong to the test-profile registry, and CI sets or names them:\n" + string.Join("\n", problems) + "\n\n"
            + "A lane is its profile: tests/TestProfiles/TestProfiles.cs states every switch the lane depends on, and a value "
            + "set from CI wins over the profile's. Put the value in the profile the step runs (or a new profile), and delete it here.");
    }

    /// <summary>A step with an allowed shape; for a test run, the assembly it runs and the profiles it names (both legs, for a matrix).</summary>
    private sealed record Verdict(CiStep Step, string? Assembly, IReadOnlyList<TestProfile> Profiles);

    /// <summary>The lanes a verdict step runs: what its profiles claim and do not let skip, when it runs the assembly the lanes live in.</summary>
    private static IEnumerable<EngineLane> Lanes(Verdict verdict) =>
        verdict.Assembly == EngineLanes.Assembly
            ? verdict.Profiles
                .SelectMany(static p => p.Selection.Claims.Where(c => !p.MaySkip.Any(m => m.Engine == c)))
                .Select(EngineLanes.Find)
                .OfType<EngineLane>()
            : [];

    /// <summary>Every step the <see cref="Marker"/> matches, held to the shapes; the ones that fit, and what is wrong with the rest.</summary>
    private static (List<Verdict> Verdicts, List<string> Problems) Read(IEnumerable<CiStep> steps)
    {
        var verdicts = new List<Verdict>();
        var problems = new List<string>();
        foreach (var step in steps)
        {
            if (step.Run is not { } written || !Marker.IsMatch(written))
            {
                continue;
            }

            var run = written.Trim();
            var test = ProfileRun.Match(run) is { Success: true } profileRun ? profileRun : DotnetTest.Match(run);
            if (!test.Success && !CliRun.IsMatch(Regex.Replace(run, @"[ \t]*\\\n[ \t]*", " ")))
            {
                problems.Add($"  {step.Where}: `{run}` is none of the shapes");
                continue;
            }

            var where = step.Where;
            problems.AddRange(step.Keys.Except(StepKeys).Select(k => $"  {where}: `{k}:` is not a key a verdict step may carry"));

            var bound = CiWorkflows.Minutes(step.Job.TimeoutMinutes) ?? GitHubDefaultTimeoutMinutes;
            if (CiWorkflows.Minutes(step.TimeoutMinutes) is not { } minutes || minutes >= bound)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"  {where}: timeout-minutes {step.TimeoutMinutes ?? "(none)"} is not below its job's {bound}"));
            }

            if (step.If?.Trim() != Independent && !(step.Job.Workflow == CiFile && step.Job.Id == SequentialJob))
            {
                problems.Add($"  {where}: if: {step.If ?? "(none — the default success(), so it waits on every step above it)"}");
            }

            if (!test.Success)
            {
                verdicts.Add(new(step, null, []));
                continue;
            }

            var project = test.Groups["p"].Value;
            var assembly = TestProfiles.Projects.SingleOrDefault(p => p.Value == project).Key;
            if (assembly is null)
            {
                problems.Add($"  {where}: --project {project} is not a test project (TestProfiles.Projects)");
            }

            var named = test.Groups["n"].Value;
            var names = named.StartsWith("${{", StringComparison.Ordinal) ? MatrixProfiles(step.Job) : [named];
            if (names.Count == 0)
            {
                problems.Add($"  {where}: runs {named}, and the job has no strategy.matrix.suite list to resolve it");
            }

            var profiles = new List<TestProfile>();
            foreach (var name in names)
            {
                if (TestProfiles.Find(name) is not { } profile)
                {
                    problems.Add($"  {where}: names profile '{name}', which tests/TestProfiles/TestProfiles.cs does not have");
                    continue;
                }

                if (assembly is not null && !profile.Assemblies.Contains(assembly, StringComparer.Ordinal))
                {
                    problems.Add($"  {where}: runs {assembly} under profile {name}, which does not cover it");
                }

                profiles.Add(profile);
            }

            verdicts.Add(new(step, assembly, profiles));
        }

        return (verdicts, problems);
    }

    /// <summary>What <c>${{ matrix.suite.profile }}</c> resolves to: the <c>profile</c> of each <c>strategy.matrix.suite</c> entry.</summary>
    private static List<string> MatrixProfiles(CiJob job) =>
        job.Node.Children.TryGetValue(new YamlScalarNode("strategy"), out var s) && s is YamlMappingNode strategy
        && strategy.Children.TryGetValue(new YamlScalarNode("matrix"), out var m) && m is YamlMappingNode matrix
        && matrix.Children.TryGetValue(new YamlScalarNode("suite"), out var suite) && suite is YamlSequenceNode legs
            ? [.. legs.Children.Select(static l => l is YamlMappingNode leg ? CiWorkflows.Scalar(leg, "profile") ?? "" : "")]
            : [];

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
