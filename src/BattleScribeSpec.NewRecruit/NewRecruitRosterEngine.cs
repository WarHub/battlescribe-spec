namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// IRosterEngine implementation that wraps the New Recruit web app via Playwright.
/// Follows the same pattern as OracleRosterEngine but drives a browser session.
///
/// Since IRosterEngine is synchronous, async Playwright calls are bridged via
/// .GetAwaiter().GetResult(). For the test runner, this is acceptable — each
/// spec run is sequential. A future IAsyncRosterEngine could eliminate this.
/// </summary>
public sealed class NewRecruitRosterEngine : IRosterEngine
{
    private readonly NewRecruitBrowser _browser;
    private bool _disposed;

    private NewRecruitRosterEngine(NewRecruitBrowser browser)
    {
        _browser = browser;
    }

    /// <summary>
    /// Create and initialize a NewRecruitRosterEngine with a browser session.
    /// </summary>
    public static async Task<NewRecruitRosterEngine> CreateAsync(
        string baseUrl = "https://newrecruit.eu",
        bool headless = true)
    {
        var browser = await NewRecruitBrowser.CreateAsync(baseUrl, headless);
        return new NewRecruitRosterEngine(browser);
    }

    public IReadOnlyList<string> Setup(GameSystemSpec gameSystem, CatalogueSpec[] catalogues)
    {
        // Generate .cat/.gst XML from spec data using WarHub.ArmouryModel,
        // then load into NR via route interception or store injection.
        // This is the key bridge between synthetic spec data and NR's real engine.
        return SetupAsync(gameSystem, catalogues).GetAwaiter().GetResult();
    }

    private async Task<IReadOnlyList<string>> SetupAsync(GameSystemSpec gameSystem, CatalogueSpec[] catalogues)
    {
        // TODO Phase 2 implementation:
        // 1. Generate .cat/.gst XML from gameSystem and catalogue specs
        //    using CatXmlGenerator (WarHub.ArmouryModel serialization)
        // 2. Intercept NR's network requests to serve our generated XML
        //    OR inject data directly into NR's Pinia store
        // 3. Navigate to editor with our data loaded
        // 4. Return any initialization errors

        await _browser.NavigateToEditorAsync();
        return [];
    }

    public void AddForce(int forceEntryIndex, int catalogueIndex = 0)
    {
        NewRecruitActions.AddForceAsync(_browser.Page, forceEntryIndex, catalogueIndex)
            .GetAwaiter().GetResult();
    }

    public void RemoveForce(int forceIndex)
    {
        NewRecruitActions.RemoveForceAsync(_browser.Page, forceIndex)
            .GetAwaiter().GetResult();
    }

    public void SelectEntry(int forceIndex, int entryIndex)
    {
        NewRecruitActions.SelectEntryAsync(_browser.Page, forceIndex, entryIndex)
            .GetAwaiter().GetResult();
    }

    public void SelectChildEntry(int forceIndex, int selectionIndex, int childEntryIndex)
    {
        NewRecruitActions.SelectChildEntryAsync(_browser.Page, forceIndex, selectionIndex, childEntryIndex)
            .GetAwaiter().GetResult();
    }

    public void DeselectSelection(int forceIndex, int selectionIndex)
    {
        NewRecruitActions.DeselectSelectionAsync(_browser.Page, forceIndex, selectionIndex)
            .GetAwaiter().GetResult();
    }

    public void SetSelectionCount(int forceIndex, int entryIndex, int count)
    {
        NewRecruitActions.SetSelectionCountAsync(_browser.Page, forceIndex, entryIndex, count)
            .GetAwaiter().GetResult();
    }

    public void DuplicateSelection(int forceIndex, int selectionIndex)
    {
        NewRecruitActions.DuplicateSelectionAsync(_browser.Page, forceIndex, selectionIndex)
            .GetAwaiter().GetResult();
    }

    public void SetCostLimit(string costTypeId, double value)
    {
        NewRecruitActions.SetCostLimitAsync(_browser.Page, costTypeId, value)
            .GetAwaiter().GetResult();
    }

    public RosterState GetRosterState()
    {
        return NewRecruitStateReader.ReadRosterStateAsync(_browser.Page)
            .GetAwaiter().GetResult();
    }

    public IReadOnlyList<string> GetValidationErrors()
    {
        return NewRecruitStateReader.ReadValidationErrorsAsync(_browser.Page)
            .GetAwaiter().GetResult();
    }

    public bool HasValidationErrors()
    {
        return GetValidationErrors().Count > 0;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _browser.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
