using System.Text.Json;
using System.Text.RegularExpressions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>The .NET SDK feature band this repo builds on is a decision, and these tests are where it is
/// kept one.</b> <c>global.json</c> pins the band, every CI job and the docker image install from that
/// file, and Dependabot is the channel that moves it.
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
    /// <b>The image builds on the SDK <c>global.json</c> pins, read from <c>global.json</c> by the build
    /// itself, and runs on the runtime the app targets.</b> No Dockerfile under <c>docker/</c> may name a
    /// <c>dotnet/sdk</c> image, whatever its tag; every one that builds .NET must read <c>sdk.version</c>
    /// from <c>global.json</c>; and every other .NET image must name the channel of the CLI's
    /// <c>TargetFramework</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No SDK image at all, not one that matches.</b> This test used to hold an SDK image tag to
    /// <c>global.json</c>'s feature band, and that was the wrong invariant twice over. Under
    /// <c>latestPatch</c> a tag in the right band but below the pinned patch cannot satisfy the pin either:
    /// #559 moved the pin from 10.0.400 to 10.0.402, this test passed, and the <c>docker</c> job failed
    /// with "A compatible .NET SDK was not found". And a tag that matched exactly would still have been a
    /// second record of the SDK, in a file Dependabot's dotnet-sdk updater does not know exists — right
    /// until the next bump, and moved by hand in every one. So the build stage installs the SDK from
    /// <c>global.json</c> (<c>docker/bs-spec.Dockerfile</c> says how), the one record is the one Dependabot
    /// moves, and this test refuses both ways back: a <c>dotnet/sdk</c> image, by name, and a build stage
    /// that no longer reads <c>global.json</c>.
    /// </para>
    /// <para>
    /// <b>The runtime images follow the target framework, not <c>global.json</c>.</b> An SDK builds every
    /// target framework up to its own, so a later-major SDK in <c>global.json</c> still produces a
    /// <c>net10.0</c> app, and that app starts only on a 10.0 runtime: roll-forward does not cross a major
    /// by default. Tying these tags to <c>global.json</c> would be wrong in exactly the window where the
    /// two differ. The tag is the channel (<c>10.0</c>, or a variant of it such as <c>10.0-noble</c>), not
    /// a patch, because nothing bumps a patch tag here: it would freeze the runtime's security fixes.
    /// </para>
    /// <para>
    /// Comment lines are not read — the Dockerfile tells its history in prose that names the images it no
    /// longer uses. <see cref="EveryBrokenDockerToolchain_IsReported"/> holds the ways each rule can be
    /// broken, #559's among them.
    /// </para>
    /// </remarks>
    [Fact]
    public void DockerImages_TakeTheSdkFromGlobalJson_AndTheRuntimeFromTheTargetFramework()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "global.json")));
        var pinned = doc.RootElement.GetProperty("sdk").GetProperty("version").GetString();
        var channel = TargetFrameworkChannel();

        var dockerfiles = Directory
            .EnumerateFiles(Path.Combine(RepoRoot, "docker"), "*.Dockerfile", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(dockerfiles);

        var (images, builds, problems) = DockerToolchainProblems(channel, dockerfiles.Select(static f => (Path.GetFileName(f), File.ReadAllText(f))));

        Assert.True(
            images > 0 && builds > 0,
            $"Read {images} `mcr.microsoft.com/dotnet/*` image(s) and {builds} Dockerfile(s) that build .NET under docker/. "
            + "Either the images stopped building or running .NET, or they spell it in a form this gate cannot read — "
            + "check before assuming either rule still holds.");

        Assert.True(
            problems.Count == 0,
            "These Dockerfiles take their .NET toolchain from somewhere other than the record that owns it:\n"
            + string.Join("\n", problems)
            + $"\n\nThe SDK is global.json's alone (pinned: {pinned}, rollForward latestPatch). The build stage starts "
            + "from runtime-deps and installs global.json's `sdk.version` itself, so an SDK bump needs no edit under "
            + "docker/; a `dotnet/sdk` image is a second record of it that Dependabot never moves — #559's 10.0.400 "
            + "image sat inside the band of a 10.0.402 pin and still could not satisfy it. The runtime images name the "
            + $"channel of the CLI's TargetFramework (net{channel}, so {channel}): that, not the SDK, is what the app "
            + "needs to start.");
    }

    /// <summary>
    /// <b>Each way back to a second record is reported</b>: an SDK image in the pinned band but below the
    /// pinned patch (#559, which the old band check passed), one at the pinned version itself, a floating
    /// SDK tag, a build stage that no longer reads <c>global.json</c>, and a runtime image off the target
    /// framework's channel — by major, by patch pin, or by naming no tag — or not a runtime image at all.
    /// Each finding is matched by its own wording, so a rule that only fires through another's branch
    /// does not count. The clean sample's comment names an SDK image, so it also proves comment lines are
    /// not read.
    /// </summary>
    [Fact]
    public void EveryBrokenDockerToolchain_IsReported() => LintSamples.AssertEachBreakIsRejected(
        "# It was FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build until the pin outran it.\n"
        + "FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS build\n"
        + "COPY global.json .\n"
        + "RUN version=\"$(jq -er '.sdk.version' global.json)\"\n"
        + "RUN dotnet publish src/App/App.csproj -c Release -o /app\n"
        + "FROM mcr.microsoft.com/dotnet/aspnet:10.0\n",
        [
            // #559: global.json at 10.0.402, the image at 10.0.400 — the same band, a patch below the pin.
            ("runtime-deps:10.0 AS build", "sdk:10.0.400 AS build", "sdk:10.0.400 is an SDK image"),
            ("runtime-deps:10.0 AS build", "sdk:10.0.402 AS build", "sdk:10.0.402 is an SDK image"),
            ("runtime-deps:10.0 AS build", "sdk:10.0 AS build", "sdk:10.0 is an SDK image"),
            ("jq -er '.sdk.version' global.json", "echo 10.0.402", "without reading `sdk.version` from global.json"),
            ("aspnet:10.0\n", "aspnet:11.0\n", "aspnet:11.0 is not the 10.0 channel"),
            ("aspnet:10.0\n", "aspnet:10.0.13\n", "aspnet:10.0.13 is not the 10.0 channel"),
            ("aspnet:10.0\n", "aspnet\n", "aspnet with no tag"),
            ("aspnet:10.0\n", "monitor:10.0\n", "monitor:10.0 is not a runtime image"),
        ],
        static text => DockerToolchainProblems("10.0", [("sample.Dockerfile", text)]).Problems);

    /// <summary>
    /// How many <c>mcr.microsoft.com/dotnet/*</c> images the Dockerfiles name, how many of the files build
    /// .NET, and each rule of <see cref="DockerImages_TakeTheSdkFromGlobalJson_AndTheRuntimeFromTheTargetFramework"/>
    /// one breaks, as <c>  &lt;file&gt;: …</c>. <paramref name="channel"/> is the target framework's
    /// <c>major.minor</c>. Comment lines are skipped.
    /// </summary>
    private static (int Images, int Builds, List<string> Problems) DockerToolchainProblems(string channel, IEnumerable<(string Name, string Text)> dockerfiles)
    {
        var images = 0;
        var builds = 0;
        var problems = new List<string>();
        foreach (var (name, text) in dockerfiles)
        {
            var instructions = string.Join('\n', text.Split('\n').Where(static l => !l.TrimStart().StartsWith('#')));
            foreach (Match match in DotnetImage.Matches(instructions))
            {
                images++;
                var repo = match.Groups["repo"].Value;
                var reference = match.Groups["ref"].Value;
                if (repo == "sdk" || repo.EndsWith("/sdk", StringComparison.Ordinal))
                {
                    problems.Add($"  {name}: {repo}{reference} is an SDK image; the SDK is the one global.json pins, installed by the build stage");
                }
                else if (repo is not ("runtime-deps" or "runtime" or "aspnet"))
                {
                    problems.Add($"  {name}: {repo}{reference} is not a runtime image (runtime-deps, runtime or aspnet)");
                }
                else if (reference.Length == 0)
                {
                    problems.Add($"  {name}: {repo} with no tag is `latest`, not the {channel} channel net{channel} runs on");
                }
                else if (reference != $":{channel}" && !reference.StartsWith($":{channel}-", StringComparison.Ordinal))
                {
                    problems.Add($"  {name}: {repo}{reference} is not the {channel} channel net{channel} runs on");
                }
            }

            if (BuildsDotnet.IsMatch(instructions))
            {
                builds++;
                if (!ReadsTheSdkVersionFromGlobalJson.IsMatch(instructions))
                {
                    problems.Add($"  {name}: builds .NET without reading `sdk.version` from global.json, so its SDK is not the pinned one");
                }
            }
        }

        return (images, builds, problems);
    }

    /// <summary>An image from <c>mcr.microsoft.com/dotnet/</c>: the repository, and the <c>:tag</c> or <c>@digest</c> after it, if any.</summary>
    private static readonly Regex DotnetImage = new(@"mcr\.microsoft\.com/dotnet/(?<repo>[a-z0-9/-]+)(?<ref>[:@]\S*)?");

    /// <summary>A <c>dotnet</c> command that needs an SDK.</summary>
    private static readonly Regex BuildsDotnet = new(@"\bdotnet\s+(?:restore|build|publish|pack|test)\b");

    /// <summary>
    /// <c>sdk.version</c> read out of <c>global.json</c> on one line, as the build stage's
    /// <c>jq -er '.sdk.version' global.json</c> does. A different way of reading the pin is a change to
    /// make here too, deliberately.
    /// </summary>
    private static readonly Regex ReadsTheSdkVersionFromGlobalJson = new(@"\.sdk\.version\b.*\bglobal\.json\b");

    /// <summary>
    /// The <c>major.minor</c> of the CLI's target framework, the one app the image ships: from its own
    /// project if that sets one, else from <c>Directory.Build.props</c>, which every project inherits.
    /// </summary>
    private static string TargetFrameworkChannel()
    {
        var match = new[] { Path.Combine("src", "BattleScribeSpec.Cli", "BattleScribeSpec.Cli.csproj"), "Directory.Build.props" }
            .Select(static f => TargetFramework.Match(File.ReadAllText(Path.Combine(RepoRoot, f))))
            .FirstOrDefault(static m => m.Success);

        Assert.True(
            match is not null,
            "Neither src/BattleScribeSpec.Cli/BattleScribeSpec.Cli.csproj nor Directory.Build.props sets a "
            + "`<TargetFramework>netX.Y</TargetFramework>` this gate can read, so it cannot say which runtime the image needs.");
        return match.Groups["channel"].Value;
    }

    private static readonly Regex TargetFramework = new(@"<TargetFramework>net(?<channel>\d+\.\d+)</TargetFramework>");

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
