using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BattleScribeSpec.Tests;

/// <summary>What a CI step runs, as far as the workflow lints care.</summary>
public enum CiStepKind
{
    /// <summary>Neither a test run nor a <c>bs-spec</c> run.</summary>
    Other,

    /// <summary>Runs one of this repo's test projects, by any spelling.</summary>
    TestRun,

    /// <summary>Runs <c>bs-spec</c> as a verdict (a CLI gate step) — not a test project.</summary>
    CliRun,
}

/// <summary>A classified step or command line.</summary>
/// <param name="Kind">What it runs.</param>
/// <param name="Projects">Repo-relative csproj paths of the test projects it names.</param>
/// <param name="TargetsSolution">
/// A test run of the whole solution — a solution file named on the line, or a <c>dotnet test</c> that
/// names no target at all and starts at the checkout root, where the one thing for it to find is
/// <c>BattleScribeSpec.slnx</c>. It covers every test project. Never set for a run whose target is
/// merely unknown: an expression is not the solution.
/// </param>
/// <param name="FollowedScript">The repo script it calls that turned out to run tests, if that is how it was identified.</param>
/// <param name="Unresolved">
/// Why the classifier cannot tell which project this run executes — an expression where the project
/// goes, or nothing literal to go on — or null. Such a run is a <see cref="CiStepKind.TestRun"/> (so the
/// timeout, one-invocation and no-swallow lints still hold it) that covers no project, and
/// <c>CiWorkflowDriftTests.EveryTestStep_NamesItsProject</c> fails on it.
/// </param>
internal sealed record CiInvocation(
    CiStepKind Kind,
    IReadOnlyList<string> Projects,
    bool TargetsSolution,
    string? FollowedScript,
    string? Unresolved = null)
{
    public static readonly CiInvocation None = new(CiStepKind.Other, [], false, null);
}

/// <summary>A project the classifier recognises by identity: csproj path, directory, assembly name.</summary>
internal sealed record CiProject(string RelativePath, string Directory, bool DirectoryHasOneProject, string AssemblyName)
{
    /// <summary>Whether a (normalised) command-line token names this project or its build output.</summary>
    public bool IsNamedBy(string token)
    {
        if (token == RelativePath || (DirectoryHasOneProject && token.TrimEnd('/') == Directory))
        {
            return true;
        }

        // An absolute spelling (`/home/runner/work/x/x/tests/…`, `D:/a/x/x/tests/…`): the repo-relative
        // path as its tail. Only for absolute tokens — `.deps/wham/tests` must not read as `tests`.
        if (CiTestInvocations.IsAbsolute(token)
            && (token.EndsWith($"/{RelativePath}", StringComparison.Ordinal)
                || (DirectoryHasOneProject && token.TrimEnd('/').EndsWith($"/{Directory}", StringComparison.Ordinal))))
        {
            return true;
        }

        var leaf = token[(token.LastIndexOf('/') + 1)..];
        return leaf == AssemblyName || leaf == $"{AssemblyName}.dll" || leaf == $"{AssemblyName}.exe";
    }
}

/// <summary>
/// <b>Which CI steps run tests — identified by WHAT they run, not by how the line is spelled.</b> One
/// definition, shared by every workflow lint that has to find the test steps.
/// </summary>
/// <remarks>
/// <para>
/// A step is a <see cref="CiStepKind.TestRun"/> when its command line names one of this repo's test
/// projects — the <c>.slnx</c> projects that reference xunit — by csproj path, by project directory, or
/// by assembly (<c>BattleScribeSpec.Tests</c>, <c>.dll</c>, <c>.exe</c>), whatever the verb and whatever
/// the flag order; when it runs <c>dotnet test</c> or <c>--test-profile</c>; or when it calls a
/// <c>scripts/</c> file whose own code does any of those. A step that runs
/// <c>bs-spec</c> (the <c>src/BattleScribeSpec.Cli</c> project, or its <c>bs-spec</c> assembly) is a
/// <see cref="CiStepKind.CliRun"/>: a verdict step, but not a test project.
/// </para>
/// <para>
/// <b>Paths are read where the step runs.</b> A relative token resolves against the step's
/// <c>working-directory</c> (else the job's, else the workflow's <c>defaults.run</c>), and a
/// <c>dotnet test</c> or <c>dotnet run</c> that names no target runs whatever project that
/// directory holds — the checkout root holds only the solution. <c>working-directory: tests</c> with
/// <c>dotnet run --no-build -- --test-profile bs</c> is a run of <c>tests/BattleScribeSpec.Tests.csproj</c>,
/// not an unclassified step that every lint here skips.
/// </para>
/// <para>
/// <b>An expression is not a project.</b> <c>${{ … }}</c> is opaque, so a run whose project is one
/// (<c>--project ${{ matrix.project }}</c>, <c>dotnet test ${{ env.X }}</c>, <c>dotnet ${{ … }}</c>),
/// or which names no target literally while an expression sits on its line, is
/// <see cref="CiInvocation.Unresolved"/>: still a test run for every lint that bounds a step, never the
/// whole solution for the coverage lint, and a failure of its own. The alternative — counting it as a
/// solution run — let a matrix over projects satisfy <c>EveryTestProject_IsRunBySomeCiStep</c> for a
/// project no step names. <c>${{ github.workspace }}</c> is the one expression read as a path.
/// </para>
/// <para>
/// <b>Why identity.</b> The scans this replaces matched literal substrings — <c>dotnet test</c>, a path
/// prefix — and the review of the MTP plan found them failing open on every spelling a migration
/// produces: <c>dotnet run --no-build --project tests/…</c>, the test exe by path, reordered flags,
/// folded lines, a wrapper script. Substring matching also false-redded the other way, because
/// <c>artifacts/bin/BattleScribeSpec</c> is a prefix of the <c>bs-spec</c> CLI's own path. The project
/// list comes from <c>BattleScribeSpec.slnx</c>, so a third test project is recognised without an edit
/// here.
/// </para>
/// <para>
/// <b>Every step CI runs, composite actions included.</b> <see cref="ClassifiedSteps"/> reads the steps
/// of <c>.github/actions</c> as well as the workflows' (<see cref="CiWorkflows.AllSteps"/>), and fails
/// the calling lint if it read no action — so a test step moved into the setup action is still a test
/// step to every lint that holds one, and a classifier that stopped seeing actions is a red run rather
/// than a quiet one.
/// </para>
/// <para>
/// <b>A shell's quoted command string is read, and refused.</b> <c>pwsh -c "dotnet run … --filter x"</c>
/// tokenises as one word, so the run inside it used to be no test step at all — invisible to every rule
/// about profiles, filters and swallowed exit codes. The string is now classified as a command of its
/// own, and a verdict run inside it is <see cref="CiInvocation.Unresolved"/>: run the command directly.
/// </para>
/// <para>
/// Tokenising is quote-aware (<c>--filter "(A|B)&amp;C"</c> holds no shell operator) and treats
/// <c>${{ … }}</c> as one opaque token. A step's <c>run</c> is split into commands on newlines after
/// joining backslash continuations (and backtick continuations under <c>pwsh</c>); comment lines are
/// dropped.
/// </para>
/// </remarks>
internal static class CiTestInvocations
{
    /// <summary>The CLI's assembly name; its project is found in the <c>.slnx</c> by it.</summary>
    internal const string CliAssemblyName = "bs-spec";

    /// <summary>What a <c>${{ … }}</c> expression becomes in a token: opaque, and never a path.</summary>
    internal const string Expression = "${{…}}";

    private static readonly Lazy<(IReadOnlyList<CiProject> Tests, CiProject Cli)> Projects = new(LoadProjects);

    /// <summary>Every test project in <c>BattleScribeSpec.slnx</c>.</summary>
    internal static IReadOnlyList<CiProject> TestProjects => Projects.Value.Tests;

    /// <summary>The <c>bs-spec</c> CLI project.</summary>
    internal static CiProject CliProject => Projects.Value.Cli;

    /// <summary>
    /// Every step in every workflow and composite action, with its classification. Fails the calling
    /// lint if no step came from an action (<see cref="CiDefinitionFiles.AssertReadAnAction"/>).
    /// </summary>
    internal static IEnumerable<(CiStep Step, CiInvocation Invocation)> ClassifiedSteps()
    {
        var steps = CiWorkflows.AllSteps.ToList();
        CiDefinitionFiles.AssertReadAnAction(steps.Select(static s => s.Job.Workflow), nameof(CiTestInvocations));
        return steps.Select(static s => (s, Classify(s)));
    }

    /// <summary>Steps that run a test project or <c>bs-spec</c>: the steps whose exit code is a verdict.</summary>
    internal static IEnumerable<(CiStep Step, CiInvocation Invocation)> VerdictSteps() =>
        ClassifiedSteps().Where(static c => c.Invocation.Kind != CiStepKind.Other);

    /// <summary>Classifies one step: a test run if any of its commands is one, else a CLI run if any is.</summary>
    internal static CiInvocation Classify(CiStep step) =>
        step.Run is { } run
            ? Combine(Commands(run, step.Shell).Select(c => ClassifyCommand(c, workingDirectory: step.WorkingDirectory)))
            : CiInvocation.None;

    /// <summary>
    /// The commands in a <c>run:</c> block: continuation lines joined, comments and blank lines dropped.
    /// More than one command in a test step is itself a finding (a second command's exit code is the
    /// step's).
    /// </summary>
    internal static List<string> Commands(string run, string? shell = null)
    {
        var commands = new List<string>();
        var pending = new StringBuilder();
        var backtick = shell is not null && shell.StartsWith("pwsh", StringComparison.Ordinal);

        foreach (var raw in run.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (pending.Length == 0 && (line.TrimStart().StartsWith('#') || string.IsNullOrWhiteSpace(line)))
            {
                continue;
            }

            if (line.EndsWith('\\') || (backtick && line.EndsWith('`')))
            {
                pending.Append(line, 0, line.Length - 1).Append(' ');
                continue;
            }

            commands.Add(pending.Append(line).ToString().Trim());
            pending.Clear();
        }

        if (pending.Length > 0)
        {
            commands.Add(pending.ToString().Trim());
        }

        return commands;
    }

    /// <summary>
    /// Classifies one command line. <paramref name="scriptRoot"/> is where <c>scripts/…</c> resolves (the
    /// repo by default); <paramref name="workingDirectory"/> is the step's, as written in the workflow.
    /// </summary>
    internal static CiInvocation ClassifyCommand(string command, string? scriptRoot = null, string? workingDirectory = null) =>
        ClassifyCommand(command, scriptRoot ?? CiWorkflows.Root, WorkingDirectoryOf(workingDirectory), depth: 0, visited: new HashSet<string>(StringComparer.Ordinal));

    private static CiInvocation ClassifyCommand(string command, string scriptRoot, string wd, int depth, HashSet<string> visited)
    {
        // `raw` keeps word order for verbs; `paths` is every way a token can name a file, resolved
        // against the working directory.
        var raw = Tokenize(command).Tokens.Select(Normalise).ToList();

        // A shell handed the command as one string (`pwsh -c "dotnet run …"`): read as a command line of
        // its own, and a verdict run inside it is unresolved — to every rule that reads a step's profile,
        // arguments and exit-code handling, the whole run is one opaque token.
        if (depth < 3 && ShellCommandString(Tokenize(command).Tokens) is { } wrapped)
        {
            var inner = ClassifyCommand(wrapped.Command, scriptRoot, wd, depth + 1, visited);
            if (inner.Kind != CiStepKind.Other)
            {
                return new CiInvocation(inner.Kind, [], false, inner.FollowedScript,
                    $"it runs `{wrapped.Command}` inside `{wrapped.Shell}`, as one quoted string: the workflow lints cannot read the "
                    + "project, profile, arguments or exit-code handling of a run in there. Run the command directly");
            }
        }
        var paths = raw.SelectMany(Spellings).Select(t => Locate(t, wd)).Distinct(StringComparer.Ordinal).ToList();
        var pairs = raw.Zip(raw.Skip(1)).ToList();

        var dotnetTest = pairs.Any(static p => IsDotnet(p.First) && p.Second == "test");
        var dotnetRun = pairs.Any(static p => IsDotnet(p.First) && p.Second == "run");
        var profile = raw.Any(static t => t == "--test-profile" || t.StartsWith("--test-profile=", StringComparison.Ordinal));
        var runsAProject = dotnetTest || dotnetRun;
        var testish = dotnetTest || profile;

        var hasExpression = raw.Any(static t => t.Contains(Expression, StringComparison.Ordinal));
        var expressionTarget =
            pairs.Any(static p => (p.First == "--project" || IsDotnet(p.First)) && p.Second.Contains(Expression, StringComparison.Ordinal))
            || raw.Any(static t => t.StartsWith("--project=", StringComparison.Ordinal) && t.Contains(Expression, StringComparison.Ordinal));
        var namesTarget =
            raw.Any(static t => t == "--project" || t.StartsWith("--project=", StringComparison.Ordinal))
            || paths.Any(t => IsProjectOrSolutionFile(t) || IsProjectDirectory(scriptRoot, t));
        var wdIsExpression = wd.Contains(Expression, StringComparison.Ordinal);

        // dotnet runs the project or solution in its working directory when the line names none.
        var implicitTarget = runsAProject && !namesTarget && wd.Length > 0 && !wdIsExpression;
        if (implicitTarget)
        {
            paths.Add(wd);
        }

        var projects = TestProjects.Where(p => paths.Any(p.IsNamedBy)).Select(static p => p.RelativePath).ToList();
        if (projects.Count > 0)
        {
            return new CiInvocation(CiStepKind.TestRun, projects, TargetsSolution: false, FollowedScript: null);
        }

        var unresolved =
            expressionTarget ? "the project it runs is an expression"
            : (runsAProject || testish) && wdIsExpression ? "its working directory is an expression"
            : (runsAProject || testish) && hasExpression && !namesTarget && !implicitTarget
                ? "it names no project or solution literally, and an expression on its line could be one"
            : dotnetRun && !namesTarget && !implicitTarget ? "`dotnet run` names no project, and the checkout root holds none"
            : null;

        if (testish || unresolved is not null)
        {
            var solution = unresolved is null
                && (paths.Any(static t => t.EndsWith(".slnx", StringComparison.Ordinal) || t.EndsWith(".sln", StringComparison.Ordinal))
                    || (dotnetTest && !namesTarget && wd.Length == 0));
            return new CiInvocation(CiStepKind.TestRun, [], solution, FollowedScript: null, unresolved);
        }

        // A repo script this command calls (not a glob: the shell expands those into the files themselves).
        if (depth < 3)
        {
            foreach (var token in paths.Where(static t => t.StartsWith("scripts/", StringComparison.Ordinal) && t.IndexOfAny(['*', '?']) < 0))
            {
                var path = Path.Combine(scriptRoot, token);
                if (!File.Exists(path) || !visited.Add(token))
                {
                    continue;
                }

                var inner = Combine(Commands(StripComments(File.ReadAllText(path), Path.GetExtension(path)))
                    .Select(c => ClassifyCommand(c, scriptRoot, wd, depth + 1, visited)));
                if (inner.Kind == CiStepKind.TestRun)
                {
                    return inner with { FollowedScript = inner.FollowedScript ?? token };
                }
            }
        }

        return paths.Any(CliProject.IsNamedBy)
            ? CiInvocation.None with { Kind = CiStepKind.CliRun }
            : CiInvocation.None;
    }

    private static CiInvocation Combine(IEnumerable<CiInvocation> parts)
    {
        var list = parts.ToList();
        var tests = list.Where(static p => p.Kind == CiStepKind.TestRun).ToList();
        if (tests.Count > 0)
        {
            return new CiInvocation(
                CiStepKind.TestRun,
                [.. tests.SelectMany(static t => t.Projects).Distinct(StringComparer.Ordinal)],
                tests.Any(static t => t.TargetsSolution),
                tests.Select(static t => t.FollowedScript).FirstOrDefault(static s => s is not null),
                tests.Select(static t => t.Unresolved).FirstOrDefault(static u => u is not null));
        }

        var cli = list.Where(static p => p.Kind == CiStepKind.CliRun).ToList();
        return cli.Count > 0
            ? CiInvocation.None with { Kind = CiStepKind.CliRun, Unresolved = cli.Select(static c => c.Unresolved).FirstOrDefault(static u => u is not null) }
            : CiInvocation.None;
    }

    /// <summary>
    /// The command a shell is handed as one string — <c>pwsh -c "…"</c> (or <c>-Command</c>,
    /// <c>-CommandWithArgs</c>), <c>bash -c '…'</c> (or <c>-lc</c>, <c>-ec</c>: any flag cluster holding
    /// <c>c</c>), <c>cmd /c …</c> — with the shell's spelling; null when the line hands no shell a string.
    /// </summary>
    internal static (string Shell, string Command)? ShellCommandString(IReadOnlyList<string> tokens)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var leaf = Normalise(tokens[i]);
            leaf = leaf[(leaf.LastIndexOf('/') + 1)..].ToLowerInvariant();
            leaf = leaf.EndsWith(".exe", StringComparison.Ordinal) ? leaf[..^4] : leaf;
            Func<string, bool>? isCommandFlag = leaf switch
            {
                "pwsh" or "powershell" => static f => f.ToLowerInvariant() is "-c" or "-command" or "-cwa" or "-commandwithargs"
                    || (f.Length >= 4 && "-command".StartsWith(f, StringComparison.OrdinalIgnoreCase)),
                "bash" or "sh" or "zsh" or "dash" or "ksh" => static f => Regex.IsMatch(f, "^-[a-zA-Z]*c[a-zA-Z]*$"),
                "cmd" => static f => f.ToLowerInvariant() is "/c" or "/k",
                _ => null,
            };
            if (isCommandFlag is null)
            {
                continue;
            }

            for (var j = i + 1; j < tokens.Count - 1; j++)
            {
                if (isCommandFlag(tokens[j]))
                {
                    return (tokens[i], leaf == "cmd" ? string.Join(' ', tokens.Skip(j + 1)) : tokens[j + 1]);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The targets a <c>dotnet test</c> command names without an option — a project, solution, directory,
    /// <c>.dll</c> or <c>.exe</c> in a word of its own rather than as the value of <c>--project</c>,
    /// <c>--solution</c>, <c>--directory</c> or <c>--test-modules</c>. Empty for any other command.
    /// </summary>
    /// <remarks>
    /// The SDK's platform mode takes a positional project, solution or directory only as the first word
    /// it does not recognise as one of its own options (anywhere else: <c>Specifying a project for
    /// 'dotnet test' should be via '--project'.</c>), and a positional <c>.dll</c> or <c>.exe</c> as test
    /// modules — which evaluates no project, so the arguments MSBuild carries never arrive and the host
    /// refuses the run (<c>MSBuildUtility.GetPositionalArguments</c>, SDK 10.0.4xx). Which of the words
    /// before it the SDK recognises is its business, so the lints ask for the option every time.
    /// </remarks>
    internal static IReadOnlyList<string> PositionalTargets(string command, string? root = null)
    {
        var tokens = Tokenize(command).Tokens.Select(Normalise).ToList();
        var start = Enumerable.Range(0, Math.Max(tokens.Count - 1, 0)).FirstOrDefault(i => IsDotnet(tokens[i]) && tokens[i + 1] == "test", -1);
        if (start < 0)
        {
            return [];
        }

        string[] targetOptions = ["--project", "--solution", "--directory", "--test-modules"];
        var found = new List<string>();
        for (var i = start + 2; i < tokens.Count && tokens[i] != "--"; i++)
        {
            var token = tokens[i];
            if (token.StartsWith('-') || targetOptions.Contains(tokens[i - 1], StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsProjectOrSolutionFile(token) || IsProjectDirectory(root ?? CiWorkflows.Root, token))
            {
                found.Add(token);
            }
        }

        return found;
    }

    /// <summary>Whether <paramref name="command"/> runs <c>dotnet &lt;verb&gt;</c>, by any spelling of <c>dotnet</c>.</summary>
    internal static bool RunsDotnet(string command, string verb)
    {
        var tokens = Tokenize(command).Tokens.Select(Normalise).ToList();
        return tokens.Zip(tokens.Skip(1)).Any(p => IsDotnet(p.First) && p.Second == verb);
    }

    /// <summary>
    /// The MSBuild global properties a command line sets, in order: <c>-p:</c>, <c>/p:</c>,
    /// <c>-property:</c> and <c>--property:</c> (any case), <c>-p Name=Value</c>, and several pairs in one
    /// switch separated by <c>;</c> or <c>,</c>. Names keep their spelling; MSBuild compares them
    /// case-insensitively, and so should a caller.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Value)> MsBuildProperties(string command)
    {
        var tokens = Tokenize(command).Tokens;
        var properties = new List<(string, string)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            string? assignments = null;
            if (Regex.Match(tokens[i], @"^(?:--|-|/)(?:p|property):(.+)$", RegexOptions.IgnoreCase) is { Success: true } m)
            {
                assignments = m.Groups[1].Value;
            }
            else if (Regex.IsMatch(tokens[i], @"^(?:--|-|/)(?:p|property)$", RegexOptions.IgnoreCase) && i + 1 < tokens.Count)
            {
                assignments = tokens[++i];
            }

            foreach (var pair in (assignments ?? "").Split([';', ',']))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    properties.Add((pair[..eq].Trim(), pair[(eq + 1)..].Trim()));
                }
            }
        }

        return properties;
    }

    private static bool IsDotnet(string token)
    {
        var leaf = token[(token.LastIndexOf('/') + 1)..];
        return leaf is "dotnet" or "dotnet.exe";
    }

    /// <summary>A rooted path: `/…` or a drive letter.</summary>
    internal static bool IsAbsolute(string token) =>
        token.StartsWith('/') || (token.Length > 2 && char.IsAsciiLetter(token[0]) && token[1] == ':' && token[2] == '/');

    private static readonly string[] TargetExtensions = [".csproj", ".fsproj", ".vbproj", ".slnx", ".sln", ".slnf", ".dll", ".exe"];

    /// <summary>A token that names a project, a solution or a built assembly (not a shell or <c>dotnet</c> itself).</summary>
    private static bool IsProjectOrSolutionFile(string token)
    {
        var leaf = token[(token.LastIndexOf('/') + 1)..];
        return leaf is not ("dotnet.exe" or "pwsh.exe" or "powershell.exe")
            && TargetExtensions.Any(e => leaf.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A token that names a directory of the checkout holding a project file — a target for <c>dotnet test</c>/<c>run</c>.</summary>
    private static bool IsProjectDirectory(string root, string token)
    {
        if (token.Length == 0 || token == ".." || token.StartsWith("../", StringComparison.Ordinal) || IsAbsolute(token)
            || token.Contains(Expression, StringComparison.Ordinal) || token.IndexOfAny(['*', '?', '"', '<', '>', '|', ':']) >= 0)
        {
            return false;
        }

        var directory = Path.Combine(root, token);
        return Directory.Exists(directory)
            && Directory.EnumerateFiles(directory).Any(static f => f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".fsproj", StringComparison.Ordinal));
    }

    /// <summary>
    /// A relative token as a repo-relative path: joined to the working directory, `.` and `..` folded.
    /// Options, rooted paths, variables, `scheme:` values and expressions are left as they are.
    /// </summary>
    private static string Locate(string token, string wd)
    {
        if (token.Length == 0 || token[0] is '-' or '/' or '$' || token.Contains(':', StringComparison.Ordinal)
            || token.Contains(Expression, StringComparison.Ordinal))
        {
            return token;
        }

        return Collapse(wd.Length > 0 ? $"{wd}/{token}" : token);
    }

    private static string Collapse(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == ".." && parts.Count > 0 && parts[^1] != "..")
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }

    /// <summary>A step's <c>working-directory</c> as a repo-relative path; empty for the checkout root.</summary>
    private static string WorkingDirectoryOf(string? written)
    {
        if (string.IsNullOrWhiteSpace(written))
        {
            return "";
        }

        var wd = Normalise(ReplaceExpressions(written.Trim()));
        return wd == "$GITHUB_WORKSPACE" ? "" : wd.Contains(Expression, StringComparison.Ordinal) ? wd : Collapse(wd);
    }

    /// <summary><c>${{ github.workspace }}</c> becomes <c>$GITHUB_WORKSPACE</c>; every other expression becomes <see cref="Expression"/>.</summary>
    private static string ReplaceExpressions(string text) =>
        Regex.Replace(
            Regex.Replace(text, @"\$\{\{\s*github\.workspace\s*\}\}", static _ => "$GITHUB_WORKSPACE"),
            @"\$\{\{.*?\}\}",
            static _ => Expression);

    /// <summary>
    /// The ways one token can name a path: as written (normalised to forward slashes, no leading
    /// <c>./</c>), and — for <c>--project=x</c> or <c>NAME=x</c> — the part after the last <c>=</c>.
    /// </summary>
    internal static IEnumerable<string> Spellings(string token)
    {
        var normalised = Normalise(token);
        yield return normalised;

        var eq = normalised.LastIndexOf('=');
        if (eq >= 0 && eq < normalised.Length - 1)
        {
            yield return Normalise(normalised[(eq + 1)..]);
        }
    }

    private static string Normalise(string token)
    {
        var t = token.Replace('\\', '/');
        while (t.StartsWith("./", StringComparison.Ordinal))
        {
            t = t[2..];
        }

        return t.StartsWith("$GITHUB_WORKSPACE/", StringComparison.Ordinal) ? t["$GITHUB_WORKSPACE/".Length..] : t;
    }

    /// <summary>
    /// Shell-like tokens, quote-aware, with the control operators (<c>|</c>, <c>||</c>, <c>&amp;</c>,
    /// <c>&amp;&amp;</c>, <c>;</c>) found outside quotes reported separately. <c>${{ … }}</c> is
    /// opaque (<see cref="Expression"/>), <c>2&gt;&amp;1</c> is a redirection rather than an operator,
    /// and an unquoted <c>#</c> at the start of a word begins a comment.
    /// </summary>
    internal static (List<string> Tokens, List<string> Operators) Tokenize(string command)
    {
        var text = ReplaceExpressions(command);
        var tokens = new List<string>();
        var operators = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        var quote = '\0';

        void Flush()
        {
            if (inToken)
            {
                tokens.Add(current.ToString());
            }

            current.Clear();
            inToken = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else if (quote == '"' && c == '\\' && i + 1 < text.Length)
                {
                    current.Append(text[++i]);
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case ' ' or '\t':
                    Flush();
                    break;
                case '\'' or '"':
                    quote = c;
                    inToken = true;
                    break;
                // An unquoted backslash stays literal: continuations are already joined, and under pwsh
                // it is a path separator. A bash `\|` therefore reads as an operator — a false red, which
                // is the safe direction for a lint about swallowed exit codes.
                case '&' when current.Length > 0 && current[^1] is '>' or '<':
                    current.Append(c);
                    break;
                case '|' or '&' or ';':
                    Flush();
                    var op = new StringBuilder().Append(c);
                    while (i + 1 < text.Length && text[i + 1] is '|' or '&' or ';')
                    {
                        op.Append(text[++i]);
                    }

                    operators.Add(op.ToString());
                    break;
                case '#' when !inToken:
                    i = text.Length;
                    break;
                default:
                    current.Append(c);
                    inToken = true;
                    break;
            }
        }

        Flush();
        return (tokens, operators);
    }

    /// <summary>A script's code with its comments removed, so prose about <c>dotnet test</c> is not a test run.</summary>
    internal static string StripComments(string text, string extension)
    {
        switch (extension)
        {
            case ".ps1" or ".psm1":
                text = Regex.Replace(text, @"<#.*?#>", "", RegexOptions.Singleline);
                return string.Join('\n', text.Split('\n').Where(static l => !l.TrimStart().StartsWith('#')));
            case ".sh" or ".bash":
                return string.Join('\n', text.Split('\n').Where(static l => !l.TrimStart().StartsWith('#')));
            case ".js" or ".mjs" or ".cjs":
                text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
                return string.Join('\n', text.Split('\n').Where(static l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            default:
                return text;
        }
    }

    private static (IReadOnlyList<CiProject> Tests, CiProject Cli) LoadProjects()
    {
        var root = CiWorkflows.Root;
        var slnx = XDocument.Load(Path.Combine(root, RepoRoot.MarkerFileName));
        var projects = slnx
            .Descendants("Project")
            .Select(static p => p.Attribute("Path")?.Value)
            .OfType<string>()
            .Select(static p => p.Replace('\\', '/'))
            .Select(path =>
            {
                var text = File.ReadAllText(Path.Combine(root, path));
                var directory = Path.GetDirectoryName(path)!.Replace('\\', '/');
                var assembly = Regex.Match(text, @"<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>") is { Success: true } m
                    ? m.Groups[1].Value
                    : Path.GetFileNameWithoutExtension(path);
                var siblings = Directory.EnumerateFiles(Path.Combine(root, directory), "*.csproj", SearchOption.TopDirectoryOnly).Count();
                var isTest = Regex.IsMatch(text, @"<PackageReference\s+Include=""xunit\.v3");
                return (Project: new CiProject(path, directory, siblings == 1, assembly), IsTest: isTest);
            })
            .ToList();

        var tests = projects.Where(static p => p.IsTest).Select(static p => p.Project).ToList();
        if (tests.Count == 0)
        {
            throw new InvalidOperationException(
                $"{RepoRoot.MarkerFileName} lists no project that references xunit.v3: the test-step classifier would recognise nothing.");
        }

        var cli = projects.Select(static p => p.Project).SingleOrDefault(static p => p.AssemblyName == CliAssemblyName)
            ?? throw new InvalidOperationException(
                $"{RepoRoot.MarkerFileName} lists no project with <AssemblyName>{CliAssemblyName}</AssemblyName>: the CLI-step classifier would recognise nothing.");

        return (tests, cli);
    }
}
