using System.Text.Json;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Asserts that the fixtures on disk are the ones <c>testdata.json</c> pins.
/// </summary>
/// <remarks>
/// <para>
/// The frozen suites replay whatever is in <c>.testdata/</c>. <c>setup.ps1</c> puts the pinned
/// bytes there and records the pin in a <c>.tag</c> marker beside them — but only when it runs.
/// Nothing else checked, so a checkout whose pin moved after its last setup replayed the OLD
/// snapshot and said nothing: a green run proving the wrong thing, and the whole point of a pin
/// lost. That is not hypothetical — it is how a stale HAR survived a bump in a live worktree.
/// </para>
/// <para>
/// A missing directory is not a failure: lanes download only the fixtures they need, and absence
/// cannot be stale. A directory with no marker is, because its provenance is unknown — which is the
/// same reason <c>setup.ps1</c> writes the marker only after the download is verified, rather than
/// recording whatever happened to arrive.
/// </para>
/// </remarks>
[Trait("Category", "Lint")]
public sealed class TestDataPinDriftTests
{
    [Fact]
    public void EveryDownloadedFixtureIsAtThePinTestDataJsonDeclares()
    {
        var repoRoot = ConcurrencyConfigurationDriftTests.RepoRoot;
        using var pins = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "testdata.json")));

        var drifted = new List<string>();
        var checkedAny = false;

        foreach (var entry in pins.RootElement.EnumerateObject())
        {
            // "tag" for a release pin, "commit" for an archive pin — whichever it declares is what
            // setup.ps1 writes to the marker.
            var expected = entry.Value.TryGetProperty("tag", out var tag) ? tag.GetString()
                : entry.Value.TryGetProperty("commit", out var commit) ? commit.GetString()
                : null;
            if (expected is null)
            {
                drifted.Add($"{entry.Name}: testdata.json declares neither 'tag' nor 'commit'.");
                continue;
            }

            var dir = entry.Value.TryGetProperty("path", out var path) && path.GetString() is { } p
                ? Path.Combine(repoRoot, p)
                : Path.Combine(repoRoot, ".testdata", entry.Name);

            if (!Directory.Exists(dir))
            {
                continue;
            }

            checkedAny = true;
            var marker = Path.Combine(dir, ".tag");
            if (!File.Exists(marker))
            {
                drifted.Add($"{entry.Name}: '{dir}' exists with no .tag marker, so what it holds is "
                    + $"unknown. Expected the pin '{expected}'.");
                continue;
            }

            var actual = File.ReadAllText(marker).Trim();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                drifted.Add($"{entry.Name}: on disk '{actual}', testdata.json pins '{expected}'.");
            }
        }

        Assert.True(drifted.Count == 0,
            "Fixtures on disk do not match testdata.json — the frozen suites are replaying something "
            + "other than the pin, and a pass proves nothing about it. Run './setup.ps1' (add -Force "
            + "if a marker is missing) to re-download.\n  " + string.Join("\n  ", drifted));

        // A run where every fixture directory was absent asserted nothing. Say so rather than
        // reporting a pass: this gate is only meaningful where the fixtures actually are.
        Assert.SkipUnless(checkedAny,
            "No pinned fixture directories are present, so there was nothing to compare.");
    }
}
