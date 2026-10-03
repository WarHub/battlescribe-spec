using System.Text.RegularExpressions;
using BattleScribeSpec.Tests.Profiles;

namespace BattleScribeSpec.Tests;

/// <summary>
/// One command of a CI step that runs a test project, as one leg of its job's matrix runs it: the
/// <c>${{ matrix.… }}</c> expressions resolved, the project classified, the profile it names read off
/// the line.
/// </summary>
/// <param name="Step">The step.</param>
/// <param name="Matrix">The matrix combination this leg runs (empty for a job with no matrix).</param>
/// <param name="Command">The command as this leg runs it.</param>
/// <param name="Invocation">What <see cref="CiTestInvocations"/> makes of it.</param>
/// <param name="ProfileNames">
/// Every profile the line names, in any spelling (<see cref="CiProfileRuns.ProfilesNamedBy"/>); one, for a
/// well-formed profiled step.
/// </param>
/// <param name="UnderXvfb">Whether the command runs under <c>xvfb-run</c>.</param>
/// <param name="Overrides">
/// The tokens on the line that would narrow or replace the profile's selection or weaken its verdict
/// (<see cref="CiProfileRuns.OverridesIn"/>).
/// </param>
internal sealed record CiTestRun(
    CiStep Step,
    IReadOnlyDictionary<string, string> Matrix,
    string Command,
    CiInvocation Invocation,
    IReadOnlyList<string> ProfileNames,
    bool UnderXvfb,
    IReadOnlyList<string> Overrides)
{
    /// <summary>How each profile name on the line is spelled (<see cref="CiProfileRuns.ProfileSpellings"/>).</summary>
    public IReadOnlyList<(string Name, ProfileSpelling How)> Spellings => CiProfileRuns.ProfileSpellings(Command);

    /// <summary>The registry profile the line names, when it names exactly one that exists.</summary>
    public TestProfile? Profile => ProfileNames is [var only] ? TestProfiles.Find(only) : null;

    /// <summary>
    /// The lanes this run exists to run: what its profile claims, less any lane the profile allows to
    /// skip whole (<see cref="TestProfile.MaySkip"/>) — <c>core</c> claims <c>BsRosterUi</c>, and CI's
    /// offline job does not provision the app, so there that lane is not run.
    /// </summary>
    public IEnumerable<EngineLane> Lanes =>
        Profile is { } p
            ? p.Selection.Claims.Where(c => !p.MaySkip.Any(m => m.Engine == c)).Select(EngineLanes.Find).OfType<EngineLane>()
            : [];

    /// <summary>Where this run is, for messages: the step, and the matrix leg when there is one.</summary>
    public string Where => Matrix.Count == 0
        ? Step.Where
        : $"{Step.Where} [{string.Join(", ", Matrix.Select(static kv => $"{kv.Key}={kv.Value}"))}]";
}

/// <summary>How a command line names a test profile.</summary>
internal enum ProfileSpelling
{
    /// <summary>The MSBuild property <c>-p:TestProfile=&lt;name&gt;</c>, which reaches the app as <c>--test-profile</c>.</summary>
    MsBuildProperty,

    /// <summary>The test app's own <c>--test-profile &lt;name&gt;</c>, with no <c>--</c> before it.</summary>
    Option,

    /// <summary><c>--test-profile &lt;name&gt;</c> after a <c>--</c>: handed to the app by <c>dotnet run</c>.</summary>
    OptionAfterSeparator,
}

/// <summary>
/// A CI step that drives a UI engine through <c>bs-spec</c> rather than a test project, with the lane
/// whose driver it runs (one record per lane: <c>bs-spec verify</c> can drive two). Those drivers write
/// the same diagnostics, and need the same display, as the lane's test classes.
/// </summary>
/// <param name="Step">The step.</param>
/// <param name="Matrix">The matrix combination this leg runs (empty for a job with no matrix).</param>
/// <param name="Command">The command as this leg runs it.</param>
/// <param name="Arguments">bs-spec's own arguments, from the verb on (<c>run --engine battlescribe --ui protocol-kitchen-sink</c>).</param>
/// <param name="Lane">The lane whose driver the command drives.</param>
/// <param name="UnderXvfb">Whether the command runs under <c>xvfb-run</c>.</param>
internal sealed record CiCliUiRun(
    CiStep Step,
    IReadOnlyDictionary<string, string> Matrix,
    string Command,
    IReadOnlyList<string> Arguments,
    EngineLane Lane,
    bool UnderXvfb)
{
    /// <summary>Where this run is, for messages: the step, and the matrix leg when there is one.</summary>
    public string Where => Matrix.Count == 0
        ? Step.Where
        : $"{Step.Where} [{string.Join(", ", Matrix.Select(static kv => $"{kv.Key}={kv.Value}"))}]";
}

/// <summary>
/// <b>Every CI test run, with the profile it names.</b> The workflow lints that hold CI to the profile
/// registry read CI through this: <see cref="CiTestInvocations"/> finds the steps that run a test project,
/// this expands each over its job's matrix and reads the profile, the display wrapper and any filter the
/// line adds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the matrix is expanded rather than skipped.</b> <c>thorough-ui-bs</c> runs
/// <c>--test-profile ${{ matrix.suite.profile }}</c>; read as written, that step names no profile at all, and
/// every rule about profiled steps would have to exempt it — the leg that drives the desktop app for
/// thirteen minutes. Expanded, each leg is a run with a profile the lints can check, and a typo in the
/// matrix key stays an unresolved expression, which is not a profile.
/// </para>
/// <para>
/// Every workflow and composite action is read (the classifier fails if it read no action).
/// </para>
/// </remarks>
internal static class CiProfileRuns
{
    private static readonly Lazy<(IReadOnlyList<CiTestRun> Tests, IReadOnlyList<CiCliUiRun> CliUi)> Loaded = new(Load);

    /// <summary>Every command of every CI step that runs a test project, once per matrix leg.</summary>
    internal static IReadOnlyList<CiTestRun> All => Loaded.Value.Tests;

    /// <summary>Every <c>bs-spec</c> command in CI that drives a UI engine, once per lane whose driver it runs.</summary>
    internal static IReadOnlyList<CiCliUiRun> CliUi => Loaded.Value.CliUi;

    /// <summary>
    /// Which lane each of the CLI's built-in UI drivers is, by base engine name (the <c>-ui</c> suffix
    /// stripped) and domain. The CLI's engine names are not trait values, so this one map says it. A
    /// <c>newrecruit</c> roster run is the frozen lane: it goes live only with <c>NR_ENGINE_URL</c> set,
    /// and that switch belongs to the live profiles, so <see cref="CiProfileLaneTests.LaneDefiningKnobs_AppearNowhereInGithub"/>
    /// keeps it out of CI. Its gamedata side is always this machine.
    /// </summary>
    private static readonly Dictionary<(string Engine, bool GameData), string> CliUiLanes = new()
    {
        [("battlescribe", false)] = "BsRosterUi",
        [("battlescribe", true)] = "BsGameDataUi",
        [("newrecruit", false)] = "FrozenNrUiRoster",
        [("newrecruit", true)] = "FrozenNrGameDataUi",
    };

    /// <summary><c>bs-spec verify</c>'s engines when <c>--engines</c> is absent (<c>VerifyCommand.AllGameDataEngines</c>).</summary>
    private static readonly string[] VerifyDefaultEngines = ["battlescribe", "battlescribe-ui", "newrecruit", "newrecruit-ui"];

    /// <summary>
    /// Every way a gamedata spec can be named on bs-spec's command line — its id, its
    /// <c>category/id</c>, and that path with <c>.yaml</c> — as the CLI's <c>SpecLoading.InferEngineType</c>
    /// resolves a bare spec argument against <c>specs/gamedata</c>.
    /// </summary>
    private static readonly Lazy<HashSet<string>> GameDataSpecNames = new(static () =>
    {
        var dir = Path.Combine(CiWorkflows.Root, "specs", "gamedata");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
            names.Add(Path.GetFileNameWithoutExtension(file));
            names.Add(relative);
            names.Add(relative[..^".yaml".Length]);
        }

        return names.Count > 0 ? names : throw new InvalidOperationException($"Found no gamedata spec under {dir}; bs-spec's domain cannot be inferred.");
    });

    /// <summary>
    /// The profiles a command line names, in every spelling that selects one: the test app's
    /// <c>--test-profile x</c> / <c>--test-profile=x</c> / <c>--test-profile:x</c> (the platform reads all
    /// three, in any case), and the MSBuild property <c>-p:TestProfile=x</c>.
    /// </summary>
    internal static IReadOnlyList<string> ProfilesNamedBy(string command) => [.. ProfileSpellings(command).Select(static s => s.Name)];

    /// <summary>
    /// Each profile a command line names (<see cref="ProfilesNamedBy"/>), with how it is spelled — which
    /// <c>CiProfileLaneTests.EveryCiTestRun_NamesAProfile</c> holds to one spelling per verb.
    /// </summary>
    internal static IReadOnlyList<(string Name, ProfileSpelling How)> ProfileSpellings(string command)
    {
        var tokens = CiTestInvocations.Tokenize(command).Tokens;
        var separator = tokens.IndexOf("--");
        var found = new List<(string, ProfileSpelling)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var how = separator >= 0 && i > separator ? ProfileSpelling.OptionAfterSeparator : ProfileSpelling.Option;
            var token = tokens[i];
            if (token.Equals(ProfileOption, StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
            {
                found.Add((tokens[++i], how));
            }
            else if (token.Length > ProfileOption.Length + 1
                && token.StartsWith(ProfileOption, StringComparison.OrdinalIgnoreCase)
                && token[ProfileOption.Length] is ':' or '=')
            {
                found.Add((token[(ProfileOption.Length + 1)..], how));
            }
        }

        found.AddRange(CiTestInvocations.MsBuildProperties(command)
            .Where(static p => p.Name.Equals("TestProfile", StringComparison.OrdinalIgnoreCase))
            .Select(static p => (p.Value, ProfileSpelling.MsBuildProperty)));
        return found;
    }

    /// <summary>
    /// The tokens on a command line that would narrow or replace a profile's selection, or weaken its
    /// verdict: a test filter in any runner's spelling (<c>--filter</c>, xunit's <c>--filter-*</c>, an
    /// inline <c>RunConfiguration.TestCaseFilter</c>), a settings file (<c>--settings</c>, <c>-s</c>), the
    /// MSBuild properties those become (<c>VSTestTestCaseFilter</c>, <c>VSTestSetting</c>,
    /// <c>RunSettingsFilePath</c>), a zero-tests policy, a logger, and <c>--test-modules</c> (which runs
    /// assemblies without evaluating their projects, so the MSBuild-carried profile never arrives) — and
    /// every option the test host refuses alongside a profile (<see cref="TestHost.RefusedWithAProfile"/>:
    /// <c>--ignore-exit-code</c>, <c>--config-file</c>, <c>--xunit-config-filename</c>, a response file),
    /// read by the host's own parser. An option's value may follow it as the next token or after <c>=</c>
    /// or <c>:</c> — both <c>dotnet test</c> and the platform take all three. Options of an <c>xvfb-run</c>
    /// prefix are not the runner's.
    /// </summary>
    internal static IReadOnlyList<string> OverridesIn(string command)
    {
        var tokens = WithoutXvfb(CiTestInvocations.Tokenize(command).Tokens);
        string[] options = ["--filter", "--settings", "-s", "--zero-tests-policy", "--logger", "--test-modules"];
        var found = tokens
            .Where(static t => !MsBuildPropertySwitch.IsMatch(t))
            .Where(t => options.Any(o => t == o || OptionValue(t, o) is not null)
                || t.StartsWith("--filter-", StringComparison.Ordinal)
                || t.Contains("TestCaseFilter", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var refusedByTheHost = TestHost.OptionsRefusedWithAProfile(tokens).Where(t => !found.Contains(t, StringComparer.Ordinal)).ToList();
        found.AddRange(refusedByTheHost);
        string[] properties = ["VSTestTestCaseFilter", "VSTestSetting", "RunSettingsFilePath"];
        found.AddRange(CiTestInvocations.MsBuildProperties(command)
            .Where(p => properties.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .Select(static p => $"-p:{p.Name}={p.Value}"));
        return found;
    }

    /// <summary>The value of <paramref name="option"/> when <paramref name="token"/> spells it inline (<c>--opt=x</c>, <c>--opt:x</c>), else null.</summary>
    private static string? OptionValue(string token, string option) =>
        token.Length > option.Length + 1 && token.StartsWith(option, StringComparison.Ordinal) && token[option.Length] is '=' or ':'
            ? token[(option.Length + 1)..]
            : null;

    /// <summary>An MSBuild property switch (<c>-p:</c>, <c>/p:</c>, <c>-property:</c>), read through <see cref="CiTestInvocations.MsBuildProperties"/> instead.</summary>
    private static readonly Regex MsBuildPropertySwitch = new(@"^(?:--|-|/)(?:p|property):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The test app's option that names a profile.</summary>
    private const string ProfileOption = "--test-profile";

    /// <summary>Whether a command runs under <c>xvfb-run</c>.</summary>
    internal static bool RunsUnderXvfb(string command) =>
        CiTestInvocations.Tokenize(command).Tokens is [var first, ..] && first[(first.LastIndexOf('/') + 1)..] == "xvfb-run";

    /// <summary>The tokens after an <c>xvfb-run</c> prefix and its own options; the tokens as they are otherwise.</summary>
    private static List<string> WithoutXvfb(List<string> tokens)
    {
        if (tokens is not [var first, ..] || first[(first.LastIndexOf('/') + 1)..] != "xvfb-run")
        {
            return tokens;
        }

        // xvfb-run's options that take a value: -e/--error-file, -f/--auth-file, -n/--server-num,
        // -p/--xauth-protocol, -s/--server-args, -w/--wait.
        string[] withValue = ["-e", "-f", "-n", "-p", "-s", "-w"];
        var i = 1;
        while (i < tokens.Count && tokens[i].StartsWith('-'))
        {
            i += withValue.Contains(tokens[i], StringComparer.Ordinal) ? 2 : 1;
        }

        return tokens[Math.Min(i, tokens.Count)..];
    }

    private static (IReadOnlyList<CiTestRun>, IReadOnlyList<CiCliUiRun>) Load()
    {
        var tests = new List<CiTestRun>();
        var cliUi = new List<CiCliUiRun>();
        foreach (var (step, invocation) in CiTestInvocations.ClassifiedSteps().Where(static c => c.Invocation.Kind != CiStepKind.Other))
        {
            foreach (var combination in step.Job.MatrixCombinations())
            {
                var run = CiWorkflows.ExpandMatrix(step.Run!, combination);
                var workingDirectory = step.WorkingDirectory is { } wd ? CiWorkflows.ExpandMatrix(wd, combination) : null;
                foreach (var command in CiTestInvocations.Commands(run, step.Shell))
                {
                    var classified = CiTestInvocations.ClassifyCommand(command, workingDirectory: workingDirectory);
                    if (classified.Kind == CiStepKind.TestRun)
                    {
                        tests.Add(new CiTestRun(step, combination, command, classified, ProfilesNamedBy(command), RunsUnderXvfb(command), OverridesIn(command)));
                    }
                    // A bs-spec run this cannot read (inside a shell's quoted string) has no arguments to read
                    // lanes from; CiWorkflowDriftTests.EveryTestStep_NamesItsProject fails on it instead.
                    else if (classified.Kind == CiStepKind.CliRun && classified.Unresolved is null)
                    {
                        var (lanes, arguments) = CliUiLanesOf(command);
                        cliUi.AddRange(lanes.Select(lane => new CiCliUiRun(step, combination, command, arguments, lane, RunsUnderXvfb(command))));
                    }
                }
            }
        }

        return (tests, cliUi);
    }

    /// <summary>
    /// The lanes whose UI drivers a <c>bs-spec</c> command drives, resolved the way the CLI resolves its
    /// engine selection — not by one spelling of it — with bs-spec's own arguments, from the verb on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>run</c>, <c>probe</c> and <c>compare</c> take <c>--engine</c> (<c>x</c>, <c>=x</c> or <c>:x</c>;
    /// <c>battlescribe</c> when absent, as <c>EngineOptions.Engine</c> defaults it). The engine is a UI
    /// driver when <c>--ui</c> is given or its name ends in <c>-ui</c> (<c>battlescribe-ui</c> is the
    /// same driver as <c>battlescribe --ui</c>). The domain is <c>--gamedata</c> or <c>--roster</c>, else
    /// the spec argument's — gamedata when it names a spec under <c>specs/gamedata</c> or has a
    /// <c>gamedata</c> path segment, as <c>SpecLoading.InferEngineType</c> decides. <c>verify</c> runs
    /// gamedata specs only, over <c>--engines</c> or its four defaults, two of them UI drivers. A
    /// connectable (<c>exec:</c>, <c>dotnet:</c>, <c>name=…</c>) is a foreign adapter and nobody's lane;
    /// the CLI refuses <c>--ui</c> on one.
    /// </para>
    /// <para>
    /// <b>Refused rather than approximated.</b> A UI engine this cannot map to a lane, or an engine that
    /// is an unresolved expression, throws: read as "no lane", the step would drop out of the display
    /// and upload rules that exist for it, which is how the smoke job's BattleScribe roster driver would
    /// have lost its upload had its step been respelled <c>run --engine battlescribe-ui …</c>.
    /// </para>
    /// </remarks>
    internal static (IReadOnlyList<EngineLane> Lanes, IReadOnlyList<string> Arguments) CliUiLanesOf(string command)
    {
        var tokens = WithoutXvfb(CiTestInvocations.Tokenize(command).Tokens);
        List<string> arguments;
        if (CiTestInvocations.RunsDotnet(command, "run"))
        {
            // `dotnet run --project src/BattleScribeSpec.Cli … -- <bs-spec's arguments>`.
            var separator = tokens.IndexOf("--");
            arguments = separator < 0 ? [] : tokens[(separator + 1)..];
        }
        else
        {
            var cli = tokens.FindIndex(static t => CiTestInvocations.Spellings(t).Any(CiTestInvocations.CliProject.IsNamedBy));
            arguments = cli >= 0
                ? tokens[(cli + 1)..]
                : throw new NotSupportedException($"CiProfileRuns cannot find where bs-spec's own arguments start in `{command}`: no token names the CLI.");
        }

        if (arguments is not [var verb, .. var options])
        {
            return ([], arguments);
        }

        bool gameData;
        IEnumerable<(string Engine, bool Ui)> engines;
        switch (verb)
        {
            case "verify":
                gameData = true;
                engines = (Value(options, "--engines")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    ?? VerifyDefaultEngines).Select(static e => (e, false));
                break;
            case "run" or "probe" or "compare":
                gameData = Flag(options, "--gamedata") || (!Flag(options, "--roster") && options.Any(NamesAGameDataSpec));
                engines = [(Value(options, "--engine") ?? "battlescribe", Flag(options, "--ui"))];
                break;
            default:
                return ([], arguments);
        }

        var lanes = new List<EngineLane>();
        foreach (var (engine, ui) in engines)
        {
            if (engine.Contains(CiTestInvocations.Expression, StringComparison.Ordinal))
            {
                throw new NotSupportedException($"CiProfileRuns cannot tell which engine `{command}` drives: its engine is an expression this leg does not resolve.");
            }

            var connectable = engine.Contains('=', StringComparison.Ordinal)
                || engine.StartsWith("exec:", StringComparison.Ordinal) || engine.StartsWith("dotnet:", StringComparison.Ordinal);
            if (connectable || !(ui || engine.EndsWith("-ui", StringComparison.Ordinal)))
            {
                continue;
            }

            var name = engine.EndsWith("-ui", StringComparison.Ordinal) ? engine[..^"-ui".Length] : engine;
            lanes.Add(CliUiLanes.TryGetValue((name, gameData), out var trait) && EngineLanes.Find(trait) is { } lane
                ? lane
                : throw new NotSupportedException(
                    $"CiProfileRuns cannot tell which lane `{command}` drives: UI engine '{engine}' on the {(gameData ? "gamedata" : "roster")} "
                    + "domain is not in its CliUiLanes map. Add it there, so the step's display and diagnostics rules apply to it."));
        }

        return (lanes, arguments);

        static bool NamesAGameDataSpec(string token) =>
            !token.StartsWith('-')
            && (GameDataSpecNames.Value.Contains(token.Replace('\\', '/'))
                || token.Replace('\\', '/').Split('/').Any(static s => s.Equals("gamedata", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>A bs-spec option's value: the next token, or the rest of <c>--opt=x</c> / <c>--opt:x</c>; null when absent.</summary>
    private static string? Value(IReadOnlyList<string> options, string option)
    {
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i] == option)
            {
                return i + 1 < options.Count ? options[i + 1] : null;
            }

            if (OptionValue(options[i], option) is { } inline)
            {
                return inline;
            }
        }

        return null;
    }

    /// <summary>Whether a bs-spec switch is on: <c>--x</c>, <c>--x true</c>, <c>--x=true</c> or <c>--x:true</c>, and not <c>false</c>.</summary>
    private static bool Flag(IReadOnlyList<string> options, string option)
    {
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i] == option)
            {
                return !(i + 1 < options.Count && options[i + 1].Equals("false", StringComparison.OrdinalIgnoreCase));
            }

            if (OptionValue(options[i], option) is { } inline)
            {
                return !inline.Equals("false", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    /// <summary>
    /// A profile name as a document or a workflow writes it, or null when the token is a placeholder or a
    /// pattern rather than a name (<c>&lt;x&gt;</c>, <c>nr-live*</c>, <c>...</c>, an expression).
    /// </summary>
    /// <remarks>
    /// What may trail a name in prose is stripped first, repeatedly and in any order: sentence and
    /// list punctuation, closing quotes and brackets, markdown emphasis (<c>**</c>, <c>__</c>), an HTML
    /// tag (<c>&lt;br&gt;</c>) and a <c>.runsettings</c> extension — so
    /// <c>-p:TestProfile=gone**</c> and <c>tests/test-profiles/gone.runsettings.</c> are references to
    /// <c>gone</c>, not patterns to skip. What is left must be kebab-case; one trailing <c>*</c> is a glob.
    /// </remarks>
    internal static string? AsProfileName(string token)
    {
        string name = token, before;
        do
        {
            before = name;
            name = TrailingDecoration.Replace(name, "");
            if (name.EndsWith(".runsettings", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^".runsettings".Length];
            }
        }
        while (name != before);

        return Regex.IsMatch(name, "^[a-z0-9]+(?:-[a-z0-9]+)*$") ? name : null;
    }

    /// <summary>Prose that can trail a name: punctuation, closing quotes and brackets, <c>**</c>/<c>__</c> emphasis, an HTML tag.</summary>
    private static readonly Regex TrailingDecoration = new(@"(?:\*\*|__|<[^<>]*>|[.,:;)\]}`'""])+$", RegexOptions.CultureInvariant);
}
