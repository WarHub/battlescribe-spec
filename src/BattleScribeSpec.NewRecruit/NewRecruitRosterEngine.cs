namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// IRosterEngine implementation that wraps the New Recruit web app via Playwright.
/// Follows the same pattern as OracleRosterEngine but drives a browser session.
///
/// Data flow:
/// 1. Setup: Generate .gst/.cat XML → upload to NR via BSFilesUploaded() → create new list
/// 2. Actions: Drive NR's UI via Playwright locators or JS evaluation
/// 3. State: Read from NR's Pinia stores (lists, listsPage) via page.EvaluateAsync()
/// </summary>
public sealed class NewRecruitRosterEngine : IRosterEngine
{
    private readonly NewRecruitBrowser _browser;
    private bool _disposed;

    // Cached game system and catalogue specs for index-based lookups during actions
    private GameSystemSpec? _gameSystem;
    private CatalogueSpec[]? _catalogues;

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
        _gameSystem = gameSystem;
        _catalogues = catalogues;
        return SetupAsync(gameSystem, catalogues).GetAwaiter().GetResult();
    }

    private async Task<IReadOnlyList<string>> SetupAsync(GameSystemSpec gameSystem, CatalogueSpec[] catalogues)
    {
        var errors = new List<string>();

        try
        {
            // Step 1: Generate .gst and .cat XML from spec data
            var gstXml = CatXmlGenerator.GenerateGameSystemXml(gameSystem);
            var catXmlList = new List<(string name, string xml)>();
            foreach (var cat in catalogues)
            {
                var catXml = CatXmlGenerator.GenerateCatalogueXml(gameSystem, cat);
                catXmlList.Add(($"{cat.Name}.cat", catXml));
            }

            // Step 2: Navigate to NR app
            await _browser.NavigateToAppAsync();

            // Step 3: Upload .gst/.cat files via NR's systemsStore.BSFilesUploaded()
            // This creates a local system from the uploaded files.
            // BSFilesUploaded() expects File objects — we create them via JS.
            var uploadResult = await _browser.Page.EvaluateAsync<string?>("""
                async ({gstName, gstContent, catFiles}) => {
                    try {
                        const app = document.querySelector('#__nuxt')?.__vue_app__;
                        const pinia = app?.config?.globalProperties?.$pinia;
                        if (!pinia) return 'Pinia store not found';

                        const systemsStore = pinia._s.get('systemsStore');
                        if (!systemsStore) return 'systemsStore not found';

                        // Create File objects from our generated XML
                        const files = [];
                        files.push(new File([gstContent], gstName, { type: 'application/xml' }));
                        for (const cf of catFiles) {
                            files.push(new File([cf.content], cf.name, { type: 'application/xml' }));
                        }

                        // Upload via NR's BSFilesUploaded method
                        await systemsStore.BSFilesUploaded(files);
                        return null; // success
                    } catch(e) {
                        return 'Upload error: ' + e.message;
                    }
                }
                """, new
            {
                gstName = $"{gameSystem.Name}.gst",
                gstContent = gstXml,
                catFiles = catXmlList.Select(c => new { name = c.name, content = c.xml }).ToArray()
            });

            if (uploadResult != null)
            {
                errors.Add(uploadResult);
                return errors;
            }

            // Step 4: Select the uploaded system
            var selectResult = await _browser.Page.EvaluateAsync<string?>("""
                async (systemName) => {
                    try {
                        const app = document.querySelector('#__nuxt')?.__vue_app__;
                        const pinia = app?.config?.globalProperties?.$pinia;
                        const systemsStore = pinia._s.get('systemsStore');

                        // Find and select our uploaded system
                        const allSystems = systemsStore.allSystems || [];
                        const system = allSystems.find(s => s.name === systemName);
                        if (!system) return 'System not found after upload: ' + systemName;

                        await systemsStore.selectSystem(system);
                        return null;
                    } catch(e) {
                        return 'Select error: ' + e.message;
                    }
                }
                """, gameSystem.Name);

            if (selectResult != null)
            {
                errors.Add(selectResult);
                return errors;
            }

            // Step 5: Create a new list with this system
            var createResult = await _browser.Page.EvaluateAsync<string?>("""
                async () => {
                    try {
                        const app = document.querySelector('#__nuxt')?.__vue_app__;
                        const pinia = app?.config?.globalProperties?.$pinia;
                        const lists = pinia._s.get('lists');
                        if (!lists) return 'lists store not found';

                        await lists.addList();
                        return null;
                    } catch(e) {
                        return 'Create list error: ' + e.message;
                    }
                }
                """);

            if (createResult != null)
            {
                errors.Add(createResult);
                return errors;
            }

            // Wait for NR to process the list creation
            await _browser.Page.WaitForTimeoutAsync(1000);
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
