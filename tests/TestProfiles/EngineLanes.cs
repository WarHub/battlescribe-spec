namespace BattleScribeSpec.Tests.Profiles;

/// <summary>What an engine lane needs from the machine beyond a build of this repository.</summary>
[Flags]
internal enum Needs
{
    /// <summary>Nothing: an in-process engine, offline and headless.</summary>
    None = 0,

    /// <summary>A Playwright browser on this machine (installed by <c>setup.ps1</c>), driven headless by default.</summary>
    LocalBrowser = 1,

    /// <summary>The real BattleScribe desktop app, its Java agent and a display (<c>xvfb-run</c> in CI).</summary>
    DesktopApp = 2,

    /// <summary>Sessions on somebody else's production website.</summary>
    ThirdPartySite = 4,
}

/// <summary>How much of an engine lane the pre-push gate runs.</summary>
internal enum PrePushPart
{
    /// <summary>None of it.</summary>
    None,

    /// <summary>All of it.</summary>
    Whole,

    /// <summary>All of it but its <see cref="EngineLane.OtherSpecs"/> test: the lane's kitchen-sink half.</summary>
    KitchenSink,
}

/// <summary>
/// One <c>Engine</c> trait value in the suite: what running it needs, how much of it the pre-push gate
/// runs, and which test classes are the lane's own.
/// </summary>
/// <param name="Trait">The <c>Engine</c> trait value, exactly as the test classes carry it.</param>
/// <param name="Needs">What the lane needs from the machine.</param>
/// <param name="InPrePush">
/// How much of this lane <c>pre-push</c> runs. The profile's filter is derived from this column
/// (<see cref="Selection.PrePush"/>), so the decision and the filter cannot disagree; a lane that
/// needs the desktop app or a third party's site must say <see cref="PrePushPart.None"/>.
/// </param>
/// <param name="Why">
/// The reason for <paramref name="InPrePush"/>, with the cost that justifies it. Costs are summed test
/// time from the TRX of one <c>pre-push</c> run on the 32-core dev box, 2026-08-12; the lanes overlap,
/// so they add up to more than the run.
/// </param>
/// <param name="LaneTests">
/// Full type names of the lane's own classes: the ones that run the spec suite through this engine
/// (and, for the live roster engine, the smoke class <c>nr-live-smoke</c> consists of). Every class in
/// the assembly that carries an <c>Engine</c> trait is either listed here under its own engine or in
/// <see cref="EngineLanes.NotLaneTests"/> with a reason.
/// </param>
internal sealed record EngineLane(string Trait, Needs Needs, PrePushPart InPrePush, string Why, IReadOnlyList<string> LaneTests)
{
    /// <summary>
    /// The environment switches without which every test of this lane skips: a live lane's endpoint
    /// URL. Every profile that claims the lane must set each one
    /// (<c>TestProfileRegistryTests.EveryProfile_SuppliesItsEnginesRequiredEnv</c>), so a profile
    /// that names a live lane and cannot reach it — green, having run nothing — is a red lint run.
    /// </summary>
    public IReadOnlyList<string> RequiredEnv { get; init; } = [];

    /// <summary>
    /// Where this lane's driver writes its failure diagnostics (a screenshot, the DOM, the store),
    /// repo-relative; the driver may append a per-worker suffix. A CI job that runs the lane must
    /// upload it (<c>CiProfileLaneTests.EveryUiLane_UploadsTheDiagnosticsItWrites</c>), or the record
    /// of a failure dies with the runner.
    /// </summary>
    public string? DiagnosticsDir { get; init; }

    /// <summary>
    /// The switch the driver needs before it writes anything to <see cref="DiagnosticsDir"/>, when it
    /// has one. It is a CI decision, not part of the lane: the capture runs on every failed action,
    /// expected failures included, so the job that uploads the directory sets it where the lane runs.
    /// </summary>
    public string? DiagnosticsSwitch { get; init; }

    /// <summary>
    /// The switch that moves <see cref="DiagnosticsDir"/> somewhere else; the driver honours a value
    /// the caller sets over its default. CI may not set it — not on a step, a job, the workflow or in
    /// any other YAML under <c>.github/</c> — because the upload rule is derived from the default
    /// directory, and a redirected driver writes where no upload looks
    /// (<c>CiProfileLaneTests.EveryUiLane_UploadsTheDiagnosticsItWrites</c>). Required with
    /// <see cref="DiagnosticsDir"/>.
    /// </summary>
    public string? DiagnosticsDirSwitch { get; init; }

    /// <summary>
    /// Whether the lane runs its spec suite inside single tests — a <c>[Fact] AllSpecs()</c>, or a
    /// <c>KitchenSink</c> and an <c>OtherSpecs</c> <c>[Fact]</c> that split it — each driving its specs
    /// in turn through one engine, rather than one theory row per spec. Such a lane prints nothing for
    /// minutes while it works, so in GitHub Actions <c>TestHost</c> turns on <c>--show-live-output</c>
    /// for a profile that claims one; and no spec-id <c>DisplayName</c> clause can narrow it.
    /// <c>TestProfileRegistryTests.EveryAggregateLane_IsDeclared</c> holds this to the lane classes.
    /// </summary>
    public bool Aggregate { get; init; }

    /// <summary>
    /// For an aggregate lane split in two, the fully qualified name of its <c>OtherSpecs</c> test, which
    /// drives every applicable spec but kitchen-sink (its <c>KitchenSink</c> test drives the rest). A
    /// selection of the lane's kitchen-sink half excludes it by name: <see cref="Selection.KitchenSink"/>,
    /// and <see cref="Selection.PrePush"/> for a <see cref="PrePushPart.KitchenSink"/> lane.
    /// <c>TestProfileRegistryTests.EveryAggregateLane_IsDeclared</c> holds it to a <c>[Fact]</c> of the lane.
    /// </summary>
    public string? OtherSpecs { get; init; }

    /// <summary>
    /// Why no CI job runs this lane, when none does. A lane is either run by a CI step's profile or
    /// carries this — never both, and never neither (<c>CiProfileLaneTests.EveryEngineLane_IsRunByCi_OrSaysWhyNot</c>).
    /// </summary>
    public string? CiExempt { get; init; }

    /// <summary>
    /// What it means when a run that claims this lane executed none of its <see cref="LaneTests"/> — every
    /// one skipped, or none was selected — and what to do about it. <see cref="TestHost"/> prints it,
    /// prefixed with the lane's name, when the engine-composition check fails (exit 8), and when the
    /// platform's own zero-tests policy failed the run with this lane empty
    /// (<see cref="LaneComposition"/>). Required, so a new lane cannot arrive without saying how it goes
    /// empty: the compiler refuses an <see cref="EngineLane"/> that does not set it.
    /// </summary>
    public required string EmptyHint { get; init; }
}

/// <summary>
/// <b>Every engine lane in the suite, and the decision each one carries.</b> The one record of which
/// <c>Engine</c> trait values exist, what they need, and whether the offline gate, <c>pre-push</c>,
/// includes them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a table and not a rule.</b> <c>pre-push</c> is a deny-list, and a lane with no decision used
/// to join it by silence: when <c>BsRosterUi</c> arrived (#353) nothing excluded it, and it spent
/// 688.8s of a 689.2s run driving the BattleScribe desktop app in a profile advertised at <c>~40s</c>
/// (#405) — <c>Failed: 1, Passed: 2936, Skipped: 368, Total: 3305, Duration: 11 m 29 s</c>.
/// <c>LiveNrUiRoster</c> had drifted in the same way and was invisible only because it self-skips
/// without <c>NR_ENGINE_URL</c>. "Exclude anything with Ui in the name" would have caught
/// <c>BsRosterUi</c> and been wrong about the two frozen Playwright lanes, which are UI drivers too and
/// are not the cost: no predicate over a name separates them, only a measurement does, and a
/// measurement is a decision somebody made. So each row records the decision and its cost, and
/// <c>TestProfileRegistryTests</c> requires a row for every <c>Engine</c> value it finds by reflection.
/// </para>
/// <para>
/// <b>The measurements refute the rule everybody reaches for first.</b> The critical path of
/// <c>pre-push</c> is <c>BsRoster</c> — an in-process engine, no UI at all, 266.8s across 367 specs and
/// effectively the whole 267.9s wall clock. Excluding the frozen Playwright lanes (22.6s and 51.8s)
/// would have bought nothing measurable and cost the NR Editor UI driver its only local signal. What
/// was expensive was never "a UI"; it was a desktop application.
/// </para>
/// <para>
/// Every engine lane lives in <see cref="Assembly"/>; <c>BattleScribeSpec.Cli.Tests</c> carries no
/// <c>Engine</c> trait.
/// </para>
/// </remarks>
internal static class EngineLanes
{
    /// <summary>The test assembly every engine lane lives in.</summary>
    public const string Assembly = "BattleScribeSpec.Tests";

    /// <summary>Every engine lane, in the order the pre-push filter lists its exclusions.</summary>
    public static IReadOnlyList<EngineLane> All { get; } =
    [
        // ── In pre-push: offline, no app, and cheap against a 267.9s profile.
        new("BsRoster", Needs.None, InPrePush: PrePushPart.Whole,
            "in-process IKVM reference engine; 266.8s across 367 specs, the critical path of pre-push, and not a UI",
            ["BattleScribeSpec.Tests.BsRosterConformanceTests"])
        {
            EmptyHint = SpecCorpusMissing,
        },
        new("BsGameData", Needs.None, InPrePush: PrePushPart.Whole,
            "in-process IKVM reference engine; 0.8s",
            ["BattleScribeSpec.Tests.BsGameDataConformanceTests"])
        {
            EmptyHint = SpecCorpusMissing,
        },
        new("FrozenNrRoster", Needs.LocalBrowser, InPrePush: PrePushPart.Whole,
            "offline HAR replay, no network; 70.3s",
            ["BattleScribeSpec.Tests.FrozenNrRosterConformanceTests"])
        {
            EmptyHint = FrozenHarMissing,
            Aggregate = true,
            OtherSpecs = "BattleScribeSpec.Tests.FrozenNrRosterConformanceTests.OtherSpecs",
        },
        new("FrozenNrGameData", Needs.LocalBrowser, InPrePush: PrePushPart.Whole,
            "offline static-file serving of the pinned NR Editor snapshot, no network; 133.9s",
            ["BattleScribeSpec.Tests.FrozenNrGameDataConformanceTests"])
        {
            EmptyHint = NrEditorSnapshotMissing,
        },
        new("FrozenNrUiRoster", Needs.LocalBrowser, InPrePush: PrePushPart.KitchenSink,
            "Playwright over the frozen HAR; kitchen-sink only, 22.6s — the whole lane is one browser driving every spec, ~27 minutes",
            ["BattleScribeSpec.Tests.FrozenNrUiRosterConformanceTests"])
        {
            EmptyHint = FrozenHarOrBrowsersMissing,
            Aggregate = true,
            OtherSpecs = "BattleScribeSpec.Tests.FrozenNrUiRosterConformanceTests.OtherSpecs",
            DiagnosticsDir = NrUiDiagnostics,
            DiagnosticsDirSwitch = NrUiDiagnosticsDirSwitch,
        },
        new("FrozenNrGameDataUi", Needs.LocalBrowser, InPrePush: PrePushPart.Whole,
            "Playwright over the frozen NR Editor snapshot; 51.8s, and the NR Editor UI driver's only local signal",
            ["BattleScribeSpec.Tests.FrozenNrGameDataUiConformanceTests"])
        {
            EmptyHint = NrEditorSnapshotOrBrowsersMissing,
            Aggregate = true,
            OtherSpecs = "BattleScribeSpec.Tests.FrozenNrGameDataUiConformanceTests.OtherSpecs",
            DiagnosticsDir = NrGameDataUiDiagnostics,
            DiagnosticsSwitch = NrGameDataUiDiagnosticsSwitch,
            DiagnosticsDirSwitch = NrGameDataUiDiagnosticsDirSwitch,
        },

        // ── Excluded: needs the BattleScribe desktop app (setup.ps1 artifacts, the Java agent and a
        //    display). CI's `thorough-ui-bs` job runs both halves, whole.
        new("BsRosterUi", Needs.DesktopApp, InPrePush: PrePushPart.None,
            "launches the BattleScribe desktop app; 687.8s across 367 specs, sequential — it WAS the 689.2s run it joined by default (#405)",
            ["BattleScribeSpec.Tests.BsRosterUiConformanceTests"])
        {
            EmptyHint = DesktopAppMissing,
            DiagnosticsDir = "artifacts/bs-ui-diagnostics",
            DiagnosticsDirSwitch = "BS_UI_DIAGNOSTICS_DIR",
        },
        // Its driver writes nothing yet: BsGameDataUiDiagnostics.CaptureAsync has no caller, and nothing
        // anchors its directory at the repo root for the test host the way BsRosterUiFixture does for
        // the roster driver. Wiring both is driver work; the directory is recorded so the uploads are
        // ready for it.
        new("BsGameDataUi", Needs.DesktopApp, InPrePush: PrePushPart.None,
            "launches the BattleScribe desktop app (the Data Editor half)",
            ["BattleScribeSpec.Tests.BsGameDataUiConformanceTests"])
        {
            EmptyHint = DesktopAppMissing,
            DiagnosticsDir = "artifacts/bs-gamedata-ui-diagnostics",
            DiagnosticsDirSwitch = "BS_GAMEDATA_UI_DIAGNOSTICS_DIR",
        },

        // ── Excluded: opens sessions on somebody else's production website. A pre-push gate runs on
        //    every push by every contributor, the last traffic profile these sites should see
        //    (ConcurrencyConfigurationDriftTests.EveryLiveFixture_DrawsItsSessionsFromTheThirdPartyLoadBudget).
        //    Each skips whole without its endpoint URL, hence RequiredEnv.
        new("LiveNrRoster", Needs.LocalBrowser | Needs.ThirdPartySite, InPrePush: PrePushPart.None,
            "opens sessions on newrecruit.eu",
            ["BattleScribeSpec.Tests.LiveNrRosterConformanceTests", "BattleScribeSpec.Tests.LiveNrRosterSmokeTests"])
        {
            EmptyHint = NrEngineUnreachable,
            Aggregate = true,
            RequiredEnv = ["NR_ENGINE_URL"],
        },
        new("LiveNrUiRoster", Needs.LocalBrowser | Needs.ThirdPartySite, InPrePush: PrePushPart.None,
            "opens sessions on newrecruit.eu",
            ["BattleScribeSpec.Tests.LiveNrUiRosterConformanceTests", "BattleScribeSpec.Tests.SequentialLiveNrUiRosterConformanceTests"])
        {
            EmptyHint = NrEngineUnreachable,
            Aggregate = true,
            RequiredEnv = ["NR_ENGINE_URL"],
            DiagnosticsDir = NrUiDiagnostics,
            DiagnosticsDirSwitch = NrUiDiagnosticsDirSwitch,
            CiExempt = "nr-conformance, the one job that drives newrecruit.eu, runs the store-direct live lane only; the UI "
                + "driver over every spec would add a browser clicking through the whole suite to a volunteer-run site's "
                + "load on every scheduled run. Run nr-ui-live by hand when the UI driver changes.",
        },
        new("LiveNrGameData", Needs.LocalBrowser | Needs.ThirdPartySite, InPrePush: PrePushPart.None,
            "opens sessions on the NR Editor deployment",
            ["BattleScribeSpec.Tests.LiveNrGameDataConformanceTests"])
        {
            EmptyHint = NrEditorUnreachable,
            RequiredEnv = ["NR_EDITOR_URL"],
            CiExempt = NrEditorLiveNotInCi,
        },
        new("LiveNrGameDataUi", Needs.LocalBrowser | Needs.ThirdPartySite, InPrePush: PrePushPart.None,
            "opens sessions on the NR Editor deployment",
            ["BattleScribeSpec.Tests.LiveNrGameDataUiConformanceTests"])
        {
            EmptyHint = NrEditorUnreachable,
            RequiredEnv = ["NR_EDITOR_URL"],
            DiagnosticsDir = NrGameDataUiDiagnostics,
            DiagnosticsSwitch = NrGameDataUiDiagnosticsSwitch,
            DiagnosticsDirSwitch = NrGameDataUiDiagnosticsDirSwitch,
            CiExempt = NrEditorLiveNotInCi,
        },
    ];

    private const string NrUiDiagnostics = "artifacts/nr-ui-diagnostics";
    private const string NrGameDataUiDiagnostics = "artifacts/nr-gamedata-ui-diagnostics";
    private const string NrGameDataUiDiagnosticsSwitch = "NR_GAMEDATA_UI_DIAGNOSTICS";
    private const string NrUiDiagnosticsDirSwitch = "NR_UI_DIAGNOSTICS_DIR";
    private const string NrGameDataUiDiagnosticsDirSwitch = "NR_GAMEDATA_UI_DIAGNOSTICS_DIR";

    // ── What an empty lane means (EngineLane.EmptyHint). Each names the cause a reader can act on first:
    //    what setup.ps1 provisions, or the switch the lane cannot run without. Missing Playwright browsers
    //    skip only the two UI lanes; the HAR-replay and NR Editor engines' fixtures rethrow a failed browser
    //    launch, so those lanes fail instead of going empty.
    private const string SpecCorpusMissing =
        "the in-process engine never skips, so its spec rows were never produced: the spec corpus (specs/) was not found "
        + "from the test output folder. Run from a full checkout of this repository.";

    private const string FrozenHarMissing =
        "run ./setup.ps1 — the frozen New Recruit snapshot (.testdata/newrecruit-har/newrecruit.har) is missing.";

    private const string FrozenHarOrBrowsersMissing =
        "run ./setup.ps1 — the frozen New Recruit snapshot (.testdata/newrecruit-har/newrecruit.har) or the Playwright "
        + "browsers are missing.";

    private const string NrEditorSnapshotMissing =
        "run ./setup.ps1 — the pinned NR Editor snapshot (.testdata/nr-editor/) is missing.";

    private const string NrEditorSnapshotOrBrowsersMissing =
        "run ./setup.ps1 — the pinned NR Editor snapshot (.testdata/nr-editor/) or the Playwright browsers are missing.";

    private const string DesktopAppMissing =
        "run ./setup.ps1 — the BattleScribe app (lib/battlescribe), the JavaFX-capable JDK (lib/liberica-jdk) or the Java "
        + "agent jar is missing — and give it a display (xvfb-run -a on Linux).";

    private const string NrEngineUnreachable =
        "NR_ENGINE_URL is unset, or another live fixture in this process holds newrecruit.eu's whole session budget "
        + "(the skip reason says which); Playwright browsers come from ./setup.ps1.";

    private const string NrEditorUnreachable =
        "NR_EDITOR_URL is unset, or another live fixture in this process holds the NR Editor deployment's session budget "
        + "(the skip reason says which); Playwright browsers come from ./setup.ps1.";

    private const string NrEditorLiveNotInCi =
        "the frozen NR Editor lanes replay a pinned snapshot of the deployment on every thorough run; whether "
        + "nr-conformance should also drive the live one is an open decision, not an oversight. Run it by hand to check "
        + "a new deployment against the snapshot.";

    /// <summary>
    /// Classes that carry an <c>Engine</c> trait — so a lane's filter selects them — but are not the
    /// lane: they check one contract on that engine, or the shape of a failure, and several pass with
    /// the engine never having run a spec. Each is listed so that a new <c>Engine</c>-tagged class has
    /// to be put on one side or the other.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="EngineLane.LaneTests"/> because "the lane executed something" must
    /// mean the spec suite ran: the six <c>FrozenNrUiRoster</c> regression facts below drive a blank
    /// page and pass on a machine with no HAR, while the lane they share a trait with skips whole.
    /// </remarks>
    public static IReadOnlyList<(string Type, string Why)> NotLaneTests { get; } =
    [
        ("BattleScribeSpec.Tests.Regression.NrUiActionFailureMessageRegressionTests",
            "the shape of a failed action's message, driven against about:blank; passes without the HAR"),
        ("BattleScribeSpec.Tests.Regression.NrUiSetupFailureMessageRegressionTests",
            "the shape of a setup failure's message, driven against about:blank; passes without the HAR"),
        ("BattleScribeSpec.Tests.Regression.NrListCleanupRegressionTests",
            "one regression (Cleanup deletes the roster row a spec created) on the frozen NR roster engine"),
        ("BattleScribeSpec.Tests.Features.BsRosterUiLinkReachedErrorTests",
            "one contract (the link-reached error) on the BattleScribe desktop app"),
        ("BattleScribeSpec.Tests.BsUiGameSystemSelectionTests",
            "one contract (the New Roster dialog picks the exact game system) on the desktop app"),
        ("BattleScribeSpec.Tests.Features.BsRosterUiCategoryNodeIdTests",
            "one contract (category node ids) on the BattleScribe desktop app"),
        ("BattleScribeSpec.Tests.Features.FrozenNrCategoryNodeIdTests",
            "one contract (category node ids) on the frozen NR roster engine"),
        ("BattleScribeSpec.Tests.Features.FrozenNrUiCategoryNodeIdTests",
            "one contract (category node ids) on the frozen NR UI roster driver"),
        ("BattleScribeSpec.Tests.Features.FrozenNrRaisedOnNodeTests",
            "one contract (which node an error is raised on) on the frozen NR roster engine"),
        ("BattleScribeSpec.Tests.Features.FrozenNrRaisedOnIdentityTests",
            "one contract (raised-on identity) on the frozen NR roster engine"),
        ("BattleScribeSpec.Tests.Features.FrozenNrUiRaisedOnNodeTests",
            "one contract (which node an error is raised on) on the frozen NR UI roster driver"),
        ("BattleScribeSpec.Tests.Features.NewRecruitEnginePoolResourceMetricsTests",
            "the resource metrics of the frozen NR roster engine pool's browser and contexts"),
        ("BattleScribeSpec.Tests.Features.NrGameDataUiEnginePoolResourceMetricsTests",
            "the resource metrics of the NR Editor GameData UI pool's browser and contexts"),
        ("BattleScribeSpec.Tests.Features.NewRecruitGameDataEngineResourceMetricsTests",
            "the resource metrics of the frozen NR Editor GameData engine's own browser and context"),
        ("BattleScribeSpec.Tests.Features.NrGameDataUiEngineResourceMetricsTests",
            "the resource metrics of the NR Editor GameData UI driver's own browser and context"),
        ("BattleScribeSpec.Tests.Features.NrGameDataUiEnginePoolPartialFailureTests",
            "a construction failure partway through the NR Editor UI pool leaks nothing (#304)"),
        ("BattleScribeSpec.Tests.LiveNrRosterIntegrationTests",
            "hand-written adapter checks against the live site; the lane's spec suite is LiveNrRosterConformanceTests"),
    ];

    /// <summary>The lane whose <c>Engine</c> trait is <paramref name="trait"/>, or <see langword="null"/>.</summary>
    public static EngineLane? Find(string trait) => All.FirstOrDefault(l => string.Equals(l.Trait, trait, StringComparison.Ordinal));
}
