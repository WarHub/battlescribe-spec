using System.Text.Json;
using System.Text.RegularExpressions;

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
    private static string RepoRoot => TestPaths.Root;

    /// <summary>
    /// <b>No CI job may choose its own SDK.</b> A <c>setup-dotnet</c> step that names a version
    /// installs whatever matches at that moment, independently of <c>global.json</c> — which is the
    /// hole this closes, and the one a copy-pasted new job would reopen without noticing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It reads every CI definition file — the workflows and the composite actions they call
    /// (<see cref="CiDefinitionFiles"/>) — because the jobs in <c>ci.yml</c> install the SDK through
    /// <c>.github/actions/setup</c>: a scan of the workflows alone would see only the snapshot
    /// workflow's step, and a floating version in the action would pass. It fails if it read no action.
    /// </para>
    /// <para>
    /// Falsifiable: change any <c>global-json-file: global.json</c> — in a workflow, or in
    /// <c>.github/actions/setup/action.yml</c> — back to <c>dotnet-version: '10.0.x'</c> and this test
    /// names the file and the line (mutation-checked on the action).
    /// </para>
    /// </remarks>
    [Fact]
    public void EverySetupDotnetStep_InstallsTheSdkDeclaredInGlobalJson()
    {
        var lines = CiDefinitionFiles.Lines().ToList();
        CiDefinitionFiles.AssertReadAnAction(lines.Select(static l => l.File.Path), nameof(EverySetupDotnetStep_InstallsTheSdkDeclaredInGlobalJson));

        var offenders = lines
            .Where(l => l.Text.Contains("dotnet-version:", StringComparison.Ordinal)
                && !l.Text.TrimStart().StartsWith('#'))
            .Select(l => $"  {l.File.Path}:{l.Number}: {l.Text.Trim()}")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These CI steps (workflows and composite actions) pin a .NET SDK version outside global.json:\n"
            + string.Join("\n", offenders)
            + "\n\nUse `global-json-file: global.json` instead. setup-dotnet honours the `latest*` "
            + "rollForward variants, so it installs the newest SDK inside the pinned band — one "
            + "declaration of the toolchain for CI and contributors alike, and one file for "
            + "Dependabot to bump.");

        // ...and the replacement is actually present, so deleting every setup-dotnet step does not
        // pass this gate by vacuous truth.
        var declared = lines
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
    /// <b><c>global.json</c> puts <c>dotnet test</c> on Microsoft.Testing.Platform.</b> Without the
    /// <c>test.runner</c> entry the .NET 10 SDK runs <c>dotnet test</c> in VSTest mode, which these
    /// projects cannot run at all: xunit.v3 4's platform targets refuse it ("Testing with VSTest target
    /// is no longer supported"), so every <c>dotnet test</c> — the pre-push gate, the CLI's CI step —
    /// would fail on the toolchain rather than on a test.
    /// </summary>
    /// <remarks>Mutation-checked when written: the <c>test</c> block deleted, and the runner renamed <c>VSTest</c>, each go red.</remarks>
    [Fact]
    public void GlobalJson_SelectsTheTestingPlatformRunner()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "global.json")));
        var runner = doc.RootElement.TryGetProperty("test", out var test) && test.TryGetProperty("runner", out var value)
            ? value.GetString()
            : null;

        Assert.True(runner == "Microsoft.Testing.Platform",
            $"global.json's test.runner is {(runner is null ? "missing" : $"'{runner}'")}; it must be \"Microsoft.Testing.Platform\". "
            + "The suites are Microsoft.Testing.Platform apps (tests/Directory.Build.props, tests/TestProfiles/TestHost.cs), and "
            + "`dotnet test` in the SDK's default VSTest mode cannot run them.");
    }

    /// <summary>
    /// <b>No VSTest package is referenced anywhere in the repository's projects</b>, nor pinned in
    /// <c>Directory.Packages.props</c>: <c>Microsoft.NET.Test.Sdk</c>, <c>xunit.runner.visualstudio</c>,
    /// <c>xunit.v3.mtp-off</c> (the VSTest flavour of xunit.v3), <c>coverlet.collector</c> (a VSTest data
    /// collector) and the <c>Microsoft.TestPlatform.*</c> family.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One of them back in a test project would bring a second runner into a build that has one: the
    /// VSTest adapter and test SDK compete with the platform's entry point and its MSBuild targets, and
    /// <c>Microsoft.NET.Test.Sdk</c> generates an entry point of its own. A pin with no reference is the
    /// other half: invisible to Dependabot, and ready to hold a transitive version back.
    /// </para>
    /// <para><see cref="EveryVsTestPackageForm_IsFound"/> holds the forms it must recognise.</para>
    /// </remarks>
    [Fact]
    public void NoVsTestPackage_IsReferencedOrPinned()
    {
        // The repository's own MSBuild files: the root, and the source trees. The vendored .deps/ and the
        // downloaded lib/ and artifacts/ are not this repository's projects.
        string[] extensions = [".csproj", ".props", ".targets"];
        string[] trees = ["src", "tests", "tools", "docker"];
        var files = Directory.EnumerateFiles(RepoRoot)
            .Concat(trees
                .Select(d => Path.Combine(RepoRoot, d))
                .Where(Directory.Exists)
                .SelectMany(static d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)))
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => (Path: f, Relative: Path.GetRelativePath(RepoRoot, f).Replace('\\', '/')))
            .Where(static f => !f.Relative.Split('/').Any(static s => s is "bin" or "obj" or "node_modules"))
            .ToList();
        Assert.Contains(files, static f => f.Relative == "Directory.Packages.props");
        Assert.Contains(files, static f => f.Relative.StartsWith("tests/", StringComparison.Ordinal) && f.Relative.EndsWith(".csproj", StringComparison.Ordinal));

        var offenders = files
            .SelectMany(f => VsTestPackage.Matches(File.ReadAllText(f.Path)).Select(m => $"  {f.Relative}: {m.Groups[1].Value}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These VSTest packages are referenced or pinned:\n" + string.Join("\n", offenders) + "\n\n"
            + "The suites run on Microsoft.Testing.Platform with xunit.v3's own runner; a VSTest adapter or test SDK in a test "
            + "project is a second runner with its own entry point. Remove it, and its pin from Directory.Packages.props.");
    }

    /// <summary>A reference or a central pin of a VSTest package, in any casing; group 1 is the package.</summary>
    private static readonly Regex VsTestPackage = new(
        @"<Package(?:Reference|Version)\s+(?:Include|Update)=""(Microsoft\.NET\.Test\.Sdk|xunit\.runner\.visualstudio|xunit\.v3\.mtp-off|coverlet\.collector|Microsoft\.TestPlatform[^""]*)""",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// <b>Every VSTest package is found</b>, referenced or pinned, by <c>Include</c> or <c>Update</c>, in any
    /// casing, and xunit.v3 itself is not.
    /// </summary>
    [Fact]
    public void EveryVsTestPackageForm_IsFound() => LintSamples.AssertEachBreakIsRejected(
        "<PackageReference Include=\"xunit.v3\" />\n<PackageVersion Include=\"xunit.v3\" Version=\"4.0.1\" />\n",
        [
            ("<PackageReference Include=\"xunit.v3\"", "<PackageReference Include=\"Microsoft.NET.Test.Sdk\"", "Microsoft.NET.Test.Sdk"),
            ("<PackageVersion Include=\"xunit.v3\"", "<PackageVersion Update=\"xunit.runner.visualstudio\"", "xunit.runner.visualstudio"),
            ("<PackageReference Include=\"xunit.v3\"", "<PackageReference Include=\"xunit.v3.mtp-off\"", "xunit.v3.mtp-off"),
            ("<PackageReference Include=\"xunit.v3\"", "<PackageReference Include=\"coverlet.collector\"", "coverlet.collector"),
            ("<PackageVersion Include=\"xunit.v3\"", "<packageversion include=\"microsoft.testplatform.objectmodel\"", "microsoft.testplatform.objectmodel"),
        ],
        static text => [.. VsTestPackage.Matches(text).Select(static m => m.Groups[1].Value)]);

    /// <summary>
    /// <b>A Dockerfile's SDK tag is the same decision, spelled somewhere Dependabot's dotnet-sdk
    /// updater cannot reach.</b> The images COPY <c>global.json</c>, so a tag outside the pinned band
    /// does not build differently — it fails outright ("A compatible .NET SDK was not found"). Two
    /// numbers that must agree, in two files, is exactly the shape that drifts, so it is asserted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bands only: the patch within the band is free to move, because <c>latestPatch</c> accepts any
    /// patch at or above the pinned one. So <c>sdk:10.0.302</c> satisfies a <c>10.0.300</c> pin and
    /// this test says nothing about it; <c>sdk:10.0.400</c> does not, and this test says so by name.
    /// </para>
    /// <para>
    /// A tag in another band fails here with both values — the failure the <c>docker</c> CI job would
    /// produce, several minutes later. <see cref="EveryBrokenSdkTag_IsReported"/> holds the ways a tag
    /// can leave the band.
    /// </para>
    /// </remarks>
    [Fact]
    public void DockerImagesUseTheSdkBandPinnedInGlobalJson()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "global.json")));
        var pinned = doc.RootElement.GetProperty("sdk").GetProperty("version").GetString()!;
        var pinnedBand = FeatureBandOf(pinned);

        var dockerfiles = Directory
            .EnumerateFiles(Path.Combine(RepoRoot, "docker"), "*.Dockerfile", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(dockerfiles);

        var (read, mismatched) = SdkTagsOutsideTheBand(pinned, dockerfiles.Select(static f => (Path.GetFileName(f), File.ReadAllText(f))));

        Assert.True(
            read > 0,
            "No `mcr.microsoft.com/dotnet/sdk` tag found under docker/. Either the images stopped using "
            + "the .NET SDK image or the tag was written in a form this gate cannot read — check before "
            + "assuming the pin still holds.");

        Assert.True(
            mismatched.Count == 0,
            $"global.json pins the SDK to the {pinnedBand} feature band (version {pinned}, "
            + $"rollForward latestPatch), but these images ask for a different band:\n"
            + string.Join("\n", mismatched)
            + "\n\nThe Dockerfiles COPY global.json, so this is a build failure, not a nuance: the "
            + "SDK in the image cannot satisfy the pin. Move both together — Dependabot's dotnet-sdk "
            + "updater rewrites global.json and does not know these files exist.");
    }

    /// <summary>
    /// <b>A tag outside the band is reported</b>, whichever way it leaves it: another feature band, another
    /// major version, or no band at all. Pinned to <c>10.0.400</c>, the sample asks for <c>10.0.402</c>.
    /// </summary>
    [Fact]
    public void EveryBrokenSdkTag_IsReported() => LintSamples.AssertEachBreakIsRejected(
        "FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build\nFROM mcr.microsoft.com/dotnet/aspnet:10.0\n",
        [
            ("sdk:10.0.402", "sdk:10.0.300", "sdk:10.0.300"),
            ("sdk:10.0.402", "sdk:11.0.400", "sdk:11.0.400"),
            ("sdk:10.0.402", "sdk:10.0", "sdk:10.0"),
            ("dotnet/sdk:10.0.402", "dotnet/sdk-preview:10.0.402", "read no sdk tag"),
        ],
        text =>
        {
            var (read, mismatched) = SdkTagsOutsideTheBand("10.0.400", [("sample.Dockerfile", text)]);
            return read == 0 ? ["read no sdk tag"] : mismatched;
        });

    /// <summary>
    /// How many <c>mcr.microsoft.com/dotnet/sdk</c> tags the Dockerfiles ask for, and each one outside the
    /// feature band of <paramref name="pinned"/>, as <c>  &lt;file&gt;: sdk:&lt;tag&gt;</c>.
    /// </summary>
    private static (int Read, List<string> Mismatched) SdkTagsOutsideTheBand(string pinned, IEnumerable<(string Name, string Text)> dockerfiles)
    {
        var pinnedBand = FeatureBandOf(pinned);
        var read = 0;
        var mismatched = new List<string>();
        foreach (var (name, text) in dockerfiles)
        {
            foreach (var match in Regex.Matches(text, @"mcr\.microsoft\.com/dotnet/sdk:(?<tag>[^\s]+)").Cast<Match>())
            {
                read++;
                var tag = match.Groups["tag"].Value;
                if (FeatureBandOf(tag) != pinnedBand)
                {
                    mismatched.Add($"  {name}: sdk:{tag}");
                }
            }
        }

        return (read, mismatched);
    }

    /// <summary>
    /// The <c>major.minor.Fxx</c> band of an SDK version or image tag, or the input unchanged when it
    /// names no band (<c>10.0</c>, <c>latest</c>) — those float by definition and are reported as a
    /// mismatch rather than silently treated as compatible.
    /// </summary>
    private static string FeatureBandOf(string version)
    {
        var match = Regex.Match(version, @"^(?<major>\d+)\.(?<minor>\d+)\.(?<band>\d)\d\d$");
        return match.Success
            ? $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}.{match.Groups["band"].Value}xx"
            : version;
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
}
