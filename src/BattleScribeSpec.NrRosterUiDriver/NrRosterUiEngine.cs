using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

#pragma warning disable IDE0060 // Remove unused parameter — interface implementations and UI stubs

namespace BattleScribeSpec.NrRosterUiDriver;

/// <summary>
/// IRosterEngine implementation that drives the New Recruit web app through
/// Playwright UI interactions rather than direct JS/API access.
///
/// Actions (AddForce, SelectEntry, etc.) are performed via real UI clicks and
/// form inputs. State is read from NR's Pinia store via JS (hybrid approach).
///
/// Data loading (Setup) uses the File System Access API mock or &lt;input type="file"&gt;
/// depending on what NR uses — see NrUiSetup.LoadGameDataAsync.
/// </summary>
public sealed class NrRosterUiEngine : IRosterEngine
{
    internal NewRecruitBrowser Browser { get; }
    private bool _disposed;
    private string _rosterName = "Spec Test";
    private string? _listId;
    private bool _systemLoaded;
    private string? _loadedSystemId;

    // ID → Name lookups built from spec data during Setup.
    // Used by UI actions that must find entries by their visible label.
    private readonly Dictionary<string, string> _forceEntryNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _entryNames = new(StringComparer.Ordinal);

    private NrRosterUiEngine(NewRecruitBrowser browser)
    {
        Browser = browser;
    }

    /// <summary>Create a live (internet-connected) engine instance.</summary>
    public static async Task<NrRosterUiEngine> CreateAsync(
        string baseUrl = "https://newrecruit.eu",
        bool headless = true,
        float? slowMo = null)
    {
        var browser = await NewRecruitBrowser.CreateAsync(baseUrl, headless, slowMo);
        return new NrRosterUiEngine(browser);
    }

    /// <summary>Create an engine that replays all network traffic from a HAR file.</summary>
    public static async Task<NrRosterUiEngine> CreateFrozenAsync(
        string harFilePath,
        string baseUrl = "https://newrecruit.eu",
        bool headless = true,
        float? slowMo = null)
    {
        var browser = await NewRecruitBrowser.CreateFrozenAsync(harFilePath, baseUrl, headless, slowMo);
        return new NrRosterUiEngine(browser);
    }

    public void SetTestContext(string specId) => _rosterName = specId;

    // ===== IRosterEngine: Setup =====

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
        => SetupAsync(gameSystem, catalogues).GetAwaiter().GetResult();

    private async Task<IReadOnlyList<string>> SetupAsync(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        BuildEntryLookups(gameSystem, catalogues);

        var gstXml = CatXmlGenerator.GenerateGameSystemXml(gameSystem);
        var catFiles = CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues);
        var allFiles = new List<(string FileName, string Content)>
        {
            ("system.gst", gstXml),
        };
        allFiles.AddRange(catFiles.Select(f => (f.FileName, f.Xml)));

        // Navigate to app and wait for Pinia
        if (!Browser.FrozenReady)
        {
            await Browser.NavigateToAppAsync();
            await Browser.WaitForPiniaAsync();
        }

        // Load game data into NR via UI (only once per unique system in frozen mode)
        if (!_systemLoaded || _loadedSystemId != gameSystem.Id)
        {
            await NrUiSetup.LoadGameDataAsync(Browser, allFiles, gameSystem.Id);
            _systemLoaded = true;
            _loadedSystemId = gameSystem.Id;

            if (Browser.IsFrozen)
            {
                Browser.FrozenReady = true;
            }
        }

        // Create a new roster via NR UI and navigate to the editor
        var listId = await NrUiSetup.CreateRosterAsync(Browser.Page, _rosterName, gameSystem);
        _listId = listId;

        if (listId is not null)
        {
            await Browser.NavigateToEditorAsync(listId);
            await NrUiSetup.WaitForEditorLoadedAsync(Browser.Page);
        }

        return [];
    }

    public IReadOnlyList<string> SetupFromFiles(IReadOnlyList<(string FileName, string Content)> files)
        => SetupFromFilesAsync(files).GetAwaiter().GetResult();

    private async Task<IReadOnlyList<string>> SetupFromFilesAsync(IReadOnlyList<(string FileName, string Content)> files)
    {
        if (!Browser.FrozenReady)
        {
            await Browser.NavigateToAppAsync();
            await Browser.WaitForPiniaAsync();
        }

        await NrUiSetup.LoadGameDataAsync(Browser, files, systemId: null);
        _systemLoaded = true;

        var listId = await NrUiSetup.CreateRosterAsync(Browser.Page, _rosterName, gameSystem: null);
        _listId = listId;

        if (listId is not null)
        {
            await Browser.NavigateToEditorAsync(listId);
            await NrUiSetup.WaitForEditorLoadedAsync(Browser.Page);
        }

        return [];
    }

    // ===== IRosterEngine: Roster mutations (all UI-driven) =====

    public ActionOutputs AddForce(string forceEntryId, string catalogueId)
        => AddForceAsync(forceEntryId, catalogueId).GetAwaiter().GetResult();

    private async Task<ActionOutputs> AddForceAsync(string forceEntryId, string catalogueId)
    {
        _ = catalogueId;
        var name = _forceEntryNames.GetValueOrDefault(forceEntryId, forceEntryId);
        var uid = await NrUiActions.AddForceByNameAsync(Browser.Page, name);
        return new ActionOutputs { ForceId = uid };
    }

    public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId)
        => AddChildForceAsync(parentForceId, forceEntryId, catalogueId).GetAwaiter().GetResult();

    private async Task<ActionOutputs> AddChildForceAsync(string parentForceId, string forceEntryId, string catalogueId)
    {
        _ = catalogueId;
        var name = _forceEntryNames.GetValueOrDefault(forceEntryId, forceEntryId);
        var uid = await NrUiActions.AddChildForceByNameAsync(Browser.Page, parentForceId, name);
        return new ActionOutputs { ForceId = uid };
    }

    public void RemoveForce(string forceId)
        => NrUiActions.RemoveForceAsync(Browser.Page, forceId).GetAwaiter().GetResult();

    public ActionOutputs SelectEntry(string forceId, string entryId)
        => SelectEntryAsync(forceId, entryId).GetAwaiter().GetResult();

    private async Task<ActionOutputs> SelectEntryAsync(string forceId, string entryId)
    {
        var name = _entryNames.GetValueOrDefault(entryId, entryId);
        var uid = await NrUiActions.SelectEntryByNameAsync(Browser.Page, forceId, name);
        return new ActionOutputs { SelectionId = uid };
    }

    public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId)
        => SelectChildEntryAsync(forceId, parentSelectionId, entryId).GetAwaiter().GetResult();

    private async Task<ActionOutputs> SelectChildEntryAsync(string forceId, string parentSelectionId, string entryId)
    {
        _ = forceId;
        var name = _entryNames.GetValueOrDefault(entryId, entryId);
        var uid = await NrUiActions.SelectChildEntryByNameAsync(Browser.Page, parentSelectionId, name);
        return new ActionOutputs { SelectionId = uid };
    }

    public void DeselectSelection(string forceId, string selectionId)
    {
        _ = forceId;
        NrUiActions.DeselectSelectionAsync(Browser.Page, selectionId).GetAwaiter().GetResult();
    }

    public void SetSelectionCount(string forceId, string selectionId, int count)
    {
        _ = forceId;
        NrUiActions.SetSelectionCountAsync(Browser.Page, selectionId, count).GetAwaiter().GetResult();
    }

    public ActionOutputs DuplicateSelection(string forceId, string selectionId)
        => DuplicateSelectionAsync(forceId, selectionId).GetAwaiter().GetResult();

    private async Task<ActionOutputs> DuplicateSelectionAsync(string forceId, string selectionId)
    {
        _ = forceId;
        var uid = await NrUiActions.DuplicateSelectionAsync(Browser.Page, selectionId);
        return new ActionOutputs { SelectionId = uid };
    }

    public ActionOutputs DuplicateForce(string forceId)
        => DuplicateForceAsync(forceId).GetAwaiter().GetResult();

    private async Task<ActionOutputs> DuplicateForceAsync(string forceId)
    {
        var uid = await NrUiActions.DuplicateForceAsync(Browser.Page, forceId);
        return new ActionOutputs { ForceId = uid };
    }

    public void SetCostLimit(string costTypeId, decimal value)
        => NrUiActions.SetCostLimitAsync(Browser.Page, costTypeId, value).GetAwaiter().GetResult();

    public void SetCustomization(string forceId, string? selectionId, string? categoryEntryId, string? customName, string? customNotes)
        => NrUiActions.SetCustomizationAsync(Browser.Page, forceId, selectionId, categoryEntryId, customName, customNotes).GetAwaiter().GetResult();

    // ===== IRosterEngine: State (JS reads — hybrid approach) =====

    public RosterState GetRosterState()
        => NewRecruitStateReader.ReadRosterStateAsync(Browser.Page).GetAwaiter().GetResult();

    public IReadOnlyList<ValidationErrorState> GetValidationErrors()
        => NewRecruitStateReader.ReadValidationErrorsAsync(Browser.Page).GetAwaiter().GetResult();

    // ===== Lifecycle =====

    public void Cleanup()
    {
        _listId = null;
        _systemLoaded = false;
        _loadedSystemId = null;
        _forceEntryNames.Clear();
        _entryNames.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Browser.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // ===== Internal: entry name lookups =====

    private void BuildEntryLookups(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        _forceEntryNames.Clear();
        _entryNames.Clear();

        foreach (var fe in gameSystem.ForceEntries ?? [])
        {
            RegisterForceEntry(fe);
        }

        foreach (var se in gameSystem.SelectionEntries ?? [])
        {
            RegisterSelectionEntry(se);
        }

        foreach (var el in gameSystem.EntryLinks ?? [])
        {
            RegisterEntryLink(el);
        }

        foreach (var cat in catalogues)
        {
            RegisterCatalogue(cat);
        }
    }

    private void RegisterForceEntry(ProtocolForceEntry fe)
    {
        _forceEntryNames[fe.Id] = fe.Name;
        foreach (var child in fe.ForceEntries ?? [])
        {
            RegisterForceEntry(child);
        }
    }

    private void RegisterSelectionEntry(ProtocolSelectionEntry se)
    {
        _entryNames[se.Id] = se.Name;
        foreach (var child in se.SelectionEntries ?? [])
        {
            RegisterSelectionEntry(child);
        }

        foreach (var grp in se.SelectionEntryGroups ?? [])
        {
            RegisterSelectionEntryGroup(grp);
        }

        foreach (var link in se.EntryLinks ?? [])
        {
            RegisterEntryLink(link);
        }
    }

    private void RegisterSelectionEntryGroup(ProtocolSelectionEntryGroup grp)
    {
        _entryNames[grp.Id] = grp.Name;
        foreach (var child in grp.SelectionEntries ?? [])
        {
            RegisterSelectionEntry(child);
        }

        foreach (var nested in grp.SelectionEntryGroups ?? [])
        {
            RegisterSelectionEntryGroup(nested);
        }

        foreach (var link in grp.EntryLinks ?? [])
        {
            RegisterEntryLink(link);
        }
    }

    private void RegisterEntryLink(ProtocolEntryLink link)
    {
        // An entry link's name overrides the target's name (or falls back to it)
        var name = string.IsNullOrEmpty(link.Name)
            ? _entryNames.GetValueOrDefault(link.TargetId, link.TargetId)
            : link.Name;
        _entryNames[link.Id] = name;
    }

    private void RegisterCatalogue(ProtocolCatalogue cat)
    {
        foreach (var se in cat.SelectionEntries ?? [])
        {
            RegisterSelectionEntry(se);
        }

        foreach (var grp in cat.SharedSelectionEntryGroups ?? [])
        {
            RegisterSelectionEntryGroup(grp);
        }

        foreach (var se in cat.SharedSelectionEntries ?? [])
        {
            RegisterSelectionEntry(se);
        }

        foreach (var link in cat.EntryLinks ?? [])
        {
            RegisterEntryLink(link);
        }

        foreach (var fe in cat.ForceEntries ?? [])
        {
            RegisterForceEntry(fe);
        }
    }
}
