using System.Globalization;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace BattleScribeSpec.Tests;

/// <summary>
/// The CI definition as data: every workflow under <c>.github/workflows</c>, parsed as YAML into jobs
/// and steps, every composite action under <c>.github/actions</c> (its <c>runs.steps</c>, held as a
/// job-shaped <see cref="CiJob"/>), plus <c>scripts/ci-gate.json</c>. The workflow lints read CI
/// through this rather than through lines of text; the files come from <see cref="CiDefinitionFiles"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why YAML and not lines.</b> The text scans this replaces were defeated in both directions by the
/// shape of the file: a comment that named a project satisfied "this project is run by CI" (the first
/// draft of <c>EveryTestProject_IsRunBySomeCiStep</c> was beaten by the comment three lines above the
/// step it guarded), and "find the step body up to the next <c>- name:</c>" cannot tell a step key from
/// a key inside an <c>env:</c> or <c>with:</c> block. Parsed, a comment is not a value, a step is a
/// mapping, and <c>timeout-minutes</c> on a step is not <c>timeout-minutes</c> on its job.
/// </para>
/// </remarks>
internal static class CiWorkflows
{
    /// <summary>The repository root, from the test binaries' own location.</summary>
    internal static string Root => CiDefinitionFiles.Root;

    /// <summary>What <see cref="CiJob.Id"/> is for a composite action's steps.</summary>
    internal const string ActionStepsId = "runs.steps";

    private static readonly Lazy<IReadOnlyList<CiWorkflow>> Loaded = new(LoadAll);

    private static readonly Lazy<IReadOnlyList<CiJob>> LoadedActions = new(LoadActions);

    /// <summary>Every workflow under <c>.github/workflows</c>, in path order.</summary>
    internal static IReadOnlyList<CiWorkflow> All => Loaded.Value;

    /// <summary>
    /// Every action under <c>.github/actions</c>, as a job whose steps are its <c>runs.steps</c> (none
    /// for an action that is not composite). Not a workflow job: it has no <c>needs</c>, no
    /// <c>timeout-minutes</c> and no runner, so the job-level lints do not read it — the step-level ones do.
    /// </summary>
    internal static IReadOnlyList<CiJob> Actions => LoadedActions.Value;

    /// <summary>The main workflow, <c>.github/workflows/ci.yml</c>.</summary>
    internal static CiWorkflow Ci => All.Single(static w => w.File == ".github/workflows/ci.yml");

    /// <summary>
    /// Every step CI runs: each job's of every workflow, then each composite action's. A step moved into
    /// an action is still a step every step-level lint sees.
    /// </summary>
    internal static IEnumerable<CiStep> AllSteps =>
        All.SelectMany(static w => w.Jobs).SelectMany(static j => j.Steps).Concat(Actions.SelectMany(static a => a.Steps));

    /// <summary>The path of <paramref name="absolute"/> relative to the root, with forward slashes.</summary>
    internal static string Relative(string absolute) =>
        Path.GetRelativePath(Root, absolute).Replace(Path.DirectorySeparatorChar, '/');

    private static List<CiWorkflow> LoadAll() =>
        [.. CiDefinitionFiles.Workflows.Select(static f => Parse(f.Path, f.Text))];

    private static List<CiJob> LoadActions() =>
        [.. CiDefinitionFiles.Actions.Select(static f => ParseAction(f.Path, f.Text))];

    /// <summary>Parses one action's metadata. Public to the tests so a lint's rules can be exercised on inline YAML.</summary>
    internal static CiJob ParseAction(string file, string yaml)
    {
        var root = LoadDocument(file, yaml);
        var runs = root.Children.TryGetValue(new YamlScalarNode("runs"), out var node) && node is YamlMappingNode map
            ? map
            : throw new InvalidDataException($"{file}: an action needs a `runs:` mapping.");
        return new CiJob(file, ActionStepsId, runs);
    }

    /// <summary>Parses one workflow document. Public to the tests so a lint's own rules can be exercised on inline YAML.</summary>
    internal static CiWorkflow Parse(string file, string yaml)
    {
        var root = LoadDocument(file, yaml);
        var jobs = new List<CiJob>();
        var defaults = RunDefaults(root);

        if (root.Children.TryGetValue(new YamlScalarNode("jobs"), out var jobsNode) && jobsNode is YamlMappingNode jobsMap)
        {
            foreach (var (key, value) in jobsMap.Children)
            {
                if (key is YamlScalarNode { Value: { } id } && value is YamlMappingNode job)
                {
                    jobs.Add(new CiJob(file, id, job, defaults));
                }
            }
        }

        return new CiWorkflow(file, root, jobs);
    }

    /// <summary>Loads a YAML file's single document as a mapping (any YAML under <c>.github</c>, not only workflows).</summary>
    internal static YamlMappingNode LoadDocument(string file, string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException($"{file}: expected one YAML document with a mapping at its root.");
        }

        return root;
    }

    /// <summary>The scalar value at <paramref name="key"/>, or null when it is absent or not a scalar.</summary>
    internal static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    /// <summary>
    /// A workflow's or job's <c>defaults.run</c> block (<c>working-directory</c>, <c>shell</c>), or an
    /// empty mapping. A step inherits from its job, the job from the workflow.
    /// </summary>
    internal static YamlMappingNode RunDefaults(YamlMappingNode node) =>
        node.Children.TryGetValue(new YamlScalarNode("defaults"), out var defaults)
        && defaults is YamlMappingNode map
        && map.Children.TryGetValue(new YamlScalarNode("run"), out var run)
        && run is YamlMappingNode runMap
            ? runMap
            : [];

    /// <summary>Every scalar in <paramref name="node"/>, keys included, depth first.</summary>
    internal static IEnumerable<YamlScalarNode> Scalars(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                yield return scalar;
                break;
            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    foreach (var s in Scalars(child))
                    {
                        yield return s;
                    }
                }

                break;
            case YamlMappingNode mapping:
                foreach (var (key, value) in mapping.Children)
                {
                    foreach (var s in Scalars(key))
                    {
                        yield return s;
                    }

                    foreach (var s in Scalars(value))
                    {
                        yield return s;
                    }
                }

                break;
        }
    }

    /// <summary>
    /// A <c>timeout-minutes</c> value as a number, or null when it is absent, not a plain number, or not
    /// positive. An expression is refused on purpose: a bound nobody can read is a bound nobody checked.
    /// </summary>
    internal static double? Minutes(string? value) =>
        double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
            ? minutes
            : null;
}

/// <summary>One workflow file.</summary>
internal sealed record CiWorkflow(string File, YamlMappingNode Node, IReadOnlyList<CiJob> Jobs)
{
    /// <summary>The job with this id; fails the calling test, naming the file, when there is none.</summary>
    public CiJob Job(string id) =>
        Jobs.SingleOrDefault(j => j.Id == id)
        ?? throw new InvalidOperationException($"{File} has no job '{id}'. If it was renamed, update the lint that names it.");
}

/// <summary>One job of a workflow.</summary>
internal sealed class CiJob
{
    private readonly YamlMappingNode _workflowRunDefaults;

    public CiJob(string workflow, string id, YamlMappingNode node, YamlMappingNode? workflowRunDefaults = null)
    {
        Workflow = workflow;
        Id = id;
        Node = node;
        _workflowRunDefaults = workflowRunDefaults ?? [];
        Steps = node.Children.TryGetValue(new YamlScalarNode("steps"), out var steps) && steps is YamlSequenceNode sequence
            ? [.. sequence.Children.OfType<YamlMappingNode>().Select((s, i) => new CiStep(this, i, s))]
            : [];
        Needs = node.Children.TryGetValue(new YamlScalarNode("needs"), out var needs)
            ? needs switch
            {
                YamlScalarNode { Value: { } one } => [one],
                YamlSequenceNode many => [.. many.Children.OfType<YamlScalarNode>().Select(static n => n.Value ?? "")],
                _ => [],
            }
            : [];
    }

    public string Workflow { get; }

    public string Id { get; }

    public YamlMappingNode Node { get; }

    public IReadOnlyList<CiStep> Steps { get; }

    public IReadOnlyList<string> Needs { get; }

    public string? If => CiWorkflows.Scalar(Node, "if");

    public string? TimeoutMinutes => CiWorkflows.Scalar(Node, "timeout-minutes");

    public string? ContinueOnError => CiWorkflows.Scalar(Node, "continue-on-error");

    /// <summary>The <c>defaults.run</c> value for <paramref name="key"/>: the job's, else the workflow's.</summary>
    public string? RunDefault(string key) =>
        CiWorkflows.Scalar(CiWorkflows.RunDefaults(Node), key) ?? CiWorkflows.Scalar(_workflowRunDefaults, key);

    /// <summary>Where this job starts, for messages: <c>file:line job</c>.</summary>
    public string Where => $"{Workflow}:{Node.Start.Line} {Id}";
}

/// <summary>One step of a job.</summary>
internal sealed class CiStep(CiJob job, int index, YamlMappingNode node)
{
    public CiJob Job { get; } = job;

    public int Index { get; } = index;

    public YamlMappingNode Node { get; } = node;

    public string? Id => CiWorkflows.Scalar(Node, "id");

    public string? Name => CiWorkflows.Scalar(Node, "name");

    public string? Uses => CiWorkflows.Scalar(Node, "uses");

    public string? Run => CiWorkflows.Scalar(Node, "run");

    public string? If => CiWorkflows.Scalar(Node, "if");

    /// <summary>The shell this step runs under: its own <c>shell</c>, else the job's or workflow's <c>defaults.run.shell</c>.</summary>
    public string? Shell => CiWorkflows.Scalar(Node, "shell") ?? Job.RunDefault("shell");

    /// <summary>
    /// The directory this step's <c>run</c> starts in, as written: its own <c>working-directory</c>,
    /// else the job's or workflow's <c>defaults.run.working-directory</c>; null for the checkout root.
    /// </summary>
    public string? WorkingDirectory => CiWorkflows.Scalar(Node, "working-directory") ?? Job.RunDefault("working-directory");

    public string? TimeoutMinutes => CiWorkflows.Scalar(Node, "timeout-minutes");

    public string? ContinueOnError => CiWorkflows.Scalar(Node, "continue-on-error");

    /// <summary>The value of one <c>env:</c> entry on this step, or null.</summary>
    public string? Env(string key) =>
        Node.Children.TryGetValue(new YamlScalarNode("env"), out var env) && env is YamlMappingNode map
            ? CiWorkflows.Scalar(map, key)
            : null;

    /// <summary>The value of one <c>with:</c> input on this step, or null.</summary>
    public string? With(string key) =>
        Node.Children.TryGetValue(new YamlScalarNode("with"), out var with) && with is YamlMappingNode map
            ? CiWorkflows.Scalar(map, key)
            : null;

    /// <summary>What a reader calls this step: its name, else its id, else what it uses.</summary>
    public string Label => Name ?? Id ?? Uses ?? $"step {Index + 1}";

    /// <summary>Where this step is, for messages: <c>file:line job / label</c>.</summary>
    public string Where => $"{Job.Workflow}:{Node.Start.Line} {Job.Id} / {Label}";
}

/// <summary>
/// <c>scripts/ci-gate.json</c>: the inputs that force the thorough suites, and the jobs gated on the
/// gate's <c>thorough</c> and <c>live</c> outputs. The node scripts read the same file.
/// </summary>
internal sealed record CiGateConfig(IReadOnlyList<string> ThoroughInputs, IReadOnlyList<string> ThoroughJobs, IReadOnlyList<string> LiveJobs)
{
    public const string RelativePath = "scripts/ci-gate.json";

    public static CiGateConfig Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(CiWorkflows.Root, "scripts", "ci-gate.json")));
        var root = doc.RootElement;
        return new CiGateConfig(
            [.. root.GetProperty("thoroughInputs").EnumerateArray().Select(static e => e.GetProperty("path").GetString()!)],
            [.. root.GetProperty("thoroughJobs").EnumerateArray().Select(static e => e.GetString()!)],
            [.. root.GetProperty("liveJobs").EnumerateArray().Select(static e => e.GetString()!)]);
    }
}
