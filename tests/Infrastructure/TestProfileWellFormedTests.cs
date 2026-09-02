using System.Xml;
using System.Xml.Linq;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Asserts that every file in <c>tests/test-profiles/</c> is a runsettings file VSTest can use.
/// </summary>
/// <remarks>
/// <para>
/// A malformed settings file is not a loud failure. VSTest prints one line about it, carries on
/// WITHOUT the settings, and the run that follows selects whatever the bare invocation selects —
/// which for a profile whose whole content is a <c>TestCaseFilter</c> is nothing at all. The step
/// then exits 0 unless something else notices, and <c>-p:TestProfile=&lt;x&gt;</c> quietly stops
/// meaning anything.
/// </para>
/// <para>
/// The trap that produced this test is that XML forbids <c>--</c> INSIDE a comment, and these files
/// are mostly comment: writing a command-line flag by name breaks the file, in the part of it that
/// looks least like code. Nothing cheap parsed these files, so it reached main and surfaced as a
/// thorough lane selecting zero tests.
/// </para>
/// </remarks>
[Trait("Category", "Lint")]
public sealed class TestProfileWellFormedTests
{
    [Fact]
    public void EveryTestProfileParsesAndSelectsTests()
    {
        var dir = Path.Combine(ConcurrencyConfigurationDriftTests.RepoRoot, "tests", "test-profiles");
        var profiles = Directory.GetFiles(dir, "*.runsettings").Order(StringComparer.Ordinal).ToArray();

        Assert.SkipWhen(profiles.Length == 0, $"No runsettings files found under '{dir}'.");

        var broken = new List<string>();

        foreach (var profile in profiles)
        {
            var name = Path.GetFileName(profile);

            XDocument document;
            try
            {
                document = XDocument.Load(profile);
            }
            catch (XmlException ex)
            {
                broken.Add($"{name}: not well-formed XML — {ex.Message}");
                continue;
            }

            var filters = document.Descendants("TestCaseFilter").ToArray();
            if (filters.Length != 1)
            {
                broken.Add($"{name}: has {filters.Length} <TestCaseFilter> elements; a profile is one "
                    + "filter, and a run with none selects the whole suite.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(filters[0].Value))
            {
                broken.Add($"{name}: its <TestCaseFilter> is empty.");
            }
        }

        Assert.True(broken.Count == 0,
            "A test profile VSTest cannot use is a profile that silently stops selecting what it "
            + "names. Note that XML comments may not contain '--', which is how a flag written by "
            + "name breaks one.\n  " + string.Join("\n  ", broken));
    }
}
