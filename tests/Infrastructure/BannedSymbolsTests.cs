using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>The compile-time ban on the working directory in test code is wired, nothing switches it off, and every
/// entry in it means something.</b> <c>tests/BannedSymbols.txt</c> lists what the test projects may not call —
/// <c>Directory.GetCurrentDirectory</c>, <c>Directory.SetCurrentDirectory</c>, <c>Environment.CurrentDirectory</c>,
/// <c>RepoRoot.FromWorkingDirectory</c>, and the production overloads that read the working directory on the
/// CLI's behalf — and <c>Microsoft.CodeAnalysis.BannedApiAnalyzers</c> turns each call into error RS0030. Tests
/// resolve the checkout from their binaries instead (<see cref="TestPaths"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a lint guards an analyzer.</b> The analyzer fails open far more ways than it fails loud, and every
/// one of them is silent: the banned call compiles and the build stays green. Each way below was probed on a
/// scratch project (SDK 10.0.4xx, analyzer 5.6.0) and seen to let a banned call build, then given a check:
/// <list type="bullet">
/// <item><b>The project loses the analyzer or the list</b> — a deleted line, a <c>Condition</c> on its item
/// group, an <c>AdditionalFiles Remove</c> in a <c>Directory.Build.targets</c>.</item>
/// <item><b>The warning is suppressed</b> — <c>NoWarn</c>; <c>WarningLevel</c> 0; a <c>#pragma warning
/// disable</c> naming RS0030 or naming nothing; a suppression attribute naming RS0030; a severity of
/// <c>none</c>, <c>silent</c> or <c>suggestion</c> in an <c>.editorconfig</c> or <c>.globalconfig</c>, for
/// RS0030, for its <c>ApiDesign</c> category, or for every analyzer — the last two never mention RS0030.</item>
/// <item><b>The warning stays a warning</b> — RS0030 is a warning that only this repository's
/// <c>TreatWarningsAsErrors</c> makes an error, so <c>TreatWarningsAsErrors</c> off, or RS0030 in
/// <c>WarningsNotAsErrors</c> (where <c>CodeAnalysisTreatWarningsAsErrors=false</c> puts it: the package's props
/// add its rule ids), leaves a banned call a warning in a green build.</item>
/// <item><b>No analyzer runs</b> — <c>RunAnalyzers</c> or <c>RunAnalyzersDuringBuild</c> false.</item>
/// <item><b>An entry matches nothing</b> — a documentation-comment id whose member was renamed, whose parameter
/// type changed, or that was mistyped is skipped without a word, so the ban it was written for lapses while
/// the file still reads as if it held.</item>
/// </list>
/// Probed and <em>not</em> an off-switch with this toolchain, so not checked: a source file marked
/// <c>&lt;auto-generated&gt;</c> or named <c>*.g.cs</c>, or <c>generated_code = true</c> (the analyzer reports in
/// generated code); <c>AnalysisLevel</c> or <c>AnalysisMode</c> <c>none</c> (they govern the SDK's own rules).
/// </para>
/// <para>
/// <b>The project-level checks read MSBuild's evaluation, not the files.</b> They ask MSBuild for each test
/// project's evaluated properties and items (<c>dotnet msbuild -getProperty/-getItem</c>: evaluation only,
/// nothing built or restored, about a second a project), which is what the build itself uses — after every
/// condition, import, <c>Directory.Build.*</c> file and package props has had its say. A text scan of the
/// csproj passed a conditioned item group and could not see a targets file at all.
/// </para>
/// <para>
/// <b>Where it runs.</b> Analyzers run in local builds and in CI's <c>checks</c> job, the analyzer gate;
/// <c>-p:FunctionalBuild=true</c> (every other CI build) turns them off, so a banned call is a <c>checks</c>
/// failure, which is where every other analyzer error surfaces.
/// </para>
/// <para>
/// Mutation-checked when written; each check names its mutations.
/// </para>
/// </remarks>
[Trait("Category", "Lint")]
public sealed class BannedSymbolsTests
{
    private const string AnalyzerPackage = "Microsoft.CodeAnalysis.BannedApiAnalyzers";

    private const string ListPath = "tests/BannedSymbols.txt";

    private const string Rule = "RS0030";

    /// <summary>The properties that, set to <c>false</c>, stop every analyzer from running.</summary>
    private static readonly string[] AnalyzerSwitches = ["RunAnalyzers", "RunAnalyzersDuringBuild"];

    /// <summary>
    /// <b>Every test project, as MSBuild evaluates it, references the analyzer, hands it
    /// <c>tests/BannedSymbols.txt</c>, runs it, and keeps RS0030 an error.</b>
    /// </summary>
    /// <remarks>
    /// The reference is in each csproj, not in <c>tests/Directory.Build.props</c>, so Dependabot sees the
    /// package (a central pin no project references is one it cannot see). Mutation-checked: in Cli.Tests'
    /// csproj, the <c>AdditionalFiles</c> line removed, its item group given <c>Condition="false"</c>, and the
    /// package reference removed; an <c>AdditionalFiles Remove</c> in a new <c>tests/Directory.Build.targets</c>;
    /// <c>ExcludeAssets="analyzers"</c> on the reference; and in the Tests csproj <c>RS0030</c> in
    /// <c>NoWarn</c>, <c>CodeAnalysisTreatWarningsAsErrors</c> false in <c>tests/Directory.Build.props</c>,
    /// <c>TreatWarningsAsErrors</c> false, <c>WarningLevel</c> 0 and <c>RunAnalyzers</c> false — each goes red
    /// naming the project and what it found.
    /// </remarks>
    [Fact]
    public async Task EveryTestProject_RunsTheAnalyzer_OnTheSharedList_AsAnError()
    {
        var list = Path.GetFullPath(Path.Combine(TestPaths.Root, ListPath));
        Assert.True(File.Exists(list), $"{ListPath} does not exist: the analyzer has nothing to ban.");

        var problems = new List<string>();
        foreach (var project in await Evaluated.Value)
        {
            var name = project.RelativePath;
            var references = project.ItemsOf("PackageReference").Where(static i => i.Identity == AnalyzerPackage).ToList();
            if (references.Count == 0)
            {
                problems.Add($"  {name} does not reference {AnalyzerPackage} (as evaluated: a missing line, a Condition and a Remove all end here)");
            }

            foreach (var reference in references)
            {
                foreach (var assets in new[] { "IncludeAssets", "ExcludeAssets" })
                {
                    if (reference.Metadata.GetValueOrDefault(assets) is { Length: > 0 } value)
                    {
                        problems.Add($"  {name}: the {AnalyzerPackage} reference sets {assets}=\"{value}\" — NuGet's documented way to "
                            + "drop a package's analyzers (today's SDK still loads them from a direct reference; nothing makes that a contract)");
                    }
                }
            }

            if (!project.ItemsOf("AdditionalFiles").Any(i => string.Equals(Path.GetFullPath(i.FullPath), list, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"  {name} does not pass {ListPath} to the analyzer as AdditionalFiles (as evaluated)");
            }

            if (!string.Equals(project.Property("TreatWarningsAsErrors"), "true", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"  {name}: TreatWarningsAsErrors is '{project.Property("TreatWarningsAsErrors")}' — {Rule} is a warning, and only "
                    + "TreatWarningsAsErrors makes a banned call fail the build");
            }

            if (Ids(project.Property("NoWarn")).Contains(Rule))
            {
                problems.Add($"  {name}: NoWarn contains {Rule}");
            }

            if (Ids(project.Property("WarningsNotAsErrors")).Contains(Rule))
            {
                problems.Add($"  {name}: WarningsNotAsErrors contains {Rule}, which keeps a banned call a warning "
                    + "(CodeAnalysisTreatWarningsAsErrors=false puts it there: the analyzer package's props add their rule ids)");
            }

            if (project.Property("WarningLevel") == "0")
            {
                problems.Add($"  {name}: WarningLevel is 0, which suppresses every warning, {Rule} included");
            }

            foreach (var switchedOff in AnalyzerSwitches.Where(p => string.Equals(project.Property(p), "false", StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"  {name}: {switchedOff} is false, so no analyzer runs (only -p:FunctionalBuild=true may say so, on a command line)");
            }
        }

        Assert.True(problems.Count == 0,
            $"The working-directory ban is not in force in every test project ({ListPath}, {AnalyzerPackage}):\n"
            + string.Join('\n', problems));
    }

    /// <summary>
    /// <b>No source file a test project compiles, and no analyzer configuration file that applies to one, turns
    /// RS0030 off.</b>
    /// </summary>
    /// <remarks>
    /// The sources are the projects' evaluated <c>Compile</c> items, linked files included; the configuration
    /// files are their evaluated <c>EditorConfigFiles</c> — every <c>.editorconfig</c> and <c>.globalconfig</c>
    /// above any source, wherever it is, as the compiler gets them — with an <c>.editorconfig</c> above the
    /// nearest <c>root = true</c> dropped, as the compiler drops it. Mutation-checked: a
    /// <c>#pragma warning disable</c> naming RS0030, and one naming nothing, in a Tests source; an
    /// assembly-level suppression attribute naming RS0030 in a Cli.Tests source; and in a new
    /// <c>tests/.editorconfig</c>, the RS0030 severity, the <c>ApiDesign</c> category's and every analyzer's set
    /// to <c>none</c> — each goes red naming the file and line.
    /// </remarks>
    [Fact]
    public async Task NoTestSourceOrAnalyzerConfig_SwitchesTheRuleOff()
    {
        var projects = await Evaluated.Value;
        var problems = new List<string>();

        var sources = projects.SelectMany(static p => p.ItemsOf("Compile"))
            .Select(static i => Path.GetFullPath(i.FullPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            foreach (Match pragma in PragmaDisable.Matches(text))
            {
                var ids = Ids(StripLineComment(pragma.Groups["ids"].Value));
                if (ids.Count == 0)
                {
                    problems.Add($"  {Display(source)}:{LineOf(text, pragma.Index)}: `#pragma warning disable` with no ids disables every warning, {Rule} included");
                }
                else if (ids.Contains(Rule))
                {
                    problems.Add($"  {Display(source)}:{LineOf(text, pragma.Index)}: `#pragma warning disable` names {Rule}");
                }
            }

            foreach (Match suppression in SuppressionAttribute.Matches(text))
            {
                if (suppression.Groups["args"].Value.Contains(Rule, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"  {Display(source)}:{LineOf(text, suppression.Index)}: a suppression attribute names {Rule}");
                }
            }
        }

        foreach (var config in AnalyzerConfigFiles(projects))
        {
            var lines = File.ReadAllLines(config);
            for (var i = 0; i < lines.Length; i++)
            {
                if (SeverityOff(lines[i]) is { } setting)
                {
                    problems.Add($"  {Display(config)}:{i + 1}: `{setting}` turns {Rule} off for the test sources it applies to");
                }
            }
        }

        Assert.True(problems.Count == 0,
            $"Something in the test projects' sources or analyzer configuration switches the working-directory ban ({ListPath}) off:\n"
            + string.Join('\n', problems));
    }

    /// <summary>
    /// <b>Every entry in <c>tests/BannedSymbols.txt</c> names a member that exists, and says what to use
    /// instead.</b>
    /// </summary>
    /// <remarks>
    /// The ids are resolved by reflection against the assemblies this test project references, which are the
    /// ones the analyzer resolves them against. Mutation-checked: <c>FindFrozenHarFile</c> misspelt in the
    /// list; <c>LoadDefault</c> given a parameter list that matches no overload; an entry with no message —
    /// each goes red naming the line.
    /// </remarks>
    [Fact]
    public void EveryBannedSymbol_NamesAMemberThatExists()
    {
        var entries = Entries();
        Assert.NotEmpty(entries);

        var problems = new List<string>();
        foreach (var (line, id, message) in entries)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                problems.Add($"  line {line}: {id} has no message after ';' — the error a ban raises should say what to use instead");
            }

            if (Resolve(id) is { } why)
            {
                problems.Add($"  line {line}: {id} {why}");
            }
        }

        Assert.True(problems.Count == 0,
            $"{ListPath} has entries the analyzer will match against nothing, so their bans silently lapse:\n"
            + string.Join('\n', problems));
    }

    /// <summary>
    /// <b>The working directory itself is banned, and every banned argument-less overload of this repo's own
    /// code has an overload a test can call instead.</b>
    /// </summary>
    /// <remarks>
    /// The first half is the point of the file: the three ways test code reads the working directory, and the
    /// one way it changes it for every test running alongside. The second keeps a ban honest — banning the
    /// CLI's lookup without an explicit-root alternative would leave a test with no way to do the right thing.
    /// Mutation-checked: the <c>P:System.Environment.CurrentDirectory</c> line deleted, and the
    /// <c>Directory.SetCurrentDirectory</c> line deleted; a ban added for <c>SpecLoader.FindSpecsDirectory</c>,
    /// which has no overload to offer — each goes red.
    /// </remarks>
    [Fact]
    public void TheWorkingDirectory_IsBanned_AndEveryBannedLookupHasAnAlternative()
    {
        var ids = Entries().Select(static e => e.Id).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var required in new[]
        {
            "M:System.IO.Directory.GetCurrentDirectory",
            "M:System.IO.Directory.SetCurrentDirectory(System.String)",
            "P:System.Environment.CurrentDirectory",
            "P:BattleScribeSpec.RepoRoot.FromWorkingDirectory",
        })
        {
            if (!ids.Contains(required))
            {
                problems.Add($"  {required} is not banned");
            }
        }

        foreach (var id in ids.Where(static i => i.StartsWith("M:BattleScribeSpec.", StringComparison.Ordinal) && !i.Contains('(', StringComparison.Ordinal)))
        {
            var (typeName, member) = SplitMember(id[2..]);
            var type = FindType(typeName);
            if (type is not null && !type.GetMethods(AnyMember).Any(m => m.Name == member && m.GetParameters().Length > 0))
            {
                problems.Add($"  {id} is banned, and {type.Name} has no {member} overload taking a root or a directory for a test to call instead");
            }
        }

        Assert.True(problems.Count == 0, $"{ListPath}:\n" + string.Join('\n', problems));
    }


    // ---- MSBuild's evaluation of the test projects ----

    /// <summary>The evaluated properties the project-level check reads.</summary>
    private static readonly string[] EvaluatedProperties =
        ["TreatWarningsAsErrors", "WarningsNotAsErrors", "NoWarn", "WarningLevel", "RunAnalyzers", "RunAnalyzersDuringBuild"];

    /// <summary>The evaluated items both MSBuild-backed checks read.</summary>
    private static readonly string[] EvaluatedItems = ["PackageReference", "AdditionalFiles", "Compile", "EditorConfigFiles"];

    /// <summary>One evaluated item: its identity, its full path, and every metadata value MSBuild reported.</summary>
    private sealed record EvaluatedItem(string Identity, string FullPath, IReadOnlyDictionary<string, string> Metadata);

    /// <summary>One test project as MSBuild evaluates it.</summary>
    private sealed record EvaluatedProject(
        string RelativePath,
        IReadOnlyDictionary<string, string> Properties,
        IReadOnlyDictionary<string, IReadOnlyList<EvaluatedItem>> Items)
    {
        /// <summary>The evaluated value, trimmed; empty when unset.</summary>
        public string Property(string name) => Properties.GetValueOrDefault(name, "").Trim();

        /// <summary>The evaluated items of <paramref name="type"/>; empty when there are none.</summary>
        public IReadOnlyList<EvaluatedItem> ItemsOf(string type) => Items.GetValueOrDefault(type) ?? [];
    }

    /// <summary>Every test project in the solution, evaluated once for the class, in parallel.</summary>
    private static readonly Lazy<Task<EvaluatedProject[]>> Evaluated = new(static () =>
        Task.WhenAll(CiTestInvocations.TestProjects.Select(static p => EvaluateAsync(p.RelativePath))));

    /// <summary>
    /// <c>dotnet msbuild &lt;project&gt; -getProperty:… -getItem:…</c>, started at the repository root so its
    /// <c>global.json</c> picks the SDK, and parsed. Evaluation only: no target runs, nothing is written.
    /// </summary>
    private static async Task<EvaluatedProject> EvaluateAsync(string relativePath)
    {
        var root = TestPaths.Root;
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("msbuild");
        psi.ArgumentList.Add(Path.Combine(root, relativePath));
        psi.ArgumentList.Add("-nologo");
        foreach (var property in EvaluatedProperties)
        {
            psi.ArgumentList.Add($"-getProperty:{property}");
        }

        foreach (var item in EvaluatedItems)
        {
            psi.ArgumentList.Add($"-getItem:{item}");
        }

        // `dotnet test` leaves its own MSBuild's locations in the environment it starts this app with; the child
        // resolves its own from global.json, like any `dotnet` command started in the checkout.
        foreach (var inherited in new[] { "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH" })
        {
            psi.Environment.Remove(inherited);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start `dotnet msbuild {relativePath}`.");
        using var bound = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var stdOut = process.StandardOutput.ReadToEndAsync(bound.Token);
        var stdErr = process.StandardError.ReadToEndAsync(bound.Token);
        try
        {
            await process.WaitForExitAsync(bound.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"`dotnet msbuild {relativePath}` (evaluation only) did not finish in two minutes.");
        }

        var output = await stdOut;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"`dotnet msbuild {relativePath} -getProperty/-getItem` exited {process.ExitCode}:\n{output}{await stdErr}");
        }

        using var json = JsonDocument.Parse(output);
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (json.RootElement.TryGetProperty("Properties", out var evaluatedProperties))
        {
            foreach (var property in evaluatedProperties.EnumerateObject())
            {
                properties[property.Name] = property.Value.GetString() ?? "";
            }
        }

        var items = new Dictionary<string, IReadOnlyList<EvaluatedItem>>(StringComparer.OrdinalIgnoreCase);
        if (json.RootElement.TryGetProperty("Items", out var evaluatedItems))
        {
            foreach (var type in evaluatedItems.EnumerateObject())
            {
                items[type.Name] = [.. type.Value.EnumerateArray().Select(static item =>
                {
                    var metadata = item.EnumerateObject().ToDictionary(
                        static m => m.Name,
                        static m => m.Value.ValueKind == JsonValueKind.String ? m.Value.GetString() ?? "" : m.Value.ToString(),
                        StringComparer.OrdinalIgnoreCase);
                    return new EvaluatedItem(metadata.GetValueOrDefault("Identity", ""), metadata.GetValueOrDefault("FullPath", ""), metadata);
                })];
            }
        }

        return new EvaluatedProject(relativePath, properties, items);
    }

    // ---- What a source file or an analyzer configuration file can say ----

    /// <summary>A <c>#pragma warning disable</c> directive and whatever follows it on its line.</summary>
    private static readonly Regex PragmaDisable = new(
        @"^[ \t]*#[ \t]*pragma[ \t]+warning[ \t]+disable\b(?<ids>[^\r\n]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>A suppression attribute (<c>SuppressMessage</c>, <c>UnconditionalSuppressMessage</c>) and its arguments.</summary>
    private static readonly Regex SuppressionAttribute = new(
        @"\b(?:Unconditional)?SuppressMessage(?:Attribute)?\s*\((?<args>[^)]*)\)", RegexOptions.CultureInvariant);

    /// <summary>
    /// The analyzer-configuration keys that reach RS0030: its own severity, its category's, and every analyzer's.
    /// The last two are the quiet ones — neither names the rule.
    /// </summary>
    private static readonly string[] SeverityKeys =
    [
        $"dotnet_diagnostic.{Rule}.severity",
        "dotnet_analyzer_diagnostic.category-ApiDesign.severity",
        "dotnet_analyzer_diagnostic.severity",
    ];

    /// <summary>The severities below <c>warning</c>: each keeps a banned call out of the build's errors.</summary>
    private static readonly string[] SeveritiesOff = ["none", "silent", "suggestion"];

    /// <summary>
    /// The analyzer configuration that applies to the projects' sources: every global configuration file the
    /// compiler is handed, and each <c>.editorconfig</c> from a source's directory up to and including the nearest
    /// one that declares <c>root = true</c>.
    /// </summary>
    private static SortedSet<string> AnalyzerConfigFiles(IEnumerable<EvaluatedProject> projects)
    {
        var applied = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            var handedOver = project.ItemsOf("EditorConfigFiles")
                .Select(static i => Path.GetFullPath(i.FullPath))
                .Where(File.Exists)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            applied.UnionWith(handedOver.Where(static f => !Path.GetFileName(f).Equals(".editorconfig", StringComparison.OrdinalIgnoreCase)));

            var directories = project.ItemsOf("Compile")
                .Select(static i => Path.GetDirectoryName(Path.GetFullPath(i.FullPath)))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in directories)
            {
                for (var dir = directory; dir is not null; dir = Path.GetDirectoryName(dir))
                {
                    var candidate = Path.Combine(dir, ".editorconfig");
                    if (handedOver.Contains(candidate))
                    {
                        applied.Add(candidate);
                        if (DeclaresRoot(candidate))
                        {
                            break;
                        }
                    }
                }
            }
        }

        return applied;
    }

    /// <summary>Whether an <c>.editorconfig</c> says <c>root = true</c> before its first section.</summary>
    private static bool DeclaresRoot(string editorConfig) =>
        File.ReadLines(editorConfig)
            .Select(static l => l.Trim())
            .TakeWhile(static l => !l.StartsWith('['))
            .Any(static l => Regex.IsMatch(l, @"^root\s*=\s*true$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    /// <summary>
    /// The setting on <paramref name="line"/> when it lowers one of <see cref="SeverityKeys"/> below
    /// <c>warning</c>; otherwise null. Parsed as Roslyn parses it: <c>=</c> or <c>:</c>, case-insensitive keys,
    /// <c>#</c> and <c>;</c> start a comment.
    /// </summary>
    private static string? SeverityOff(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text[0] is '#' or ';' or '[')
        {
            return null;
        }

        var separator = text.IndexOfAny(['=', ':']);
        if (separator < 0)
        {
            return null;
        }

        var key = text[..separator].Trim();
        var value = text[(separator + 1)..];
        var comment = value.IndexOfAny(['#', ';']);
        value = (comment < 0 ? value : value[..comment]).Trim();
        return SeverityKeys.Contains(key, StringComparer.OrdinalIgnoreCase) && SeveritiesOff.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? text
            : null;
    }

    /// <summary>Diagnostic ids in an MSBuild list or a pragma: split on <c>;</c>, <c>,</c> and whitespace.</summary>
    private static HashSet<string> Ids(string value) =>
        value.Split([';', ',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string StripLineComment(string text) =>
        text.IndexOf("//", StringComparison.Ordinal) is var at and >= 0 ? text[..at] : text;

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    /// <summary>A path relative to the checkout when it is inside it; absolute otherwise (a config file above it).</summary>
    private static string Display(string path)
    {
        var relative = Path.GetRelativePath(TestPaths.Root, path);
        return (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative).Replace('\\', '/');
    }

    private const BindingFlags AnyMember =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    /// <summary>The list's entries: line number, documentation-comment id, message. <c>//</c> lines are comments.</summary>
    private static List<(int Line, string Id, string Message)> Entries()
    {
        var lines = File.ReadAllLines(Path.Combine(TestPaths.Root, ListPath));
        var entries = new List<(int, string, string)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var text = lines[i].Trim();
            if (text.Length == 0 || text.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = text.IndexOf(';', StringComparison.Ordinal);
            entries.Add(separator < 0
                ? (i + 1, text, "")
                : (i + 1, text[..separator].Trim(), text[(separator + 1)..].Trim()));
        }

        return entries;
    }

    /// <summary>Null when <paramref name="id"/> resolves to a member; otherwise why it does not.</summary>
    private static string? Resolve(string id)
    {
        if (id.Length < 3 || id[1] != ':' || id.IndexOfAny(['`', '{', '[', '@', '~']) >= 0)
        {
            return "is not a documentation-comment id this lint can check (generic, array, by-ref or conversion shapes): extend BannedSymbolsTests.Resolve";
        }

        var kind = id[0];
        var body = id[2..];
        if (kind == 'T')
        {
            return FindType(body) is null ? "names no type" : null;
        }

        var parameters = (string[]?)null;
        var open = body.IndexOf('(', StringComparison.Ordinal);
        if (open >= 0)
        {
            if (!body.EndsWith(')'))
            {
                return "has an unbalanced parameter list";
            }

            parameters = body[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            body = body[..open];
        }

        var (typeName, member) = SplitMember(body);
        var type = FindType(typeName);
        if (type is null)
        {
            return $"names no type '{typeName}'";
        }

        bool Matches(MethodBase m) =>
            m.GetParameters().Select(static p => p.ParameterType.FullName).SequenceEqual(parameters ?? []);

        var found = kind switch
        {
            'M' when member == "#ctor" => type.GetConstructors(AnyMember).Any(Matches),
            'M' => type.GetMethods(AnyMember).Any(m => m.Name == member && Matches(m)),
            'P' => parameters is null && type.GetProperty(member, AnyMember) is not null,
            'F' => type.GetField(member, AnyMember) is not null,
            'E' => type.GetEvent(member, AnyMember) is not null,
            _ => false,
        };

        var what = kind switch { 'M' => "method", 'P' => "property", 'F' => "field", 'E' => "event", _ => $"'{kind}:' member" };
        return found ? null : $"matches no {what} of {type.FullName}";
    }

    private static (string Type, string Member) SplitMember(string body)
    {
        var dot = body.LastIndexOf('.');
        return (body[..dot], body[(dot + 1)..]);
    }

    /// <summary>A type by its documentation-comment name, in the runtime library or an assembly this project references.</summary>
    private static Type? FindType(string name)
    {
        // Nested types are separated by '.' in an id and by '+' in reflection: try each split from the right.
        var candidates = new List<string> { name };
        for (var i = name.LastIndexOf('.'); i > 0; i = name.LastIndexOf('.', i - 1))
        {
            candidates.Add(name[..i] + name[i..].Replace('.', '+'));
        }

        return candidates.Select(static c => Type.GetType(c)
                ?? ReferencedAssemblies.Value.Select(a => a.GetType(c)).FirstOrDefault(static t => t is not null))
            .FirstOrDefault(static t => t is not null);
    }

    private static readonly Lazy<IReadOnlyList<Assembly>> ReferencedAssemblies = new(static () =>
    [
        typeof(BannedSymbolsTests).Assembly,
        .. typeof(BannedSymbolsTests).Assembly.GetReferencedAssemblies().Select(Assembly.Load),
    ]);
}
