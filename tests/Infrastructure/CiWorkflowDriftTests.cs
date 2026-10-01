using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using YamlDotNet.RepresentationModel;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>The CI definition, held to the claims it makes.</b> Every way a workflow can go green while
/// checking less than it says — a skipped job the gate cannot explain, a hang with no bound, a test
/// phase skipped by a typo, an exit code swallowed by a pipe — is a structural property of the YAML,
/// so it is asserted here, on the parsed file, rather than remembered.
/// </summary>
/// <remarks>
/// <para>
/// Each test names the defect it exists for and how it was mutation-checked: the rule was broken on
/// purpose and this class went red naming the job or step. A lint that cannot fail is not a lint.
/// </para>
/// <para>
/// Test steps are found by <see cref="CiTestInvocations"/> — by the project they run, not by how the
/// line is spelled — and the jobs the gate turns on are read from <c>scripts/ci-gate.json</c>, the same
/// file the gate and <c>ci-gate</c> scripts read. The steps include the composite actions' under
/// <c>.github/actions</c> (<see cref="CiDefinitionFiles"/>), where every job's setup lives.
/// </para>
/// </remarks>
[Trait("Category", "Lint")]
public sealed class CiWorkflowDriftTests
{
    private const string GateJob = "gate";
    private const string CiGateJob = "ci-gate";
    private const string ChecksJob = "checks";
    private const string CiFile = ".github/workflows/ci.yml";

    /// <summary>The switch in the root <c>Directory.Build.props</c> that turns the build-time gates off.</summary>
    private const string FunctionalBuildProperty = "FunctionalBuild";

    /// <summary>
    /// What the switch must turn off, at least — the three gates <c>checks</c> exists to run. The
    /// <c>FunctionalBuild</c> group may add to them; it may not drop one (<see cref="FunctionalBuildGates"/>).
    /// </summary>
    private static readonly string[] RequiredFunctionalBuildGates = ["RunAnalyzers", "EnforceCodeStyleInBuild", "GenerateDocumentationFile"];

    /// <summary>
    /// Properties that turn the analyzer gate off whether or not the <c>FunctionalBuild</c> group lists
    /// them: the analyzers under another name, and what makes their diagnostics fail the build.
    /// </summary>
    private static readonly string[] AnalyzerGateProperties =
    [
        "RunAnalyzersDuringBuild", "EnableNETAnalyzers", "TreatWarningsAsErrors", "CodeAnalysisTreatWarningsAsErrors",
    ];

    /// <summary>
    /// Property-name prefixes that set the analyzer rule set — <c>AnalysisLevel</c>, <c>AnalysisMode</c>
    /// and their per-category forms (<c>AnalysisLevelSecurity</c>, <c>AnalysisModeDesign</c>, …).
    /// </summary>
    private static readonly string[] AnalyzerGatePrefixes = ["AnalysisLevel", "AnalysisMode"];

    /// <summary>The condition that makes a verdict step independent of the ones before it.</summary>
    private const string IndependentCondition = "!cancelled() && steps.build.outcome == 'success'";

    /// <summary>
    /// The jobs in <c>ci.yml</c> whose verdict steps are sequential on purpose — default <c>success()</c>,
    /// no <see cref="IndependentCondition"/> — and why.
    /// </summary>
    private static readonly Dictionary<string, string> SequentialJobs = new(StringComparer.Ordinal)
    {
        ["nr-conformance"] = "it drives newrecruit.eu live, and a lane that found the site down must not be followed by lanes that hammer it",
    };

    /// <summary>
    /// What a job needs besides its verdict steps — checkout, setup, build, uploads — in minutes: about
    /// three times the slowest measured setup-and-build (225s, <c>nr-conformance</c>, run 36772377382),
    /// for cold caches and a slow runner.
    /// </summary>
    private const double SetupAllowanceMinutes = 10;

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
        var needs = ci.Job(CiGateJob).Needs.ToHashSet(StringComparer.Ordinal);
        var others = ci.Jobs.Select(static j => j.Id).Where(static id => id != CiGateJob).ToHashSet(StringComparer.Ordinal);

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

    /// <summary>
    /// <b>The jobs gated on the gate's <c>thorough</c> and <c>live</c> outputs are exactly the ones
    /// <c>scripts/ci-gate.json</c> lists.</b> That file is what <c>ci-gate</c> reads to decide which
    /// skips are excusable, so a job gated in YAML but missing from it would be a skip <c>ci-gate</c>
    /// rejects, and a job listed but not gated would be a skip it excuses for no reason.
    /// </summary>
    /// <remarks>
    /// Mutation-checked: remove <c>thorough-ui-bs</c> from <c>thoroughJobs</c> and this goes red naming
    /// it; gate a new job on <c>needs.gate.outputs.thorough</c> without listing it and it goes red too.
    /// </remarks>
    [Fact]
    public void GatedJobs_MatchTheDataFile()
    {
        var config = CiGateConfig.Load();
        var ci = CiWorkflows.Ci;
        var problems = new List<string>();

        foreach (var (output, listed, key) in new[]
                 {
                     ("thorough", config.ThoroughJobs, "thoroughJobs"),
                     ("live", config.LiveJobs, "liveJobs"),
                 })
        {
            var gated = ci.Jobs
                .Where(j => j.If is { } condition && condition.Contains($"needs.{GateJob}.outputs.{output}", StringComparison.Ordinal))
                .Select(static j => j.Id)
                .ToHashSet(StringComparer.Ordinal);

            if (gated.Count == 0)
            {
                problems.Add($"  no job in ci.yml is gated on needs.{GateJob}.outputs.{output}");
            }

            problems.AddRange(gated.Except(listed).Order(StringComparer.Ordinal)
                .Select(j => $"  {j} is gated on `{output}` in ci.yml but missing from {CiGateConfig.RelativePath} {key}"));
            problems.AddRange(listed.Except(gated).Order(StringComparer.Ordinal)
                .Select(j => $"  {j} is in {CiGateConfig.RelativePath} {key} but ci.yml does not gate it on `{output}`"));
            problems.AddRange(gated.Where(j => !ci.Job(j).Needs.Contains(GateJob)).Order(StringComparer.Ordinal)
                .Select(static j => $"  {j} reads the gate's output but does not need `{GateJob}` — the expression is null and the job never runs"));
        }

        Assert.True(
            problems.Count == 0,
            "The gated jobs and scripts/ci-gate.json disagree:\n" + string.Join("\n", problems) + "\n\n" +
            "ci-gate excuses a skipped job only when the data file says the gate's output explains it. Keep " +
            "the two lists the same, or a skip is either rejected for no reason or excused for none.");
    }

    /// <summary>
    /// <b>Every job in every workflow has a <c>timeout-minutes</c>.</b> GitHub's default is six hours.
    /// </summary>
    /// <remarks>
    /// <c>checks</c>, <c>docker</c>, <c>smoke</c> and <c>nr-conformance</c> had none — a wedged browser or
    /// JVM held a runner for six hours and reported nothing until it was killed. The value must be a plain
    /// positive number: an expression is a bound nobody can read. Mutation-checked: delete
    /// <c>timeout-minutes</c> from <c>docker</c> and this goes red naming the job and line.
    /// </remarks>
    [Fact]
    public void EveryJob_HasATimeout()
    {
        var jobs = CiWorkflows.All.SelectMany(static w => w.Jobs).ToList();
        Assert.NotEmpty(jobs);

        var unbounded = jobs
            .Where(static j => CiWorkflows.Minutes(j.TimeoutMinutes) is null)
            .Select(static j => $"  {j.Where}: timeout-minutes = {j.TimeoutMinutes ?? "(none)"}")
            .ToArray();

        Assert.True(
            unbounded.Length == 0,
            "These jobs have no usable timeout-minutes:\n" + string.Join("\n", unbounded) + "\n\n" +
            "Without one a job inherits GitHub's 360-minute default, so a hang burns six hours of runner " +
            "time and reports nothing until it is killed. Give it a plain number of minutes, a ceiling well " +
            "above its measured duration.");
    }

    /// <summary>
    /// <b>Every step that runs a test project or <c>bs-spec</c> has its own <c>timeout-minutes</c>, below
    /// its job's.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// A job timeout ends the job — and with it every <c>if: failure()</c> diagnostics upload and every
    /// <c>if: always()</c> telemetry upload after the hung step. A step timeout fails the step, and those
    /// uploads still run, which is the difference between a hang with evidence and one without. A step
    /// bound at or above its job's is the job bound in disguise.
    /// </para>
    /// <para>
    /// Mutation-checked: delete <c>timeout-minutes</c> from "Full frozen NR UI roster" and this goes red
    /// naming the step; set a step's to 200 in a 150-minute job and it goes red with both numbers.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryTestStep_HasATimeout()
    {
        var steps = CiTestInvocations.VerdictSteps().ToList();
        Assert.NotEmpty(steps);

        var problems = new List<string>();
        foreach (var (step, _) in steps)
        {
            var own = CiWorkflows.Minutes(step.TimeoutMinutes);
            var job = CiWorkflows.Minutes(step.Job.TimeoutMinutes);
            if (own is null)
            {
                problems.Add($"  {step.Where}: timeout-minutes = {step.TimeoutMinutes ?? "(none)"}");
            }
            else if (job is { } jobMinutes && own >= jobMinutes)
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {step.Where}: timeout-minutes {own} is not below its job's {jobMinutes}"));
            }
        }

        Assert.True(
            problems.Count == 0,
            "These test steps are not bounded below their job:\n" + string.Join("\n", problems) + "\n\n" +
            "A hang that hits the JOB timeout takes the failure() diagnostics and always() telemetry uploads " +
            "down with it. Give the step its own timeout-minutes — about 2.5x its measured duration, at least " +
            "5 — so a hang is a failed step whose evidence still leaves the runner.");
    }

    /// <summary>
    /// <b>Every job outlasts all of its verdict steps' bounds together, with room left for setup.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// A step timeout only does its job if the job is still alive when it fires. The rule above checks
    /// each step against its job on its own, and with a 5-minute floor the bounds add up: <c>smoke</c>
    /// had six 5-minute steps — 30 minutes — inside a 25-minute job, and <c>checks</c> four inside 20. A
    /// common-cause hang (a wedged browser install, a dead display) across the NR steps would have hit
    /// the job timeout before the later steps' own bounds, and the <c>failure()</c> diagnostics upload
    /// would have gone with it — the outcome step timeouts exist to prevent. So the sum of a job's
    /// verdict-step bounds plus <see cref="SetupAllowanceMinutes"/> must fit in its
    /// <c>timeout-minutes</c>. Missing bounds are the two rules above's to report.
    /// </para>
    /// <para>
    /// Mutation-checked: set <c>smoke</c>'s <c>timeout-minutes</c> back to 25 and this goes red with the
    /// sum and the bound.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryJob_OutlastsItsStepBounds()
    {
        var byJob = CiTestInvocations.VerdictSteps().GroupBy(static c => c.Step.Job).ToList();
        Assert.NotEmpty(byJob);

        var problems = new List<string>();
        foreach (var group in byJob)
        {
            var bounds = group.Select(static c => CiWorkflows.Minutes(c.Step.TimeoutMinutes)).ToList();
            if (CiWorkflows.Minutes(group.Key.TimeoutMinutes) is not { } job || bounds.Any(static b => b is null))
            {
                continue;
            }

            var sum = bounds.Sum(static b => b!.Value);
            if (sum + SetupAllowanceMinutes > job)
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {group.Key.Where}: {group.Count()} verdict step(s) bounded to {sum} min + {SetupAllowanceMinutes} for setup = {sum + SetupAllowanceMinutes}, over the job's {job}"));
            }
        }

        Assert.True(
            problems.Count == 0,
            "These jobs end before their own steps' timeouts can:\n" + string.Join("\n", problems) + "\n\n" +
            "If several steps hang, the job timeout fires first and cancels the failure() uploads the step " +
            "bounds were set to protect. Raise the job's timeout-minutes (it is a ceiling for hangs, not a " +
            "target), or lower step bounds that are far above their measured durations.");
    }

    /// <summary>
    /// <b>Every <c>steps.&lt;id&gt;</c> in a job names a step that exists earlier in that job.</b> Job
    /// <c>outputs:</c> may name any step of the job.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A reference to a step id that does not exist is not an error in GitHub Actions — it evaluates to
    /// null. Every test step outside <c>nr-conformance</c> is conditioned on
    /// <c>steps.build.outcome == 'success'</c> (<see cref="IndependentTestSteps_RunOnASuccessfulBuild"/>),
    /// so renaming <c>id: build</c> (a setup refactor is exactly where that happens) would skip every
    /// test step in the job, skipped steps do not fail a job, and the job would report success having
    /// tested nothing.
    /// </para>
    /// <para>
    /// The composite actions are read too, each as one job: inside an action, <c>steps.&lt;id&gt;</c>
    /// names the action's own steps (the setup action's <c>setup.ps1</c> step reads
    /// <c>steps.app-token</c>), with the same null-not-error trap. It fails if it read no action.
    /// </para>
    /// <para>
    /// Mutation-checked: rename <c>id: build</c> to <c>id: compile</c> in <c>smoke</c> and this goes red
    /// naming each step that referenced it.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryStepsReference_ResolvesToAnEarlierStepInTheSameJob()
    {
        var problems = new List<string>();
        var checkedAny = false;
        var jobs = CiWorkflows.All.SelectMany(static w => w.Jobs).Concat(CiWorkflows.Actions).ToList();
        CiDefinitionFiles.AssertReadAnAction(jobs.Select(static j => j.Workflow), nameof(EveryStepsReference_ResolvesToAnEarlierStepInTheSameJob));

        foreach (var job in jobs)
        {
            var allIds = job.Steps.Select(static s => s.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var earlier = new HashSet<string>(StringComparer.Ordinal);

            foreach (var step in job.Steps)
            {
                foreach (var id in StepReferences(CiWorkflows.Scalars(step.Node)))
                {
                    checkedAny = true;
                    if (!earlier.Contains(id))
                    {
                        problems.Add(allIds.Contains(id)
                            ? $"  {step.Where}: steps.{id} is defined LATER in the job, so it is null here"
                            : $"  {step.Where}: steps.{id} is not a step id in job '{job.Id}'");
                    }
                }

                if (step.Id is { } own)
                {
                    earlier.Add(own);
                }
            }

            if (job.Node.Children.TryGetValue(new YamlScalarNode("outputs"), out var outputs))
            {
                foreach (var id in StepReferences(CiWorkflows.Scalars(outputs)))
                {
                    checkedAny = true;
                    if (!allIds.Contains(id))
                    {
                        problems.Add($"  {job.Where} outputs: steps.{id} is not a step id in the job");
                    }
                }
            }
        }

        Assert.True(checkedAny, "No `steps.<id>` reference found in any workflow — the scan is reading nothing.");
        Assert.True(
            problems.Count == 0,
            "These step references resolve to nothing:\n" + string.Join("\n", problems) + "\n\n" +
            "GitHub evaluates an unknown steps.<id> to null instead of failing. On a test step's `if:` that " +
            "means the step is skipped, the job still succeeds, and ci-gate reads it as green.");
    }

    /// <summary>
    /// <b>Every verdict step runs on <c>!cancelled() &amp;&amp; steps.build.outcome == 'success'</c> —
    /// except in the jobs that are sequential on purpose, where none does.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The condition makes test steps independent of each other while all of them still skip when there
    /// is nothing built to test. The default <c>success()</c> made each lane hostage to every lane above
    /// it: <c>thorough-conformance</c> reported PR #338 red having never run two of the four lanes it
    /// exists to verify. A step that drops the condition goes back to that, silently — it still runs
    /// whenever the steps above it pass, which is every run anyone looks at.
    /// </para>
    /// <para>
    /// The exceptions are <see cref="SequentialJobs"/>, and there the rule inverts: the default
    /// <c>success()</c> is the point, so an independent condition is the finding. A further
    /// <c>&amp;&amp; …</c> narrowing is allowed; an <c>||</c> is not, because it can run a step on a
    /// cancelled run or a failed build.
    /// </para>
    /// <para>
    /// Mutation-checked: remove the condition from "Full frozen NR roster" and this goes red naming the
    /// step; give "Run NR conformance tests" <c>if: ${{ !cancelled() }}</c> and it goes red too.
    /// </para>
    /// </remarks>
    [Fact]
    public void IndependentTestSteps_RunOnASuccessfulBuild()
    {
        var ci = CiWorkflows.Ci;
        foreach (var job in SequentialJobs.Keys)
        {
            _ = ci.Job(job); // a renamed job must not leave a stale exemption behind
        }

        var steps = CiTestInvocations.VerdictSteps().ToList();
        var sequential = steps.Where(static c => c.Step.Job.Workflow == CiFile && SequentialJobs.ContainsKey(c.Step.Job.Id)).ToList();
        var independent = steps.Except(sequential).ToList();
        Assert.True(
            independent.Count > 0 && sequential.Count > 0,
            "Found no independent or no sequential verdict step — the scan is reading nothing.");

        var problems = new List<string>();
        foreach (var (step, _) in sequential)
        {
            if (Condition(step.If) is { } condition && condition != "success()")
            {
                problems.Add(
                    $"  {step.Where}: if: {step.If} — {step.Job.Id} is sequential on purpose " +
                    $"({SequentialJobs[step.Job.Id]}); its verdict steps keep the default success()");
            }
        }

        foreach (var (step, _) in independent)
        {
            var condition = Condition(step.If);
            var ok = condition == IndependentCondition
                || (condition is not null
                    && condition.StartsWith($"{IndependentCondition} && ", StringComparison.Ordinal)
                    && !condition.Contains("||", StringComparison.Ordinal));
            if (!ok)
            {
                problems.Add($"  {step.Where}: if: {step.If ?? "(none — the default success(), so it waits on every step above it)"}");
            }
        }

        Assert.True(
            problems.Count == 0,
            "These verdict steps have the wrong condition:\n" + string.Join("\n", problems) + "\n\n" +
            $"A test step runs on `${{{{ {IndependentCondition} }}}}`: independent of the lanes before it, " +
            "skipped only when nothing was built. The jobs in SequentialJobs are the deliberate exception, " +
            "and there the default success() is the rule.");

        static string? Condition(string? written)
        {
            if (written is null)
            {
                return null;
            }

            var text = written.Trim();
            if (text.StartsWith("${{", StringComparison.Ordinal) && text.EndsWith("}}", StringComparison.Ordinal))
            {
                text = text[3..^2];
            }

            return Regex.Replace(text.Trim(), @"\s+", " ");
        }
    }

    /// <summary>
    /// <b>Every test step names the project it runs, literally.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coverage lint (<c>EveryTestProject_IsRunBySomeCiStep</c>) and every per-project rule can only
    /// check a project they can read. A step whose project is an expression — a matrix over projects,
    /// <c>--project ${{ env.X }}</c>, the wrapper given <c>${{ env.CLI_TESTS }}</c> — used to be read as
    /// a run of the whole solution, so every project looked covered: replacing the CLI step's csproj
    /// with <c>${{ env.CLI_TESTS }}</c> left every lint green while no step named
    /// <c>BattleScribeSpec.Cli.Tests</c>. <see cref="CiTestInvocations"/> now reports such a run as
    /// <see cref="CiInvocation.Unresolved"/>, and this fails on it with the reason.
    /// </para>
    /// <para>
    /// Mutation-checked: that same replacement goes red here (and in the coverage lint), and so does
    /// <c>dotnet run --project ${{ matrix.project }} --no-build -- --test-profile x</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryTestStep_NamesItsProject()
    {
        var steps = CiTestInvocations.VerdictSteps().ToList();
        Assert.NotEmpty(steps);

        var unresolved = steps
            .Where(static c => c.Invocation.Unresolved is not null)
            .Select(static c => $"  {c.Step.Where}: {c.Invocation.Unresolved}")
            .ToArray();

        Assert.True(
            unresolved.Length == 0,
            "These test steps run a project the workflow lints cannot identify:\n" + string.Join("\n", unresolved) + "\n\n" +
            "An expression is opaque here, so the step counts as covering no project, and the per-project " +
            "rules cannot see it. Name the csproj (or BattleScribeSpec.slnx) on the line — vary the profile or " +
            "the filter in a matrix, not the project — or give each project its own step.");
    }

    /// <summary>
    /// <b>Every <c>needs.&lt;job&gt;</c> in a job names a job it needs.</b> The same null-not-error trap
    /// as step references, one level up: a job reading an output of a job it does not need gets null,
    /// and a gated job then never runs.
    /// </summary>
    /// <remarks>Mutation-checked: drop <c>needs: [gate]</c> from <c>nr-conformance</c> and this goes red.</remarks>
    [Fact]
    public void EveryNeedsReference_NamesAJobThisJobNeeds()
    {
        var problems = new List<string>();
        var checkedAny = false;

        foreach (var job in CiWorkflows.All.SelectMany(static w => w.Jobs))
        {
            foreach (var m in CiWorkflows.Scalars(job.Node)
                         .SelectMany(static s => Regex.Matches(s.Value ?? "", @"\bneeds\.([A-Za-z_][A-Za-z0-9_-]*)")))
            {
                checkedAny = true;
                var needed = m.Groups[1].Value;
                if (!job.Needs.Contains(needed))
                {
                    problems.Add($"  {job.Where}: reads needs.{needed} but `needs:` is [{string.Join(", ", job.Needs)}]");
                }
            }
        }

        Assert.True(checkedAny, "No `needs.<job>` reference found in any workflow — the scan is reading nothing.");
        Assert.True(
            problems.Count == 0,
            "These jobs read outputs of jobs they do not need:\n" + string.Join("\n", problems) + "\n\n" +
            "An unneeded job's outputs evaluate to null, so a condition on them is silently false.");
    }

    /// <summary>
    /// <b>A step that runs tests (or <c>bs-spec</c>) is exactly one invocation</b> — optionally under
    /// <c>xvfb-run -a</c> — with no <c>|</c>, <c>||</c>, <c>&amp;&amp;</c>, <c>&amp;</c> or <c>;</c>, and
    /// no second command on another line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>run:</c> steps here are <c>bash -e</c> without <c>pipefail</c>, so <c>dotnet test … | tee log</c>
    /// exits with <c>tee</c>'s status: a red test run, reported green. <c>|| true</c>, <c>; exit 0</c> and
    /// a second command on the next line (whose exit code becomes the step's) do the same. That is also
    /// the natural shape of a "let me grep the CI log" edit, which is why it is a lint and not a review
    /// comment. Operators inside quotes (<c>--filter "(A|B)&amp;C"</c>) are not operators.
    /// </para>
    /// <para>
    /// Mutation-checked: append <c>| tee log</c> to a test step and this goes red naming the step and
    /// the operator.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryTestStep_IsOneInvocation()
    {
        var steps = CiTestInvocations.VerdictSteps().ToList();
        Assert.NotEmpty(steps);

        var problems = new List<string>();
        foreach (var (step, _) in steps)
        {
            var commands = CiTestInvocations.Commands(step.Run!, step.Shell);
            if (commands.Count != 1)
            {
                problems.Add($"  {step.Where}: {commands.Count} commands — the last one's exit code is the step's");
                continue;
            }

            var operators = CiTestInvocations.Tokenize(commands[0]).Operators;
            if (operators.Count > 0)
            {
                problems.Add($"  {step.Where}: shell operator(s) {string.Join(" ", operators.Select(static o => $"`{o}`"))}");
            }
        }

        Assert.True(
            problems.Count == 0,
            "These test steps are not a single invocation:\n" + string.Join("\n", problems) + "\n\n" +
            "Without pipefail a pipe reports the last command's status, `|| true` and `; exit 0` discard the " +
            "test's, and a second line's exit code replaces it. Run the one command; if you need its output " +
            "elsewhere, the test step already writes telemetry and a job summary.");
    }

    /// <summary>
    /// <b>Nothing in CI swallows a test failure.</b> No <c>continue-on-error</c> on a test step or on a
    /// job that runs one or that <c>ci-gate</c> judges, and no <c>--ignore-exit-code</c>,
    /// <c>TESTINGPLATFORM_EXITCODE_IGNORE</c> or <c>|| true</c> anywhere under <c>.github</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each is a way to make a red step green from outside the test run: <c>continue-on-error</c> marks
    /// the step's failure as success for the job — and on a job, the job's failure as acceptable to the
    /// run, the same swallow one level up, handing <c>ci-gate</c> a result GitHub has already softened;
    /// the two Microsoft.Testing.Platform switches turn chosen
    /// exit codes into 0 (the variable does it for every test process in the job, from an <c>env:</c>
    /// block or a <c>$GITHUB_ENV</c> write nobody connects to the test step); <c>|| true</c> discards the
    /// status in the shell. The scan reads YAML values and keys — not comments — so this file's
    /// neighbours can explain the rule without tripping it.
    /// </para>
    /// <para>
    /// Mutation-checked: add <c>continue-on-error: true</c> to a test step, separately to the
    /// <c>thorough-conformance</c> job, and separately an <c>env: TESTINGPLATFORM_EXITCODE_IGNORE: 8</c>
    /// to a job; each goes red naming its location.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoTestStepSwallowsFailure()
    {
        var problems = new List<string>();

        var verdictSteps = CiTestInvocations.VerdictSteps().ToList();
        foreach (var (step, _) in verdictSteps)
        {
            if (step.ContinueOnError is { } value && !value.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"  {step.Where}: continue-on-error: {value}");
            }
        }

        var judged = CiWorkflows.Ci.Job(CiGateJob).Needs.ToHashSet(StringComparer.Ordinal);
        var verdictJobs = verdictSteps.Select(static c => c.Step.Job).ToHashSet();
        var jobs = CiWorkflows.All.SelectMany(static w => w.Jobs)
            .Where(j => verdictJobs.Contains(j) || (j.Workflow == CiFile && judged.Contains(j.Id)))
            .ToList();
        Assert.NotEmpty(jobs);
        foreach (var job in jobs)
        {
            if (job.ContinueOnError is { } value && !value.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"  {job.Where}: continue-on-error: {value} (on the job — the run passes with it failed)");
            }
        }

        var github = Path.Combine(CiWorkflows.Root, ".github");
        var files = Directory.EnumerateFiles(github, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var relative = CiWorkflows.Relative(file);
            var text = File.ReadAllText(file);
            var yaml = file.EndsWith(".yml", StringComparison.Ordinal) || file.EndsWith(".yaml", StringComparison.Ordinal);
            var values = yaml
                ? CiWorkflows.Scalars(CiWorkflows.LoadDocument(relative, text)).Select(static s => (Line: (long)s.Start.Line, Text: s.Value ?? ""))
                : text.Split('\n').Select(static (l, i) => (Line: (long)(i + 1), Text: l));

            foreach (var (line, value) in values)
            {
                foreach (var swallow in SwallowedExitCodes.Where(p => p.Pattern.IsMatch(value)))
                {
                    problems.Add($"  {relative}:{line}: {swallow.What}");
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            "CI swallows test failures here:\n" + string.Join("\n", problems) + "\n\n" +
            "Each of these turns a failing test step green from outside the test run. If a lane is " +
            "genuinely allowed to fail, it does not belong in a required job; if a failure is flaky, it is a " +
            "finding to fix, not an exit code to ignore.");
    }

    private static readonly (Regex Pattern, string What)[] SwallowedExitCodes =
    [
        (new Regex(@"--ignore-exit-code\b", RegexOptions.CultureInvariant), "--ignore-exit-code"),
        (new Regex(@"\bTESTINGPLATFORM_EXITCODE_IGNORE\b", RegexOptions.CultureInvariant), "TESTINGPLATFORM_EXITCODE_IGNORE"),
        (new Regex(@"\|\|\s*true\b", RegexOptions.CultureInvariant), "|| true"),
    ];

    /// <summary>
    /// <b>The gate decides with <c>scripts/thorough-inputs.mjs</c>, and its outputs are that script's.</b>
    /// </summary>
    /// <remarks>
    /// The decision used to be an inline file loop plus an inline pwsh expression — untested, and the
    /// list of inputs in it was a second copy of what the docs said. The script is unit-tested (node
    /// <c>--test</c> in <c>checks</c>) against the same data file this class reads. This pins the wiring:
    /// the gate checks out <c>scripts/</c>, runs the script with the event, labels and refs in
    /// <c>env</c> (never spliced into the command — a label is user text), and exports exactly its
    /// <c>thorough</c> and <c>live</c>. Mutation-checked: point the job's <c>live</c> output at a different
    /// step and this goes red.
    /// </remarks>
    [Fact]
    public void Gate_RunsTheInputsScript()
    {
        var gate = CiWorkflows.Ci.Job(GateJob);
        AssertChecksOutScripts(gate);

        var decide = gate.Steps.SingleOrDefault(static s =>
            s.Run is { } run && CiTestInvocations.Commands(run) is [var only] && only.StartsWith("node scripts/thorough-inputs.mjs", StringComparison.Ordinal));
        Assert.True(decide is not null, $"{gate.Where}: no step runs `node scripts/thorough-inputs.mjs` as its only command.");
        Assert.True(decide!.Id is not null, $"{decide.Where}: the decide step needs an id for the job outputs to read.");

        foreach (var key in new[] { "EVENT", "LABELS", "BASE_REF", "DEFAULT_BRANCH" })
        {
            Assert.True(decide.Env(key) is { Length: > 0 }, $"{decide.Where}: env {key} is not set; the script refuses to guess it.");
        }

        var outputs = (YamlMappingNode)gate.Node.Children[new YamlScalarNode("outputs")];
        foreach (var output in new[] { "thorough", "live" })
        {
            Assert.Equal($"${{{{ steps.{decide.Id}.outputs.{output} }}}}", CiWorkflows.Scalar(outputs, output));
        }
    }

    /// <summary>
    /// <b><c>ci-gate</c> runs <c>scripts/ci-gate.mjs</c> over <c>toJSON(needs)</c>, always.</b>
    /// </summary>
    /// <remarks>
    /// <c>if: always()</c> because a required check that is skipped when a job fails is a pass; the
    /// script over the whole <c>needs</c> object because a hand-kept table of results is how
    /// <c>docker</c> fell off the failure issue. Mutation-checked: replace <c>toJSON(needs)</c> with
    /// <c>toJSON(needs.checks)</c> and this goes red.
    /// </remarks>
    [Fact]
    public void CiGate_RunsTheGateScriptOverItsNeeds()
    {
        var ciGate = CiWorkflows.Ci.Job(CiGateJob);
        Assert.Equal("always()", ciGate.If);
        AssertChecksOutScripts(ciGate);

        var verdict = ciGate.Steps.SingleOrDefault(static s =>
            s.Run is { } run && CiTestInvocations.Commands(run) is [var only] && only == "node scripts/ci-gate.mjs");
        Assert.True(verdict is not null, $"{ciGate.Where}: no step runs `node scripts/ci-gate.mjs` as its only command.");
        Assert.Equal("${{ toJSON(needs) }}", verdict!.Env("NEEDS"));
        Assert.Equal("${{ github.event_name }}", verdict.Env("EVENT"));
    }

    private static void AssertChecksOutScripts(CiJob job)
    {
        var checkout = job.Steps.FirstOrDefault(static s => s.Uses?.StartsWith("actions/checkout@", StringComparison.Ordinal) == true);
        Assert.True(checkout is not null, $"{job.Where}: no actions/checkout step, so `node scripts/…` has nothing to run.");
        var sparse = checkout!.With("sparse-checkout");
        Assert.True(
            sparse is null || sparse.Split('\n').Any(static l => l.Trim() == "scripts"),
            $"{checkout.Where}: sparse-checkout '{sparse}' does not include scripts/.");
    }

    /// <summary>
    /// <b>The <c>checks</c> job is the analyzer gate: its build runs with analyzers, code style and doc
    /// generation on.</b> Nothing in the job — not its build line, not an <c>env:</c> block the build
    /// inherits — may set <c>FunctionalBuild</c>, any property that switch controls, or any other
    /// property that turns the gate off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TreatWarningsAsErrors</c> and <c>AnalysisLevel=latest-recommended</c> only gate anything in a
    /// build that runs the analyzers, and every other job builds with
    /// <c>-p:FunctionalBuild=true</c> to save the time. So this one build is where CI checks them at all;
    /// a <c>FunctionalBuild</c> that spread to it — a copy-paste from a neighbouring job, or a job-level
    /// <c>env:</c> (MSBuild reads environment variables as properties, so <c>FunctionalBuild: true</c>
    /// there turns the gates off without appearing on the build line) — would leave every job green and
    /// none of them checking.
    /// </para>
    /// <para>
    /// The switch is not the only way there. <c>-p:TreatWarningsAsErrors=false</c> keeps the analyzers
    /// and fails on none of them; <c>RunAnalyzersDuringBuild</c>, <c>EnableNETAnalyzers</c> or an
    /// <c>AnalysisLevel</c> of <c>none</c> turn them off under another name. So the properties this
    /// forbids are the switch, the group's own list, and a fixed set that is not read from the group —
    /// <see cref="AnalyzerGateProperties"/> and <see cref="AnalyzerGatePrefixes"/> — because a list read
    /// only from data shrinks when the data does. (<c>-warnaserror-</c> needs no rule: MSBuild rejects it
    /// as an unknown switch, so it fails the build rather than quietly passing it.)
    /// </para>
    /// <para>
    /// Mutation-checked: add <c>-p:FunctionalBuild=true</c> to the <c>checks</c> build and this goes red
    /// naming the step; separately, <c>env: FunctionalBuild: true</c> on the job goes red naming the job,
    /// and <c>-p:TreatWarningsAsErrors=false</c> on the build goes red naming the property.
    /// </para>
    /// </remarks>
    [Fact]
    public void Checks_BuildsWithAnalyzers()
    {
        var ci = CiWorkflows.Ci;
        var checks = ci.Job(ChecksJob);
        var controlled = FunctionalBuildGates().Prepend(FunctionalBuildProperty).Concat(AnalyzerGateProperties)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool TurnsTheGateOff(string name) =>
            controlled.Contains(name) || AnalyzerGatePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        var builds = BuildSteps(checks).ToList();
        Assert.True(builds.Count > 0, $"{checks.Where}: no `dotnet build` step — the analyzer gate is gone from CI.");

        var problems = new List<string>();
        foreach (var (step, command) in builds)
        {
            problems.AddRange(CiTestInvocations.MsBuildProperties(command)
                .Where(p => TurnsTheGateOff(p.Name))
                .Select(p => $"  {step.Where}: -p:{p.Name}={p.Value}"));
        }

        var envBlocks = new List<(string Where, YamlNode Node)> { ($"{ci.File} (workflow env)", ci.Node), ($"{checks.Where} (job env)", checks.Node) };
        envBlocks.AddRange(builds.Select(static b => ($"{b.Step.Where} (step env)", (YamlNode)b.Step.Node)));
        foreach (var (where, node) in envBlocks)
        {
            problems.AddRange(EnvKeys(node).Where(TurnsTheGateOff).Select(k => $"  {where}: {k}"));
        }

        Assert.True(
            problems.Count == 0,
            $"The {ChecksJob} job's build turns off the gates it exists to run:\n" + string.Join("\n", problems) + "\n\n" +
            $"{ChecksJob} is the one build in CI with analyzers, code style and XML docs on; every other job builds with " +
            $"-p:{FunctionalBuildProperty}=true. Turn them off here and TreatWarningsAsErrors gates nothing anywhere. " +
            "Build it with a plain `dotnet build`.");
    }

    /// <summary>
    /// <b>Every other build in <c>ci.yml</c> is a functional build</b>: <c>dotnet build -p:FunctionalBuild=true</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The analyzers, code style and doc generation run once, in <c>checks</c>; running them again in
    /// every other job cost ~30s per job (81s functional against 108-113s full in run 36772377382) and
    /// checked nothing <c>checks</c> does not. Before the switch, one job spelled the three flags out and
    /// the other three did not, so they paid for a second analyzer pass by omission. Scoped to
    /// <c>ci.yml</c>: the snapshot workflow builds one tool project, and how it does is its own business.
    /// </para>
    /// <para>
    /// Mutation-checked: drop the switch from <c>thorough-ui-bs</c>'s build and this goes red naming the
    /// step.
    /// </para>
    /// </remarks>
    [Fact]
    public void OtherCiBuilds_UseFunctionalBuild()
    {
        _ = FunctionalBuildGates(); // a switch with no props group behind it would pass here and do nothing
        var builds = CiWorkflows.Ci.Jobs.Where(static j => j.Id != ChecksJob).SelectMany(BuildSteps).ToList();
        Assert.True(builds.Count > 0, $"No `dotnet build` step in {CiFile} outside {ChecksJob} — the scan is reading nothing.");

        var plain = builds
            .Where(static b => !CiTestInvocations.MsBuildProperties(b.Command)
                .Any(static p => p.Name.Equals(FunctionalBuildProperty, StringComparison.OrdinalIgnoreCase)
                    && p.Value.Equals("true", StringComparison.OrdinalIgnoreCase)))
            .Select(static b => $"  {b.Step.Where}: {b.Command}")
            .ToArray();

        Assert.True(
            plain.Length == 0,
            "These ci.yml builds run the analyzers again:\n" + string.Join("\n", plain) + "\n\n" +
            $"Only the {ChecksJob} job is the analyzer gate. Build with `dotnet build -p:{FunctionalBuildProperty}=true` " +
            "(Directory.Build.props) — the same binaries, without a second analyzer pass.");
    }

    /// <summary>
    /// <b>No CI step spells out the properties <c>FunctionalBuild</c> controls.</b> The switch is the one
    /// way to turn the gates off, and the list of what it turns off is the
    /// <c>FunctionalBuild</c> group in <c>Directory.Build.props</c> — read from there, not restated here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The raw form (<c>-p:RunAnalyzers=false -p:EnforceCodeStyleInBuild=false
    /// -p:GenerateDocumentationFile=false</c>) is what one job used to carry, and it is a second record
    /// of the switch that drifts from the first: a fourth gate added to the props group would be turned
    /// off by the switch and left on by every raw copy, and a raw copy in <c>checks</c> is invisible to a
    /// lint that looks for the switch. Every workflow and composite action is read, and it fails if it
    /// read no action (<see cref="CiDefinitionFiles.AssertReadAnAction"/>).
    /// </para>
    /// <para>
    /// Mutation-checked: put <c>-p:RunAnalyzers=false</c> on a build in <c>smoke</c> and this goes red
    /// naming the step and the property; move <c>.github/actions</c> away and it goes red saying it
    /// scanned no action.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoCiStep_SpellsOutTheFunctionalBuildProperties()
    {
        var gates = FunctionalBuildGates().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var steps = CiWorkflows.AllSteps.Where(static s => s.Run is not null).ToList();
        CiDefinitionFiles.AssertReadAnAction(steps.Select(static s => s.Job.Workflow), nameof(NoCiStep_SpellsOutTheFunctionalBuildProperties));

        var problems = steps
            .SelectMany(static s => CiTestInvocations.Commands(s.Run!, s.Shell).Select(c => (Step: s, Command: c)))
            .SelectMany(c => CiTestInvocations.MsBuildProperties(c.Command)
                .Where(p => gates.Contains(p.Name))
                .Select(p => $"  {c.Step.Where}: -p:{p.Name}={p.Value}"))
            .ToArray();

        Assert.True(
            problems.Length == 0,
            "These CI steps set a property -p:FunctionalBuild=true controls, by hand:\n" + string.Join("\n", problems) + "\n\n" +
            $"Use -p:{FunctionalBuildProperty}=true (or nothing, in {ChecksJob}). The properties it turns off are listed " +
            "once, in Directory.Build.props; a hand-written copy drifts from that list.");
    }

    /// <summary>
    /// <b>Dependabot watches every action directory</b>, not only the workflows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>github-actions</c> ecosystem with <c>directory: "/"</c> reads <c>.github/workflows</c> and a
    /// root <c>action.yml</c>. The setup action pins <c>setup-dotnet</c>, <c>setup-java</c>,
    /// <c>cache</c> and the app-token action, and from <c>.github/actions/setup</c> those would never be
    /// offered a bump — while the same actions in the workflows were — so the two copies would drift
    /// apart with nothing to say so: the blind spot of a central-only NuGet pin, in YAML. Every
    /// directory holding an <c>action.yml</c> must match an entry of that ecosystem's
    /// <c>directory</c>/<c>directories</c> (Dependabot's globs: <c>*</c> within a segment, <c>**</c>
    /// across them), and <c>/</c> must stay listed for the workflows.
    /// </para>
    /// <para>
    /// Mutation-checked: replace <c>directories</c> with <c>directory: "/"</c> and this goes red naming
    /// <c>/.github/actions/setup</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryActionDirectory_IsWatchedByDependabot()
    {
        var actions = CiDefinitionFiles.Actions.ToList();
        CiDefinitionFiles.AssertReadAnAction(actions.Select(static a => a.Path), nameof(EveryActionDirectory_IsWatchedByDependabot));
        var actionDirectories = actions
            .Select(static a => "/" + a.Path[..a.Path.LastIndexOf('/')])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        const string config = ".github/dependabot.yml";
        var root = CiWorkflows.LoadDocument(config, File.ReadAllText(Path.Combine(CiWorkflows.Root, ".github", "dependabot.yml")));
        var entries = root.Children.TryGetValue(new YamlScalarNode("updates"), out var updates) && updates is YamlSequenceNode list
            ? list.Children.OfType<YamlMappingNode>().Where(static u => CiWorkflows.Scalar(u, "package-ecosystem") == "github-actions").ToList()
            : [];
        Assert.True(entries.Count > 0, $"{config} has no `github-actions` entry: no action in this repo is ever offered a bump.");

        var watched = entries
            .SelectMany(static e =>
                (CiWorkflows.Scalar(e, "directory") is { } one ? [one] : Array.Empty<string>())
                .Concat(e.Children.TryGetValue(new YamlScalarNode("directories"), out var many) && many is YamlSequenceNode seq
                    ? seq.Children.OfType<YamlScalarNode>().Select(static n => n.Value ?? "")
                    : []))
            .Select(static p => p.Length > 1 ? p.TrimEnd('/') : p)
            .ToList();

        var unwatched = actionDirectories.Where(d => !watched.Any(p => DependabotGlob(p).IsMatch(d))).ToList();
        if (!watched.Contains("/"))
        {
            unwatched.Insert(0, "/ (the workflows)");
        }

        Assert.True(
            unwatched.Count == 0,
            $"{config}'s github-actions entry does not watch:\n" + string.Join("\n", unwatched.Select(static d => $"  {d}")) + "\n\n" +
            $"It watches [{string.Join(", ", watched)}]. An action pinned in a directory Dependabot does not read is never " +
            "offered a bump, and drifts from the same action pinned in the workflows. List it in `directories` " +
            "(\"/.github/actions/*\" covers every action directory).");

        static Regex DependabotGlob(string pattern) => new(
            "^" + Regex.Escape(pattern).Replace(@"\*\*", ".*", StringComparison.Ordinal).Replace(@"\*", "[^/]*", StringComparison.Ordinal) + "$",
            RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// <b>A composite action runs no test and no <c>bs-spec</c> verdict.</b> Verdict steps live in the
    /// job.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything that holds a test step to its claims reads the job: its <c>timeout-minutes</c> against
    /// the job's, its condition on the job's <c>steps.build</c>, the <c>failure()</c> uploads after it,
    /// and <c>ci-gate</c>'s verdict on the job. Inside an action a step has none of those — the action's
    /// steps cannot see the job's step ids — so a test step moved into the setup action ("it is the same
    /// in every job anyway") would run with no bound and no condition anyone checks. The classifier reads
    /// the actions' steps (it fails if it read none), so this is a rule about them, not a blind spot.
    /// </para>
    /// <para>
    /// Mutation-checked: add a <c>dotnet test tests/BattleScribeSpec.Tests.csproj --no-build</c> step to
    /// <c>.github/actions/setup</c> and this goes red naming it.
    /// </para>
    /// </remarks>
    [Fact]
    public void CompositeActions_RunNoVerdictSteps()
    {
        var actionSteps = CiTestInvocations.ClassifiedSteps().Where(static c => c.Step.Job.Id == CiWorkflows.ActionStepsId).ToList();
        Assert.NotEmpty(actionSteps);

        var verdicts = actionSteps
            .Where(static c => c.Invocation.Kind != CiStepKind.Other)
            .Select(static c => $"  {c.Step.Where}: {c.Invocation.Kind}")
            .ToArray();

        Assert.True(
            verdicts.Length == 0,
            "These composite-action steps run tests or bs-spec:\n" + string.Join("\n", verdicts) + "\n\n" +
            "A verdict step belongs in the job, where its timeout, its condition on steps.build, the uploads " +
            "after it and ci-gate can see it. Keep the action to setup; put the step in each job that needs it.");
    }

    /// <summary>
    /// The properties <c>-p:FunctionalBuild=true</c> sets, by name, from the <c>FunctionalBuild</c>
    /// group of the root <c>Directory.Build.props</c> — the one record of what the switch turns off.
    /// Fails unless the group turns off at least <see cref="RequiredFunctionalBuildGates"/>.
    /// </summary>
    /// <remarks>
    /// The floor is what keeps "read from the group" from meaning "whatever the group still says":
    /// deleting <c>&lt;RunAnalyzers&gt;</c> from it would otherwise quietly bring the analyzers back to
    /// every functional build and drop <c>RunAnalyzers</c> from what the raw-flag ban covers, with every
    /// lint still green. Mutation-checked: delete that line and the three lints that read the group go
    /// red naming it.
    /// </remarks>
    private static IReadOnlyList<string> FunctionalBuildGates()
    {
        var props = XDocument.Load(Path.Combine(CiWorkflows.Root, "Directory.Build.props"));
        var group = props.Root!.Elements("PropertyGroup").SingleOrDefault(static g =>
            g.Attribute("Condition")?.Value is { } condition
            && condition.Contains($"$({FunctionalBuildProperty})", StringComparison.Ordinal)
            && condition.Contains("'true'", StringComparison.Ordinal));
        Assert.True(
            group is not null,
            $"Directory.Build.props has no PropertyGroup conditioned on '$({FunctionalBuildProperty})' == 'true': the switch " +
            "every non-checks CI build passes does nothing, and the lints that police it have nothing to read.");

        var names = group!.Elements().Select(static e => e.Name.LocalName).ToList();
        var missing = RequiredFunctionalBuildGates
            .Where(r => !group.Elements().Any(e => e.Name.LocalName == r && e.Value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.True(
            missing.Count == 0,
            $"Directory.Build.props's {FunctionalBuildProperty} group does not set {string.Join(", ", missing.Select(static m => m + "=false"))}. " +
            "The switch every non-checks CI build passes would leave that gate running in each of them, and the ban on " +
            "spelling the switch's properties out in CI — read from this group — would stop covering it. The group may add " +
            $"to {string.Join(", ", RequiredFunctionalBuildGates)}; it may not drop one.");
        return names;
    }

    /// <summary>Each <c>dotnet build</c> command in <paramref name="job"/>, with its step.</summary>
    private static IEnumerable<(CiStep Step, string Command)> BuildSteps(CiJob job) =>
        job.Steps
            .Where(static s => s.Run is not null)
            .SelectMany(static s => CiTestInvocations.Commands(s.Run!, s.Shell)
                .Where(static c => CiTestInvocations.RunsDotnet(c, "build"))
                .Select(c => (s, c)));

    /// <summary>The keys of a workflow's, job's or step's <c>env:</c> mapping.</summary>
    private static IEnumerable<string> EnvKeys(YamlNode node) =>
        node is YamlMappingNode map && map.Children.TryGetValue(new YamlScalarNode("env"), out var env) && env is YamlMappingNode envMap
            ? envMap.Children.Keys.OfType<YamlScalarNode>().Select(static k => k.Value ?? "")
            : [];

    // ── the classifier itself ──

    /// <summary>
    /// <b><see cref="CiTestInvocations"/> recognises a test run by the project it runs, in every
    /// spelling a migration produces — and does not mistake <c>bs-spec</c> for one.</b>
    /// </summary>
    /// <remarks>
    /// These rows are the mutation checks the plan called for, kept: reordered flags, <c>--project=</c>,
    /// the exe and the dll by path, <c>dotnet run</c>, a bare <c>dotnet test</c> are all test runs; the
    /// CLI by dll, by project and by name is a CLI run; a solution-wide <c>dotnet format</c>, a quoted
    /// mention and the Docker image's name are neither.
    /// </remarks>
    [Theory]
    [InlineData("pwsh scripts/dotnet-test-step.ps1 tests/BattleScribeSpec.Tests.csproj --no-build --filter \"Engine=X\"", CiStepKind.TestRun)]
    [InlineData("dotnet test --no-build --filter \"(A|B)&C\" tests/BattleScribeSpec.Tests.csproj", CiStepKind.TestRun)]
    [InlineData("dotnet test --project=tests/BattleScribeSpec.Cli.Tests/BattleScribeSpec.Cli.Tests.csproj", CiStepKind.TestRun)]
    [InlineData("dotnet run --no-build --project ./tests/BattleScribeSpec.Tests.csproj -- --test-profile bs", CiStepKind.TestRun)]
    [InlineData("xvfb-run -a dotnet artifacts/bin/BattleScribeSpec.Tests/debug/BattleScribeSpec.Tests.dll", CiStepKind.TestRun)]
    [InlineData("artifacts\\bin\\BattleScribeSpec.Cli.Tests\\debug\\BattleScribeSpec.Cli.Tests.exe --list-tests", CiStepKind.TestRun)]
    [InlineData("dotnet test", CiStepKind.TestRun)]
    [InlineData("dotnet test BattleScribeSpec.slnx -p:TestProfile=pre-push", CiStepKind.TestRun)]
    [InlineData("dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll run --all --engine \"battlescribe=dotnet:artifacts/bin/BattleScribeSpec.ReferenceAdapter/debug/bs-reference-adapter.dll\"", CiStepKind.CliRun)]
    [InlineData("xvfb-run -a dotnet run --project src/BattleScribeSpec.Cli --no-build -- run --engine battlescribe --ui protocol-kitchen-sink", CiStepKind.CliRun)]
    [InlineData("bs-spec run --all --engine newrecruit", CiStepKind.CliRun)]
    [InlineData("dotnet format whitespace BattleScribeSpec.slnx --no-restore --verify-no-changes", CiStepKind.Other)]
    [InlineData("dotnet build -p:RunAnalyzers=false", CiStepKind.Other)]
    [InlineData("docker run --rm bs-spec:ci --help", CiStepKind.Other)]
    [InlineData("echo \"remember to dotnet test tests/BattleScribeSpec.Tests.csproj\"", CiStepKind.Other)]
    [InlineData("node --test scripts/*.test.mjs", CiStepKind.Other)]
    [InlineData("dotnet run --project ${{ matrix.project }} --no-build -- --test-profile x", CiStepKind.TestRun)]
    [InlineData("dotnet run --project src/BattleScribeSpec.Cli --no-build -- run --engine ${{ matrix.engine }} protocol-kitchen-sink", CiStepKind.CliRun)]
    [InlineData("dotnet run --project src/BattleScribeSpec.NewRecruit.HarTool --no-build -- -o .har-staging", CiStepKind.Other)]
    public void CiTestInvocations_ClassifiesByWhatIsRun(string command, CiStepKind expected)
    {
        Assert.Equal(expected, CiTestInvocations.ClassifyCommand(command).Kind);
    }

    /// <summary>The project a test run names is reported, so coverage can be checked per project.</summary>
    [Fact]
    public void CiTestInvocations_ReportsTheProjectsARunNames()
    {
        var one = CiTestInvocations.ClassifyCommand("pwsh scripts/dotnet-test-step.ps1 -TestProfile core tests/BattleScribeSpec.Tests.csproj --no-build");
        Assert.Equal("tests/BattleScribeSpec.Tests.csproj", Assert.Single(one.Projects));
        Assert.True(one.ThroughTestStepScript);
        Assert.False(one.TargetsSolution);

        var bare = CiTestInvocations.ClassifyCommand("dotnet test -p:TestProfile=pre-push");
        Assert.Empty(bare.Projects);
        Assert.True(bare.TargetsSolution);
        Assert.False(bare.ThroughTestStepScript);
    }

    /// <summary>
    /// <b>A test run's project is read where the step runs, and an expression is never the solution.</b>
    /// </summary>
    /// <remarks>
    /// Relative tokens resolve against the step's working directory, and a run that names no target runs
    /// the one in that directory (the checkout root holds only the solution). An expression where the
    /// project goes — or on a line that names no target literally — leaves the run unresolved: no
    /// project, not the solution. Before this, <c>working-directory: tests</c> with
    /// <c>dotnet run --no-build -- --test-profile bs</c> was not a test step at all, and the wrapper given
    /// <c>${{ env.CLI_TESTS }}</c> counted as a run of every project.
    /// </remarks>
    [Theory]
    [InlineData("dotnet run --no-build -- --test-profile bs", "tests", "tests/BattleScribeSpec.Tests.csproj", false, false)]
    [InlineData("pwsh ../scripts/dotnet-test-step.ps1 BattleScribeSpec.Tests.csproj --no-build", "tests", "tests/BattleScribeSpec.Tests.csproj", false, false)]
    [InlineData("dotnet test --no-build", "tests/BattleScribeSpec.Cli.Tests", "tests/BattleScribeSpec.Cli.Tests/BattleScribeSpec.Cli.Tests.csproj", false, false)]
    [InlineData("dotnet test --no-build", "${{ github.workspace }}/tests/", "tests/BattleScribeSpec.Tests.csproj", false, false)]
    [InlineData("dotnet test --no-build", null, "", true, false)]
    [InlineData("dotnet test --no-build", "${{ github.workspace }}", "", true, false)]
    [InlineData("dotnet test --project ${{ github.workspace }}/tests/BattleScribeSpec.Tests.csproj", null, "tests/BattleScribeSpec.Tests.csproj", false, false)]
    [InlineData("dotnet test tests/BattleScribeSpec.Tests.csproj --filter \"Engine=${{ matrix.engine }}\"", null, "tests/BattleScribeSpec.Tests.csproj", false, false)]
    [InlineData("dotnet test BattleScribeSpec.slnx --filter \"${{ matrix.filter }}\"", null, "", true, false)]
    [InlineData("dotnet run --project ${{ matrix.project }} --no-build -- --test-profile x", null, "", false, true)]
    [InlineData("dotnet test --project=${{ matrix.project }}", null, "", false, true)]
    [InlineData("pwsh scripts/dotnet-test-step.ps1 ${{ env.CLI_TESTS }} --no-build", null, "", false, true)]
    [InlineData("dotnet ${{ env.TEST_DLL }} --list-tests", null, "", false, true)]
    [InlineData("dotnet test --filter \"${{ matrix.filter }}\"", null, "", false, true)]
    [InlineData("dotnet test --no-build", "${{ matrix.directory }}", "", false, true)]
    [InlineData("dotnet run -- --test-profile bs", null, "", false, true)]
    public void CiTestInvocations_ResolvesTheProjectARunNames(
        string command, string? workingDirectory, string projects, bool solution, bool unresolved)
    {
        var invocation = CiTestInvocations.ClassifyCommand(command, workingDirectory: workingDirectory);

        Assert.Equal(CiStepKind.TestRun, invocation.Kind);
        Assert.Equal(projects, string.Join(",", invocation.Projects));
        Assert.Equal(solution, invocation.TargetsSolution);
        Assert.Equal(unresolved, invocation.Unresolved is not null);
    }

    /// <summary>
    /// <b>A step's working directory comes from the workflow</b> — its own <c>working-directory</c>, else
    /// the job's <c>defaults.run</c>, else the workflow's — so the classifier sees a step as GitHub runs it.
    /// </summary>
    [Fact]
    public void CiTestInvocations_ReadTheWorkingDirectoryFromTheWorkflow()
    {
        var workflow = CiWorkflows.Parse("inline.yml", """
            defaults:
              run:
                working-directory: tests/BattleScribeSpec.Cli.Tests
            jobs:
              inherits-workflow:
                steps:
                  - run: dotnet test --no-build
              job-default:
                defaults:
                  run:
                    working-directory: tests
                steps:
                  - run: dotnet run --no-build -- --test-profile bs
                  - working-directory: .
                    run: dotnet test --no-build
            """);

        var classified = workflow.Jobs.SelectMany(static j => j.Steps).Select(CiTestInvocations.Classify).ToList();

        Assert.Equal(3, classified.Count);
        Assert.Equal("tests/BattleScribeSpec.Cli.Tests/BattleScribeSpec.Cli.Tests.csproj", Assert.Single(classified[0].Projects));
        Assert.Equal("tests/BattleScribeSpec.Tests.csproj", Assert.Single(classified[1].Projects));
        Assert.True(classified[2].TargetsSolution);
    }

    /// <summary>
    /// <b>A step that calls a repo script which runs tests is a test step.</b> Otherwise moving the
    /// <c>dotnet test</c> line into <c>scripts/run-lane.sh</c> would take the step out of every lint here.
    /// Prose in the script's comments is not a test run.
    /// </summary>
    [Fact]
    public void CiTestInvocations_FollowsTheScriptsAStepCalls()
    {
        var root = Directory.CreateTempSubdirectory("ci-invocations-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            File.WriteAllText(Path.Combine(root, "scripts", "run-lane.sh"), "#!/bin/bash\nset -e\ndotnet test tests/BattleScribeSpec.Tests.csproj \\\n  --no-build\n");
            File.WriteAllText(Path.Combine(root, "scripts", "outer.ps1"), "<# calls the lane #>\n& bash scripts/run-lane.sh\n");
            File.WriteAllText(Path.Combine(root, "scripts", "notes.sh"), "#!/bin/bash\n# run dotnet test tests/BattleScribeSpec.Tests.csproj by hand\necho hi\n");

            var direct = CiTestInvocations.ClassifyCommand("bash scripts/run-lane.sh", root);
            Assert.Equal(CiStepKind.TestRun, direct.Kind);
            Assert.Equal("scripts/run-lane.sh", direct.FollowedScript);
            Assert.False(direct.ThroughTestStepScript);

            Assert.Equal(CiStepKind.TestRun, CiTestInvocations.ClassifyCommand("pwsh scripts/outer.ps1", root).Kind);
            Assert.Equal(CiStepKind.Other, CiTestInvocations.ClassifyCommand("bash scripts/notes.sh", root).Kind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// <b>The MSBuild properties a command line sets are read in every spelling MSBuild accepts</b>, so
    /// the functional-build lints cannot be dodged by <c>/p:</c>, <c>-property:</c>, a separate value or
    /// a combined switch.
    /// </summary>
    [Theory]
    [InlineData("dotnet build -p:FunctionalBuild=true", "FunctionalBuild=true")]
    [InlineData("dotnet build /p:RunAnalyzers=false", "RunAnalyzers=false")]
    [InlineData("dotnet build -property:A=1 --property:B=2", "A=1 B=2")]
    [InlineData("dotnet build \"-P:A=1;B=2\"", "A=1 B=2")]
    [InlineData("dotnet build -p:A=1,B=2", "A=1 B=2")]
    [InlineData("dotnet build -p A=1", "A=1")]
    [InlineData("dotnet build \"-p:EnforceCodeStyleInBuild=false\"", "EnforceCodeStyleInBuild=false")]
    [InlineData("dotnet build --no-restore", "")]
    [InlineData("dotnet test --project x -- --filter-trait p:x", "")]
    public void CiTestInvocations_ReadsTheMsBuildPropertiesACommandSets(string command, string expected)
    {
        Assert.Equal(expected, string.Join(" ", CiTestInvocations.MsBuildProperties(command).Select(static p => $"{p.Name}={p.Value}")));
    }

    /// <summary>Control operators count only outside quotes and outside <c>${{ }}</c>.</summary>
    [Theory]
    [InlineData("dotnet test x --filter \"(A|B)&C\"", "")]
    [InlineData("dotnet test x --filter 'A|B;C'", "")]
    [InlineData("dotnet test x 2>&1", "")]
    [InlineData("dotnet test x --filter \"Engine=${{ matrix.a || 'b' }}\"", "")]
    [InlineData("dotnet test x --filter Engine=${{ matrix.a || 'b' }}", "")]
    [InlineData("dotnet test x | tee log", "|")]
    [InlineData("dotnet test x || true", "||")]
    [InlineData("dotnet test x && echo ok", "&&")]
    [InlineData("dotnet test x; exit 0", ";")]
    [InlineData("dotnet test x & wait", "&")]
    public void CiTestInvocations_SeesShellOperatorsOnlyOutsideQuotes(string command, string expected)
    {
        Assert.Equal(expected, string.Join(" ", CiTestInvocations.Tokenize(command).Operators));
    }

    /// <summary>
    /// <b>The classifier finds this repo's real steps</b> — test runs and CLI runs both — so every lint
    /// above is checking something, and the <c>bs-spec</c> reference-adapter step is not taken for a test
    /// step.
    /// </summary>
    [Fact]
    public void CiTestInvocations_FindTheVerdictStepsInCi()
    {
        var classified = CiTestInvocations.ClassifiedSteps().Where(static c => c.Step.Job.Workflow == ".github/workflows/ci.yml").ToList();

        Assert.True(
            classified.Count(static c => c.Invocation.Kind == CiStepKind.TestRun) >= 10,
            "Fewer than 10 test steps found in ci.yml — the classifier has stopped recognising them.");
        Assert.Contains(classified, static c => c.Invocation.Kind == CiStepKind.CliRun);

        var referenceAdapter = classified.Single(static c => c.Step.Name == "Reference adapter (dotnet) — roster kitchen-sink");
        Assert.Equal(CiStepKind.CliRun, referenceAdapter.Invocation.Kind);

        // The setup action's steps are classified too — every one of them setup, none a verdict.
        var setup = CiTestInvocations.ClassifiedSteps().Where(static c => c.Step.Job.Workflow == ".github/actions/setup/action.yml").ToList();
        Assert.Contains(setup, static c => c.Step.Name == "Setup dependencies" && c.Invocation.Kind == CiStepKind.Other);
    }

    private static IEnumerable<string> StepReferences(IEnumerable<YamlScalarNode> scalars) =>
        scalars.SelectMany(static s => Regex.Matches(s.Value ?? "", @"\bsteps\.([A-Za-z_][A-Za-z0-9_-]*)"))
            .Select(static m => m.Groups[1].Value);
}
