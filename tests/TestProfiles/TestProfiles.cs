namespace BattleScribeSpec.Tests.Profiles;

/// <summary>One named lane: what it runs, in which test assemblies, under which environment.</summary>
/// <param name="Name">The name <c>-p:TestProfile=</c> takes; lower-case kebab-case.</param>
/// <param name="Purpose">What the lane is for and what a reader must know before relying on it.</param>
/// <param name="Selection">Which tests it runs.</param>
/// <param name="Env">
/// The environment switches the lane needs — a live lane's endpoint URL — each one a
/// <see cref="KnobKind.Default"/> switch classified in <see cref="Knobs"/>: a value the caller exported wins.
/// </param>
/// <param name="Assemblies">The test assemblies the lane covers.</param>
/// <param name="MaySkip">
/// Engines this lane claims that may legitimately skip whole in it, each with the reason. Only an
/// engine that needs the desktop app may be listed.
/// </param>
internal sealed record TestProfile(
    string Name,
    string Purpose,
    Selection Selection,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyList<string> Assemblies,
    IReadOnlyList<(string Engine, string Why)> MaySkip);

/// <summary>
/// <b>Every test profile — the one record of every lane.</b> <c>-p:TestProfile=&lt;name&gt;</c> (under
/// <c>dotnet test</c>) or <c>--test-profile &lt;name&gt;</c> (on the test app) selects one; every CI test
/// step runs one, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>A profile is a whole lane.</b> What CI runs under a name is what a developer gets from the same
/// name: the selection and every environment switch the lane depends on live here, not in a workflow
/// step's <c>env:</c>. <c>CiProfileLaneTests</c> holds CI to that — every Tests-project step names a
/// profile and adds no filter of its own — so <c>-p:TestProfile=nr-ui-frozen</c> on a laptop is the
/// thorough job's lane, all of it, rather than the one spec it ran before the job's <c>env:</c> block
/// supplied the rest. No switch changes which tests a lane runs (<see cref="Knobs"/>): the selection is
/// the filter, over test identity.
/// </para>
/// <para>
/// <b>The test app reads this table itself.</b> Both test projects' entry point is <see cref="TestHost"/>:
/// it finds the profile, refuses one that does not cover the assembly it is running in, sets the
/// profile's environment in its own process before the first test, applies the strict zero-tests
/// policy, and hands the platform the profile's filter — ANDed with a caller's <c>--filter</c>, which
/// narrows a profile rather than replacing it. Nothing is rendered to a file: there is no second
/// record of a lane to drift from this one. <c>--list-test-profiles [--json]</c> prints the table.
/// </para>
/// <para>
/// The lint that holds this table to the suite is <c>TestProfileRegistryTests</c>: names, clause
/// grammar, the <c>Engine</c>/<c>Category</c> values (by reflection over the assembly),
/// environment keys (against <see cref="Knobs"/>), assemblies (against <c>BattleScribeSpec.slnx</c>),
/// <c>MaySkip</c>, and every selection's claims against the lanes its filter reaches when evaluated over
/// the traits of each lane's own classes.
/// </para>
/// </remarks>
internal static class TestProfiles
{
    /// <summary>The main test assembly, which holds every engine lane.</summary>
    public const string Tests = EngineLanes.Assembly;

    /// <summary>The <c>bs-spec</c> CLI's own tests.</summary>
    public const string Cli = "BattleScribeSpec.Cli.Tests";

    /// <summary>
    /// Each test assembly's project, repo-relative: what <see cref="TestHost"/> tells a caller to name
    /// with <c>--project</c> when a solution-wide run reaches an assembly the profile does not cover.
    /// <c>TestProfileRegistryTests.EveryProfileAssembly_IsASolutionTestProject</c> holds it to the solution.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Projects { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Tests] = "tests/BattleScribeSpec.Tests.csproj",
        [Cli] = "tests/BattleScribeSpec.Cli.Tests/BattleScribeSpec.Cli.Tests.csproj",
    };

    private const string NrEngineUrl = "https://www.newrecruit.eu";
    private const string NrEditorUrl = "https://giloushaker.github.io/nr-editor/";

    private static readonly IReadOnlyDictionary<string, string> NoEnv = new Dictionary<string, string>();

    /// <summary>Every profile, grouped as the lanes are: the offline gate and CI unit lanes, smoke, thorough, live.</summary>
    public static IReadOnlyList<TestProfile> All { get; } =
    [
        // ── The offline gate and the CI unit lanes.
        Profile("pre-push",
            "The offline gate: CI's windows-pre-push job runs it on every PR, and running it locally is optional. It "
            + "runs everything that is OFFLINE and CHEAP and nothing else: lint, the in-process BattleScribe engines, and the frozen NR lanes "
            + "(HAR replay and the local NR Editor snapshot, Playwright included; those cost seconds) — the frozen NR UI "
            + "roster lane as its kitchen-sink half, since the whole of it is ~27 minutes. It covers both test assemblies. "
            + "Its exclusions are not written here: the filter is derived from EngineLane.InPrePush, so every engine lane "
            + "carries a recorded decision and its measured cost, and a lane that needs the desktop app or a third party's "
            + "site cannot be put in (TestProfileRegistryTests.PrePushHonoursItsPromise). "
            + "A lane with no decision used to RUN here by silence, which is how BsRosterUi came to spend 688.8s of a "
            + "689.2s run driving a desktop app in a profile documented as offline and fast (#405). Every lane it claims "
            + "must execute: on a machine where setup.ps1 has not fetched a lane's snapshot (the HAR, the NR Editor "
            + "snapshot), the run fails with exit 8, naming the lane and the fix; without the Playwright browsers the "
            + "browser tests fail outright (exit 2), and the lanes that went empty are named all the same.",
            Selection.PrePush(), assemblies: [Tests, Cli]),
        Profile("core",
            "The full offline suite CI's thorough-conformance job runs: everything except the NR and NR UI engines "
            + "and the BattleScribe Data Editor UI engine (BsGameDataUi, run under xvfb by bs-ui-gamedata) — unit, "
            + "lint, protocol, and the in-process BattleScribe engines. The deny-list is stated verbatim rather than "
            + "derived. THIS PROFILE MEANS TWO DIFFERENT THINGS: BsRosterUi is not excluded, so on a developer machine "
            + "it launches the real desktop app and drives every roster spec through it (minutes, and a display), "
            + "while CI's offline lanes run setup.ps1 -SkipJavaAgent, the agent jar is absent, and every one of those "
            + "tests skips. Kept that way on purpose: the local half is what has caught this suite's cross-spec reuse "
            + "defects, each invisible to CI at the time. To sit out the app for one run, narrow it with "
            + "--filter \"Engine!=BsRosterUi\", which the test app ANDs onto this profile's filter.",
            Selection.AllExcept("FrozenNrRoster", "FrozenNrGameData", "LiveNrRoster", "LiveNrGameData", "FrozenNrUiRoster",
                "LiveNrUiRoster", "FrozenNrGameDataUi", "LiveNrGameDataUi", "BsGameDataUi"),
            maySkip: [("BsRosterUi", "CI does not provision the app: thorough-conformance runs setup.ps1 -SkipJavaAgent, so the agent jar is absent and every BsRosterUi test skips there")]),
        Profile("non-conformance",
            "Everything that is not a conformance test: unit, integration, lint, protocol. The checks job's first test "
            + "step. Category=Conformance is what keeps every browser- and app-driving test out of it, which is why "
            + "a regression test that launches Chromium carries that category too.",
            Selection.Raw("Category!=Conformance", claims: [],
                incidental: [("LiveNrRoster", "LiveNrRosterSmokeTests, the class nr-live-smoke runs, carries Category=Smoke and not "
                    + "Conformance, so this filter selects it. This lane sets no NR_ENGINE_URL: its three site tests skip and its "
                    + "offline CatXmlGenerator check runs. It does not exist to run the live lane.")])),
        Profile("cli",
            "The bs-spec CLI's own tests, unfiltered: engine selection, the load target, the --policy rejections, "
            + "the protocol surface. They are offline and CPU-only (the in-process BattleScribe engine and the dotnet: "
            + "reference adapter, no browser, no network), so they run in the checks job. CI once ran only the other "
            + "test project, so every gate on the CLI's third-party load limit had never been executed by CI; a gate "
            + "nobody invokes is a gate nobody has. CI runs it as its one dotnet test step (dotnet test --project "
            + "<csproj> -p:TestProfile=cli), so the MSBuild-carried arguments and server mode stay exercised.",
            Selection.Whole, assemblies: [Cli]),
        Profile("lint",
            "Spec lint and structure validation: every Category=Lint test, the repo's own drift gates included.",
            Selection.Raw("Category=Lint", claims: [])),
        Profile("bs",
            "BattleScribe reference engine roster conformance: every roster spec through the in-process IKVM engine.",
            Selection.Engines("BsRoster")),

        // ── Smoke: every push, kitchen-sink through each engine.
        Profile("smoke-bs",
            "Both in-process BattleScribe engines over kitchen-sink, the checks job's engine smoke.",
            Selection.Engines("BsRoster", "BsGameData").Where("DisplayName~kitchen-sink")),
        Profile("smoke-nr-frozen",
            "The frozen NR roster engine over kitchen-sink: the lane without its OtherSpecs test. The lane's suite is two "
            + "[Fact] aggregates whose display names carry no spec id, so a DisplayName clause cannot narrow them: "
            + "Engine=FrozenNrRoster&DisplayName~kitchen-sink once selected exactly one test, a self-skipping sequential "
            + "row (Passed: 0, Skipped: 1, exit 0, green), while the real suite was never selected. The narrowing is by "
            + "test name instead (Selection.KitchenSink).",
            Selection.KitchenSink("FrozenNrRoster")),
        Profile("smoke-nr-ui",
            "The frozen NR UI roster driver over kitchen-sink: the lane without its OtherSpecs test, as smoke-nr-frozen. "
            + "Engine=FrozenNrUiRoster&DisplayName~kitchen-sink matched zero tests, exited 0, and reported green right "
            + "through the frozen NR UI suite breaking on a HAR client change.",
            Selection.KitchenSink("FrozenNrUiRoster")),
        Profile("smoke-nr-editor",
            "The frozen NR Editor GameData engine over kitchen-sink (a per-spec theory, so the DisplayName clause narrows it).",
            Selection.Engines("FrozenNrGameData").Where("DisplayName~kitchen-sink")),
        Profile("smoke-nr-editor-ui",
            "The frozen NR Editor GameData UI driver over kitchen-sink: the lane without its OtherSpecs test, as smoke-nr-frozen.",
            Selection.KitchenSink("FrozenNrGameDataUi")),
        Profile("smoke-bs-gamedata-ui",
            "The BattleScribe Data Editor UI driver over kitchen-sink: the desktop app, its Java agent and a display "
            + "(xvfb-run in CI).",
            Selection.Engines("BsGameDataUi").Where("DisplayName~kitchen-sink")),

        // ── Thorough: whole lanes, on demand and on every stack or CI-definition PR.
        Profile("nr-frozen",
            "Frozen New Recruit roster conformance over the pre-recorded HAR snapshot: offline, needs ./setup.ps1.",
            Selection.Engines("FrozenNrRoster")),
        Profile("nr-ui-frozen",
            "The full NR UI roster lane: one browser drives every applicable roster spec in turn over the pre-recorded "
            + "HAR snapshot — about 27 minutes in CI (thorough-nr-ui-roster), roughly the same locally. It is the engine "
            + "whole: the KitchenSink test smoke-nr-ui and pre-push run alone, and OtherSpecs, every other spec, in the "
            + "same browser. It used to take an environment switch to make it full, which CI's step env set, so this "
            + "profile meant the full lane in CI and one spec everywhere else. To put a few specs through the driver, "
            + "`bs-spec run --engine newrecruit --ui <spec>` replays the same HAR.",
            Selection.Engines("FrozenNrUiRoster")),
        Profile("nr-editor-frozen",
            "Frozen NR Editor GameData conformance, served locally from the pinned static snapshot.",
            Selection.Engines("FrozenNrGameData")),
        Profile("nr-editor-ui-frozen",
            "Frozen NR Editor GameData UI conformance (static file serving, offline).",
            Selection.Engines("FrozenNrGameDataUi")),
        Profile("bs-ui-gamedata",
            "BattleScribe Data Editor UI conformance: needs the BattleScribe app and the Java agent (setup.ps1) and a display.",
            Selection.Engines("BsGameDataUi")),
        Profile("bs-ui-roster",
            "BattleScribe roster UI conformance: every roster spec through the real desktop app. Needs the app and "
            + "the Java agent (both provisioned by setup.ps1) and a display; minutes.",
            Selection.Engines("BsRosterUi")),

        // ── Live: sessions on a third party's production site.
        Profile("nr-live",
            "Live New Recruit roster conformance and integration tests against newrecruit.eu (sets NR_ENGINE_URL). To "
            + "watch a run, set NR_HEADLESS=false (and NR_VISUAL=true for the roster editor) in your own environment: "
            + "no profile sets either, so your value reaches the run. That replaced the nr-live-visible and "
            + "nr-ui-live-visible profiles, which were these lanes with those two switches fixed.",
            Selection.Engines("LiveNrRoster"), env: new() { ["NR_ENGINE_URL"] = NrEngineUrl }),
        Profile("nr-live-smoke",
            "Live New Recruit roster smoke tests against newrecruit.eu: the nr-conformance job's first step.",
            Selection.Engines("LiveNrRoster").Where("Category=Smoke"), env: new() { ["NR_ENGINE_URL"] = NrEngineUrl }),
        Profile("nr-live-conformance",
            "Live New Recruit roster conformance against newrecruit.eu: the nr-conformance job's spec suite.",
            Selection.Engines("LiveNrRoster").Where("Category=Conformance"), env: new() { ["NR_ENGINE_URL"] = NrEngineUrl }),
        Profile("nr-ui-live",
            "Live NR UI driver roster conformance against newrecruit.eu. Set NR_HEADLESS=false to watch it.",
            Selection.Engines("LiveNrUiRoster"), env: new() { ["NR_ENGINE_URL"] = NrEngineUrl }),
        Profile("nr-editor-live",
            "Live NR Editor GameData conformance against the NR Editor deployment (sets NR_EDITOR_URL).",
            Selection.Engines("LiveNrGameData"), env: new() { ["NR_EDITOR_URL"] = NrEditorUrl }),
        Profile("nr-editor-ui-live",
            "Live NR Editor GameData UI conformance against the NR Editor deployment (sets NR_EDITOR_URL). It used to "
            + "leave the URL to the caller, and without it every test skips: a profile that ran nothing and passed.",
            Selection.Engines("LiveNrGameDataUi"), env: new() { ["NR_EDITOR_URL"] = NrEditorUrl }),
    ];

    /// <summary>The profile named <paramref name="name"/>, or <see langword="null"/>.</summary>
    public static TestProfile? Find(string name) => All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    private static TestProfile Profile(
        string name,
        string purpose,
        Selection selection,
        Dictionary<string, string>? env = null,
        string[]? assemblies = null,
        (string Engine, string Why)[]? maySkip = null) =>
        new(name, purpose, selection, env ?? NoEnv, assemblies ?? [Tests], maySkip ?? []);
}
