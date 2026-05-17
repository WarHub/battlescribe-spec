using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests;

[Collection("SequentialLiveNrRoster")]
[Trait("Category", "Integration")]
[Trait("Engine", "LiveNrRoster")]
public sealed class NrProbe
{
    private readonly ITestOutputHelper _output;
    private readonly SequentialLiveNrRosterFixture _fixture;

    public NrProbe(ITestOutputHelper output, SequentialLiveNrRosterFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    [Fact]
    public async Task ProbeListsStoreAndEditorFix()
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

        var page = _fixture.Engine!.Browser.Page;
        await _fixture.Engine.Browser.NavigateToAppAsync();
        await _fixture.Engine.Browser.WaitForPiniaAsync();

        _fixture.Engine.Setup(gs, [cat]);

        var listKey = await page.EvaluateAsync<string?>("() => window.__bsspec?.row?.list_key");
        _output.WriteLine($"listKey: {listKey}");
        if (listKey == null)
        {
            _output.WriteLine("No listKey — setup failed");
            return;
        }

        // === Step 1: Inject bookRef into listData item AND read item.id_system ===
        _output.WriteLine("\n=== Step 1: Inject bookRef, read id_system ===");
        var injectResult = await page.EvaluateAsync<string?>("""
            ([listKey]) => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                const ls = pinia?._s?.get('lists');
                const ss = pinia?._s?.get('systemsStore');
                if (!ls || !ss) return 'no stores';
                const item = ls.listData?.find?.(i => i?.list_key === listKey);
                if (!item) return 'item not found';
                const sys = ss._selectedSystem;
                const books = sys?.books?.array || sys?.books || [];
                const bookRef = books.find(b => b.bsid === item.bsid_book);
                if (!bookRef) return 'no bookRef: bsid_book=' + item.bsid_book;
                item.book = bookRef;
                return JSON.stringify({
                    id_system: item.id_system,
                    bsid_book: item.bsid_book,
                    bookRefBsid: bookRef.bsid,
                    bookRefKeys: Object.keys(bookRef).slice(0, 10),
                });
            }
            """, new object[] { listKey });
        _output.WriteLine(injectResult ?? "(null)");

        // === Navigate to editor ===
        await page.EvaluateAsync("""
            async (route) => {
                const router = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$router;
                if (router) await router.push(route);
            }
            """, $"/app/Lists/{listKey}");
        await Task.Delay(2000, TestContext.Current.CancellationToken);
        await page.EvaluateAsync("() => { document.querySelector('.fc-cta-do-not-consent')?.click(); }");
        await Task.Delay(300, TestContext.Current.CancellationToken);

        // === Step 2: Inject into treeData.systems (raw reactive state) ===
        // Hypothesis: currentList is a computed that reads treeData.systems[id_system].books[bsid_book].
        // Populating this in raw Pinia state SHOULD trigger Vue to recompute currentList.
        _output.WriteLine("\n=== Step 2: Inject into treeData.systems ===");
        var treeInjectResult = await page.EvaluateAsync<string?>("""
            async ([listKey]) => {
                const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
                const ls = pinia?._s?.get('lists');
                if (!ls) return 'no store';
                const rawState = pinia.state.value.lists;
                const item = ls.listData?.find?.(i => i?.list_key === listKey);
                const bookRef = item?.book;
                if (!item || !bookRef) return JSON.stringify({ error: 'missing', itemPresent: !!item, bookPresent: !!bookRef });

                // Check treeData structure before injection
                const td = rawState.treeData;
                const beforeKeys = Object.keys(td.systems || {});

                // Inject bookRef into treeData.systems[id_system].books[bsid_book]
                if (!td.systems) td.systems = {};
                if (!td.systems[item.id_system]) td.systems[item.id_system] = {};
                const sysEntry = td.systems[item.id_system];
                if (!sysEntry.books) sysEntry.books = {};
                sysEntry.books[item.bsid_book] = bookRef;

                // Wait for Vue to re-render
                await new Promise(r => setTimeout(r, 300));

                return JSON.stringify({
                    beforeSysKeys: beforeKeys,
                    injectedInto: `treeData.systems[${item.id_system}].books[${item.bsid_book}]`,
                    currentListBookAfter: ls.currentList?.book?.bsid ?? null,
                });
            }
            """, new object[] { listKey });
        _output.WriteLine(treeInjectResult ?? "(null)");

        await Task.Delay(1000, TestContext.Current.CancellationToken);

        // === Step 3: Check editor content ===
        _output.WriteLine($"\n=== Step 3: Editor content (URL: {page.Url}) ===");
        var mainHtml = await page.EvaluateAsync<string>("""
            () => {
                const main = document.querySelector('.mainContent') || document.body;
                const hasError = main.innerHTML.includes('could not be loaded');
                const hasAddForce = main.innerHTML.toLowerCase().includes('add force') ||
                    main.innerHTML.toLowerCase().includes('add detachment');
                const content = main.querySelector('.content');
                const contentText = content?.innerText?.trim()?.slice(0, 300);
                return JSON.stringify({
                    hasError,
                    hasAddForce,
                    contentText,
                    buttons: Array.from(main.querySelectorAll('button'))
                        .map(b => b.innerText?.trim()?.slice(0, 40)).filter(t => t),
                });
            }
            """);
        _output.WriteLine(mainHtml ?? "(empty)");
    }
}
