using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using BattleScribeSpec.Tests.Profiles;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>Temporary, until the suites leave VSTest:</b> the runsettings files in <c>tests/test-profiles/</c>
/// are generated from the test-profile registry, byte for byte, and nothing else lives there.
/// </summary>
/// <remarks>
/// <para>
/// <c>-p:TestProfile=&lt;name&gt;</c> hands VSTest <c>tests/test-profiles/&lt;name&gt;.runsettings</c>,
/// so while VSTest runs the suites those files are what a profile does. They used to be the record;
/// now <c>tests/TestProfiles/TestProfiles.cs</c> is, and each file is its rendering: one per profile
/// that covers <see cref="EngineLanes.Assembly"/>, holding the profile's filter and every environment
/// value it sets. A "must be unset" entry has no runsettings form and is left out. A hand edit, a
/// missing file or a stray one turns this red, with the expected text in the message and every
/// expected file written under <c>artifacts/test-profiles-expected/</c> for copying over.
/// </para>
/// <para>
/// Once the test app resolves profiles itself, nothing reads these files and this test goes with them.
/// </para>
/// <para>
/// Mutation-checked when written: a hand edit to one file's filter goes red naming the file, the
/// filter it has and the one the registry renders.
/// </para>
/// </remarks>
[Trait("Category", "Lint")]
public sealed class RunsettingsGenerationTests
{
    [Fact]
    public void Runsettings_MatchTheRegistry()
    {
        var repoRoot = ConcurrencyConfigurationDriftTests.RepoRoot;
        var dir = Path.Combine(repoRoot, "tests", "test-profiles");
        var expected = TestProfiles.All
            .Where(static p => p.Assemblies.Contains(EngineLanes.Assembly, StringComparer.Ordinal))
            .ToDictionary(static p => $"{p.Name}.runsettings", Render, StringComparer.Ordinal);
        Assert.NotEmpty(expected);

        var actual = Directory.GetFiles(dir, "*.runsettings").Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();

        problems.AddRange(expected.Keys.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(f => $"  {f} is missing; the registry renders:\n{Indent(expected[f])}"));
        problems.AddRange(actual.Except(expected.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(static f => $"  {f} matches no profile that covers {EngineLanes.Assembly}; delete it, or add the profile to the registry"));

        foreach (var (file, text) in expected.Where(e => actual.Contains(e.Key)).OrderBy(static e => e.Key, StringComparer.Ordinal))
        {
            var onDisk = File.ReadAllText(Path.Combine(dir, file)).ReplaceLineEndings("\n");
            if (onDisk != text)
            {
                problems.Add($"  {file} differs from the registry ({Difference(onDisk, text)}); the registry renders:\n{Indent(text)}");
            }
        }

        if (problems.Count > 0)
        {
            var outDir = Path.Combine(repoRoot, "artifacts", "test-profiles-expected");
            Directory.CreateDirectory(outDir);
            foreach (var (file, text) in expected)
            {
                File.WriteAllText(Path.Combine(outDir, file), text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            problems.Add($"\n  Every expected file is written to {Path.GetRelativePath(repoRoot, outDir).Replace('\\', '/')}/; to take them all: "
                + $"delete tests/test-profiles/*.runsettings and copy those files in.");
        }

        Assert.True(problems.Count == 0,
            "tests/test-profiles/ is generated from tests/TestProfiles/TestProfiles.cs and must not be edited by hand: change "
            + "the profile in the registry instead, and take the file it renders.\n" + string.Join("\n", problems));
    }

    /// <summary>The runsettings file a profile renders to.</summary>
    internal static string Render(TestProfile profile)
    {
        var filter = profile.Selection.Filter;
        Assert.False(string.IsNullOrEmpty(filter),
            $"Profile {profile.Name} covers {EngineLanes.Assembly} with no filter; on VSTest a profile is its filter, and a "
            + "runsettings file without one runs the whole suite.");

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append($"<!-- GENERATED from the test-profile registry: profile '{profile.Name}' in tests/TestProfiles/TestProfiles.cs,\n");
        sb.Append("     whose Purpose says what it is for. Do not edit: RunsettingsGenerationTests.Runsettings_MatchTheRegistry\n");
        sb.Append("     fails on any difference and prints the file the registry renders. -->\n");
        sb.Append("<RunSettings>\n");
        sb.Append("  <RunConfiguration>\n");

        var env = profile.Env.Where(static kv => kv.Value is not null).OrderBy(static kv => kv.Key, StringComparer.Ordinal).ToList();
        if (env.Count > 0)
        {
            sb.Append("    <EnvironmentVariables>\n");
            foreach (var (key, value) in env)
            {
                sb.Append($"      <{key}>{SecurityElement.Escape(value)}</{key}>\n");
            }

            sb.Append("    </EnvironmentVariables>\n");
        }

        sb.Append($"    <TestCaseFilter>{SecurityElement.Escape(filter)}</TestCaseFilter>\n");
        sb.Append("  </RunConfiguration>\n");
        sb.Append("</RunSettings>\n");
        return sb.ToString();
    }

    /// <summary>What differs, read as runsettings: the filter, then the environment; or that the file does not parse.</summary>
    private static string Difference(string onDisk, string expected)
    {
        static (string? Filter, string Env) Read(string text)
        {
            var doc = XDocument.Parse(text);
            var env = doc.Descendants("EnvironmentVariables").Elements().Select(static e => $"{e.Name.LocalName}={e.Value}").Order(StringComparer.Ordinal);
            return (doc.Descendants("TestCaseFilter").SingleOrDefault()?.Value, string.Join(";", env));
        }

        try
        {
            var (diskFilter, diskEnv) = Read(onDisk);
            var (wantFilter, wantEnv) = Read(expected);
            return diskFilter != wantFilter ? $"its filter is '{diskFilter}', the registry's is '{wantFilter}'"
                : diskEnv != wantEnv ? $"its environment is '{diskEnv}', the registry's is '{wantEnv}'"
                : "same filter and environment; the text around them was edited";
        }
        catch (XmlException ex)
        {
            return $"it is not well-formed XML: {ex.Message}";
        }
    }

    private static string Indent(string text) => string.Join("\n", text.TrimEnd('\n').Split('\n').Select(static l => "      " + l));
}
