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

/// <summary>
/// One <c>Engine</c> trait value in the suite: what running it needs, whether the pre-push gate runs
/// it, and which test classes are the lane's own.
/// </summary>
/// <param name="Trait">The <c>Engine</c> trait value, exactly as the test classes carry it.</param>
/// <param name="Needs">What the lane needs from the machine.</param>
/// <param name="InPrePush">
/// Whether <c>pre-push</c> runs this lane. The profile's filter is derived from this column
/// (<see cref="Selection.PrePush"/>), so the decision and the filter cannot disagree; a lane that
/// needs the desktop app or a third party's site may not say <see langword="true"/>.
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
internal sealed record EngineLane(string Trait, Needs Needs, bool InPrePush, string Why, IReadOnlyList<string> LaneTests);

/// <summary>
/// <b>Every engine lane in the suite, and the decision each one carries.</b> The one record of which
/// <c>Engine</c> trait values exist, what they need, and whether the gate every contributor runs before
/// every push includes them.
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
        new("BsRoster", Needs.None, InPrePush: true,
            "in-process IKVM reference engine; 266.8s across 367 specs, the critical path of pre-push, and not a UI",
            ["BattleScribeSpec.Tests.BsRosterConformanceTests"]),
        new("BsGameData", Needs.None, InPrePush: true,
            "in-process IKVM reference engine; 0.8s",
            ["BattleScribeSpec.Tests.BsGameDataConformanceTests"]),
        new("FrozenNrRoster", Needs.LocalBrowser, InPrePush: true,
            "offline HAR replay, no network; 70.3s",
            ["BattleScribeSpec.Tests.FrozenNrRosterConformanceTests", "BattleScribeSpec.Tests.SequentialFrozenNrRosterConformanceTests"]),
        new("FrozenNrGameData", Needs.LocalBrowser, InPrePush: true,
            "offline static-file serving of the pinned NR Editor snapshot, no network; 133.9s",
            ["BattleScribeSpec.Tests.FrozenNrGameDataConformanceTests"]),
        new("FrozenNrUiRoster", Needs.LocalBrowser, InPrePush: true,
            "Playwright over the frozen HAR, kitchen-sink only unless NR_UI_ROSTER_FULL is set; 22.6s",
            ["BattleScribeSpec.Tests.FrozenNrUiRosterConformanceTests"]),
        new("FrozenNrGameDataUi", Needs.LocalBrowser, InPrePush: true,
            "Playwright over the frozen NR Editor snapshot; 51.8s, and the NR Editor UI driver's only local signal",
            ["BattleScribeSpec.Tests.FrozenNrGameDataUiConformanceTests"]),

        // ── Excluded: needs the BattleScribe desktop app (setup.ps1 artifacts, the Java agent and a
        //    display). CI's `thorough-ui-bs` job runs both halves, whole.
        new("BsRosterUi", Needs.DesktopApp, InPrePush: false,
            "launches the BattleScribe desktop app; 687.8s across 367 specs, sequential — it WAS the 689.2s run it joined by default (#405)",
            ["BattleScribeSpec.Tests.BsRosterUiConformanceTests"]),
        new("BsGameDataUi", Needs.DesktopApp, InPrePush: false,
            "launches the BattleScribe desktop app (the Data Editor half)",
            ["BattleScribeSpec.Tests.BsGameDataUiConformanceTests"]),

        // ── Excluded: opens sessions on somebody else's production website. A pre-push gate runs on
        //    every push by every contributor, the last traffic profile these sites should see
        //    (ConcurrencyConfigurationDriftTests.EveryLiveFixture_DrawsItsSessionsFromTheThirdPartyLoadBudget).
        new("LiveNrRoster", Needs.LocalBrowser | Needs.ThirdPartySite, InPrePush: false,
            "opens sessions on newrecruit.eu",
            ["BattleScribeSpec.Tests.LiveNrRosterConformanceTests", "BattleScribeSpec.Tests.SequentialLiveNrRosterConformanceTests",
             "BattleScribeSpec.Tests.LiveNrRosterSmokeTests"]),
        new("LiveNrUiRoster", Needs.LocalBrowser | Needs.ThirdPartySite, InPrePush: false,
            "opens sessions on newrecruit.eu",
            ["BattleScribeSpec.Tests.LiveNrUiRosterConformanceTests", "BattleScribeSpec.Tests.SequentialLiveNrUiRosterConformanceTests"]),
        new("LiveNrGameData", Needs.LocalBrowser | Needs.ThirdPartySite, InPrePush: false,
            "opens sessions on the NR Editor deployment",
            ["BattleScribeSpec.Tests.LiveNrGameDataConformanceTests"]),
        new("LiveNrGameDataUi", Needs.LocalBrowser | Needs.ThirdPartySite, InPrePush: false,
            "opens sessions on the NR Editor deployment",
            ["BattleScribeSpec.Tests.LiveNrGameDataUiConformanceTests"]),
    ];

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
