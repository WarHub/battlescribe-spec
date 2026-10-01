namespace BattleScribeSpec.Tests;

/// <summary>What a CI definition file is.</summary>
internal enum CiDefinitionKind
{
    /// <summary>A workflow under <c>.github/workflows</c>.</summary>
    Workflow,

    /// <summary>An action's metadata file, <c>.github/actions/&lt;name&gt;/action.yml</c>.</summary>
    Action,
}

/// <summary>One CI definition file, read once.</summary>
/// <param name="Path">Repo-relative, forward slashes.</param>
/// <param name="Kind">Workflow or action.</param>
/// <param name="Text">The file's contents.</param>
internal sealed record CiDefinitionFile(string Path, CiDefinitionKind Kind, string Text)
{
    /// <summary>The file's lines, numbered from 1.</summary>
    public IEnumerable<(int Number, string Text)> Lines() =>
        Text.Split('\n').Select(static (line, i) => (i + 1, line.TrimEnd('\r')));
}

/// <summary>
/// <b>Every file that defines what CI runs: the workflows and the composite actions they call.</b> The
/// one enumerator behind every scan of the CI definition — <see cref="CiWorkflows"/> (and so
/// <see cref="CiTestInvocations"/>, the step-reference lint and the functional-build flag ban), the
/// setup-dotnet scan in <see cref="ToolchainPinDriftTests"/> and the retired-knob scan in
/// <see cref="ConcurrencyConfigurationDriftTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one, and why it includes actions.</b> Each of those scans used to walk
/// <c>.github/workflows</c> on its own. Moving the job setup into <c>.github/actions/setup</c> put a
/// <c>setup-dotnet</c> step, every cache and the app token in a file none of them read — so a floating
/// <c>dotnet-version:</c> or a retired knob in the action would have passed every lint that exists to
/// forbid it, and a test step moved there would have left every test-step rule. A scan of the CI
/// definition that skips part of the definition is the vacuous-truth failure these lints are written
/// against.
/// </para>
/// <para>
/// So every consumer calls <see cref="AssertReadAnAction"/> on what it actually scanned: a consumer
/// that silently stopped seeing actions — a moved directory, a renamed file, an enumerator change —
/// goes red instead of passing on workflows alone.
/// </para>
/// </remarks>
internal static class CiDefinitionFiles
{
    /// <summary>Where workflows live, relative to the root.</summary>
    internal const string WorkflowsDirectory = ".github/workflows";

    /// <summary>Where this repo's own actions live, relative to the root.</summary>
    internal const string ActionsDirectory = ".github/actions";

    /// <summary>The repository root, from the test binaries' own location.</summary>
    internal static string Root { get; } = RepoRoot.FromBinaries
        ?? throw new DirectoryNotFoundException(
            $"No {RepoRoot.MarkerFileName} above {AppContext.BaseDirectory}: the CI lints read the checkout's .github/ and scripts/.");

    private static readonly Lazy<IReadOnlyList<CiDefinitionFile>> Loaded = new(Load);

    /// <summary>Every workflow and action file, workflows first, each group in path order.</summary>
    internal static IReadOnlyList<CiDefinitionFile> All => Loaded.Value;

    /// <summary>The workflows.</summary>
    internal static IEnumerable<CiDefinitionFile> Workflows => All.Where(static f => f.Kind == CiDefinitionKind.Workflow);

    /// <summary>The actions.</summary>
    internal static IEnumerable<CiDefinitionFile> Actions => All.Where(static f => f.Kind == CiDefinitionKind.Action);

    /// <summary>Every line of every file, numbered from 1 per file.</summary>
    internal static IEnumerable<(CiDefinitionFile File, int Number, string Text)> Lines() =>
        All.SelectMany(static f => f.Lines().Select(l => (f, l.Number, l.Text)));

    /// <summary>
    /// Fails the calling lint unless <paramref name="scanned"/> — the repo-relative paths it actually
    /// read — includes an action file, so a scan that stopped seeing <c>.github/actions</c> cannot pass
    /// on the workflows alone.
    /// </summary>
    internal static void AssertReadAnAction(IEnumerable<string> scanned, string lint)
    {
        var read = scanned.ToHashSet(StringComparer.Ordinal);
        var actions = Actions.Select(static a => a.Path).ToList();
        Assert.True(
            actions.Count > 0 && actions.Any(read.Contains),
            $"{lint} scanned no file under {ActionsDirectory}/ " +
            (actions.Count == 0
                ? $"— and there is none: the setup action every building job uses has moved or been renamed, so {lint} " +
                  "is checking the workflows only. Point CiDefinitionFiles at its new home."
                : $"(it read {read.Count} file(s), none of them {string.Join(", ", actions)}). The composite setup action " +
                  "holds setup-dotnet, the caches and the app token; a scan that skips it checks part of CI and reports all of it."));
    }

    private static List<CiDefinitionFile> Load()
    {
        var workflows = Find(WorkflowsDirectory, static name => name.EndsWith(".yml", StringComparison.Ordinal) || name.EndsWith(".yaml", StringComparison.Ordinal))
            .Select(static f => Read(f, CiDefinitionKind.Workflow))
            .ToList();

        if (workflows.Count == 0)
        {
            throw new InvalidOperationException($"No workflow files under {Path.Combine(Root, WorkflowsDirectory)}: every CI lint would pass by vacuous truth.");
        }

        var actions = Find(ActionsDirectory, static name => name is "action.yml" or "action.yaml")
            .Select(static f => Read(f, CiDefinitionKind.Action));

        return [.. workflows, .. actions];
    }

    private static IEnumerable<string> Find(string relativeDirectory, Func<string, bool> name)
    {
        var directory = Path.Combine(Root, relativeDirectory);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(f => name(Path.GetFileName(f)))
                .Order(StringComparer.Ordinal)
            : [];
    }

    private static CiDefinitionFile Read(string absolute, CiDefinitionKind kind) =>
        new(Path.GetRelativePath(Root, absolute).Replace(Path.DirectorySeparatorChar, '/'), kind, File.ReadAllText(absolute));
}
