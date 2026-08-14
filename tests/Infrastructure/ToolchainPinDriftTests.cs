using System.Text.Json;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>The .NET SDK feature band this repo builds on is a decision, and these tests are where it is
/// kept one.</b> <c>global.json</c> pins the band, every CI job installs from that file, and
/// Dependabot is the channel that moves it.
/// </summary>
/// <remarks>
/// <para>
/// The reason a pin is needed at all is <c>Directory.Build.props</c>:
/// <c>AnalysisLevel=latest-recommended</c> resolves the enabled CA rule set from the installed SDK,
/// and <c>TreatWarningsAsErrors=true</c> turns any newly-recommended rule into a build error. So the
/// question "can this build fail?" was answered by whichever SDK the runner happened to fetch, on a
/// commit that changed nothing.
/// </para>
/// <para>
/// <b>It already fired once, unobserved.</b> SDK 10.0.400 was released 2026-08-11. Every
/// <c>setup-dotnet</c> step asked for a floating <c>'10.0.x'</c> and <c>global.json</c> said
/// <c>rollForward: latestFeature</c>, so from the next run onward CI built on a feature band that
/// appears in no commit, no changelog entry and no review — run 31746128450 on <c>main</c>,
/// 2026-08-13. It was green. A band change is exactly where analyzers widen (the previous one
/// surfaced ~22 CA errors across TestKit and TraceSummary on untouched files, #312), so "green" was
/// the coin landing the right way up, not a property anything in the repo guaranteed.
/// </para>
/// <para>
/// Each test below guards one half of the arrangement, because either half alone is worse than
/// neither: a pin with no bump path rots into an ancient toolchain nobody dares move, and a bump
/// path with no pin is what we already had.
/// </para>
/// </remarks>
[Trait("Category", "Lint")]
public sealed class ToolchainPinDriftTests
{
    private static string RepoRoot => ConcurrencyConfigurationDriftTests.RepoRoot;

    /// <summary>
    /// <b>No CI job may choose its own SDK.</b> A <c>setup-dotnet</c> step that names a version
    /// installs whatever matches at that moment, independently of <c>global.json</c> — which is the
    /// hole this closes, and the one a copy-pasted new job would reopen without noticing.
    /// </summary>
    /// <remarks>
    /// Falsifiable: change any <c>global-json-file: global.json</c> in <c>.github/workflows</c> back
    /// to <c>dotnet-version: '10.0.x'</c> and this test names the file and the line.
    /// </remarks>
    [Fact]
    public void EverySetupDotnetStep_InstallsTheSdkDeclaredInGlobalJson()
    {
        var offenders = WorkflowLines()
            .Where(l => l.Text.Contains("dotnet-version:", StringComparison.Ordinal)
                && !l.Text.TrimStart().StartsWith('#'))
            .Select(l => $"  {l.File}:{l.Number}: {l.Text.Trim()}")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These workflow steps pin a .NET SDK version outside global.json:\n"
            + string.Join("\n", offenders)
            + "\n\nUse `global-json-file: global.json` instead. setup-dotnet honours the `latest*` "
            + "rollForward variants, so it installs the newest SDK inside the pinned band — one "
            + "declaration of the toolchain for CI and contributors alike, and one file for "
            + "Dependabot to bump.");

        // ...and the replacement is actually present, so deleting every setup-dotnet step does not
        // pass this gate by vacuous truth.
        var declared = WorkflowLines()
            .Count(l => l.Text.Contains("global-json-file: global.json", StringComparison.Ordinal));

        Assert.True(
            declared > 0,
            "No workflow step installs the SDK from global.json. Either setup-dotnet was removed "
            + "from CI entirely, or the input was renamed — both make this gate meaningless.");
    }

    /// <summary>
    /// <b>The pin itself.</b> <c>latestPatch</c> is the only <c>rollForward</c> value that holds a
    /// feature band: <c>latestFeature</c>, <c>latestMinor</c> and <c>latestMajor</c> all accept a
    /// band nobody chose, and omitting <c>rollForward</c> defaults to <c>latestPatch</c> only for
    /// exact-match purposes people reliably misremember. Stated explicitly so it reads as a decision.
    /// </summary>
    [Fact]
    public void GlobalJson_PinsTheSdkToOneFeatureBand()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "global.json")));
        var sdk = doc.RootElement.GetProperty("sdk");

        Assert.Equal("latestPatch", sdk.GetProperty("rollForward").GetString());

        // A band is major.minor.Fxx — the hundreds digit is the feature band, and patches within it
        // are the only movement `latestPatch` permits.
        var version = sdk.GetProperty("version").GetString();
        Assert.Matches(@"^\d+\.\d+\.\d{3}$", version);
    }

    /// <summary>
    /// <b>The bump path.</b> Without this entry the pin above is a slow leak rather than a fix — the
    /// band freezes, the toolchain ages, and the eventual move is a large one made under pressure.
    /// Dependabot's <c>dotnet-sdk</c> updater rewrites <c>sdk.version</c> and does cross feature
    /// bands (dependabot-core#11668), which is the case that matters here.
    /// </summary>
    /// <remarks>
    /// Falsifiable: delete the <c>dotnet-sdk</c> block from <c>.github/dependabot.yml</c>.
    /// </remarks>
    [Fact]
    public void Dependabot_OwnsTheSdkBump()
    {
        var config = File.ReadAllText(Path.Combine(RepoRoot, ".github", "dependabot.yml"));

        Assert.Contains("package-ecosystem: \"dotnet-sdk\"", config, StringComparison.Ordinal);
    }

    private static IEnumerable<(string File, int Number, string Text)> WorkflowLines()
    {
        var workflows = Path.Combine(RepoRoot, ".github", "workflows");
        foreach (var file in Directory
            .EnumerateFiles(workflows, "*.yml", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(RepoRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            var number = 0;
            foreach (var line in File.ReadAllLines(file))
            {
                yield return (relative, ++number, line);
            }
        }
    }
}
