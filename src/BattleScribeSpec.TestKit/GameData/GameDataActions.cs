namespace BattleScribeSpec.GameData;

/// <summary>
/// The actions a GameData step can name — in a GameData spec, and in a scenario's <c>data:</c> block. The one
/// list: <see cref="GameDataRunner"/> dispatches on these names, the GameData lint refuses any other, and
/// <c>docs/gamedata-spec-schema.json</c>'s <c>actionName</c> enum is held to exactly this set
/// (<c>SpecSchemaDriftTests</c>), so an action added here and nowhere else fails a test.
/// </summary>
public static class GameDataActions
{
    /// <summary>Create a node under <see cref="GameDataStepDef.ParentId"/>.</summary>
    public const string AddEntry = "addEntry";

    /// <summary>Delete the node <see cref="GameDataStepDef.EntryId"/>.</summary>
    public const string RemoveEntry = "removeEntry";

    /// <summary>Set fields, costs and characteristics on <see cref="GameDataStepDef.EntryId"/>.</summary>
    public const string SetFields = "setFields";

    /// <summary>Create a link under <see cref="GameDataStepDef.ParentId"/> to <see cref="GameDataStepDef.TargetId"/>.</summary>
    public const string AddLink = "addLink";

    /// <summary>Open a loaded file, or load one from inline content or a side-file, and make it the active file.</summary>
    public const string OpenFile = "openFile";

    /// <summary>Save the active file and load it back.</summary>
    public const string Reload = "reload";

    /// <summary>A debugger dump point: no engine work, so the runner does nothing with it.</summary>
    public const string Dump = "dump";

    public static readonly IReadOnlyList<string> All = [AddEntry, RemoveEntry, SetFields, AddLink, OpenFile, Reload, Dump];
}
