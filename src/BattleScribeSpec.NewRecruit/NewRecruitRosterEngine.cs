namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// IRosterEngine implementation that wraps the New Recruit web app via Playwright.
///
/// Uses NR's Pinia store API discovered via bundle decompilation:
/// 1. Setup: Select system from NR library → load book → createRoster → addList
/// 2. Actions: Interact with roster nodes via prototype methods (getEntries, setAmount, etc.)
/// 3. State: Read from roster tree via getCurrentList().army
///
/// Currently only supports real-world data (systems from NR's library).
/// Synthetic spec data cannot be loaded into NR's web mode.
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
        return SetupAsync(gameSystem, catalogues).GetAwaiter().GetResult();
    }

    private async Task<IReadOnlyList<string>> SetupAsync(GameSystemSpec gameSystem, CatalogueSpec[] catalogues)
    {
        var errors = new List<string>();

        try
        {
            // Navigate to /app and wait for systems to load
            await _browser.NavigateToAppAsync();
            await _browser.Page.WaitForTimeoutAsync(2000);

            // Select system by name, load book by catalogue name, create roster + list
            var setupResult = await _browser.Page.EvaluateAsync<string?>($$"""
                async ({systemName, catalogueName}) => {
                    try {
                        const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                        if (!pinia) return 'Pinia store not found';

                        const sysStore = pinia._s.get('systemsStore');
                        const listsStore = pinia._s.get('lists');
                        if (!sysStore || !listsStore) return 'Required stores not found';

                        // Step 1: Select matching system from NR library
                        const systems = sysStore.systems || [];
                        let sys = systems.find(s => s.name === systemName);
                        if (!sys) {
                            // Try partial match
                            sys = systems.find(s => s.name.includes(systemName) || systemName.includes(s.name));
                        }
                        if (!sys) return 'System not found in NR library: ' + systemName + '. Available: ' + systems.map(s => s.name).slice(0, 10).join(', ');

                        // Select the system (triggers book loading)
                        await sysStore.selectSystem(sys);

                        // Step 2: Find matching playable book
                        const playableBooks = sys.books?.array?.filter(b => b.playable) || [];
                        if (!playableBooks.length) return 'No playable books for system: ' + sys.name;

                        let selectedBook = playableBooks.find(b => b.name === catalogueName);
                        if (!selectedBook) {
                            selectedBook = playableBooks.find(b => b.name.includes(catalogueName) || catalogueName.includes(b.name));
                        }
                        if (!selectedBook) selectedBook = playableBooks[0];

                        // Step 3: Load book data
                        const bookData = await sys.getBook(selectedBook.id);
                        if (!bookData) return 'Failed to load book data for: ' + selectedBook.name;

                        // Step 4: Create roster
                        const costs = bookData.getCosts();
                        const roster = bookData.createRoster(costs);
                        if (!roster) return 'Failed to create roster';
                        roster.setCustomName('Spec Test');

                        // Step 5: Build row metadata and add list
                        const row = {
                            list_key: 'spec_' + Date.now(),
                            name: 'Spec Test',
                            id_game_system: selectedBook.id_game_system || sys.id,
                            id_system: selectedBook.id || sys.id,
                            nrversion: selectedBook.nrversion,
                            date_mod: new Date(),
                            date_create: new Date(),
                            synced: false,
                            uid: null,
                            bsid_book: selectedBook.bsid,
                            bsid_system: sys.bsid
                        };

                        await listsStore.addList({row, army: roster, book: bookData});
                        return null; // success
                    } catch(e) {
                        return 'Setup error: ' + e.message;
                    }
                }
                """, new { systemName = gameSystem.Name, catalogueName = catalogues.FirstOrDefault()?.Name ?? "" });

            if (setupResult != null)
                errors.Add(setupResult);
        }
        catch (Exception ex)
        {
            errors.Add($"Setup exception: {ex.Message}");
        }

        return errors;
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
