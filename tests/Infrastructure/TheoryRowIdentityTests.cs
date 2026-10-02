using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit.Sdk;
using Xunit.v3;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Every computed theory row is named by what it stands for — a spec's <c>category/id</c>, a file's
/// repo-relative path — and never by where the repo is checked out.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a row's arguments decide.</b> xunit renders a theory row's arguments into the test's
/// display name, derives its uid from them, and cuts each one at 50 characters. The conformance,
/// lint and schema rows used to carry the spec's absolute path first and its name second, so:
/// </para>
/// <list type="bullet">
///   <item>every one of those ~4,000 names began with the checkout path, and differed between two
///   checkouts of the same commit;</item>
///   <item>76 of them were cut short, and the four specs whose <c>category/id</c> runs past 50
///   characters could not be selected at all: <c>--filter "DisplayName~&lt;id&gt;"</c>, the AGENTS.md
///   recipe for one spec, matched none of their eight rows each.</item>
/// </list>
/// <para>
/// The rows now carry the spec's name alone and set <see cref="ITheoryDataRow.Label"/> to it, which
/// xunit renders instead of the argument list, uncut: <c>&lt;Namespace&gt;.&lt;Class&gt;.&lt;Method&gt;
/// [category/id]</c>. The test resolves the file through <see cref="SpecLoader.ResolveRosterSpec"/>.
/// </para>
/// <para>
/// <b>Checked against the rows, not the rendered names.</b> This sweep asks xunit's own data
/// attributes for their rows — exactly what discovery does — and inspects arguments and labels,
/// so it does not depend on how a runner prints a name, or on running one. Inline data is left
/// out: it is a compile-time constant and cannot depend on the checkout.
/// </para>
/// <para>
/// Mutation-checked when written. Each of these turns
/// <see cref="EveryComputedRow_CarriesNoCheckoutPath_AndEverySpecRowIsLabelledWithItsName"/> red:
/// <see cref="SpecSchemaTests"/>' rows put back to <c>[path, relPath]</c>, or to an unlabelled
/// repo-relative path; <see cref="JsonSchemaLintTests"/>' rows carrying the absolute, forward-slashed
/// path again; the <c>Label</c> dropped from <see cref="ConformanceTestBase.AllSpecs"/>; a row given a
/// <c>TestDisplayName</c> of its own; and the sweep reading attributes by plain reflection. The
/// display-name overrides that turn <see cref="EveryTestName_KeepsItsClass"/> red are listed there.
/// </para>
/// </remarks>
[Trait("Category", "Lint")]
public sealed class TheoryRowIdentityTests
{
    [Fact]
    public async Task EveryComputedRow_CarriesNoCheckoutPath_AndEverySpecRowIsLabelledWithItsName()
    {
        var checkout = RepoRoot.FromBinaries
            ?? throw new InvalidOperationException($"No BattleScribeSpec.slnx above '{AppContext.BaseDirectory}'.");
        string[] checkoutSpellings = [checkout.Replace('\\', '/'), checkout.Replace('/', '\\')];

        var rosterSpecs = SpecNames(SpecLoader.FindRosterSpecsDirectory(), SpecLoader.DiscoverSpecs);
        var gameDataSpecs = SpecNames(SpecLoader.FindGameDataSpecsDirectory(), SpecLoader.DiscoverGameDataSpecs);
        Assert.True(rosterSpecs.Count > 0 && gameDataSpecs.Count > 0,
            $"Found {rosterSpecs.Count} roster and {gameDataSpecs.Count} GameData specs above '{AppContext.BaseDirectory}'. " +
            "Without the corpus this sweep cannot tell a spec row from any other, and would pass on nothing.");
        var specs = new HashSet<string>(rosterSpecs.Concat(gameDataSpecs), StringComparer.Ordinal);

        var violations = new List<string>();
        var specsSeen = new HashSet<string>(StringComparer.Ordinal);
        var sources = 0;
        var rows = 0;
        var emptySources = new List<string>();

        await using var tracker = new DisposalTracker();
        foreach (var (testClass, method, source) in ComputedDataSources())
        {
            sources++;
            var where = $"{testClass.Name}.{method.Name}";
            var sourceRows = await source.GetData(method, tracker);

            // xunit fails a theory that has no rows, so a source that gives this sweep none is one it is
            // reading differently from discovery — and every check below would pass on it unseen.
            if (sourceRows.Count == 0)
            {
                emptySources.Add($"{where} [{source.GetType().Name}]");
            }

            var sourceCarriesSpecs = false;
            foreach (var row in sourceRows)
            {
                rows++;
                var arguments = row.GetData();
                var label = row.Label ?? row.TestDisplayName ?? $"({string.Join(", ", arguments)})";

                var strings = arguments.OfType<string>().Select((s, i) => (What: $"argument {i}", Value: s))
                    .Append(("Label", row.Label ?? ""))
                    .Append(("TestDisplayName", row.TestDisplayName ?? ""));
                foreach (var (what, value) in strings)
                {
                    if (value.Length == 0)
                    {
                        continue;
                    }

                    if (Path.IsPathRooted(value) || checkoutSpellings.Any(c => value.Contains(c, StringComparison.OrdinalIgnoreCase)))
                    {
                        violations.Add(
                            $"{where} {label}: {what} \"{value}\" is or contains an absolute path. It becomes part of the " +
                            "test's name and uid, so both change with where the repo is checked out, and xunit cuts it at " +
                            "50 characters. Carry the name relative to the repo and resolve the file inside the test " +
                            $"({nameof(SpecLoader)}.{nameof(SpecLoader.ResolveRosterSpec)} / {nameof(SpecLoader.ResolveGameDataSpec)} for a spec).");
                    }
                }

                // A row's own display name replaces the whole name, namespace, class and method included —
                // see EveryTestName_KeepsItsClass, which holds the attribute-level half of this rule.
                if (row.TestDisplayName is not null)
                {
                    violations.Add(
                        $"{where} {label}: has TestDisplayName \"{row.TestDisplayName}\". It replaces the whole test name, " +
                        "class included, so DisplayName~<Class> stops selecting the row. Name a row with Label, which xunit " +
                        $"appends to '{testClass.FullName}.{method.Name}'.");
                }

                foreach (var argument in arguments.OfType<string>())
                {
                    if (SpecNameIn(argument, specs) is not { } name)
                    {
                        continue;
                    }

                    sourceCarriesSpecs = true;
                    specsSeen.Add(name);
                    if (argument != name)
                    {
                        violations.Add(
                            $"{where} {label}: carries \"{argument}\" — a spec row carries the spec's name, \"{name}\", " +
                            "the 'category/id' every other row and report uses, and resolves the file inside the test.");
                    }

                    if (row.Label != name)
                    {
                        violations.Add(
                            $"{where}: the row for spec \"{name}\" has Label \"{row.Label}\". It needs Label = \"{name}\", so " +
                            $"xunit names it '{testClass.FullName}.{method.Name} [{name}]': selectable with --filter " +
                            "\"DisplayName~<id>\" whatever the id's length (an argument is cut at 50 characters, a label is " +
                            "not), and still carrying its class, so DisplayName~<Class> keeps selecting every row.");
                    }
                }
            }

            // The data attribute's own Label stands in for a row that has none; on a spec source the only
            // label is the row's, so nothing but the spec's name can end up in the brackets.
            if (sourceCarriesSpecs && source.Label is not null)
            {
                violations.Add(
                    $"{where} [{source.GetType().Name}]: the data attribute sets Label = \"{source.Label}\". A spec row is " +
                    "labelled by the row itself, with the spec's name; drop the attribute's Label.");
            }
        }

        Assert.True(sources > 0 && rows > 0,
            $"Swept {sources} computed data sources and {rows} rows in {typeof(TheoryRowIdentityTests).Assembly.GetName().Name}. " +
            "This sweep has stopped finding theories, so it checks nothing.");
        Assert.True(emptySources.Count == 0,
            $"{emptySources.Count} of {sources} data sources gave this sweep no rows, though discovery fails a theory without " +
            $"any — so the sweep is not reading them the way xunit does, and every check here would pass on them unseen:\n  " +
            string.Join("\n  ", emptySources));

        var unseen = specs.Where(s => !specsSeen.Contains(s)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unseen.Count == 0,
            $"No theory row carries {unseen.Count} of the {specs.Count} specs on disk (first: {string.Join(", ", unseen.Take(5))}). " +
            "Either a factory stopped emitting them, or this sweep stopped recognising spec rows — in both cases the " +
            "label check above ran on less than the corpus.");

        Assert.True(violations.Count == 0,
            $"{violations.Count} theory row(s) are not named by what they stand for:\n  " +
            string.Join("\n  ", violations.Take(40)) +
            (violations.Count > 40 ? $"\n  … and {violations.Count - 40} more." : ""));
    }

    /// <summary>
    /// A labelled row is named <c>&lt;Class&gt;.&lt;Method&gt; [label]</c> only because xunit's method
    /// display is <c>ClassAndMethod</c> and nothing replaces that base name. Set to <c>Method</c>, or
    /// overridden by a display name, a name loses its class, and every documented
    /// <c>DisplayName~SpecLint</c>-style filter quietly stops matching it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every place that can take the class out of a name is checked: <c>methodDisplay</c> in each
    /// <c>xunit.runner.json</c>, <c>&lt;MethodDisplay&gt;</c> in each runsettings file, the inline
    /// <c>xUnit.MethodDisplay=</c> argument in anything that invokes the tests, and — for every test
    /// method in this assembly — a fact or theory attribute's <c>DisplayName</c> or a data attribute's
    /// <c>TestDisplayName</c>, either of which xunit uses as the base name in place of
    /// <c>&lt;Class&gt;.&lt;Method&gt;</c>. A row's own <c>TestDisplayName</c> is the third such override;
    /// <see cref="EveryComputedRow_CarriesNoCheckoutPath_AndEverySpecRowIsLabelledWithItsName"/> checks it,
    /// since that is the test that asks the sources for their rows.
    /// </para>
    /// <para>
    /// Mutation-checked when written: <c>"methodDisplay": "method"</c>, <c>[Theory(DisplayName = …)]</c> on
    /// a conformance theory, a <c>[MemberData(…, TestDisplayName = …)]</c>, and
    /// <c>-- xUnit.MethodDisplay=Method</c> in the test step script each turn this red.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryTestName_KeepsItsClass()
    {
        var checkout = RepoRoot.FromBinaries
            ?? throw new InvalidOperationException($"No BattleScribeSpec.slnx above '{AppContext.BaseDirectory}'.");
        var violations = new List<string>();

        var methods = 0;
        foreach (var (testClass, method, facts, data) in TestMethods())
        {
            methods++;
            foreach (var fact in facts.Where(f => f.DisplayName is not null))
            {
                violations.Add(
                    $"{testClass.Name}.{method.Name}: [{fact.GetType().Name}] sets DisplayName = \"{fact.DisplayName}\", " +
                    "which xunit uses as the test's name instead of '<Class>.<Method>'");
            }

            foreach (var source in data.Where(d => d.TestDisplayName is not null))
            {
                violations.Add(
                    $"{testClass.Name}.{method.Name}: [{source.GetType().Name}] sets TestDisplayName = \"{source.TestDisplayName}\", " +
                    "which xunit uses as every row's name instead of '<Class>.<Method>'");
            }
        }

        Assert.True(methods > 0, "Found no test methods in this assembly, so the display-name check below checks nothing.");

        string[] runnerJson =
        [
            Path.Combine(checkout, "tests", "xunit.runner.json"),
            Path.Combine(checkout, "tests", "BattleScribeSpec.Cli.Tests", "xunit.runner.json"),
        ];
        foreach (var file in runnerJson)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("methodDisplay", out var display)
                && !string.Equals(display.GetString(), "classAndMethod", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{Path.GetRelativePath(checkout, file)}: methodDisplay = {display}");
            }
        }

        var runsettings = Directory.GetFiles(Path.Combine(checkout, "tests", "test-profiles"), "*.runsettings");
        Assert.NotEmpty(runsettings);
        foreach (var file in runsettings)
        {
            foreach (var element in XDocument.Load(file).Descendants("MethodDisplay"))
            {
                if (!string.Equals(element.Value.Trim(), "ClassAndMethod", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{Path.GetRelativePath(checkout, file)}: <MethodDisplay>{element.Value}</MethodDisplay>");
                }
            }
        }

        // `dotnet test -- xUnit.MethodDisplay=Method` is the same runsettings element, passed inline.
        var inline = new Regex(@"\bxUnit\.MethodDisplay\s*=", RegexOptions.IgnoreCase);
        foreach (var file in ConcurrencyConfigurationDriftTests.TestInvocationFiles(checkout))
        {
            if (inline.IsMatch(File.ReadAllText(file)))
            {
                violations.Add($"{Path.GetRelativePath(checkout, file)}: passes xUnit.MethodDisplay= inline");
            }
        }

        Assert.True(violations.Count == 0,
            "xunit's method display must stay ClassAndMethod (the default), and no display name may replace it:\n  " +
            string.Join("\n  ", violations) +
            "\nEvery test is named '<Class>.<Method>…', and every labelled theory row '<Class>.<Method> [label]', only " +
            "because of it. Name a row with Label instead.");
    }

    /// <summary>
    /// Every computed data source on a test method xunit would run: each of its data attributes that
    /// is not inline data.
    /// </summary>
    private static IEnumerable<(Type TestClass, MethodInfo Method, IDataAttribute Source)> ComputedDataSources() =>
        from m in TestMethods()
        from source in m.Data
        where source is not InlineDataAttribute
        select (m.TestClass, m.Method, source);

    /// <summary>
    /// Every test method xunit would run in this assembly — a public, concrete class's public method
    /// carrying a fact or theory attribute — with its fact and data attributes as discovery reads them.
    /// Inherited methods are listed per concrete class, as xunit runs them. Shared with the lints that
    /// read the suite's traits (<see cref="SuiteTraits"/>).
    /// </summary>
    /// <remarks>
    /// The attributes come from <see cref="ExtensibilityPointFactory"/>, as they do in discovery, and
    /// not from plain reflection: it is what tells a <c>[MemberData]</c> without a <c>MemberType</c>
    /// which class it sits on. Read directly, such an attribute returns no rows at all — the first
    /// draft of the row sweep did exactly that, for every source but two, and only the check that every
    /// spec on disk appears in some row noticed.
    /// </remarks>
    internal static IEnumerable<(Type TestClass, MethodInfo Method, IReadOnlyCollection<IFactAttribute> Facts, IReadOnlyCollection<IDataAttribute> Data)> TestMethods()
    {
        foreach (var type in typeof(TheoryRowIdentityTests).Assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract || !type.IsVisible || type.ContainsGenericParameters)
            {
                continue;
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                var facts = ExtensibilityPointFactory.GetMethodFactAttributes(method);
                if (facts.Count == 0)
                {
                    continue;
                }

                yield return (type, method, facts, ExtensibilityPointFactory.GetMethodDataAttributes(method));
            }
        }
    }

    /// <summary>
    /// The spec a string argument stands for, or <see langword="null"/>. A spec is recognised by its
    /// last two path segments, so a factory that slips back to a path of any shape — absolute,
    /// repo-relative (<c>specs/roster/&lt;category&gt;/&lt;id&gt;.yaml</c>), either slash, or the name
    /// itself with <c>.yaml</c> — is still seen as a spec row and held to the label rule, instead of
    /// passing as some other string while xunit cuts it at 50 characters again.
    /// </summary>
    private static string? SpecNameIn(string argument, HashSet<string> specs)
    {
        var s = argument.Replace('\\', '/');
        if (s.EndsWith(".yaml", StringComparison.Ordinal))
        {
            s = s[..^".yaml".Length];
        }

        var last = s.LastIndexOf('/');
        if (last <= 0)
        {
            return null;
        }

        var previous = s.LastIndexOf('/', last - 1);
        var candidate = previous < 0 ? s : s[(previous + 1)..];
        return specs.Contains(candidate) ? candidate : null;
    }

    private static HashSet<string> SpecNames(
        string? specsDir, Func<string, IEnumerable<(string Path, string Id, string Category)>> discover)
        => specsDir is null
            ? []
            : new HashSet<string>(discover(specsDir).Select(s => SpecLoader.SpecName(s.Category, s.Id)), StringComparer.Ordinal);
}
