namespace BattleScribeSpec.Tests.Profiles;

/// <summary>One named lane: what it runs, in which test assemblies, under which environment.</summary>
/// <param name="Name">The name <c>-p:TestProfile=</c> takes; lower-case kebab-case.</param>
/// <param name="Purpose">What the lane is for and what a reader must know before relying on it.</param>
/// <param name="Selection">Which tests it runs.</param>
/// <param name="Env">
/// The environment switches the lane needs, each one classified in <see cref="Knobs"/>. A
/// <see langword="null"/> value means "must be unset"; every <see cref="KnobKind.LaneDefining"/>
/// switch not listed here must be unset in this lane as well.
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
    IReadOnlyDictionary<string, string?> Env,
    IReadOnlyList<string> Assemblies,
    IReadOnlyList<(string Engine, string Why)> MaySkip);

/// <summary>
/// <b>Every test profile — the one record of every lane.</b> <c>-p:TestProfile=&lt;name&gt;</c> selects
/// one; CI runs its lanes through them.
/// </summary>
/// <remarks>
/// <para>
/// While the suites run on VSTest, each profile that covers <see cref="EngineLanes.Assembly"/> is
/// rendered into <c>tests/test-profiles/&lt;name&gt;.runsettings</c>, which is what
/// <c>-p:TestProfile</c> hands VSTest. Those files are generated from this table and committed;
/// <c>RunsettingsGenerationTests.Runsettings_MatchTheRegistry</c> fails on any difference and prints
/// the expected file, so a hand edit there is a red build, not a second record. <see cref="Cli"/> has
/// no file: it covers only <c>BattleScribeSpec.Cli.Tests</c>, which CI runs unfiltered.
/// </para>
/// <para>
/// The lint that holds this table to the suite is <c>TestProfileRegistryTests</c>: names, clause
/// grammar, the <c>Engine</c>/<c>Category</c>/<c>Mode</c> values (by reflection over the assembly),
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

    private const string NrEngineUrl = "https://www.newrecruit.eu";
    private const string NrEditorUrl = "https://giloushaker.github.io/nr-editor/";

    private static readonly IReadOnlyDictionary<string, string?> NoEnv = new Dictionary<string, string?>();

    /// <summary>Every profile, grouped as the lanes are: local gate and CI unit lanes, smoke, thorough, live, sequential.</summary>
    public static IReadOnlyList<TestProfile> All { get; } =
    [
        // ── The local gate and the CI unit lanes.
        Profile("pre-push",
            "The gate AGENTS.md tells every contributor to run before every push, so it runs everything that is "
            + "OFFLINE and CHEAP and nothing else: lint, the in-process BattleScribe engines, and the frozen NR lanes "
            + "(HAR replay and the local NR Editor snapshot, Playwright included; those cost seconds). It covers both "
            + "test assemblies. Its exclusions are not written here: the filter is derived from EngineLane.InPrePush, "
            + "so every engine lane carries a recorded decision and its measured cost, and a lane that needs the "
            + "desktop app or a third party's site cannot be put in (TestProfileRegistryTests.PrePushHonoursItsPromise). "
            + "Mode=Sequential classes are out too: manual-only, gated behind NR_SEQUENTIAL. A lane with no decision "
            + "used to RUN here by silence, which is how BsRosterUi came to spend 688.8s of a 689.2s run driving a "
            + "desktop app in a profile documented as offline and fast (#405).",
            Selection.PrePush(), assemblies: [Tests, Cli]),
        Profile("core",
            "The full offline suite CI's thorough-conformance job runs: everything except the NR and NR UI engines "
            + "and the BattleScribe Data Editor UI engine (BsGameDataUi, run under xvfb by bs-ui-gamedata) — unit, "
            + "lint, protocol, and the in-process BattleScribe engines. The deny-list is stated verbatim rather than "
            + "derived. THIS PROFILE MEANS TWO DIFFERENT THINGS: BsRosterUi is not excluded, so on a developer machine "
            + "it launches the real desktop app and drives every roster spec through it (minutes, and a display), "
            + "while CI's offline lanes run setup.ps1 -SkipJavaAgent, the agent jar is absent, and every one of those "
            + "tests skips. Kept that way on purpose: the local half is what has caught this suite's cross-spec reuse "
            + "defects, each invisible to CI at the time. To sit out the app for one run, set BS_UI_SKIP=true. On "
            + "VSTest a filter given on the command line REPLACES this profile's filter rather than narrowing it, so "
            + "it is not the way to subtract one engine.",
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
            + "nobody invokes is a gate nobody has. No runsettings file: on VSTest this assembly runs without one.",
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
            "The frozen NR roster engine over kitchen-sink. Its class is a single [Fact] aggregate whose display name "
            + "carries no spec id, so a DisplayName clause cannot narrow it: Engine=FrozenNrRoster&DisplayName~kitchen-sink "
            + "once selected exactly one test, the Mode=Sequential theory row gated behind NR_SEQUENTIAL, which skipped "
            + "on every run (Passed: 0, Skipped: 1, exit 0, green) while the real suite was never selected. The "
            + "narrowing comes from the engine side instead (NR_FROZEN_SMOKE), and Mode!=Sequential keeps the skipped "
            + "sequential rows out.",
            Selection.Engines("FrozenNrRoster").Where("Mode!=Sequential"), env: new() { ["NR_FROZEN_SMOKE"] = "1" }),
        Profile("smoke-nr-ui",
            "The frozen NR UI roster driver over kitchen-sink: the aggregate's default when NR_UI_ROSTER_FULL is unset. "
            + "The class is the narrowing; Engine=FrozenNrUiRoster&DisplayName~kitchen-sink matched zero tests, exited "
            + "0, and reported green right through the frozen NR UI suite breaking on a HAR client change.",
            Selection.Engines("FrozenNrUiRoster")),
        Profile("smoke-nr-editor",
            "The frozen NR Editor GameData engine over kitchen-sink (a per-spec theory, so the DisplayName clause narrows it).",
            Selection.Engines("FrozenNrGameData").Where("DisplayName~kitchen-sink")),
        Profile("smoke-nr-editor-ui",
            "The frozen NR Editor GameData UI driver over kitchen-sink. Its aggregate is narrowed by NR_UI_SMOKE, the "
            + "same shape as NR_FROZEN_SMOKE.",
            Selection.Engines("FrozenNrGameDataUi"), env: new() { ["NR_UI_SMOKE"] = "1" }),
        Profile("smoke-bs-gamedata-ui",
            "The BattleScribe Data Editor UI driver over kitchen-sink: the desktop app, its Java agent and a display "
            + "(xvfb-run in CI).",
            Selection.Engines("BsGameDataUi").Where("DisplayName~kitchen-sink")),

        // ── Thorough: whole lanes, on demand and on every stack or CI-definition PR.
        Profile("nr-frozen",
            "Frozen New Recruit roster conformance over the pre-recorded HAR snapshot: offline, needs ./setup.ps1. "
            + "Excludes Mode=Sequential (manual-only; also excluded by pre-push and CI).",
            Selection.Engines("FrozenNrRoster").Where("Mode!=Sequential")),
        Profile("nr-ui-frozen",
            "The NR UI roster driver over the pre-recorded HAR snapshot. Without NR_UI_ROSTER_FULL the class runs "
            + "kitchen-sink alone; CI's thorough step sets it for the whole applicable spec set.",
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
            "Live New Recruit roster conformance and integration tests against newrecruit.eu (sets NR_ENGINE_URL).",
            Selection.Engines("LiveNrRoster"), env: new() { ["NR_ENGINE_URL"] = NrEngineUrl }),
        Profile("nr-live-smoke",
            "Live New Recruit roster smoke tests against newrecruit.eu: the nr-conformance job's first step.",
            Selection.Engines("LiveNrRoster").Where("Category=Smoke"), env: new() { ["NR_ENGINE_URL"] = NrEngineUrl }),
        Profile("nr-live-conformance",
            "Live New Recruit roster conformance against newrecruit.eu: the nr-conformance job's spec suite.",
            Selection.Engines("LiveNrRoster").Where("Category=Conformance"), env: new() { ["NR_ENGINE_URL"] = NrEngineUrl }),
        Profile("nr-live-visible",
            "Same as nr-live, with a visible browser window and visual editor navigation for debugging.",
            Selection.Engines("LiveNrRoster"),
            env: new() { ["NR_ENGINE_URL"] = NrEngineUrl, ["NR_HEADLESS"] = "false", ["NR_VISUAL"] = "true" }),
        Profile("nr-ui-live",
            "Live NR UI driver roster conformance against newrecruit.eu.",
            Selection.Engines("LiveNrUiRoster"), env: new() { ["NR_ENGINE_URL"] = NrEngineUrl }),
        Profile("nr-ui-live-visible",
            "Live NR UI driver roster tests with the browser window visible, for debugging.",
            Selection.Engines("LiveNrUiRoster"), env: new() { ["NR_ENGINE_URL"] = NrEngineUrl, ["NR_HEADLESS"] = "false" }),
        Profile("nr-editor-live",
            "Live NR Editor GameData conformance against the NR Editor deployment (sets NR_EDITOR_URL).",
            Selection.Engines("LiveNrGameData"), env: new() { ["NR_EDITOR_URL"] = NrEditorUrl }),
        Profile("nr-editor-ui-live",
            "Live NR Editor GameData UI conformance. Requires NR_EDITOR_URL to be set: this profile does not set it, "
            + "and without it every test skips.",
            Selection.Engines("LiveNrGameDataUi")),

        // ── Sequential: manual-only, one engine, the specs one after another.
        Profile("nr-frozen-sequential",
            "The Mode=Sequential frozen NR roster class: one engine, every spec in turn over the frozen HAR, for "
            + "isolating a defect the pooled lane hides. Manual-only.",
            Selection.Engines("FrozenNrRoster").Where("Mode=Sequential"), env: new() { ["NR_SEQUENTIAL"] = "true" }),
        Profile("nr-live-sequential",
            "The Mode=Sequential live NR roster class against newrecruit.eu: one engine, every spec in turn. Manual-only.",
            Selection.Engines("LiveNrRoster").Where("Mode=Sequential"),
            env: new() { ["NR_SEQUENTIAL"] = "true", ["NR_ENGINE_URL"] = NrEngineUrl }),
    ];

    /// <summary>The profile named <paramref name="name"/>, or <see langword="null"/>.</summary>
    public static TestProfile? Find(string name) => All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    private static TestProfile Profile(
        string name,
        string purpose,
        Selection selection,
        Dictionary<string, string?>? env = null,
        string[]? assemblies = null,
        (string Engine, string Why)[]? maySkip = null) =>
        new(name, purpose, selection, env ?? NoEnv, assemblies ?? [Tests], maySkip ?? []);
}
