using System.Xml.Linq;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>The compile-time ban on the working directory in test code is wired.</b> <c>tests/BannedSymbols.txt</c>
/// lists what the test projects may not call, and <c>Microsoft.CodeAnalysis.BannedApiAnalyzers</c> turns each
/// call into error RS0030 wherever analyzers run (local builds and CI's <c>checks</c> job). Tests resolve the
/// checkout from their binaries instead (<see cref="TestPaths"/>).
/// </summary>
[Trait("Category", "Lint")]
public sealed class BannedSymbolsTests
{
    private const string AnalyzerPackage = "Microsoft.CodeAnalysis.BannedApiAnalyzers";

    private const string ListPath = "tests/BannedSymbols.txt";

    /// <summary>
    /// <b>The list exists and bans something, and every test project references the analyzer and hands it the
    /// list</b> as <c>AdditionalFiles</c>.
    /// </summary>
    [Fact]
    public void EveryTestProject_ReferencesTheAnalyzer_AndTheList()
    {
        var list = Path.GetFullPath(Path.Combine(TestPaths.Root, ListPath));
        Assert.True(File.Exists(list), $"{ListPath} does not exist: the analyzer has nothing to ban.");
        Assert.True(File.ReadLines(list).Any(static l => l.Trim() is { Length: > 0 } t && !t.StartsWith("//", StringComparison.Ordinal)),
            $"{ListPath} bans nothing.");

        var projects = SolutionProjects.Tests;
        Assert.NotEmpty(projects);
        var problems = new List<string>();
        foreach (var project in projects)
        {
            var path = Path.Combine(TestPaths.Root, project.RelativePath);
            var items = XDocument.Load(path).Descendants().Where(static e => e.Name.LocalName is "PackageReference" or "AdditionalFiles").ToList();
            if (!items.Any(static e => e.Name.LocalName == "PackageReference" && (string?)e.Attribute("Include") == AnalyzerPackage))
            {
                problems.Add($"  {project.RelativePath} does not reference {AnalyzerPackage}");
            }

            var directory = Path.GetDirectoryName(path)!;
            if (!items.Any(e => e.Name.LocalName == "AdditionalFiles" && (string?)e.Attribute("Include") is { } include
                && string.Equals(Path.GetFullPath(Path.Combine(directory, include.Replace('\\', '/'))), list, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"  {project.RelativePath} does not include {ListPath} as AdditionalFiles");
            }
        }

        Assert.True(problems.Count == 0, "A test project the ban does not reach can call the working directory freely:\n" + string.Join("\n", problems));
    }
}
