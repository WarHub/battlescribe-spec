using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace BattleScribeSpec.Tests;

/// <summary>
/// The CI definition as data: every workflow under <c>.github/workflows</c>, parsed as YAML into jobs
/// and steps, and every composite action under <c>.github/actions</c> (its <c>runs.steps</c>, held as a
/// job-shaped <see cref="CiJob"/>). <see cref="CiWorkflowTests"/> reads CI through this rather than
/// through lines of text; the files come from <see cref="CiDefinitionFiles"/>.
/// </summary>
/// <remarks>
/// <b>Why YAML and not lines.</b> The text scans this replaced were defeated in both directions by the
/// shape of the file: a comment that named a project satisfied "this project is run by CI", and "find
/// the step body up to the next <c>- name:</c>" cannot tell a step key from a key inside an <c>env:</c>
/// or <c>with:</c> block. Parsed, a comment is not a value, a step is a mapping, and
/// <c>timeout-minutes</c> on a step is not <c>timeout-minutes</c> on its job.
/// </remarks>
internal static class CiWorkflows
{
    /// <summary>The repository root, from the test binaries' own location.</summary>
    internal static string Root => CiDefinitionFiles.Root;

    private static readonly Lazy<IReadOnlyList<CiWorkflow>> Loaded = new(static () =>
        [.. CiDefinitionFiles.Workflows.Select(static f => Parse(f.Path, f.Text))]);

    private static readonly Lazy<IReadOnlyList<CiJob>> LoadedActions = new(static () =>
        [.. CiDefinitionFiles.Actions.Select(static f => ParseAction(f.Path, f.Text))]);

    /// <summary>Every workflow under <c>.github/workflows</c>, in path order.</summary>
    internal static IReadOnlyList<CiWorkflow> All => Loaded.Value;

    /// <summary>The main workflow, <c>.github/workflows/ci.yml</c>.</summary>
    internal static CiWorkflow Ci => All.Single(static w => w.File == ".github/workflows/ci.yml");

    /// <summary>
    /// Every step CI runs: each job's of every workflow, then each composite action's (none for an action
    /// that is not composite). A step moved into an action is still a step the lints see.
    /// </summary>
    internal static IEnumerable<CiStep> AllSteps =>
        All.SelectMany(static w => w.Jobs).SelectMany(static j => j.Steps).Concat(LoadedActions.Value.SelectMany(static a => a.Steps));

    /// <summary>The path of <paramref name="absolute"/> relative to the root, with forward slashes.</summary>
    internal static string Relative(string absolute) =>
        Path.GetRelativePath(Root, absolute).Replace(Path.DirectorySeparatorChar, '/');

    private static CiJob ParseAction(string file, string yaml)
    {
        var root = LoadDocument(file, yaml);
        var runs = root.Children.TryGetValue(new YamlScalarNode("runs"), out var node) && node is YamlMappingNode map
            ? map
            : throw new InvalidDataException($"{file}: an action needs a `runs:` mapping.");
        return new CiJob(file, "runs.steps", runs);
    }

    private static CiWorkflow Parse(string file, string yaml)
    {
        var root = LoadDocument(file, yaml);
        var jobs = root.Children.TryGetValue(new YamlScalarNode("jobs"), out var jobsNode) && jobsNode is YamlMappingNode jobsMap
            ? jobsMap.Children
                .Where(static j => j.Key is YamlScalarNode { Value: not null } && j.Value is YamlMappingNode)
                .Select(j => new CiJob(file, ((YamlScalarNode)j.Key).Value!, (YamlMappingNode)j.Value))
                .ToList()
            : [];
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

    /// <summary>The value of one entry of <paramref name="node"/>'s <c>env:</c> mapping (a workflow's, a job's or a step's), or null.</summary>
    internal static string? Env(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode("env"), out var env) && env is YamlMappingNode map ? Scalar(map, key) : null;

    /// <summary>Every scalar in <paramref name="node"/>, keys included, depth first.</summary>
    internal static IEnumerable<YamlScalarNode> Scalars(YamlNode node) => node switch
    {
        YamlScalarNode scalar => [scalar],
        YamlSequenceNode sequence => sequence.Children.SelectMany(Scalars),
        YamlMappingNode mapping => mapping.Children.SelectMany(static kv => Scalars(kv.Key).Concat(Scalars(kv.Value))),
        _ => [],
    };

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

/// <summary>One job of a workflow, or a composite action's steps.</summary>
internal sealed class CiJob
{
    public CiJob(string workflow, string id, YamlMappingNode node)
    {
        Workflow = workflow;
        Id = id;
        Node = node;
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

    public string? TimeoutMinutes => CiWorkflows.Scalar(Node, "timeout-minutes");

    /// <summary>Where this job starts, for messages: <c>file:line job</c>.</summary>
    public string Where => $"{Workflow}:{Node.Start.Line} {Id}";
}

/// <summary>One step of a job.</summary>
internal sealed class CiStep(CiJob job, int index, YamlMappingNode node)
{
    public CiJob Job { get; } = job;

    public YamlMappingNode Node { get; } = node;

    /// <summary>The step's keys, as written (<c>name</c>, <c>if</c>, <c>run</c>, …).</summary>
    public IEnumerable<string> Keys => Node.Children.Keys.OfType<YamlScalarNode>().Select(static k => k.Value ?? "");

    public string? Name => CiWorkflows.Scalar(Node, "name");

    public string? Uses => CiWorkflows.Scalar(Node, "uses");

    public string? Run => CiWorkflows.Scalar(Node, "run");

    public string? If => CiWorkflows.Scalar(Node, "if");

    public string? TimeoutMinutes => CiWorkflows.Scalar(Node, "timeout-minutes");

    /// <summary>The value of one <c>with:</c> input on this step, or null.</summary>
    public string? With(string key) =>
        Node.Children.TryGetValue(new YamlScalarNode("with"), out var with) && with is YamlMappingNode map
            ? CiWorkflows.Scalar(map, key)
            : null;

    /// <summary>Where this step is, for messages: <c>file:line job / label</c>.</summary>
    public string Where => $"{Job.Workflow}:{Node.Start.Line} {Job.Id} / {Name ?? CiWorkflows.Scalar(Node, "id") ?? Uses ?? $"step {index + 1}"}";
}
