using BattleScribeSpec.GameData;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Scenario;

/// <summary>
/// Root model for a scenario spec (<c>specs/scenario/{category}/{id}.yaml</c>): one spec that drives an
/// engine family's data editor and roster editor, joined by explicit hand-offs over a data directory.
/// See <c>docs/adr/002-scenario-specs.md</c>.
/// </summary>
public sealed class ScenarioSpecFile : SpecFileBase
{
    /// <summary>
    /// The files the data editor opens and the data directory starts with, and <c>edit</c>, the file the
    /// data editor has open — the gamedata setup verbatim, because it is the same thing: a scenario's data
    /// editor is set up exactly as a gamedata spec's is.
    /// </summary>
    public required GameDataSetupDef Setup { get; set; }

    public required List<ScenarioStepDef> Steps { get; set; }
}

/// <summary>
/// One item of a scenario's <c>steps</c>: exactly one of a <see cref="Data"/> block, a
/// <see cref="Roster"/> block, or a hand-off <see cref="Action"/>.
/// <para>
/// Blocks hold the existing step models unchanged, so a step means in a scenario what it means in a
/// roster or gamedata spec. The two vocabularies reuse field names with different meanings —
/// <c>entryId</c> is the entry to select on the roster side and the entry to edit (or the declared id)
/// on the data side — which is why steps are grouped by editor rather than merged into one type.
/// </para>
/// </summary>
public sealed class ScenarioStepDef
{
    /// <summary>Steps in the data editor, run against the family's <see cref="IGameDataEngine"/>.</summary>
    public List<GameDataStepDef>? Data { get; set; }

    /// <summary>Steps in the roster editor, run against the family's <see cref="IRosterEngine"/>.</summary>
    public List<StepDef>? Roster { get; set; }

    /// <summary>A hand-off between the editors: one of <see cref="ScenarioHandoffs.All"/>.</summary>
    public string? Action { get; set; }

    /// <summary>
    /// The name a roster is saved under (<c>saveRoster</c>) and opened by (<c>openRoster</c>). Required on
    /// those two hand-offs and meaningless on the others.
    /// </summary>
    public string? File { get; set; }

    /// <summary>
    /// Declares that the engine refuses this hand-off — an <c>openRoster</c> whose roster the edited data
    /// can no longer carry, say. Same contract as on an action step: see <see cref="ExpectFailureDef"/>.
    /// </summary>
    public ExpectFailureDef? ExpectFailure { get; set; }
}

/// <summary>The four hand-offs a scenario can make, and the rule they share.</summary>
/// <remarks>
/// The roster editor sees data only as of the last <see cref="SaveData"/>, and only when it starts
/// (<see cref="NewRoster"/>) or opens (<see cref="OpenRoster"/>) a roster. Unsaved edits stay in the
/// data editor — as they do in BattleScribe, whose Data Editor and Roster Editor are two programs that
/// share nothing but the data folder.
/// </remarks>
public static class ScenarioHandoffs
{
    /// <summary>The data editor saves its open file; the data directory takes what it wrote.</summary>
    public const string SaveData = "saveData";

    /// <summary>The roster editor reads the data directory and starts an empty roster on it.</summary>
    public const string NewRoster = "newRoster";

    /// <summary>The roster editor saves its roster under <see cref="ScenarioStepDef.File"/>.</summary>
    public const string SaveRoster = "saveRoster";

    /// <summary>The roster editor reads the data directory and opens the roster saved as <see cref="ScenarioStepDef.File"/>.</summary>
    public const string OpenRoster = "openRoster";

    public static readonly IReadOnlyList<string> All = [SaveData, NewRoster, SaveRoster, OpenRoster];

    /// <summary>The hand-offs that name a saved roster, and so require <see cref="ScenarioStepDef.File"/>.</summary>
    public static bool TakesFile(string action) => action is SaveRoster or OpenRoster;
}
