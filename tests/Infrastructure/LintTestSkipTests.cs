using System.Text.RegularExpressions;
using Xunit.Sdk;
using Xunit.v3;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>A lint test does not skip.</b> A <c>Category=Lint</c> test either holds or fails; a skip is a
/// gate reporting neither, and in a run that shows nothing on success it reads as a pass.
/// </summary>
/// <remarks>
/// <para>
/// Lint tests read the repository, so the tempting skip is "the input is not there": no profiles
/// found, no fixture downloaded. That is the shape the profile lint had —
/// <c>TestProfileWellFormedTests</c> skipped when it found no runsettings files, which is exactly the
/// state in which every profile had silently stopped working. A missing input a lint depends on is a
/// failure, said so.
/// </para>
/// <para>
/// Checked three ways, over every test method carrying <c>Category=Lint</c> (on its class or itself):
/// </para>
/// <list type="bullet">
/// <item><description><b>Its fact or theory attribute</b> must not skip it, make it explicit, turn
/// exceptions into skips, or (a theory) skip it when its data source yields nothing.</description></item>
/// <item><description><b>Its data</b>: no data attribute — <c>[InlineData]</c>, <c>[MemberData]</c>,
/// <c>[ClassData]</c> — may skip its rows or make them explicit, and no row a source returns may carry
/// a <c>Skip</c> or <c>Explicit</c> of its own. The rows are asked for as discovery asks for them; one
/// skipped row of a lint theory reads as a pass like any other skip.</description></item>
/// <item><description><b>Its source</b>: the file that declares the lint class must not call a skip
/// assertion or throw a skip exception, unless the line is in <see cref="Allowed"/> with its reason;
/// each allowed line must still be there, exactly once. This half sees the declaring file only, so a
/// skip in a helper defined elsewhere is not seen.</description></item>
/// </list>
/// <para>
/// Mutation-checked when written: an <c>Assert.Skip</c> added to a lint test; an allowed line whose
/// text changed; <c>[Fact(Skip = …)]</c> on a lint test; <c>Skip = …</c> on a lint theory's
/// <c>[MemberData]</c>; and a <c>TheoryDataRow</c> with a <c>Skip</c> in a lint source — each turns this red.
/// </para>
/// </remarks>
[Trait("Category", "Lint")]
public sealed class LintTestSkipTests
{
    /// <summary>
    /// The skips a lint test may make: the file, text the line contains (spelled without the
    /// <c>Assert.</c> prefix, so this table is not itself a skip call), and the reason.
    /// </summary>
    private static readonly (string File, string Line, string Why)[] Allowed =
    [
        ("tests/Infrastructure/TestDataPinDriftTests.cs", "SkipUnless(checkedAny,",
            "fixture directories are optional: a lane downloads only the fixtures it needs, and absence cannot be stale. "
            + "With none present there is nothing to compare, and failing would turn every bare checkout, and CI's "
            + "checks job, red for a fixture it never asked for. The skip says so rather than reporting a pass."),
    ];

    private static readonly Regex SkipCall = new(@"\bAssert\s*\.\s*Skip(?:When|Unless)?\s*\(|\bSkipException\s*\.");

    [Fact]
    public async Task NoLintTestSkips()
    {
        var repoRoot = ConcurrencyConfigurationDriftTests.RepoRoot;
        var lintMethods = SuiteTraits.LintTests.ToList();
        var lintClasses = lintMethods.Select(static m => m.TestClass).Distinct().OrderBy(static t => t.FullName, StringComparer.Ordinal).ToList();
        Assert.True(lintClasses.Count > 0, "Found no Category=Lint test in this assembly, so this check checked nothing.");

        var violations = new List<string>();

        // The attribute half: what discovery reads.
        foreach (var test in lintMethods)
        {
            var where = $"{test.TestClass.Name}.{test.Method.Name}";
            foreach (var fact in test.Facts)
            {
                if (fact.Skip is not null)
                {
                    violations.Add($"  {where}: [{fact.GetType().Name}(Skip = \"{fact.Skip}\")]");
                }

                if (fact.SkipExceptions is { Length: > 0 } exceptions)
                {
                    violations.Add($"  {where}: turns {string.Join(", ", exceptions.Select(static e => e.Name))} into a skip");
                }

                if (fact.Explicit)
                {
                    violations.Add($"  {where}: is Explicit, so no gate ever runs it");
                }

                if (fact is ITheoryAttribute { SkipTestWithoutData: true })
                {
                    violations.Add($"  {where}: [{fact.GetType().Name}(SkipTestWithoutData = true)] skips, instead of failing, when its data runs out");
                }
            }
        }

        // The data half: a data attribute, or a row its source returns, can skip a row the attributes above never show.
        var rows = 0;
        await using (var tracker = new DisposalTracker())
        {
            foreach (var test in lintMethods)
            {
                foreach (var source in test.Data)
                {
                    var where = $"{test.TestClass.Name}.{test.Method.Name} [{source.GetType().Name}]";
                    if (source.Skip is not null)
                    {
                        violations.Add($"  {where}: Skip = \"{source.Skip}\" skips every row it gives");
                    }

                    if (source.Explicit == true)
                    {
                        violations.Add($"  {where}: Explicit = true, so no gate runs the rows it gives");
                    }

                    foreach (var row in await source.GetData(test.Method, tracker))
                    {
                        rows++;
                        if (row.Skip is not null && row.Skip != source.Skip)
                        {
                            violations.Add($"  {where} row {row.Label ?? "(unlabelled)"}: Skip = \"{row.Skip}\"");
                        }

                        if (row.Explicit == true && source.Explicit != true)
                        {
                            violations.Add($"  {where} row {row.Label ?? "(unlabelled)"}: Explicit = true, so no gate runs it");
                        }
                    }
                }
            }
        }

        Assert.True(rows > 0, "The lint theories' data sources gave no rows, so the data half checked nothing.");

        // The source half: the file that declares each lint class.
        var sources = TestProfileRegistryTests.SourceFiles(repoRoot, "tests")
            .Where(static f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Path: Path.GetRelativePath(repoRoot, f).Replace('\\', '/'), Lines: File.ReadAllLines(f)))
            .ToList();
        var allowedHits = Allowed.ToDictionary(static a => (a.File, a.Line), static _ => 0);

        foreach (var lintClass in lintClasses)
        {
            var declaring = TestProfileRegistryTests.DeclaringFiles(lintClass, sources).ToList();
            if (declaring.Count == 0)
            {
                violations.Add($"  {lintClass.FullName}: no source file under tests/ declares it, so its body was not checked");
                continue;
            }

            foreach (var path in declaring)
            {
                var lines = sources.First(s => s.Path == path).Lines;
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('*') || !SkipCall.IsMatch(line))
                    {
                        continue;
                    }

                    var allowed = Allowed.Where(a => a.File == path && line.Contains(a.Line, StringComparison.Ordinal)).ToList();
                    if (allowed.Count > 0)
                    {
                        allowedHits[(allowed[0].File, allowed[0].Line)]++;
                        continue;
                    }

                    violations.Add($"  {path}:{i + 1} ({lintClass.Name}): {line}");
                }
            }
        }

        violations.AddRange(allowedHits.Where(static kv => kv.Value != 1)
            .Select(static kv => $"  allowed skip '{kv.Key.Line}' in {kv.Key.File} was found {kv.Value} times, not once — "
                + "move the entry with the line, or delete it"));

        Assert.True(violations.Count == 0,
            "A Category=Lint test must hold or fail. A skip is a gate reporting neither, and in a run that prints nothing on "
            + "success it reads as a pass; a missing input the lint depends on is a failure, said so. If a skip is genuinely "
            + "right, add the line to LintTestSkipTests.Allowed with the reason:\n" + string.Join("\n", violations));
    }
}
