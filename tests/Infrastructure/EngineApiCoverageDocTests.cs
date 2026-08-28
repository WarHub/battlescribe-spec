using System.Reflection;
using System.Text.RegularExpressions;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// The coverage report's engine-API table has to list every member of <see cref="IRosterEngine"/>,
/// because the sentence above it says every member is exercised.
/// <para>
/// It said that over a table that omitted <c>LoadRoster</c>, <c>ReloadRoster</c> and
/// <c>ExportRosterXml</c> — the three that were least covered, which is how a completeness claim
/// fails: not by being wrong about what it lists, but by not listing the thing. A defaulted
/// interface member costs nothing to add and shows up in no build output, so the table cannot be
/// kept honest by remembering to update it.
/// </para>
/// <para>
/// So the claim is checked against the interface itself. Adding a member to
/// <see cref="IRosterEngine"/> fails this test until the report gains a row for it — which is the
/// moment to decide whether the suite exercises it, not months later when someone reads the table.
/// </para>
/// </summary>
[Trait("Category", "Lint")]
public sealed class EngineApiCoverageDocTests
{
    private const string ReportPath = "docs/comprehensive-engine-coverage-report.md";

    /// <summary>
    /// Members the table deliberately does not carry, with the reason. Lifecycle hooks are called
    /// around every spec rather than driven by one, so a "specs using" count for them is either
    /// "all" or meaningless — and <c>Dispose</c> is not engine behaviour at all.
    /// </summary>
    private static readonly Dictionary<string, string> NotEngineBehaviour = new(StringComparer.Ordinal)
    {
        ["SetTestContext"] = "lifecycle — called before every spec, asserts nothing",
        ["Cleanup"] = "lifecycle — called after every spec, asserts nothing",
        ["Dispose"] = "IDisposable, not engine behaviour",
    };

    [Fact]
    public void EveryRosterEngineMember_HasARowInTheCoverageReport()
    {
        var documented = DocumentedMethods();
        var declared = DeclaredMembers();

        var missing = declared.Except(documented, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Assert.True(
            missing.Count == 0,
            $"{ReportPath}'s engine-API table claims every IRosterEngine member is exercised, but does "
            + $"not list: {string.Join(", ", missing)}. Add a row per member (with the spec count and "
            + "the specs that drive it), or — if the member is not engine behaviour — add it to "
            + $"{nameof(NotEngineBehaviour)} in {nameof(EngineApiCoverageDocTests)} with the reason.");

        var stale = documented.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Assert.True(
            stale.Count == 0,
            $"{ReportPath}'s engine-API table lists members IRosterEngine no longer declares: "
            + $"{string.Join(", ", stale)}. Remove those rows.");
    }

    /// <summary>Every member the report is expected to account for, by name.</summary>
    private static HashSet<string> DeclaredMembers()
    {
        var members = typeof(IRosterEngine)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OfType<MethodInfo>()
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Where(name => !NotEngineBehaviour.ContainsKey(name))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            members.Count > 0,
            "Reflection found no members on IRosterEngine — this test would pass vacuously.");

        return members;
    }

    /// <summary>
    /// The first column of the engine-API table, which runs from its own heading to the next one.
    /// Bounded that way so the other tables in the report — enums, features, per-engine matrices —
    /// cannot contribute rows that happen to look like method names.
    /// </summary>
    private static HashSet<string> DocumentedMethods()
    {
        var root = RepoRoot.FromWorkingDirectory
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
        var path = Path.Combine(root, ReportPath);
        var report = File.ReadAllText(path);

        var section = Regex.Match(
            report,
            @"^##\s*1\.\s*Engine\s+API\s+Coverage\s*$(?<body>.*?)^(##\s|---\s*$)",
            RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(
            section.Success,
            $"{ReportPath} has no '## 1. Engine API Coverage' section — this test cannot check a table "
            + "it cannot find, so the heading is part of the contract.");

        var rows = Regex.Matches(section.Groups["body"].Value, @"^\|\s*`(?<method>\w+)`\s*\|", RegexOptions.Multiline)
            .Select(m => m.Groups["method"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            rows.Count > 0,
            $"{ReportPath}'s engine-API section has no `Method` rows — the table shape changed.");

        return rows;
    }
}
