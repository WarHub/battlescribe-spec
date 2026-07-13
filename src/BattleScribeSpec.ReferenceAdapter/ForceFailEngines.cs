using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.ReferenceAdapter;

/// <summary>
/// Test-only hook shared by <see cref="ForceFailRosterEngine"/> and
/// <see cref="ForceFailGameDataEngine"/>: honors <c>BSSPEC_TEST_FORCE_FAIL</c> so a test can make
/// this arm of a run deliberately diverge from another arm's verdicts, without touching any real
/// engine. This exists specifically to red-test <c>bs-spec compare</c>'s verdict-equality
/// assertion — a configuration change that alters conformance results must fail that command.
/// </summary>
/// <remarks>
/// Value semantics: unset/empty — never force-fail. <c>"1"</c> — force-fail every spec this
/// process runs. Any other value — force-fail only specs whose id contains it (case-insensitive),
/// so a single adapter process can be pointed at one named spec in a larger batch.
/// </remarks>
internal static class ForceFailHook
{
    internal const string EnvVar = "BSSPEC_TEST_FORCE_FAIL";

    public static bool ShouldForceFail(string? specId)
    {
        var raw = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        return raw == "1" || (specId is not null && specId.Contains(raw, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> Apply(IReadOnlyList<string> errors, string? specId) =>
        ShouldForceFail(specId)
            ? [.. errors, $"{EnvVar}: forced failure for spec '{specId}'"]
            : errors;
}

/// <summary>
/// Decorates an <see cref="IRosterEngine"/> so <c>BSSPEC_TEST_FORCE_FAIL</c> can inject a
/// synthetic setup error — <see cref="Roster.RosterRunner"/> treats any non-empty <c>Setup</c>
/// error list as an immediate spec failure, regardless of the spec's own assertions, which makes
/// this the smallest reliable way to force a verdict. A test double only; never wraps a real
/// engine outside the reference adapter.
/// </summary>
internal sealed class ForceFailRosterEngine(IRosterEngine inner) : IRosterEngine
{
    private string? _specId;

    public void SetTestContext(string specId)
    {
        _specId = specId;
        inner.SetTestContext(specId);
    }

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues) =>
        ForceFailHook.Apply(inner.Setup(gameSystem, catalogues), _specId);

    public IReadOnlyList<string> SetupFromFiles(IReadOnlyList<(string FileName, string Content)> files) =>
        ForceFailHook.Apply(inner.SetupFromFiles(files), _specId);

    public ActionOutputs AddForce(string forceEntryId, string catalogueId) => inner.AddForce(forceEntryId, catalogueId);

    public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId) =>
        inner.AddChildForce(parentForceId, forceEntryId, catalogueId);

    public void RemoveForce(string forceId) => inner.RemoveForce(forceId);

    public ActionOutputs SelectEntry(string forceId, string entryId) => inner.SelectEntry(forceId, entryId);

    public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId) =>
        inner.SelectChildEntry(forceId, parentSelectionId, entryId);

    public void DeselectSelection(string forceId, string selectionId) => inner.DeselectSelection(forceId, selectionId);

    public void SetSelectionCount(string forceId, string selectionId, int count) =>
        inner.SetSelectionCount(forceId, selectionId, count);

    public ActionOutputs DuplicateSelection(string forceId, string selectionId) => inner.DuplicateSelection(forceId, selectionId);

    public ActionOutputs DuplicateForce(string forceId) => inner.DuplicateForce(forceId);

    public void SetCostLimit(string costTypeId, decimal value) => inner.SetCostLimit(costTypeId, value);

    public void SetCustomization(string forceId, string? selectionId, string? categoryEntryId, string? customName, string? customNotes) =>
        inner.SetCustomization(forceId, selectionId, categoryEntryId, customName, customNotes);

    public RosterState GetRosterState() => inner.GetRosterState();

    public IReadOnlyList<ValidationErrorState> GetValidationErrors() => inner.GetValidationErrors();

    public string ExportRosterXml() => inner.ExportRosterXml();

    public void Cleanup() => inner.Cleanup();

    public void Dispose() => inner.Dispose();
}

/// <summary>
/// Decorates an <see cref="IGameDataEngine"/> the same way <see cref="ForceFailRosterEngine"/>
/// decorates the roster side: <c>BSSPEC_TEST_FORCE_FAIL</c> injects a synthetic <c>Setup</c> error,
/// which <see cref="BattleScribeSpec.GameData.GameDataRunner"/> treats as an immediate spec
/// failure. A test double only; never wraps a real engine outside the reference adapter.
/// </summary>
internal sealed class ForceFailGameDataEngine(IGameDataEngine inner) : IGameDataEngine
{
    private string? _specId;

    public void SetTestContext(string specId)
    {
        _specId = specId;
        inner.SetTestContext(specId);
    }

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues) =>
        ForceFailHook.Apply(inner.Setup(gameSystem, catalogues), _specId);

    public void Cleanup() => inner.Cleanup();

    public void OpenFile(string id) => inner.OpenFile(id);

    public GameDataActionOutputs AddEntry(string parentId, string entryType, string? name = null, string? id = null) =>
        inner.AddEntry(parentId, entryType, name, id);

    public void RemoveEntry(string entryId) => inner.RemoveEntry(entryId);

    public void SetField(string entryId, string field, string? value) => inner.SetField(entryId, field, value);

    public void SetCost(string entryId, string costTypeId, string? value) => inner.SetCost(entryId, costTypeId, value);

    public void SetCharacteristic(string entryId, string nameOrTypeId, string? value) =>
        inner.SetCharacteristic(entryId, nameOrTypeId, value);

    public GameDataActionOutputs AddLink(string parentId, string linkType, string targetId, string? id = null) =>
        inner.AddLink(parentId, linkType, targetId, id);

    public void Reload() => inner.Reload();

    public string ExportActiveFile() => inner.ExportActiveFile();

    public string LoadFile(string xml) => inner.LoadFile(xml);

    public GameDataState GetState() => inner.GetState();

    public IReadOnlyList<Roster.ValidationErrorState> GetValidationErrors() => inner.GetValidationErrors();

    public void Dispose() => inner.Dispose();
}
