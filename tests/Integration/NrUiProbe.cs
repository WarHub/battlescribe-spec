using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests;

/// <summary>
/// TEMPORARY — delete before PR.
/// Probes the NrRosterUiEngine setup flow and captures editor DOM structure.
/// </summary>
[Collection("LiveNrUiRoster")]
[Trait("Category", "Integration")]
public sealed class NrUiProbe
{
    private readonly ITestOutputHelper _output;
    private readonly LiveNrUiRosterFixture _fixture;

    public NrUiProbe(ITestOutputHelper output, LiveNrUiRosterFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    /// <summary>Probe MySystems page DOM to understand file loading mechanism.</summary>
    [Fact]
    public async Task ProbeMySystems_DomStructure()
    {
        Assert.SkipWhen(!_fixture.Available, "NR_ENGINE_URL not set");

        var engine = _fixture.Engine!;
        var page = engine.Browser.Page;

        await engine.Browser.NavigateToAppAsync();
        await engine.Browser.WaitForPiniaAsync();

        // Navigate to MySystems
        await engine.Browser.NavigateToRouteAsync("/app/MySystems");
        await page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);

        _output.WriteLine($"URL: {page.Url}");
        _output.WriteLine($"Title: {await page.TitleAsync()}");

        // Check for file inputs
        var inputInfoJson = await page.EvaluateAsync<string>("""
            () => {
                const inputs = [...document.querySelectorAll('input')];
                return JSON.stringify(inputs.map(i => ({
                    type: i.type,
                    id: i.id,
                    name: i.name,
                    webkitdirectory: i.hasAttribute('webkitdirectory'),
                    accept: i.accept,
                    multiple: i.multiple,
                    hidden: i.hidden || i.style.display === 'none' || i.type === 'hidden',
                })));
            }
            """);
        _output.WriteLine($"=== INPUTS ===\n{inputInfoJson}");

        // Check for buttons
        var buttonInfoJson = await page.EvaluateAsync<string>("""
            () => {
                const btns = [...document.querySelectorAll('button, [role="button"], .btn, [class*="button"]')];
                return JSON.stringify(btns.slice(0, 30).map(b => ({
                    tag: b.tagName,
                    text: b.innerText?.trim()?.substring(0, 50),
                    class: b.className?.substring(0, 60),
                })));
            }
            """);
        _output.WriteLine($"=== BUTTONS ===\n{buttonInfoJson}");

        // Page body text
        var bodyText = await page.EvaluateAsync<string>("() => document.body.innerText.substring(0, 3000)");
        _output.WriteLine($"=== BODY TEXT ===\n{bodyText}");
    }

    /// <summary>Probe editor DOM to understand Add Force and entry selection structure.</summary>
    [Fact]
    public async Task ProbeEditorDom()
    {
        Assert.SkipWhen(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new ProtocolGameSystem
        {
            Id = "probe-gs",
            Name = "Probe System",
            ForceEntries =
            [
                new ProtocolForceEntry { Id = "fe-1", Name = "Test Force" },
                new ProtocolForceEntry { Id = "fe-2", Name = "Second Force" },
            ],
            CostTypes = [new ProtocolCostType { Id = "pts-ct", Name = "pts" }],
        };
        var cat = new ProtocolCatalogue
        {
            Id = "probe-cat",
            Name = "Probe Cat",
            GameSystemId = "probe-gs",
            SelectionEntries =
            [
                new ProtocolSelectionEntry { Id = "se-1", Name = "Test Unit", Type = "unit" },
                new ProtocolSelectionEntry { Id = "se-2", Name = "Test Model", Type = "model" },
            ],
        };

        var engine = _fixture.Engine!;
        engine.Setup(gs, [cat]);
        var page = engine.Browser.Page;

        _output.WriteLine($"URL: {page.Url}");

        // Dump full body text to understand the initial editor state
        var bodyText = await page.EvaluateAsync<string>("() => document.body.innerText.substring(0, 4000)");
        _output.WriteLine($"=== INITIAL BODY TEXT ===\n{bodyText}");

        // Get all buttons
        var buttons = await page.EvaluateAsync<string>("""
            () => {
                const btns = [...document.querySelectorAll('button, [role="button"]')];
                return JSON.stringify(btns.map(b => ({
                    text: b.innerText?.trim()?.substring(0, 80),
                    class: b.className?.substring(0, 80),
                    id: b.id,
                })));
            }
            """);
        _output.WriteLine($"=== BUTTONS ===\n{buttons}");

        // Get all clickable elements
        var links = await page.EvaluateAsync<string>("""
            () => {
                const items = [...document.querySelectorAll('a, [onclick], .clickable, .link, [class*="add"], [class*="force"]')];
                return JSON.stringify(items.slice(0, 40).map(i => ({
                    tag: i.tagName,
                    text: i.innerText?.trim()?.substring(0, 60),
                    class: i.className?.substring(0, 80),
                    href: i.href,
                })));
            }
            """);
        _output.WriteLine($"=== LINKS/CLICKABLE ===\n{links}");
    }

    /// <summary>Probe what happens after clicking Add Force.</summary>
    [Fact]
    public async Task ProbeAddForceClick()
    {
        Assert.SkipWhen(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new ProtocolGameSystem
        {
            Id = "probe-gs",
            Name = "Probe System",
            ForceEntries =
            [
                new ProtocolForceEntry { Id = "fe-1", Name = "Test Force" },
                new ProtocolForceEntry { Id = "fe-2", Name = "Second Force" },
            ],
            CostTypes = [new ProtocolCostType { Id = "pts-ct", Name = "pts" }],
        };
        var cat = new ProtocolCatalogue
        {
            Id = "probe-cat",
            Name = "Probe Cat",
            GameSystemId = "probe-gs",
            SelectionEntries =
            [
                new ProtocolSelectionEntry { Id = "se-1", Name = "Test Unit", Type = "unit" },
            ],
        };

        var engine = _fixture.Engine!;
        engine.Setup(gs, [cat]);
        var page = engine.Browser.Page;

        // Click "Add Force"
        await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Add Force" }).ClickAsync();
        await page.WaitForTimeoutAsync(1000);

        var bodyText = await page.EvaluateAsync<string>("() => document.body.innerText.substring(0, 3000)");
        _output.WriteLine($"=== AFTER ADD FORCE CLICK ===\n{bodyText}");

        var buttons = await page.EvaluateAsync<string>("""
            () => JSON.stringify([...document.querySelectorAll('button, [role="button"]')].map(b => ({
                text: b.innerText?.trim()?.substring(0, 60),
                class: b.className?.substring(0, 80),
            })))
            """);
        _output.WriteLine($"=== BUTTONS AFTER CLICK ===\n{buttons}");

        var allElements = await page.EvaluateAsync<string>("""
            () => {
                const els = [...document.querySelectorAll('.modal, .popup, .dropdown, .picker, [class*="force"], [class*="select"], [class*="choice"], [class*="option"]')];
                return JSON.stringify(els.slice(0, 30).map(e => ({
                    tag: e.tagName, class: e.className?.substring(0, 80),
                    text: e.innerText?.trim()?.substring(0, 80),
                })));
            }
            """);
        _output.WriteLine($"=== MODAL/PICKER ELEMENTS ===\n{allElements}");
    }

    /// <summary>Probe force row DOM attributes to find entry ID mapping.</summary>
    [Fact]
    public async Task ProbeForceRowAttributes()
    {
        Assert.SkipWhen(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new ProtocolGameSystem
        {
            Id = "probe-gs",
            Name = "Probe System",
            ForceEntries =
            [
                new ProtocolForceEntry { Id = "fe-1", Name = "Test Force" },
                new ProtocolForceEntry { Id = "fe-2", Name = "Second Force" },
            ],
            CostTypes = [new ProtocolCostType { Id = "pts-ct", Name = "pts" }],
        };
        var cat = new ProtocolCatalogue { Id = "probe-cat", Name = "Probe Cat", GameSystemId = "probe-gs" };

        var engine = _fixture.Engine!;
        engine.Setup(gs, [cat]);
        var page = engine.Browser.Page;

        await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Add Force" }).ClickAsync();
        await page.WaitForTimeoutAsync(500);

        // Dump HTML of the forces panel
        var forcePanelHtml = await page.EvaluateAsync<string>("""
            () => {
                const forces = document.querySelector('.forces');
                if (!forces) return 'no .forces div found';
                return forces.outerHTML.substring(0, 3000);
            }
            """);
        _output.WriteLine($"=== FORCES PANEL HTML ===\n{forcePanelHtml}");

        // Check if force rows have data-id or similar
        var forceRowInfo = await page.EvaluateAsync<string>("""
            () => {
                const rows = [...document.querySelectorAll('.forces .unit-wrap, .forces .force')];
                return JSON.stringify(rows.map(r => ({
                    text: r.innerText?.trim(),
                    class: r.className,
                    dataset: Object.fromEntries(Object.entries(r.dataset || {})),
                    outerHtml: r.outerHTML?.substring(0, 300),
                })));
            }
            """);
        _output.WriteLine($"=== FORCE ROWS ===\n{forceRowInfo}");
    }

    /// <summary>Test the full AddForce UI action.</summary>
    [Fact]
    public async Task ProbeAddForceAction()
    {
        Assert.SkipWhen(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new ProtocolGameSystem
        {
            Id = "probe-gs",
            Name = "Probe System",
            ForceEntries =
            [
                new ProtocolForceEntry { Id = "fe-1", Name = "Test Force" },
                new ProtocolForceEntry { Id = "fe-2", Name = "Second Force" },
            ],
            CostTypes = [new ProtocolCostType { Id = "pts-ct", Name = "pts" }],
        };
        var cat = new ProtocolCatalogue
        {
            Id = "probe-cat",
            Name = "Probe Cat",
            GameSystemId = "probe-gs",
            SelectionEntries =
            [
                new ProtocolSelectionEntry { Id = "se-1", Name = "Test Unit", Type = "unit" },
            ],
        };

        var engine = _fixture.Engine!;
        engine.Setup(gs, [cat]);

        // AddForce via UI
        var result = engine.AddForce("fe-1", "probe-cat");
        _output.WriteLine($"AddForce result: ForceId={result.ForceId}");

        var state = engine.GetRosterState();
        _output.WriteLine($"Forces: {state.Forces.Count}");
        foreach (var f in state.Forces)
        {
            _output.WriteLine($"  Force: {f.Name}, uid={f.Id}");
        }

        Assert.NotEmpty(result.ForceId ?? "");
        Assert.Single(state.Forces);
    }

    /// <summary>Probe SelectEntry UI — what does the force catalog look like?</summary>
    [Fact]
    public async Task ProbeSelectEntryDom()
    {
        Assert.SkipWhen(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new ProtocolGameSystem
        {
            Id = "probe-gs",
            Name = "Probe System",
            ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Test Force" }],
            CostTypes = [new ProtocolCostType { Id = "pts-ct", Name = "pts" }],
        };
        var cat = new ProtocolCatalogue
        {
            Id = "probe-cat",
            Name = "Probe Cat",
            GameSystemId = "probe-gs",
            SelectionEntries =
            [
                new ProtocolSelectionEntry { Id = "se-1", Name = "Test Unit", Type = "unit" },
                new ProtocolSelectionEntry { Id = "se-2", Name = "Test Model", Type = "model" },
            ],
        };

        var engine = _fixture.Engine!;
        engine.Setup(gs, [cat]);
        engine.AddForce("fe-1", "probe-cat");

        var page = engine.Browser.Page;

        // Dump body text after force is added
        var bodyText = await page.EvaluateAsync<string>("() => document.body.innerText.substring(0, 4000)");
        _output.WriteLine($"=== BODY AFTER ADDFORCE ===\n{bodyText}");

        // Dump HTML structure of the force/catalog area
        var forceAreaHtml = await page.EvaluateAsync<string>("""
            () => {
                // Look for the entry catalog / add-entry panel
                const candidates = [
                    document.querySelector('.unitEntries'),
                    document.querySelector('.catalog'),
                    document.querySelector('.entries'),
                    document.querySelector('[class*="catalog"]'),
                    document.querySelector('[class*="entry"]'),
                    document.querySelector('.addUnit'),
                ];
                const el = candidates.find(c => c != null);
                return el ? el.outerHTML.substring(0, 3000) : 'not found - body: ' + document.body.innerHTML.substring(0, 2000);
            }
            """);
        _output.WriteLine($"=== CATALOG/ENTRY AREA ===\n{forceAreaHtml}");

        // Dump all div elements with classes related to "unit" or "add"
        var unitDivs = await page.EvaluateAsync<string>("""
            () => {
                const divs = [...document.querySelectorAll('div')].filter(d =>
                    d.className && (d.className.includes('unit') || d.className.includes('add') || d.className.includes('entry') || d.className.includes('catalog')));
                return JSON.stringify(divs.slice(0, 20).map(d => ({
                    class: d.className?.substring(0, 80),
                    text: d.innerText?.trim()?.substring(0, 80),
                })));
            }
            """);
        _output.WriteLine($"=== UNIT/ADD DIVS ===\n{unitDivs}");
    }

    /// <summary>Test SelectEntry UI action.</summary>
    [Fact]
    public async Task ProbeSelectEntryAction()
    {
        Assert.SkipWhen(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new ProtocolGameSystem
        {
            Id = "probe-gs",
            Name = "Probe System",
            ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Test Force" }],
            CostTypes = [new ProtocolCostType { Id = "pts-ct", Name = "pts" }],
        };
        var cat = new ProtocolCatalogue
        {
            Id = "probe-cat",
            Name = "Probe Cat",
            GameSystemId = "probe-gs",
            SelectionEntries =
            [
                new ProtocolSelectionEntry { Id = "se-1", Name = "Test Unit", Type = "unit" },
                new ProtocolSelectionEntry { Id = "se-2", Name = "Test Model", Type = "model" },
            ],
        };

        var engine = _fixture.Engine!;
        engine.Setup(gs, [cat]);

        var addForceResult = engine.AddForce("fe-1", "probe-cat");
        var forceId = addForceResult.ForceId!;
        _output.WriteLine($"Force uid: {forceId}");

        var selectResult = engine.SelectEntry(forceId, "se-1");
        _output.WriteLine($"SelectEntry result: SelectionId={selectResult.SelectionId}");

        var state = engine.GetRosterState();
        _output.WriteLine($"Forces: {state.Forces.Count}");
        foreach (var f in state.Forces)
        {
            _output.WriteLine($"  Force: {f.Name}");
            foreach (var s in f.Selections)
            {
                _output.WriteLine($"    Selection: {s.Name} uid={s.Id}");
            }
        }

        Assert.NotEmpty(selectResult.SelectionId ?? "");
        Assert.Single(state.Forces[0].Selections);
    }

    [Fact]
    public async Task ProbeEditorLoads()
    {
        Assert.SkipWhen(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new ProtocolGameSystem
        {
            Id = "probe-gs",
            Name = "Probe System",
            ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Test Force" }],
            CostTypes = [new ProtocolCostType { Id = "pts-ct", Name = "pts" }],
        };
        var cat = new ProtocolCatalogue
        {
            Id = "probe-cat",
            Name = "Probe Cat",
            GameSystemId = "probe-gs",
            SelectionEntries =
            [
                new ProtocolSelectionEntry { Id = "se-1", Name = "Test Unit", Type = "unit" },
            ],
        };

        var engine = _fixture.Engine!;
        var errors = engine.Setup(gs, [cat]);
        _output.WriteLine($"Setup errors: {string.Join(", ", errors)}");

        var page = engine.Browser.Page;

        // Verify army sync
        var armyCheck = await page.EvaluateAsync<string>("""
            () => JSON.stringify({
                hasArmy: window.__bsspec?.army != null,
                armyName: window.__bsspec?.army?.getName?.(),
                hasBook: window.__bsspec?.book != null,
            })
            """);
        _output.WriteLine($"Army check: {armyCheck}");

        // Capture page URL and title
        _output.WriteLine($"Page URL: {page.Url}");
        _output.WriteLine($"Page title: {await page.TitleAsync()}");

        // Check for visible UI elements
        var bodyText = await page.EvaluateAsync<string>("() => document.body.innerText.substring(0, 2000)");
        _output.WriteLine($"=== BODY TEXT (first 2000 chars) ===");
        _output.WriteLine(bodyText);
    }
}
