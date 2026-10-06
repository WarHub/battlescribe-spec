using System.Text.Json;
using BattleScribeSpec.Tests.Profiles;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>Every rule of the test app's entry point (<see cref="TestHost"/>), each with an input that trips
/// it.</b> <see cref="TestHost.Resolve"/> is a decision without side effects, so these drive it with a
/// command line and an environment of their own and read what it would run.
/// </summary>
/// <remarks>
/// The rows are the host's mutation checks, kept: each refusal here was also seen to go green when its
/// rule was deleted from <see cref="TestHost"/>, so a rule that stops firing is a red build.
/// </remarks>
[Trait("Category", "Lint")]
public sealed class TestHostTests
{
    private static readonly string[] DotnetTestTail = ["--server", "dotnettestcli", "--dotnet-test-pipe", "testingplatform.pipe.x"];

    private static HostOutcome Resolve(IEnumerable<string> args, string assembly = TestProfiles.Tests, params (string Name, string Value)[] environment)
    {
        var env = environment.ToDictionary(static e => e.Name, static e => e.Value, StringComparer.Ordinal);
        return TestHost.Resolve([.. args], assembly, name => env.GetValueOrDefault(name));
    }

    private static HostOutcome Resolve(string commandLine, params (string Name, string Value)[] environment) =>
        Resolve(Split(commandLine), TestProfiles.Tests, environment);

    /// <summary>A command line split at spaces, with <c>"…"</c> holding one token, as a shell would hand it over.</summary>
    private static List<string> Split(string commandLine) =>
        [.. System.Text.RegularExpressions.Regex.Matches(commandLine, "\"([^\"]*)\"|(\\S+)").Select(static m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)];

    private static string Args(HostOutcome outcome) => string.Join(" ", outcome.Arguments.Select(static a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));

    /// <summary><b>The session is read as xunit reads it</b>: <c>--server</c> alone or <c>jsonrpc</c> is an IDE, any other value is <c>dotnet test</c>, none is a direct run.</summary>
    [Theory]
    [InlineData("", nameof(TestSession.Direct))]
    [InlineData("--list-tests", nameof(TestSession.Direct))]
    [InlineData("--server", nameof(TestSession.Ide))]
    [InlineData("--server jsonrpc --client-port 5000", nameof(TestSession.Ide))]
    [InlineData("--server --client-port 5000", nameof(TestSession.Ide))]
    [InlineData("--zero-tests-policy strict --server dotnettestcli --dotnet-test-pipe p", nameof(TestSession.DotnetTest))]
    public void Session_IsClassifiedByServer(string commandLine, string expected) =>
        Assert.Equal(expected, Resolve(commandLine).Session.ToString());

    /// <summary><b>A direct run gets the strict policy exactly once</b>, and one that names it in any spelling keeps its own.</summary>
    [Theory]
    [InlineData("--list-tests", "--list-tests --zero-tests-policy strict")]
    [InlineData("--zero-tests-policy strict", "--zero-tests-policy strict")]
    [InlineData("--zero-tests-policy=strict", "--zero-tests-policy=strict")]
    [InlineData("--Zero-Tests-Policy:allow-skipped", "--Zero-Tests-Policy:allow-skipped")]
    [InlineData("--test-profile bs", "--filter Engine=BsRoster --zero-tests-policy strict")]
    public void DirectRun_GetsStrictOnce(string commandLine, string expected)
    {
        var outcome = Resolve(commandLine);
        Assert.Equal(HostAction.Run, outcome.Action);
        Assert.Equal(expected, Args(outcome));
    }

    /// <summary><b>A <c>dotnet test</c> run without the policy MSBuild passes is refused</b>: the MSBuild-carried arguments, <c>-p:TestProfile</c> among them, did not arrive.</summary>
    [Fact]
    public void DotnetTestRun_WithoutTheMsBuildArguments_IsRefused()
    {
        var refused = Resolve(["--test-profile", "bs", .. DotnetTestTail]);
        Assert.Equal(HostAction.Refuse, refused.Action);
        Assert.Contains("arguments MSBuild carries did not arrive", refused.Error, StringComparison.Ordinal);

        var accepted = Resolve(["--zero-tests-policy", "strict", "--test-profile", "bs", .. DotnetTestTail]);
        Assert.Equal(HostAction.Run, accepted.Action);
        Assert.Equal($"--zero-tests-policy strict {string.Join(" ", DotnetTestTail)} --filter Engine=BsRoster", Args(accepted));
    }

    /// <summary><b><c>TESTINGPLATFORM_EXITCODE_IGNORE</c> is refused</b>, profiled or not: it turns an empty run into a pass.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("--test-profile bs")]
    public void ExitCodeIgnore_IsRefused(string commandLine)
    {
        var outcome = Resolve(commandLine, ("TESTINGPLATFORM_EXITCODE_IGNORE", "8"));
        Assert.Equal(HostAction.Refuse, outcome.Action);
        Assert.Contains("TESTINGPLATFORM_EXITCODE_IGNORE=8", outcome.Error, StringComparison.Ordinal);
    }

    /// <summary><b>The options that would override a profile's verdict or selection are refused with one</b>, and pass without one.</summary>
    [Theory]
    [InlineData("--ignore-exit-code 8", "--ignore-exit-code")]
    [InlineData("--config-file my.testconfig.json", "--config-file")]
    [InlineData("--xunit-config-filename other.json", "--xunit-config-filename")]
    [InlineData("@more.rsp", "@more.rsp")]
    [InlineData("--filter-class BattleScribeSpec.Tests.X", "--filter-class")]
    [InlineData("--filter-not-trait Engine=X", "--filter-not-trait")]
    [InlineData("--filter-query /*/*/*/*[Engine=X]", "--filter-query")]
    [InlineData("--filter-uid abc", "--filter-uid")]
    [InlineData("--Filter-Class:X", "--Filter-Class:X")]
    public void ProfiledRun_RefusesOverrides(string extra, string named)
    {
        var refused = Resolve($"--test-profile bs {extra}");
        Assert.Equal(HostAction.Refuse, refused.Action);
        Assert.StartsWith($"{TestHost.Prefix} {named} cannot be combined with a test profile", refused.Error, StringComparison.Ordinal);

        Assert.Equal(HostAction.Run, Resolve(extra).Action);
    }

    /// <summary><b>A profiled run's policy is strict</b>: another one is refused.</summary>
    [Theory]
    [InlineData("--zero-tests-policy allow-skipped")]
    [InlineData("--zero-tests-policy=none")]
    public void ProfiledRun_RefusesAnotherPolicy(string policy)
    {
        var refused = Resolve($"--test-profile bs {policy}");
        Assert.Equal(HostAction.Refuse, refused.Action);
        Assert.Contains("The policy is strict", refused.Error, StringComparison.Ordinal);
    }

    /// <summary><b>An option given twice is refused with the host's own message</b>, in any mix of spellings — the platform's own refusal reaches a <c>dotnet test</c> reader as "Exit code: 5" and nothing else.</summary>
    [Theory]
    [InlineData("--filter A=1 --filter B=2", "--filter is given 2 times")]
    [InlineData("--filter=A=1 --filter:B=2", "--filter is given 2 times")]
    [InlineData("--test-profile bs --test-profile lint", "--test-profile is given 2 times")]
    [InlineData("--zero-tests-policy strict --zero-tests-policy none", "--zero-tests-policy is given 2 times")]
    public void RepeatedOption_IsRefused(string commandLine, string message)
    {
        var refused = Resolve(commandLine);
        Assert.Equal(HostAction.Refuse, refused.Action);
        Assert.Contains(message, refused.Error, StringComparison.Ordinal);
    }

    /// <summary><b>A VSTest option is refused, naming what replaced it</b> — <c>-p:RunSettingsFilePath</c> arrives as <c>--settings</c>.</summary>
    [Theory]
    [InlineData("--settings tests/x.runsettings", "-p:TestProfile=<name>")]
    [InlineData("--logger trx", "--report-xunit-trx")]
    [InlineData("--collect \"XPlat Code Coverage\"", "collects no coverage")]
    [InlineData("--blame-hang-timeout 5m", "--timeout")]
    public void VsTestOption_IsRefused(string commandLine, string replacement)
    {
        var refused = Resolve(commandLine);
        Assert.Equal(HostAction.Refuse, refused.Action);
        Assert.Contains("is a VSTest option", refused.Error, StringComparison.Ordinal);
        Assert.Contains(replacement, refused.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A profile's values are applied, and a default switch the caller set wins over the profile's</b>, saying so;
    /// a switch of an engine the profile does not claim is not mentioned.
    /// </summary>
    [Fact]
    public void ProfileEnvironment_IsApplied_AndTheCallersDefaultWins()
    {
        var applied = Resolve("--test-profile nr-live-smoke");
        Assert.Equal("https://www.newrecruit.eu", applied.Environment["NR_ENGINE_URL"]);
        Assert.Contains($"{TestHost.Prefix} nr-live-smoke: NR_ENGINE_URL=https://www.newrecruit.eu (profile)", applied.Messages);

        var caller = Resolve("--test-profile nr-live-smoke --list-tests", ("NR_ENGINE_URL", "https://example.invalid"));
        Assert.Equal(HostAction.Run, caller.Action);
        Assert.False(caller.Environment.ContainsKey("NR_ENGINE_URL"));
        Assert.Contains($"{TestHost.Prefix} nr-live-smoke: NR_ENGINE_URL=https://example.invalid (caller)", caller.Messages);

        var unclaimed = Resolve("--test-profile bs", ("NR_UI_TIMINGS", "1"));
        Assert.Equal(HostAction.Run, unclaimed.Action);
        Assert.DoesNotContain(unclaimed.Messages, static m => m.Contains("NR_UI_TIMINGS", StringComparison.Ordinal));

        // An empty value is no value: the profile's applies.
        Assert.Equal("https://www.newrecruit.eu", Resolve("--test-profile nr-live-smoke", ("NR_ENGINE_URL", "")).Environment["NR_ENGINE_URL"]);
    }

    /// <summary><b>The caller's <c>--filter</c> narrows the profile's: <c>(P)&amp;(U)</c></b>, parenthesised so a <c>|</c> in either stays inside it.</summary>
    [Theory]
    [InlineData("--test-profile bs", "--filter Engine=BsRoster", false)]
    [InlineData("--test-profile bs --filter DisplayName~kitchen-sink", "--filter (Engine=BsRoster)&(DisplayName~kitchen-sink)", true)]
    [InlineData("--test-profile smoke-bs --filter \"Tag=cost|Tag=constraint\"",
        "--filter ((Engine=BsRoster|Engine=BsGameData)&DisplayName~kitchen-sink)&(Tag=cost|Tag=constraint)", true)]
    [InlineData("--filter DisplayName~x --test-profile=bs", "--filter (Engine=BsRoster)&(DisplayName~x)", true)]
    public void CallerFilter_NarrowsTheProfile(string commandLine, string filter, bool narrowed)
    {
        var outcome = Resolve(commandLine);
        Assert.Equal(HostAction.Run, outcome.Action);
        Assert.Equal($"{filter} --zero-tests-policy strict", Args(outcome));
        Assert.Equal(narrowed, outcome.Narrowed);
        Assert.DoesNotContain(outcome.Arguments, static a => a.StartsWith("--test-profile", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>A profiled run's <c>--filter</c> takes exactly one expression</b>: with none or several it is refused,
    /// as the platform refuses it on an unprofiled run — not repaired by keeping the first value, or dropped.
    /// </summary>
    [Theory]
    [InlineData("--test-profile bs --filter DisplayName~protocol-kitchen-sink DisplayName~zzz", "and 2 follow it")]
    [InlineData("--test-profile bs --filter", "and none follows it")]
    [InlineData("--test-profile bs --filter --list-tests", "and none follows it")]
    [InlineData("--test-profile bs --filter=", "and none follows it")]
    public void ProfiledRun_RefusesAFilterWithoutOneExpression(string commandLine, string message)
    {
        var refused = Resolve(commandLine);
        Assert.Equal(HostAction.Refuse, refused.Action);
        Assert.Contains($"--filter takes one expression, {message}", refused.Error, StringComparison.Ordinal);
    }

    /// <summary><b>A profile with no filter takes the caller's alone</b> (<c>cli</c> runs its whole assembly).</summary>
    [Fact]
    public void WholeProfile_TakesTheCallersFilterAlone()
    {
        Assert.Equal("--zero-tests-policy strict", Args(Resolve(["--test-profile", "cli"], TestProfiles.Cli)));
        Assert.Equal("--filter DisplayName~Export --zero-tests-policy strict", Args(Resolve(["--test-profile", "cli", "--filter", "DisplayName~Export"], TestProfiles.Cli)));
    }

    /// <summary><b>An unknown profile, and a profile that does not cover this assembly, are refused</b> — the second with the project to name.</summary>
    [Fact]
    public void UnknownOrForeignProfile_IsRefused()
    {
        var unknown = Resolve("--test-profile nope");
        Assert.Equal(HostAction.Refuse, unknown.Action);
        Assert.Contains("unknown test profile 'nope'", unknown.Error, StringComparison.Ordinal);
        Assert.Contains("pre-push, core,", unknown.Error, StringComparison.Ordinal);

        var foreign = Resolve(["--test-profile", "lint"], TestProfiles.Cli);
        Assert.Equal(HostAction.Refuse, foreign.Action);
        Assert.Contains("`dotnet test --project tests/BattleScribeSpec.Tests.csproj -p:TestProfile=lint`", foreign.Error, StringComparison.Ordinal);

        Assert.Equal(HostAction.Run, Resolve(["--test-profile", "pre-push"], TestProfiles.Cli).Action);
        Assert.Equal(HostAction.Refuse, Resolve(["--test-profile", "cli"], TestProfiles.Tests).Action);
    }

    /// <summary><b><c>-automated</c> and <c>@@</c> go to xunit's console runner</b>, as its generated entry point sends them — never with a profile.</summary>
    [Theory]
    [InlineData("-automated", nameof(HostAction.ConsoleRunner))]
    [InlineData("@@", nameof(HostAction.ConsoleRunner))]
    [InlineData("-automated --test-profile bs", nameof(HostAction.Refuse))]
    public void AutomatedRuns_GoToTheConsoleRunner(string commandLine, string expected) =>
        Assert.Equal(expected, Resolve(commandLine).Action.ToString());

    /// <summary><b>An IDE's run passes through untouched</b> — no policy, no profile, no refusal.</summary>
    [Fact]
    public void IdeRun_IsUntouched()
    {
        string[] args = ["--server", "jsonrpc", "--client-port", "5000", "--filter-uid", "x"];
        var outcome = Resolve(args, TestProfiles.Tests, ("TESTINGPLATFORM_EXITCODE_IGNORE", "8"), ("GITHUB_ACTIONS", "true"));
        Assert.Equal(HostAction.Run, outcome.Action);
        Assert.Equal(args, outcome.Arguments);
        Assert.Empty(outcome.Environment);
    }

    /// <summary>
    /// <b>In GitHub Actions the run reports to GitHub with a failures-only summary</b>; a direct run also gets
    /// detailed output and its ten slowest tests, and an aggregate lane live output. What the caller already chose is kept.
    /// </summary>
    [Theory]
    [InlineData("--test-profile bs", "--filter Engine=BsRoster --zero-tests-policy strict --report-github --report-github-summary-include-passed false --output Detailed --show-slowest-tests 10")]
    [InlineData("--test-profile nr-ui-frozen", "--filter Engine=FrozenNrUiRoster --zero-tests-policy strict --report-github --report-github-summary-include-passed false --output Detailed --show-slowest-tests 10 --show-live-output on")]
    [InlineData("--output Normal --report-github-summary-include-passed true --show-slowest-tests 3", "--output Normal --report-github-summary-include-passed true --show-slowest-tests 3 --zero-tests-policy strict --report-github")]
    public void GitHubActions_ReportsToGitHub(string commandLine, string expected) =>
        Assert.Equal(expected, Args(Resolve(commandLine, ("GITHUB_ACTIONS", "true"))));

    /// <summary><b>A <c>dotnet test</c> run in Actions gets the reporter but not <c>--output</c> or <c>--show-slowest-tests</c></b>, which are <c>dotnet test</c>'s own options there.</summary>
    [Fact]
    public void GitHubActions_DotnetTestRun_KeepsItsOutputOption()
    {
        var outcome = Resolve(["--zero-tests-policy", "strict", "--test-profile", "cli", .. DotnetTestTail], TestProfiles.Cli, ("GITHUB_ACTIONS", "true"));
        Assert.Equal($"--zero-tests-policy strict {string.Join(" ", DotnetTestTail)} --report-github --report-github-summary-include-passed false", Args(outcome));
    }

    /// <summary><b><c>--list-test-profiles</c> prints the registry</b>, as JSON with <c>--json</c>: every profile, with the assemblies it covers.</summary>
    [Fact]
    public void ListTestProfiles_PrintsTheRegistry()
    {
        Assert.Equal(HostAction.ListProfiles, Resolve("--list-test-profiles").Action);
        Assert.True(Resolve("--list-test-profiles --json").Json);

        using var json = JsonDocument.Parse(TestHost.RenderProfilesJson());
        var profiles = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(TestProfiles.All.Count, profiles.Count);
        var prePush = profiles.Single(static p => p.GetProperty("name").GetString() == "pre-push");
        Assert.Equal([TestProfiles.Tests, TestProfiles.Cli], prePush.GetProperty("assemblies").EnumerateArray().Select(static a => a.GetString()));
        Assert.Contains("\nnr-live-smoke — BattleScribeSpec.Tests\n  filter: Engine=LiveNrRoster&Category=Smoke\n  sets:   NR_ENGINE_URL=https://www.newrecruit.eu\n",
            TestHost.RenderProfilesText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b><c>--list-test-profiles</c> through <c>dotnet test</c> is refused, naming <c>dotnet run</c></b>: a run
    /// that reports no test is a failed one to <c>dotnet test</c> ("Zero tests ran", exit 1).
    /// </summary>
    [Fact]
    public void ListTestProfiles_ThroughDotnetTest_IsRefused()
    {
        var refused = Resolve(["--zero-tests-policy", "strict", "--list-test-profiles", .. DotnetTestTail], TestProfiles.Cli);
        Assert.Equal(HostAction.Refuse, refused.Action);
        Assert.Contains("`dotnet run --project tests/BattleScribeSpec.Cli.Tests/BattleScribeSpec.Cli.Tests.csproj -- --list-test-profiles`",
            refused.Error, StringComparison.Ordinal);
    }

    // ── RunAsync: the decision, acted on ──

    /// <summary>A <see cref="HostIo"/> that records what the host does to the world, over an environment of the test's own.</summary>
    internal sealed class RecordingIo
    {
        public Dictionary<string, string?> Environment { get; } = new(StringComparer.Ordinal);

        public string? ProfileContext { get; private set; }

        public StringWriter Out { get; } = new();

        public StringWriter Error { get; } = new();

        public string[]? ConsoleRunnerArgs { get; private set; }

        /// <summary>What <see cref="HostIo.TelemetryArtifactBase"/> answers.</summary>
        public string? ArtifactBase { get; set; }

        /// <summary>Every file the host wrote or appended to, with its text, in order.</summary>
        public List<(string Path, string Text, bool Append)> Files { get; } = [];

        public HostIo Io => new(
            name => Environment.GetValueOrDefault(name),
            (name, value) => Environment[name] = value,
            profile => ProfileContext = profile,
            Out,
            Error,
            args =>
            {
                ConsoleRunnerArgs = args;
                return Task.FromResult(0);
            },
            () => ArtifactBase,
            (path, text) => Files.Add((path, text, true)),
            (path, text) => Files.Add((path, text, false)));
    }

    /// <summary>
    /// <b>A run applies its decision before the platform starts</b>: the profile's environment is set and the
    /// profile context named by the time <c>run</c> is called, which gets the resolved arguments, and the
    /// profile's lines are printed. A profile's switches reaching the lane depend on exactly this — a host that
    /// resolved them and set nothing would run a live profile with no URL, every test skipped.
    /// </summary>
    /// <remarks>
    /// Mutation-checked when written: the loop that sets the environment deleted, and the profile context left
    /// unset, each go red here (and stayed green in every other test, which drive <see cref="TestHost.Resolve"/>).
    /// </remarks>
    [Fact]
    public async Task RunAsync_AppliesTheProfileBeforeTheRun()
    {
        var world = new RecordingIo();
        string? urlAtStart = null;
        string? contextAtStart = null;
        string[]? ran = null;

        var exit = await TestHost.RunAsync(["--test-profile", "nr-live-smoke", "--list-tests"], TestProfiles.Tests, (args, _) =>
        {
            urlAtStart = world.Environment.GetValueOrDefault("NR_ENGINE_URL");
            contextAtStart = world.ProfileContext;
            ran = args;
            return Task.FromResult(0);
        }, world.Io);

        Assert.Equal(0, exit);
        Assert.Equal("https://www.newrecruit.eu", urlAtStart);
        Assert.Equal("nr-live-smoke", contextAtStart);
        Assert.NotNull(ran);
        Assert.Equal(["--list-tests", "--filter", "Engine=LiveNrRoster&Category=Smoke", "--zero-tests-policy", "strict"], ran);
        Assert.Contains($"{TestHost.Prefix} nr-live-smoke ({TestProfiles.Tests}): --filter Engine=LiveNrRoster&Category=Smoke", world.Out.ToString(), StringComparison.Ordinal);
        Assert.Equal("", world.Error.ToString());

        var unprofiled = new RecordingIo();
        Assert.Equal(0, await TestHost.RunAsync(["--list-tests"], TestProfiles.Tests, static (_, _) => Task.FromResult(0), unprofiled.Io));
        Assert.Equal(TestProfileContext.Unprofiled, unprofiled.ProfileContext);
        Assert.Empty(unprofiled.Environment);
    }

    /// <summary>
    /// <b>What the run returns is the app's exit code</b>, and a run the host does not hand to the platform never
    /// reaches it: a refusal prints its reason to stderr and returns 5 with nothing applied; the registry is
    /// printed with 0; <c>-automated</c> goes to xunit's console runner with the original arguments; an IDE's
    /// run names no profile context.
    /// </summary>
    [Fact]
    public async Task RunAsync_ActsOnEachDecision()
    {
        static Task<int> Unreachable(string[] _, LaneTally __) => throw new InvalidOperationException("the platform was started");

        var refused = new RecordingIo();
        refused.Environment["TESTINGPLATFORM_EXITCODE_IGNORE"] = "8";
        Assert.Equal(TestHost.InvalidCommandLine, await TestHost.RunAsync(["--test-profile", "nr-live-smoke"], TestProfiles.Tests, Unreachable, refused.Io));
        Assert.StartsWith($"{TestHost.Prefix} TESTINGPLATFORM_EXITCODE_IGNORE=8 is set", refused.Error.ToString(), StringComparison.Ordinal);
        Assert.Null(refused.ProfileContext);
        Assert.Equal(["TESTINGPLATFORM_EXITCODE_IGNORE"], refused.Environment.Keys);

        var listed = new RecordingIo();
        Assert.Equal(0, await TestHost.RunAsync(["--list-test-profiles"], TestProfiles.Tests, Unreachable, listed.Io));
        Assert.Equal(TestHost.RenderProfilesText(), listed.Out.ToString());

        var automated = new RecordingIo();
        Assert.Equal(0, await TestHost.RunAsync(["-automated", "x"], TestProfiles.Tests, Unreachable, automated.Io));
        Assert.NotNull(automated.ConsoleRunnerArgs);
        Assert.Equal(["-automated", "x"], automated.ConsoleRunnerArgs);

        var ide = new RecordingIo();
        Assert.Equal(2, await TestHost.RunAsync(["--server"], TestProfiles.Tests, static (_, _) => Task.FromResult(2), ide.Io));
        Assert.Null(ide.ProfileContext);
    }
}
