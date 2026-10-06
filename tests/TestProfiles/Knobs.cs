namespace BattleScribeSpec.Tests.Profiles;

/// <summary>What an environment switch does to a run, which decides how a profile may treat it.</summary>
internal enum KnobKind
{
    /// <summary>
    /// Tunes how a lane runs, not what it runs: a URL, headless mode, slow-mo, a timeout, a path, a
    /// diagnostics switch. A profile may give a value; the caller's own value is for the caller.
    /// </summary>
    Default,

    /// <summary>Set by the harness or by a test on a child process; never the caller's to set.</summary>
    Internal,

    /// <summary>
    /// Read by nothing any more: its question is <c>ConcurrencyPolicy</c>'s now. The literal survives
    /// only in the tests that prove it is ignored, and
    /// <c>ConcurrencyConfigurationDriftTests.RetiredEnvironmentKnobs_AreReadByNoProductionCodeOrFixture_AndSetByNoWorkflow</c>
    /// fails if production code reads one again or a workflow sets one, and pins the three
    /// <c>ConcurrencyPolicy</c> retired, so moving one of those rows to another kind fails there too.
    /// </summary>
    Retired,
}

/// <summary>One environment switch the harness or the product reads.</summary>
/// <param name="Name">The variable's name.</param>
/// <param name="Kind">What it does to a run.</param>
/// <param name="Engines">
/// The <c>Engine</c> lanes whose code reads it; empty when it is not specific to an engine.
/// </param>
/// <param name="Why">What it does, in a sentence a reader can act on.</param>
internal sealed record Knob(string Name, KnobKind Kind, IReadOnlyList<string> Engines, string Why);

/// <summary>
/// <b>Every <c>NR_*</c>, <c>BS_*</c>, <c>BSSPEC_*</c> and <c>BSUI_*</c> variable named anywhere under
/// <c>src/</c> or <c>tests/</c>, classified.</b> <c>TestProfileRegistryTests.EveryKnobLiteral_IsClassified</c>
/// finds every such string literal and fails on one that is missing here, so a new switch arrives as
/// a decision about what it does to a lane — and on a row whose switch no code outside the registry,
/// the lint tests and comments names any more, so neither this table nor a lint about it can keep a
/// switch alive after the code that read it has gone.
/// </summary>
/// <remarks>
/// <para>
/// <b>No switch changes which tests a lane runs.</b> There used to be a kind for those — smoke, full,
/// filter, skip, sequential and snapshot-update switches — and a host that refused one exported in a
/// shell, because each could leave a run green that checked a fraction of what its profile names. They
/// were pruned instead (#532): a lane's selection is its profile's filter over test identity (a split
/// aggregate's <c>KitchenSink</c> and <c>OtherSpecs</c> tests), a lane sits out with
/// <c>--filter "Engine!=X"</c> or a narrower profile, and snapshots are rewritten by
/// <c>bs-spec run --update-snapshots</c>, never by a test run. A new switch that would select tests is
/// a test or a profile instead.
/// </para>
/// <para>
/// Endpoint URLs are <see cref="KnobKind.Default"/> although a live lane skips without one: pointing
/// a live profile at a staging deployment is a deliberate, visible choice, and the URL is what
/// <c>EngineEndpoint</c> derives a lane's load target from, not a selection.
/// </para>
/// </remarks>
internal static class Knobs
{
    private static readonly string[] PlaywrightEngines =
    [
        "FrozenNrRoster", "FrozenNrGameData", "FrozenNrUiRoster", "FrozenNrGameDataUi",
        "LiveNrRoster", "LiveNrUiRoster", "LiveNrGameData", "LiveNrGameDataUi",
    ];

    private static readonly string[] DesktopAppEngines = ["BsRosterUi", "BsGameDataUi"];

    /// <summary>Every classified switch, grouped by kind.</summary>
    public static IReadOnlyList<Knob> All { get; } =
    [
        // ── Default: tunes how a lane runs; a profile may supply a value where the caller has none.
        new("NR_ENGINE_URL", KnobKind.Default, ["LiveNrRoster", "LiveNrUiRoster"],
            "the New Recruit deployment the live roster lanes drive; they skip without it"),
        new("NR_EDITOR_URL", KnobKind.Default, ["LiveNrGameData", "LiveNrGameDataUi"],
            "the NR Editor deployment the live GameData lanes drive; they skip without it"),
        new("NR_HEADLESS", KnobKind.Default, PlaywrightEngines,
            "false shows the browser window; anything else, or unset, runs headless"),
        new("NR_SLOW_MO", KnobKind.Default, PlaywrightEngines,
            "Playwright slow-mo in milliseconds, a pause between browser actions for watching a run"),
        new("NR_VISUAL", KnobKind.Default, ["FrozenNrRoster", "LiveNrRoster"],
            "true navigates to the roster editor UI after setup, for watching a run"),
        new("NR_NAV_TIMEOUT_MS", KnobKind.Default, ["FrozenNrGameData", "LiveNrGameData", "FrozenNrGameDataUi", "LiveNrGameDataUi"],
            "the NR Editor store's navigation timeout in milliseconds"),
        new("NR_TRACE_STORE", KnobKind.Default, ["FrozenNrUiRoster", "LiveNrUiRoster"],
            "1 or true records NR's store mutations into the NR UI driver's diagnostics (bs-spec sets it for its own flag)"),
        new("NR_UI_TIMINGS", KnobKind.Default, ["FrozenNrUiRoster", "LiveNrUiRoster"],
            "1 or true prints where the NR UI roster driver's wall-clock went, passing runs included"),
        new("NR_UI_DIAGNOSTICS_DIR", KnobKind.Default, ["FrozenNrUiRoster", "LiveNrUiRoster"],
            "where the NR UI roster driver writes its failure diagnostics (anchored at the repo's artifacts/ by default)"),
        new("NR_GAMEDATA_UI_DIAGNOSTICS", KnobKind.Default, ["FrozenNrGameDataUi", "LiveNrGameDataUi"],
            "set, the NR Editor GameData UI driver captures a screenshot, DOM and editor state on every failed action"),
        new("NR_GAMEDATA_UI_DIAGNOSTICS_DIR", KnobKind.Default, ["FrozenNrGameDataUi", "LiveNrGameDataUi"],
            "where the NR Editor GameData UI driver writes those diagnostics"),
        new("BS_UI_DIAGNOSTICS_DIR", KnobKind.Default, ["BsRosterUi"],
            "where the BattleScribe roster UI driver writes its failure diagnostics"),
        new("BS_GAMEDATA_UI_DIAGNOSTICS_DIR", KnobKind.Default, ["BsGameDataUi"],
            "where the BattleScribe Data Editor UI driver writes its failure diagnostics"),
        new("BS_UI_JAVA_PATH", KnobKind.Default, DesktopAppEngines,
            "the JavaFX-capable java to launch the desktop app with (otherwise lib/liberica-jdk, then JAVA_HOME)"),
        new("BS_UI_APP_DIR", KnobKind.Default, DesktopAppEngines,
            "where bs-spec's engine host finds the BattleScribe app (otherwise lib/battlescribe)"),
        new("BS_UI_AGENT_JAR", KnobKind.Default, DesktopAppEngines,
            "where bs-spec's engine host finds the Java agent jar"),
        new("BS_UI_ACTION_TIMEOUT", KnobKind.Default, ["BsRosterUi"],
            "the roster UI driver's per-action timeout"),
        new("BSUI_AGENT_STDERR_LOG", KnobKind.Default, DesktopAppEngines,
            "a file to copy the Java agent's stderr into"),
        new("BS_UI_PANEL_TRACE", KnobKind.Default, ["BsRosterUi"],
            "1 makes the Java agent print which edit-panel control each labelled request drove"),
        new("BS_UI_TREE_TRACE", KnobKind.Default, ["BsRosterUi"],
            "1 makes the Java agent dump both roster trees around a selectEntry"),
        new("BS_UI_VALIDATION_TRACE", KnobKind.Default, ["BsRosterUi"],
            "1 makes the Java agent print every validation error with each id source that could name it"),
        new("BSSPEC_DATASOURCE_CACHE_DIR", KnobKind.Default, [],
            "where the TestKit caches cloned real-world data sources"),

        // ── Internal: set by the harness, or by a test on a child process it starts.
        new("BSSPEC_WORKER_INDEX", KnobKind.Internal, [],
            "the batch runner's worker index, set on each adapter process it starts (diagnostics paths are suffixed with it)"),
        new("BSSPEC_WORKER_ID", KnobKind.Internal, [],
            "a worker's id, suffixed onto the NR UI driver's diagnostics directory"),
        new("BSSPEC_ENGINE_HOST", KnobKind.Internal, [],
            "the engine host binary EngineHostLocator resolves; overridden by tests to point at a fake"),
        new("BSSPEC_TEST_FORCE_FAIL", KnobKind.Internal, [],
            "makes the reference adapter fail on purpose, for the runner's failure-path tests"),
        new("BSSPEC_TEST_FORCE_KILL", KnobKind.Internal, [],
            "makes the reference adapter die on purpose, for the runner's adapter-death tests"),
        new("BSSPEC_TEST_ROSTER_ONLY", KnobKind.Internal, [],
            "makes the reference adapter serve only the roster domain, for the runner's domain tests"),
        new("BSSPEC_TEST_ENV", KnobKind.Internal, [],
            "a probe name AdapterProcessEnvTests sets on a child process and reads back; configures nothing"),
        new("BSSPEC_EXTRA_VAR", KnobKind.Internal, [],
            "a probe name AdapterProcessEnvTests sets on a child process and reads back; configures nothing"),
        new("BSSPEC_INHERITED_SENTINEL", KnobKind.Internal, [],
            "a probe name AdapterProcessEnvTests sets to prove a child inherits the parent's environment; configures nothing"),
        new("NR_UI_DIAGNOSTICS_DIR_TEST_PROBE", KnobKind.Internal, [],
            "a probe name DiagnosticsIsolationTests anchors and reads back; configures nothing"),

        // ── Retired: read by nothing; ConcurrencyPolicy answers what each one used to.
        new("NR_PARALLEL", KnobKind.Retired, [],
            "was the browser-context count, with three fixtures and three different defaults"),
        new("BS_UI_KEEP_ALIVE", KnobKind.Retired, [],
            "was BS-UI gamedata reuse: unset ran it cold while the policy said warm"),
        new("BSSPEC_DISABLE_WARM_REUSE", KnobKind.Retired, [],
            "was the reuse ablation channel; bs-spec compare --policy-a/--policy-b replaced it"),
    ];

    /// <summary>The switch named <paramref name="name"/>, or <see langword="null"/> when it is not classified.</summary>
    public static Knob? Find(string name) => All.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.Ordinal));
}
