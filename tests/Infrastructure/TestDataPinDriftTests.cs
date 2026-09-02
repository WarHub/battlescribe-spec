using System.Security.Cryptography;
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
/// The marker answers "which pin was asked for", which leaves the bytes unaccounted for: an entry
/// may also declare <c>sha256</c> content pins, and those are checked here as well. A content pin
/// covers exactly the files it names — today the HAR the frozen NR suites replay, which is the
/// fixture with both the most reach and a history of being swapped by hand.
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
                continue;
            }

            // The marker records the pin that was ASKED for, so bytes changed in place under a
            // correct marker still read as pinned. Where an entry declares content pins, they are
            // the part the fixture itself has to satisfy.
            if (!entry.Value.TryGetProperty("sha256", out var contentPins))
            {
                continue;
            }

            foreach (var pin in contentPins.EnumerateObject())
            {
                var file = Path.Combine(dir, pin.Name);
                if (!File.Exists(file))
                {
                    drifted.Add($"{entry.Name}: testdata.json content-pins '{pin.Name}', which is not "
                        + $"in '{dir}'.");
                    continue;
                }

                using var bytes = File.OpenRead(file);
                var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (!string.Equals(digest, pin.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                {
                    drifted.Add($"{entry.Name}: '{pin.Name}' is sha256 {digest}, testdata.json pins "
                        + $"{pin.Value.GetString()}.");
                }
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
