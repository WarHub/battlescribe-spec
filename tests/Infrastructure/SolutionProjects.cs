using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BattleScribeSpec.Tests;

/// <summary>A test project in <c>BattleScribeSpec.slnx</c>.</summary>
/// <param name="RelativePath">The csproj, repo-relative, with forward slashes.</param>
/// <param name="Directory">Its directory, the same way.</param>
/// <param name="AssemblyName">Its <c>AssemblyName</c>, else its file name.</param>
internal sealed record SolutionProject(string RelativePath, string Directory, string AssemblyName);

/// <summary>
/// The solution's test projects: every project <c>BattleScribeSpec.slnx</c> lists that references
/// xunit.v3. Read from the solution, so a third test project is found without an edit here.
/// </summary>
internal static class SolutionProjects
{
    private static readonly Lazy<IReadOnlyList<SolutionProject>> Loaded = new(Load);

    /// <summary>Every test project in the solution, in the order it lists them.</summary>
    internal static IReadOnlyList<SolutionProject> Tests => Loaded.Value;

    private static List<SolutionProject> Load()
    {
        var root = TestPaths.Root;
        var tests = XDocument.Load(Path.Combine(root, RepoRoot.MarkerFileName))
            .Descendants("Project")
            .Select(static p => p.Attribute("Path")?.Value)
            .OfType<string>()
            .Select(static p => p.Replace('\\', '/'))
            .Select(csproj => (Csproj: csproj, Text: File.ReadAllText(Path.Combine(root, csproj))))
            .Where(static p => Regex.IsMatch(p.Text, @"<PackageReference\s+Include=""xunit\.v3"))
            .Select(static p => new SolutionProject(
                p.Csproj,
                Path.GetDirectoryName(p.Csproj)!.Replace('\\', '/'),
                Regex.Match(p.Text, @"<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>") is { Success: true } m
                    ? m.Groups[1].Value
                    : Path.GetFileNameWithoutExtension(p.Csproj)))
            .ToList();

        return tests.Count > 0
            ? tests
            : throw new InvalidOperationException($"{RepoRoot.MarkerFileName} lists no project that references xunit.v3.");
    }
}
