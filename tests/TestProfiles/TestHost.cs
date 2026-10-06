using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Testing.Platform.Builder;
using Xunit.MicrosoftTestingPlatform;
using Xunit.Runner.InProc.SystemConsole;

namespace BattleScribeSpec.Tests.Profiles;

/// <summary>How the test app was started, which decides how much <see cref="TestHost"/> may do to its arguments.</summary>
internal enum TestSession
{
    /// <summary>
    /// An IDE's test explorer (Visual Studio, Rider, VS Code): <c>--server</c> with no value, or
    /// <c>--server jsonrpc</c> — xunit's own rule for telling an IDE apart. The host leaves the run alone.
    /// </summary>
    Ide,

    /// <summary>
    /// <c>dotnet test</c>: <c>--server</c> with any other value (the SDK passes <c>--server dotnettestcli</c>).
    /// The arguments MSBuild carries (<c>tests/Directory.Build.props</c>) must have arrived.
    /// </summary>
    DotnetTest,

    /// <summary>The executable, <c>dotnet run</c> or <c>dotnet exec</c>: no <c>--server</c>.</summary>
    Direct,
}

/// <summary>What <see cref="TestHost"/> decided to do with one start of the test app.</summary>
internal enum HostAction
{
    /// <summary>Run the tests with <see cref="HostOutcome.Arguments"/>.</summary>
    Run,

    /// <summary>Hand the original arguments to xunit's in-process console runner (<c>-automated</c>, <c>@@</c>).</summary>
    ConsoleRunner,

    /// <summary>Print the profile registry (<c>--list-test-profiles</c>) and exit 0.</summary>
    ListProfiles,

    /// <summary>Refuse the command line: print <see cref="HostOutcome.Error"/> and exit <see cref="TestHost.InvalidCommandLine"/>.</summary>
    Refuse,
}

/// <summary>The host's decision: what to run, with which arguments and environment, and what to say first.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Session">How the app was started.</param>
/// <param name="Arguments">The arguments the test platform gets (for <see cref="HostAction.Run"/>).</param>
/// <param name="Environment">Variables to set in this process before the run: the profile's values that apply.</param>
/// <param name="Messages">Lines to print before the run: the profile, its filter, and every switch it applied or left to the caller.</param>
/// <param name="Error">Why the command line was refused (for <see cref="HostAction.Refuse"/>).</param>
/// <param name="Profile">The profile the run resolved, if any.</param>
/// <param name="Narrowed">Whether a caller's <c>--filter</c> narrowed the profile.</param>
/// <param name="Json">For <see cref="HostAction.ListProfiles"/>: whether <c>--json</c> was given.</param>
/// <param name="RunsTests">
/// Whether the run executes tests, rather than listing them or printing help or information
/// (<c>--list-tests</c>, <c>--xunit-list</c>, <c>--help</c>, <c>--info</c>). Only such a run is held to
/// the engine-composition check.
/// </param>
internal sealed record HostOutcome(
    HostAction Action,
    TestSession Session,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> Messages,
    string? Error = null,
    TestProfile? Profile = null,
    bool Narrowed = false,
    bool Json = false,
    bool RunsTests = false);

/// <summary>What <see cref="TestHost"/> touches outside itself when it acts on a <see cref="HostOutcome"/>.</summary>
/// <param name="GetEnvironment">Reads a variable of this process.</param>
/// <param name="SetEnvironment">Sets a variable of this process.</param>
/// <param name="SetProfileContext">Names the resolved profile for the run's reporting (<see cref="TestProfileContext.Current"/>).</param>
/// <param name="Out">Where the profile's lines, the registry and a passing composition verdict go.</param>
/// <param name="Error">Where a refusal and a failing composition check go (<c>dotnet test</c> replays it).</param>
/// <param name="RunConsoleRunner">xunit's in-process console runner, for <c>-automated</c> and <c>@@</c>.</param>
/// <param name="TelemetryArtifactBase">
/// Where the run's telemetry artifact set went, once the run is over (<see cref="TestProfileContext.TelemetryArtifactBase"/>).
/// </param>
/// <param name="AppendText">Appends text to a file (<c>$GITHUB_STEP_SUMMARY</c>).</param>
/// <param name="WriteText">Writes a file (the composition record).</param>
/// <param name="LauncherId">The id of the process that started this one, while it runs (<see cref="Launcher.Id"/>).</param>
internal sealed record HostIo(
    Func<string, string?> GetEnvironment,
    Action<string, string?> SetEnvironment,
    Action<string> SetProfileContext,
    TextWriter Out,
    TextWriter Error,
    Func<string[], Task<int>> RunConsoleRunner,
    Func<string?> TelemetryArtifactBase,
    Action<string, string> AppendText,
    Action<string, string> WriteText,
    Func<int?> LauncherId);

/// <summary>
/// <b>The test app's own entry point: it resolves a test profile, refuses a command line that would
/// run less than it says, and fails a run whose profile's lanes did not all execute.</b> Both test
/// projects' <c>Main</c> (<c>Program.cs</c>) is one call to
/// <see cref="RunAsync(string[], string, Action{ITestApplicationBuilder, string[]})"/>, so every way of
/// starting the suites — <c>dotnet test</c>, <c>dotnet run</c>, the executable, an IDE — goes through the
/// same code, and a profile cannot be half-applied by one of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the repo owns <c>Main</c>.</b> Microsoft.Testing.Platform has no runsettings file to carry a
/// lane, so a profile has to become three things: a <c>--filter</c>, environment variables set in this
/// process before the first test, and a zero-tests policy. A response file cannot set a variable, and a
/// <c>testconfig.json</c> per profile is replaced, not narrowed, by a command-line <c>--filter</c>. A host
/// in front of xunit's runner does all three from <see cref="TestProfiles"/>, and checks what it is
/// handed. It replaces xunit's generated entry point (<c>XunitAutoGeneratedEntryPoint</c>, turned off in
/// <c>tests/Directory.Build.props</c>) and keeps that entry point's one rule: <c>-automated</c> or
/// <c>@@</c> goes to xunit's in-process console runner, and everything else to
/// <c>TestPlatformTestFramework.RunAsync</c> with the project's self-registered extensions first. Written
/// against xunit.v3 4.0.1's generator; on an xunit bump, build a scratch project with the generated entry
/// point and compare its <c>XunitAutoGeneratedEntryPoint.cs</c> with
/// <see cref="RunAsync(string[], string, Action{ITestApplicationBuilder, string[]})"/>.
/// </para>
/// <para><b>What it does, in order</b> (<see cref="Resolve"/> holds every rule, without side effects):</para>
/// <list type="number">
/// <item><description>Classifies the session (<see cref="TestSession"/>). An IDE's run passes through untouched.</description></item>
/// <item><description>
/// <c>--list-test-profiles [--json]</c> prints the registry and exits 0 — asked of the app (<c>dotnet run</c>,
/// the executable); <c>dotnet test</c> counts a run that reports no test as failed, so there it is refused.
/// </description></item>
/// <item><description>
/// Refuses, on every run: a VSTest option (<c>--settings</c> — which <c>-p:RunSettingsFilePath</c>
/// becomes — <c>--logger</c>, <c>--collect</c>, <c>--blame*</c>); <c>TESTINGPLATFORM_EXITCODE_IGNORE</c> in
/// the environment; <c>--test-profile</c>, <c>--filter</c> or <c>--zero-tests-policy</c> given twice; and
/// a <c>dotnet test</c> run that arrived without <c>--zero-tests-policy</c>, which means the arguments
/// MSBuild carries — <c>-p:TestProfile</c> among them — were dropped on the way.
/// </description></item>
/// <item><description>
/// <c>--test-profile &lt;name&gt;</c> (<c>-p:TestProfile=&lt;name&gt;</c> arrives as that): an unknown
/// name, or a profile that does not cover this assembly, is refused. With a profile, the options that
/// would override its verdict or its selection are refused (<see cref="RefusedWithAProfile"/>). Every
/// environment switch that concerns the profile — those it sets, those of the engines it claims — takes
/// the profile's value only where the caller has none, and each is printed with whose value it is; no
/// switch changes which tests run (<see cref="Knobs"/>). The filter is the profile's ANDed with the
/// caller's <c>--filter</c>: <c>(P)&amp;(U)</c>, and a caller's <c>--filter</c> with no value or several is
/// refused, as the platform refuses it without a profile.
/// </description></item>
/// <item><description>A direct run gets <c>--zero-tests-policy strict</c> unless it names a policy; a <c>dotnet test</c> run has it from MSBuild.</description></item>
/// <item><description>
/// In GitHub Actions: the GitHub reporter with a failures-only step summary
/// (<c>--report-github --report-github-summary-include-passed false</c>); a direct run also gets
/// <c>--output Detailed</c> and the ten slowest tests in its summary (<c>--show-slowest-tests 10</c>), and
/// a profile that claims an aggregate lane <c>--show-live-output on</c>, so a 27-minute lane
/// streams its <c>[i/N]</c> progress instead of being 27 silent minutes.
/// </description></item>
/// <item><description>
/// <c>--exit-on-process-exit</c> with the launcher's id (<see cref="Launcher"/>) unless the caller passed one, so
/// the run ends, exit 11, when the <c>dotnet test</c>, <c>dotnet run</c> or shell that started it dies.
/// </description></item>
/// <item><description>
/// Sets <see cref="TestProfileContext.Current"/>, applies the environment, and runs, with a
/// <see cref="LaneTally"/> registered next to the project's own extensions to count each lane's results
/// as they arrive.
/// </description></item>
/// <item><description>
/// After a profiled run that executed tests: the engine-composition check (<see cref="LaneComposition"/>) —
/// every lane the profile claims must have executed at least one of its own tests, or the run exits 8,
/// naming each empty lane and its fix. Then the verdict and the per-lane table go to
/// <c>$GITHUB_STEP_SUMMARY</c>, and the record to <c>&lt;telemetry artifact&gt;.composition.json</c>. This
/// takes milliseconds, which it must: xunit's entry point starts a watchdog when the platform returns.
/// </description></item>
/// </list>
/// <para>
/// <see cref="Resolve"/> decides; <see cref="RunAsync(string[], string, Func{string[], LaneTally, Task{int}}, HostIo)"/>
/// acts on the decision through <see cref="HostIo"/>, so both halves are tested without touching this
/// process's environment.
/// </para>
/// <para>
/// <b>Exit codes.</b> Every refusal is 5, the platform's own code for a bad command line, so a misuse and
/// an option the platform does not know read the same from outside; the message says which. 8 is the
/// platform's "nothing executed" (with the strict policy, a run whose every selected test skipped counts)
/// and this host's "a lane the profile claims executed nothing", each with the lane's hint. The rest are
/// the platform's: 2 a test failed, 3 the session was aborted, 7 the test host process died, 11 its launcher did.
/// </para>
/// <para>
/// <b>Why refuse rather than repair.</b> Each refusal stands for a command line that would otherwise
/// produce a green run that checked less than its name says: a profile whose filter was dropped, an exit
/// code told to lie. The message names the fix. <c>TestHostTests</c> holds every rule, each with the input
/// that trips it.
/// </para>
/// </remarks>
internal static class TestHost
{
    /// <summary>The platform's exit code for an invalid command line, used for every refusal.</summary>
    public const int InvalidCommandLine = 5;

    /// <summary>What every refusal and every applied switch is prefixed with.</summary>
    public const string Prefix = "[test-profile]";

    /// <summary>
    /// The options that cannot be combined with a profile, each with the reason. <c>--filter-*</c> stands
    /// for every option with that prefix (xunit's simple filters, <c>--filter-query</c>, <c>--filter-uid</c>);
    /// <c>@&lt;file&gt;</c> for a response file.
    /// </summary>
    internal static readonly IReadOnlyList<(string Option, string Why)> RefusedWithAProfile =
    [
        ("--ignore-exit-code", "it turns the exit codes it names into a pass — for 8, a run that executed nothing"),
        ("--config-file", "a testconfig.json carries platform and xunit options of its own, a second record of the lane next to the profile"),
        ("--xunit-config-filename", "it replaces xunit.runner.json, the one record of the suites' parallelism and test naming"),
        ("@<file>", "a response file can carry any option, this host's refusals included, where nobody reading the command line sees it"),
        ("--filter-*", "the profile's selection is a --filter expression, and the platform cannot combine one with xunit's simple "
            + "filters, --filter-query or --filter-uid. Narrow the profile with --filter \"<expression>\", which is ANDed onto its own"),
    ];

    private static readonly string[] VsTestOptions = ["settings", "logger", "collect"];

    /// <summary>Options with which the platform lists, or prints help or information, instead of executing tests.</summary>
    private static readonly string[] NonExecutingOptions = ["list-tests", "xunit-list", "help", "h", "?", "info"];

    /// <summary>The entry point both test projects' <c>Main</c> calls.</summary>
    /// <param name="args">The command line.</param>
    /// <param name="assembly">The test assembly this app is (<see cref="TestProfiles.Tests"/> or <see cref="TestProfiles.Cli"/>).</param>
    /// <param name="selfRegisteredExtensions">
    /// The project's <c>SelfRegisteredExtensions.AddSelfRegisteredExtensions</c>, generated by
    /// Microsoft.Testing.Platform.MSBuild: registered first, exactly as xunit's generated entry point
    /// registers it, and then this host's <see cref="LaneTally"/>.
    /// </param>
    public static Task<int> RunAsync(string[] args, string assembly, Action<ITestApplicationBuilder, string[]> selfRegisteredExtensions)
    {
        ArgumentNullException.ThrowIfNull(selfRegisteredExtensions);
        return RunAsync(args, assembly,
            (platformArgs, tally) => TestPlatformTestFramework.RunAsync(platformArgs, (builder, builderArgs) =>
            {
                selfRegisteredExtensions(builder, builderArgs);
                tally.Register(builder);
            }),
            new HostIo(
                Environment.GetEnvironmentVariable,
                Environment.SetEnvironmentVariable,
                static profile => TestProfileContext.Current = profile,
                Console.Out,
                Console.Error,
                static a => ConsoleRunner.Run(a),
                static () => TestProfileContext.TelemetryArtifactBase,
                File.AppendAllText,
                File.WriteAllText,
                Launcher.Id));
    }

    /// <summary>
    /// <see cref="RunAsync(string[], string, Action{ITestApplicationBuilder, string[]})"/> with the platform
    /// and what the host touches outside itself passed in, so <c>TestHostTests</c> can see it applied: the
    /// profile's environment set before the run starts, the profile context named, the arguments handed
    /// over, a refusal printed and 5, and the composition check acting on what <paramref name="run"/> fed
    /// the <see cref="LaneTally"/> it is handed.
    /// </summary>
    internal static async Task<int> RunAsync(string[] args, string assembly, Func<string[], LaneTally, Task<int>> run, HostIo io)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(io);
        var outcome = Resolve(args, assembly, io.GetEnvironment, io.LauncherId());
        switch (outcome.Action)
        {
            case HostAction.ConsoleRunner:
                return await io.RunConsoleRunner(args).ConfigureAwait(false);
            case HostAction.ListProfiles:
                io.Out.Write(outcome.Json ? RenderProfilesJson() : RenderProfilesText());
                return 0;
            case HostAction.Refuse:
                io.Error.WriteLine(outcome.Error);
                return InvalidCommandLine;
        }

        foreach (var line in outcome.Messages)
        {
            io.Out.WriteLine(line);
        }

        foreach (var (name, value) in outcome.Environment)
        {
            io.SetEnvironment(name, value);
        }

        if (outcome.Session != TestSession.Ide)
        {
            io.SetProfileContext(outcome.Profile?.Name ?? TestProfileContext.Unprofiled);
        }

        var tally = new LaneTally();
        var result = await run([.. outcome.Arguments], tally).ConfigureAwait(false);
        return Conclude(outcome, assembly, tally, result, io);
    }

    /// <summary>
    /// The engine-composition check of a profiled run that executed tests, and its reporting; any other run's
    /// exit code passes through. Milliseconds of work, none of it able to throw: a summary or a record that
    /// cannot be written is a warning, never a changed verdict.
    /// </summary>
    private static int Conclude(HostOutcome outcome, string assembly, LaneTally tally, int result, HostIo io)
    {
        if (outcome.Session == TestSession.Ide || outcome.Profile is not { } profile || !outcome.RunsTests)
        {
            return result;
        }

        var report = LaneComposition.Check(profile, assembly, tally, outcome.Narrowed, result);
        var (lines, errors) = LaneComposition.ConsoleLines(report);
        foreach (var line in lines)
        {
            io.Out.WriteLine(line);
        }

        foreach (var line in errors)
        {
            io.Error.WriteLine(line);
        }

        TryWrite(io, "$GITHUB_STEP_SUMMARY", io.GetEnvironment("GITHUB_STEP_SUMMARY"), LaneComposition.StepSummary(report), append: true);
        TryWrite(io, "the composition record", io.TelemetryArtifactBase() is { } artifact ? artifact + LaneComposition.JsonSuffix : null,
            LaneComposition.Json(report), append: false);
        return report.ExitCode;
    }

    private static void TryWrite(HostIo io, string what, string? path, string text, bool append)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (append)
            {
                io.AppendText(path, text);
            }
            else
            {
                io.WriteText(path, text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            io.Error.WriteLine($"{LaneComposition.Prefix} could not write {what} ({path}): {ex.Message}");
        }
    }

    /// <summary>
    /// Every rule the host applies, as a decision without side effects: what to run, with which
    /// arguments and environment, or why not.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <param name="assembly">The test assembly this app is.</param>
    /// <param name="environment">Reads an environment variable (<see cref="Environment.GetEnvironmentVariable(string)"/> in a real run).</param>
    /// <param name="launcher">The id of the process that started this one (<see cref="Launcher.Id"/> in a real run), if known.</param>
    internal static HostOutcome Resolve(IReadOnlyList<string> args, string assembly, Func<string, string?> environment, int? launcher = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        var items = Parse(args);
        var session = Classify(items);
        var none = new Dictionary<string, string>(StringComparer.Ordinal);

        HostOutcome Refuse(string error) => new(HostAction.Refuse, session, args, none, [], $"{Prefix} {error}");

        // 1. xunit's in-process console runner, routed exactly as the generated entry point routes it.
        if (args.Any(static a => a is "-automated" or "@@"))
        {
            return Named(items, "test-profile").Count > 0
                ? Refuse("--test-profile cannot be combined with -automated or @@: those start xunit's in-process console runner, "
                    + "which reads its own arguments and knows no profiles. Run the profile through the test platform instead.")
                : new HostOutcome(HostAction.ConsoleRunner, session, args, none, []);
        }

        // 2. The registry itself — asked of the app, not through `dotnet test`, which counts a run that
        //    reports no test as a failed one ("Zero tests ran", exit 1) after printing nothing it was given.
        if (Named(items, "list-test-profiles").Count > 0)
        {
            return session == TestSession.DotnetTest
                ? Refuse("--list-test-profiles prints the profile registry and runs no test, and `dotnet test` reports a run that "
                    + "ran none as a failure. Ask the test app directly: `dotnet run --project "
                    + $"{TestProfiles.Projects.GetValueOrDefault(assembly, "<csproj>")} -- --list-test-profiles`.")
                : new HostOutcome(HostAction.ListProfiles, session, args, none, [], Json: Named(items, "json").Count > 0);
        }

        // 3. An IDE drives its own session, and asks for nothing here.
        if (session == TestSession.Ide)
        {
            return new HostOutcome(HostAction.Run, session, args, none, []);
        }

        // 4. Refused on every run outside an IDE.
        if (items.FirstOrDefault(static i => i.Name is { } n && (VsTestOptions.Contains(n) || n.StartsWith("blame", StringComparison.Ordinal))) is { } vstest)
        {
            return Refuse(VsTestRefusal(vstest));
        }

        if (environment("TESTINGPLATFORM_EXITCODE_IGNORE") is { Length: > 0 } ignored)
        {
            return Refuse($"TESTINGPLATFORM_EXITCODE_IGNORE={ignored} is set in the environment. It makes the platform report success "
                + "for the exit codes it names — for 8, a run that executed nothing — so the run would pass whatever it found. "
                + "Unset it.");
        }

        foreach (var option in new[] { "test-profile", "filter", "zero-tests-policy" })
        {
            if (Named(items, option) is { Count: > 1 } repeated)
            {
                return Refuse(DuplicateRefusal(option, repeated));
            }
        }

        if (session == TestSession.DotnetTest && Named(items, "zero-tests-policy").Count == 0)
        {
            return Refuse("this `dotnet test` run arrived without --zero-tests-policy, which tests/Directory.Build.props passes on every "
                + "`dotnet test` run through TestingPlatformCommandLineArguments. So the arguments MSBuild carries did not arrive — "
                + "and -p:TestProfile travels the same way, so it would have been dropped with them. `dotnet test --test-modules` "
                + "(or a .dll named in place of a project) does that, because it evaluates no project; so would a platform package "
                + "whose _MTPAddArguments target stopped copying TestingPlatformCommandLineArguments into RunArguments, the "
                + "property `dotnet test` reads the app's arguments from. Name the project instead: `dotnet test --project <csproj>`.");
        }

        // 5. The profile.
        var messages = new List<string>();
        var setEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        TestProfile? profile = null;
        var narrowed = false;
        if (Named(items, "test-profile").SingleOrDefault() is { } profileItem)
        {
            if (profileItem.Values is not [var name])
            {
                return Refuse("--test-profile takes one profile name; `--list-test-profiles` lists them.");
            }

            profile = TestProfiles.Find(name);
            if (profile is null)
            {
                return Refuse($"unknown test profile '{name}'. The profiles are: {string.Join(", ", TestProfiles.All.Select(static p => p.Name))}. "
                    + "`--list-test-profiles` says what each one runs.");
            }

            if (!profile.Assemblies.Contains(assembly, StringComparer.Ordinal))
            {
                var hint = profile.Assemblies is [var only] && TestProfiles.Projects.TryGetValue(only, out var path)
                    ? $"`dotnet test --project {path} -p:TestProfile={profile.Name}`"
                    : $"`dotnet test --project <csproj> -p:TestProfile={profile.Name}`";
                return Refuse($"profile {profile.Name} covers {string.Join(" and ", profile.Assemblies)}, and this is {assembly}. "
                    + "`dotnet test -p:TestProfile=…` over the whole solution starts every test project, and this one is not in the "
                    + $"lane, so name the project: {hint}.");
            }

            if (RefusedOption(items) is { } refused)
            {
                return Refuse($"{refused.Spelling} cannot be combined with a test profile: {refused.Why}. Drop it, or run without "
                    + "--test-profile.");
            }

            if (Named(items, "zero-tests-policy").SingleOrDefault() is { Values: [var policy] }
                && !policy.Equals("strict", StringComparison.OrdinalIgnoreCase))
            {
                return Refuse($"--zero-tests-policy {policy} cannot be combined with a test profile: a profiled run is a lane, and a lane "
                    + "that executed nothing — or only skipped — fails. The policy is strict.");
            }

            var applied = ApplyKnobs(profile, environment, setEnvironment);

            // The platform refuses a --filter with no value or several; with a profile the filter is the
            // host's to build, so the host refuses it the same way rather than quietly keeping one value.
            var callerFilter = Named(items, "filter").SingleOrDefault();
            if (callerFilter is { Values: not [{ Length: > 0 }] })
            {
                return Refuse($"`{string.Join(" ", callerFilter.Tokens)}`: --filter takes one expression, "
                    + $"{(callerFilter.Values.Count > 1 ? $"and {callerFilter.Values.Count} follow it" : "and none follows it")}. "
                    + "Quote an expression with spaces, and join several into one: (A)&(B).");
            }

            var caller = callerFilter?.Values[0];
            narrowed = caller is not null;
            items.Remove(profileItem);
            if (callerFilter is not null)
            {
                items.Remove(callerFilter);
            }

            var filter = (profile.Selection.Filter, caller) switch
            {
                ({ } p, { } u) => $"({p})&({u})",
                ({ } p, null) => p,
                (null, { } u) => u,
                _ => null,
            };
            if (filter is not null)
            {
                items.Add(new Item("filter", ["--filter", filter], [filter]));
            }

            messages.Add($"{Prefix} {profile.Name} ({assembly}): {(filter is null ? "every test" : $"--filter {filter}")}"
                + (narrowed ? $" — narrowed by the caller's --filter {caller}" : ""));
            messages.AddRange(applied.Select(a => $"{Prefix} {profile.Name}: {a}"));
        }

        // 6. Strict unless a direct, unprofiled run names another policy: `dotnet test` and `dotnet run` have it from
        //    MSBuild (a second one was refused above, as was a profiled run naming another); a direct run naming none
        //    gets it here.
        if (session == TestSession.Direct && Named(items, "zero-tests-policy").Count == 0)
        {
            items.Add(new Item("zero-tests-policy", ["--zero-tests-policy", "strict"], ["strict"]));
        }

        // 7. GitHub Actions.
        if (string.Equals(environment("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            AddIfAbsent(items, "report-github");
            AddIfAbsent(items, "report-github-summary-include-passed", "false");
            if (session == TestSession.Direct)
            {
                AddIfAbsent(items, "output", "Detailed");
                AddIfAbsent(items, "show-slowest-tests", "10");
            }

            if (profile is not null && profile.Selection.Claims.Any(static c => EngineLanes.Find(c)?.Aggregate == true))
            {
                AddIfAbsent(items, "show-live-output", "on");
            }
        }

        // 8. The run ends when its launcher dies. Otherwise it runs on with its browser pool or the desktop app: a shell
        //    that started the executable never takes it down, and `dotnet test` starts it as a plain child that notices
        //    only when it next reports over the pipe, which a long aggregate test does not do until it finishes.
        if (launcher is { } id)
        {
            AddIfAbsent(items, "exit-on-process-exit", id.ToString(CultureInfo.InvariantCulture));
        }

        return new HostOutcome(HostAction.Run, session, [.. items.SelectMany(static i => i.Tokens)], setEnvironment, messages,
            Profile: profile, Narrowed: narrowed, RunsTests: !items.Any(static i => i.Name is { } n && NonExecutingOptions.Contains(n)));
    }

    /// <summary>
    /// Applies every default switch that concerns <paramref name="profile"/> — those it sets, those of the
    /// engines it claims, and those of no engine in particular — into <paramref name="setEnvironment"/>,
    /// where the caller has no value of its own. Returns one line per switch applied or left to the caller.
    /// </summary>
    private static List<string> ApplyKnobs(TestProfile profile, Func<string, string?> environment, Dictionary<string, string> setEnvironment)
    {
        var applied = new List<string>();
        var claimed = profile.Selection.Claims;
        foreach (var knob in Knobs.All.Where(static k => k.Kind == KnobKind.Default))
        {
            var inProfile = profile.Env.TryGetValue(knob.Name, out var wanted);
            if (!inProfile && knob.Engines.Count > 0 && !knob.Engines.Intersect(claimed, StringComparer.Ordinal).Any())
            {
                continue;
            }

            if (environment(knob.Name) is { Length: > 0 } caller)
            {
                applied.Add($"{knob.Name}={caller} (caller)");
            }
            else if (wanted is not null)
            {
                setEnvironment[knob.Name] = wanted;
                applied.Add($"{knob.Name}={wanted} (profile)");
            }
        }

        return applied;
    }

    private static string VsTestRefusal(Item option) => option.Name switch
    {
        "settings" => $"{option.Tokens[0]} is a VSTest option, and these suites run on Microsoft.Testing.Platform, which reads no "
            + "runsettings (-p:RunSettingsFilePath arrives as --settings). A lane is a test profile: -p:TestProfile=<name> under "
            + "`dotnet test`, --test-profile <name> on the test app; `--list-test-profiles` lists them.",
        "logger" => $"{option.Tokens[0]} is a VSTest option. Results print to the console; in GitHub Actions the host turns on the "
            + "GitHub reporter (annotations and a failures-only step summary); for a TRX file, pass --report-xunit-trx.",
        "collect" => $"{option.Tokens[0]} is a VSTest option; this repository collects no coverage.",
        _ => $"{option.Tokens[0]} is a VSTest option, and these suites run on Microsoft.Testing.Platform. CI bounds every test step "
            + "with a timeout instead; for a local hang, --timeout <duration> stops the run.",
    };

    private static string DuplicateRefusal(string option, List<Item> repeated)
    {
        var spelled = string.Join(", ", repeated.Select(static i => string.Join(" ", i.Tokens)));
        return option switch
        {
            "test-profile" => $"--test-profile is given {repeated.Count} times ({spelled}); name one. -p:TestProfile=<name> arrives as "
                + "--test-profile <name>, so the two on one command line are two profiles.",
            "filter" => $"--filter is given {repeated.Count} times ({spelled}); the platform takes one. Write one expression: (A)&(B).",
            _ => $"--zero-tests-policy is given {repeated.Count} times ({spelled}). `dotnet test` and `dotnet run` already pass "
                + "--zero-tests-policy strict (tests/Directory.Build.props), so neither can choose another; start the test executable "
                + "itself, without a profile, to change it.",
        };
    }

    /// <summary>The first item on the command line a profile refuses, with how it was spelled and why.</summary>
    private static (string Spelling, string Why)? RefusedOption(List<Item> items) =>
        RefusedOptions(items).Select(static r => ((string, string)?)r).FirstOrDefault();

    /// <summary>
    /// How each option a profile refuses (<see cref="RefusedWithAProfile"/>) is spelled on
    /// <paramref name="args"/>, read as the host reads a command line — for the lints that hold CI steps and
    /// documented commands to the host's rules without a second copy of them.
    /// </summary>
    internal static IReadOnlyList<string> OptionsRefusedWithAProfile(IReadOnlyList<string> args) =>
        [.. RefusedOptions(Parse(args)).Select(static r => r.Spelling)];

    private static IEnumerable<(string Spelling, string Why)> RefusedOptions(List<Item> items)
    {
        foreach (var item in items)
        {
            var option = item.Name switch
            {
                null when item.Tokens[0].StartsWith('@') => "@<file>",
                null => null,
                var n when n.StartsWith("filter-", StringComparison.Ordinal) => "--filter-*",
                var n => RefusedWithAProfile.Any(r => r.Option == $"--{n}") ? $"--{n}" : null,
            };
            if (option is not null)
            {
                yield return (item.Tokens[0], RefusedWithAProfile.Single(r => r.Option == option).Why);
            }
        }
    }

    private static void AddIfAbsent(List<Item> items, string option, params string[] values)
    {
        if (Named(items, option).Count == 0)
        {
            items.Add(new Item(option, [$"--{option}", .. values], values));
        }
    }

    // ── the command line ──

    /// <summary>
    /// One element of a command line: an option (<paramref name="Name"/> lower-case, without its dashes)
    /// with the values that follow it or are attached to it, or a lone token (a response file, a stray value).
    /// </summary>
    private sealed record Item(string? Name, IReadOnlyList<string> Tokens, IReadOnlyList<string> Values);

    /// <summary>
    /// The command line as the platform reads it: an option is <c>-x</c> or <c>--x</c> in any case, with its
    /// value attached by <c>=</c> or <c>:</c>, or in the tokens that follow it up to the next option.
    /// </summary>
    private static List<Item> Parse(IReadOnlyList<string> args)
    {
        var items = new List<Item>();
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (OptionName(token) is not { } name)
            {
                items.Add(new Item(null, [token], []));
                continue;
            }

            var tokens = new List<string> { token };
            var values = new List<string>();
            if (AttachedValue(token) is { } attached)
            {
                values.Add(attached);
            }
            else
            {
                while (i + 1 < args.Count && OptionName(args[i + 1]) is null && !args[i + 1].StartsWith('@'))
                {
                    tokens.Add(args[++i]);
                    values.Add(args[i]);
                }
            }

            items.Add(new Item(name, tokens, values));
        }

        return items;
    }

    private static string? OptionName(string token)
    {
        if (token.Length < 2 || token[0] != '-' || token == "--" || char.IsAsciiDigit(token[1]))
        {
            return null;
        }

        var body = token.TrimStart('-');
        var end = body.IndexOfAny(['=', ':']);
        var name = (end < 0 ? body : body[..end]).ToLowerInvariant();
        return name.Length == 0 ? null : name;
    }

    private static string? AttachedValue(string token)
    {
        var body = token.TrimStart('-');
        var end = body.IndexOfAny(['=', ':']);
        return end < 0 ? null : body[(end + 1)..];
    }

    private static List<Item> Named(List<Item> items, string name) => [.. items.Where(i => i.Name == name)];

    private static TestSession Classify(List<Item> items) =>
        Named(items, "server").FirstOrDefault() switch
        {
            null => TestSession.Direct,
            { Values: [] } => TestSession.Ide,
            { Values: [var value, ..] } when value.Equals("jsonrpc", StringComparison.OrdinalIgnoreCase) => TestSession.Ide,
            _ => TestSession.DotnetTest,
        };

    // ── --list-test-profiles ──

    /// <summary>The registry as text: one block per profile.</summary>
    internal static string RenderProfilesText()
    {
        var sb = new StringBuilder();
        foreach (var p in TestProfiles.All)
        {
            sb.Append(p.Name).Append(" — ").Append(string.Join(", ", p.Assemblies)).Append('\n');
            sb.Append("  filter: ").Append(p.Selection.Filter ?? "(every test)").Append('\n');
            if (p.Env.Count > 0)
            {
                sb.Append("  sets:   ").Append(string.Join(", ", p.Env.Select(static kv => $"{kv.Key}={kv.Value}"))).Append('\n');
            }

            foreach (var (engine, why) in p.MaySkip)
            {
                sb.Append("  may skip ").Append(engine).Append(": ").Append(why).Append('\n');
            }

            sb.Append("  ").Append(p.Purpose).Append("\n\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The registry as JSON: each profile's name, purpose, filter, claimed engines, assemblies, environment,
    /// and the engines it lets skip.
    /// </summary>
    internal static string RenderProfilesJson()
    {
        var profiles = TestProfiles.All.Select(static p => new
        {
            name = p.Name,
            purpose = p.Purpose,
            filter = p.Selection.Filter,
            claims = p.Selection.Claims,
            assemblies = p.Assemblies,
            environment = p.Env,
            maySkip = p.MaySkip.Select(static m => new { engine = m.Engine, why = m.Why }),
        });

        return JsonSerializer.Serialize(profiles, JsonOptions) + "\n";
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
