using System.Text.Json;
using System.Text.Json.Nodes;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.XmlGen;
using Microsoft.Playwright;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Records NR website traffic to a HAR file for frozen (offline) testing.
/// Navigates through the NR app to capture all assets needed by the adapter.
/// </summary>
public static class HarRecorder
{
    /// <summary>
    /// Domains to keep in the HAR file. Everything else is stripped.
    /// </summary>
    private static readonly string[] AllowedDomains =
    [
        "newrecruit.eu",
        "www.newrecruit.eu",
        "raw.githubusercontent.com",
        "fonts.googleapis.com",
        "fonts.gstatic.com",
    ];

    /// <summary>
    /// Records a HAR file by navigating the NR web app and capturing all network traffic.
    /// Post-processes to strip ad/tracker domains.
    /// </summary>
    public static async Task RecordAsync(
        string harFilePath,
        string? metadataFilePath = null,
        string baseUrl = "https://www.newrecruit.eu",
        bool headless = true)
    {
        var harDir = Path.GetDirectoryName(harFilePath);
        if (harDir is not null)
        {
            Directory.CreateDirectory(harDir);
        }

        // Recording owns its own browser and must be fully disposed before verification replays the
        // file it wrote.
        await RecordTrafficAsync(harFilePath, baseUrl, headless);

        // Post-process: strip non-essential entries (ads, trackers)
        await StripNonEssentialEntriesAsync(harFilePath);

        // The snapshot is only worth publishing if it can actually serve the adapter offline.
        var verifyError = await VerifyFrozenSetupAsync(harFilePath, baseUrl, headless);
        if (verifyError is not null)
        {
            throw new InvalidOperationException(
                $"Recorded HAR cannot serve the frozen adapter's setup path: {verifyError}. "
                + "The snapshot is incomplete — publishing it would turn every frozen NR spec red. "
                + "See docs/frozen-nr-testing.md.");
        }

        var clientVersion = ExtractClientVersion(harFilePath);

        if (metadataFilePath is not null)
        {
            var metadata = new HarMetadata
            {
                FrozenAt = DateTimeOffset.UtcNow,
                SourceUrl = baseUrl,
                ClientVersion = clientVersion,
                Notes = "Recorded by HarRecorder for offline testing. Non-essential domains stripped.",
            };
            var json = JsonSerializer.Serialize(metadata, HarMetadataContext.Default.HarMetadata);
            await File.WriteAllTextAsync(metadataFilePath, json);
        }
    }

    /// <summary>
    /// Drives the NR app and writes the raw HAR. Split out from <see cref="RecordAsync"/> so the
    /// recording browser is disposed — and the HAR file flushed — before anything reads it back.
    /// </summary>
    private static async Task RecordTrafficAsync(string harFilePath, string baseUrl, bool headless)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
        });

        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            RecordHarPath = harFilePath,
            RecordHarMode = HarMode.Minimal,
        });

        var page = await context.NewPageAsync();

        // Navigate to landing page — captures HTML + JS/CSS bundles.
        // Use 'Load' instead of 'NetworkIdle' because NR's analytics/ad
        // scripts may keep connections open indefinitely, causing timeouts.
        await page.GotoAsync(baseUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 60_000,
        });
        await WaitForNetworkSettledAsync(page);

        // Dismiss consent dialog if present
        try
        {
            var consentButton = page.GetByRole(AriaRole.Button, new() { Name = "Do not consent" });
            if (await consentButton.IsVisibleAsync())
            {
                await consentButton.ClickAsync();
                await consentButton.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 2_000 });
            }
        }
        catch { /* consent dialog may not appear */ }

        // Navigate to /app — captures app-specific assets
        await page.GotoAsync($"{baseUrl.TrimEnd('/')}/app", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 60_000,
        });
        await WaitForNetworkSettledAsync(page);

        // Wait for NR's Vue/Nuxt app to fully initialize (Pinia stores available)
        try
        {
            await page.WaitForFunctionAsync(
                "() => !!document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia",
                null,
                new() { Timeout = 15_000 });
        }
        catch (TimeoutException) { /* app may have changed structure; continue */ }

        // Load a synthetic system through the store, exactly as the adapters do.
        //
        // Every other hop below is a *UI* journey, and the file-loading path has no UI journey that
        // reaches it: NR imports game data from GitHub or a directory picker, neither of which a
        // recorder can drive. So whichever chunks that path pulls were only ever captured by luck —
        // whenever NR happened to bundle them into the eager entry chunk.
        //
        // v35.76 stopped: it split the XML parser out to a chunk fetched on first parse
        // (`import("./<hash>.js")` behind `XMLParser`), the recording never parsed anything, the
        // chunk never entered the snapshot, and offline replay aborted the import. Every frozen NR
        // spec failed at setup with "System not found in localLibrary after load" — the parse threw
        // inside NR's own try/catch, so `loadSystemFromFs` returned an empty array rather than
        // surfacing the missing module.
        //
        // Recording the same call the tests make keeps that class of split captured by construction
        // instead of by luck.
        try
        {
            var (gstXml, catXml) = BuildWarmupData();
            var warmupError = await page.EvaluateAsync<string?>(
                WarmupSetupJs, new[] { gstXml, catXml, WarmupSystemId });
            if (warmupError is not null)
            {
                throw new InvalidOperationException(
                    $"Could not load a system into the live NR app while recording: {warmupError}. "
                    + "The chunks the adapter's setup path needs would be missing from the snapshot.");
            }
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException(
                "Could not load a system into the live NR app while recording — NR's systemsStore "
                + $"API may have changed: {ex.Message}", ex);
        }

        // The imports the load kicked off are fetched after the call returns; settle before moving on.
        await WaitForNetworkSettledAsync(page, timeoutMs: 10_000);

        // Navigate through pages the UI driver uses to capture lazy-loaded chunks:
        // MySystems page (loads MySystems component + CSS).
        //
        // Routes, not nav controls: this used to click `a[href*='MySystems']` guarded by
        // IsVisibleAsync, so when NR dropped that "Home" link from the navbar (gone by client v35.12)
        // the hop silently became a no-op — a recorder that stops visiting a page it believes it is
        // visiting is a snapshot with a hole in it. Pushing the route says what we mean and keeps
        // saying it across NR's navbar restyles.
        try
        {
            await NewRecruitBrowser.PushRouteAsync(page, "/app/MySystems");
            await WaitForNetworkSettledAsync(page, timeoutMs: 5_000);
        }
        catch { /* page structure may vary */ }

        // Open "Add More Games" popup to capture its component chunk
        try
        {
            var addMoreGames = page.GetByText("Add more games");
            if (await addMoreGames.IsVisibleAsync())
            {
                await addMoreGames.ClickAsync();
                await WaitForNetworkSettledAsync(page, timeoutMs: 5_000);
                // Close the popup
                var closeBtn = page.Locator(".xCross").First;
                if (await closeBtn.IsVisibleAsync())
                {
                    await closeBtn.ClickAsync();
                }
            }
        }
        catch { /* may not appear in all versions */ }

        // Every route the adapters push, each its own chunk. This replaced an `a[href*='Lists']`
        // click guarded by IsVisibleAsync, and on v35.76 that hop no longer reached the page:
        // recording without it captured neither the Lists route chunk nor anything the editor mounts
        // (44 entries' worth, `TableList` and `UnitEditor` among them), and 3 specs — export,
        // roundtrip-reload, kitchen-sink — failed on the missing import while the recorder reported
        // success. Whether the link moved or the click landed elsewhere, a guarded click cannot say;
        // the route can.
        //
        // Without a resolvable list key the editor bounces to the lists index, so this has to run
        // after the warm-up rather than instead of it.
        try
        {
            await WalkDriverRoutesAsync(page, settleMs: 10_000);
        }
        catch { /* a route may bounce; resolving it is what pulls the chunk */ }

        // Open the Create List dialog the UI driver uses
        try
        {
            var newLink = page.Locator("a[href='#']", new() { HasTextString = "New" });
            if (await newLink.IsVisibleAsync())
            {
                await newLink.ClickAsync();
                await WaitForNetworkSettledAsync(page, timeoutMs: 5_000);
                // Close dialog
                var closeBtn = page.Locator(".xCross").First;
                if (await closeBtn.IsVisibleAsync())
                {
                    await closeBtn.ClickAsync();
                }
            }
        }
        catch { /* Lists page may require data; continue */ }

        // Close context to finalize the HAR file
        await context.CloseAsync();
    }

    /// <summary>
    /// Replays <paramref name="harFilePath"/> offline through the same
    /// <see cref="NewRecruitBrowser.CreateFrozenAsync"/> path the frozen suites use, and runs the
    /// adapter's setup sequence against it. Returns null when the snapshot serves it, or the first
    /// failing step otherwise.
    /// </summary>
    /// <remarks>
    /// This is the check the recorder was missing. A recording is a guess about which chunks NR
    /// loads eagerly; only a replay proves the guess. Running it against the real frozen browser —
    /// same HAR route, same abort-on-miss fallback — means a hole cannot pass here and fail in CI.
    /// </remarks>
    public static async Task<string?> VerifyFrozenSetupAsync(
        string harFilePath,
        string baseUrl = "https://www.newrecruit.eu",
        bool headless = true)
    {
        await using var browser = await NewRecruitBrowser.CreateFrozenAsync(harFilePath, baseUrl, headless);

        // A missing chunk is not an exception the app rethrows — NR catches parse failures, and Vue
        // Router swallows a failed route component — so watch for the browser's own report instead
        // of inferring it from whichever symptom happens to surface downstream. Aborted *prefetches*
        // never produce this: only code that awaited the import does.
        var missingModules = new List<string>();
        browser.Page.Console += (_, message) =>
        {
            if (message.Text.Contains("Failed to fetch dynamically imported module", StringComparison.Ordinal))
            {
                lock (missingModules)
                {
                    missingModules.Add(message.Text);
                }
            }
        };

        await browser.WaitForPiniaAsync();
        var (gstXml, catXml) = BuildWarmupData();
        var setupError = await browser.Page.EvaluateAsync<string?>(
            WarmupSetupJs, new[] { gstXml, catXml, WarmupSystemId });
        if (setupError is not null)
        {
            return setupError;
        }

        await WalkDriverRoutesAsync(browser.Page, settleMs: 5_000);

        lock (missingModules)
        {
            return missingModules.Count == 0
                ? null
                : "chunks the snapshot does not contain — "
                    + string.Join("; ", missingModules.Distinct(StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// Every route the adapters push, in the order they push them. Each is its own lazily-imported
    /// component, so a route missing from the snapshot is a route the drivers cannot reach.
    /// </summary>
    /// <remarks>
    /// The whole list rather than the one route a given failure named, because they fail the same
    /// way and only one of them had been noticed. <c>/app/MyLists</c> is the worst of them: when its
    /// import is aborted Vue Router cannot complete the navigation, the app falls back to
    /// <c>/</c> — losing the loaded system, the selection and the roster — and the next thing the
    /// driver looks at is simply the wrong page, with no error naming the missing chunk. That
    /// surfaced as a create-list dialog offering Age of Sigmar factions to a spec whose system had
    /// been loaded and selected correctly moments earlier.
    /// </remarks>
    private static IEnumerable<string> DriverRoutes(string? listKey)
    {
        yield return "/app/MySystems";
        yield return "/app/MyLists";
        if (listKey is not null)
        {
            yield return $"/app/Lists/{listKey}";
        }
    }

    /// <summary>
    /// Walks <see cref="DriverRoutes"/> on <paramref name="page"/>, settling after each so the
    /// chunks the route pulls are fetched.
    /// </summary>
    private static async Task WalkDriverRoutesAsync(IPage page, int settleMs)
    {
        var listKey = await page.EvaluateAsync<string?>("window.__bsspecHarWarmup?.listKey");
        foreach (var route in DriverRoutes(listKey))
        {
            await NewRecruitBrowser.PushRouteAsync(page, route);
            await WaitForNetworkSettledAsync(page, timeoutMs: settleMs);
        }
    }

    private const string WarmupSystemId = "har-warmup-gs";

    /// <summary>
    /// The adapter's setup sequence, reduced to the calls that pull code: parse files, register the
    /// system, open a book, build a roster. Returns null on success, or the step that failed.
    /// </summary>
    private const string WarmupSetupJs = """
        async ([gstXml, catXml, systemId]) => {
            const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
            if (!pinia) return 'Pinia store not found';
            const sysStore = pinia._s.get('systemsStore');
            if (!sysStore) return 'systemsStore not found';

            await sysStore.loadSystemFromFs([
                { name: systemId + '.gst', path: '/har-warmup/' + systemId + '.gst', data: gstXml },
                { name: 'har-warmup-cat.cat', path: '/har-warmup/har-warmup-cat.cat', data: catXml },
            ]);

            const localSys = sysStore.localLibrary[systemId];
            if (!localSys) return 'System not found in localLibrary after load: ' + systemId;
            sysStore.selectSystem(localSys);

            const sys = sysStore._selectedSystem;
            if (!sys) return 'No selected system after selectSystem()';

            const playable = sys.books?.array?.filter(b => b.playable) || [];
            if (!playable.length) return 'No playable books for system: ' + sys.name;

            const bookData = await sys.getBook(playable[0].id);
            if (!bookData) return 'No book data for: ' + playable[0].id;

            const roster = bookData.createRoster(bookData.getCosts());
            if (!roster) return 'Failed to create roster';
            roster.setCustomName('HAR Warmup Roster');

            // A row in the lists store, so /app/Lists/<key> resolves to the editor rather than
            // bouncing to the index — the editor route is its own chunk.
            const listsStore = pinia._s.get('lists');
            if (!listsStore) return 'lists store not found';
            const book = playable[0];
            const row = {
                list_key: 'har_warmup_list',
                name: 'HAR Warmup Roster',
                id_game_system: book.id_game_system || sys.id,
                id_system: book.id || sys.id,
                nrversion: book.nrversion,
                date_mod: new Date(),
                date_create: new Date(),
                synced: false,
                uid: null,
                bsid_book: book.bsid,
                bsid_system: sys.bsid,
            };
            await listsStore.addList({ row, army: roster, book: bookData });

            window.__bsspecHarWarmup = { listKey: row.list_key };
            return null;
        }
        """;

    /// <summary>
    /// A minimal game system and catalogue, built through the same <see cref="CatXmlGenerator"/>
    /// the engines feed NR — so the recording parses the shape the specs parse, not a hand-written
    /// approximation of it that could drift.
    /// </summary>
    private static (string GstXml, string CatXml) BuildWarmupData()
    {
        var gameSystem = new ProtocolGameSystem
        {
            Id = WarmupSystemId,
            Name = "HAR Warmup",
            ForceEntries = [new ProtocolForceEntry { Id = "har-warmup-force", Name = "Warmup Force" }],
        };
        var catalogue = new ProtocolCatalogue
        {
            Id = "har-warmup-cat",
            Name = "HAR Warmup Catalogue",
            GameSystemId = gameSystem.Id,
        };
        return (
            CatXmlGenerator.GenerateGameSystemXml(gameSystem),
            CatXmlGenerator.GenerateCatalogueXml(gameSystem, catalogue));
    }

    /// <summary>
    /// Strips HAR entries from non-essential domains and deduplicates by request URL.
    /// Keeps only entries matching <see cref="AllowedDomains"/>.
    /// </summary>
    public static async Task StripNonEssentialEntriesAsync(string harFilePath)
    {
        var json = await File.ReadAllTextAsync(harFilePath);
        var doc = JsonNode.Parse(json);
        var entries = doc?["log"]?["entries"]?.AsArray();
        if (entries is null)
        {
            return;
        }

        // First pass: mark indices to keep (allowed domain + dedup GETs by URL)
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keep = new HashSet<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            var url = entries[i]?["request"]?["url"]?.GetValue<string>();
            var method = entries[i]?["request"]?["method"]?.GetValue<string>() ?? "GET";
            if (url is null || !IsAllowedUrl(url))
            {
                continue;
            }
            // Keep all POSTs with unique bodies (may have different responses); dedup GETs/HEADs
            if (method is "POST" or "PUT" or "PATCH")
            {
                var body = entries[i]?["request"]?["postData"]?["text"]?.GetValue<string>() ?? "";
                if (seen.Add($"{method} {url} {body}"))
                {
                    keep.Add(i);
                }
            }
            else if (seen.Add($"{method} {url}"))
            {
                keep.Add(i);
            }
        }

        // Second pass: remove non-kept entries in reverse
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(i))
            {
                entries.RemoveAt(i);
            }
        }

        var options = new JsonSerializerOptions { WriteIndented = false };
        await File.WriteAllTextAsync(harFilePath, doc!.ToJsonString(options));
    }

    /// <summary>
    /// Best-effort wait for network to settle. Uses NetworkIdle with a short
    /// timeout so we capture most traffic without hard-failing when persistent
    /// connections (analytics, WebSockets) keep the network busy.
    /// </summary>
    private static async Task WaitForNetworkSettledAsync(IPage page, int timeoutMs = 15_000)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = timeoutMs });
        }
        catch (TimeoutException)
        {
            // Expected when the site has persistent connections (ads, analytics, SSE).
            // Traffic recorded up to this point is sufficient.
        }
    }

    private static bool IsAllowedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        foreach (var domain in AllowedDomains)
        {
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Extracts the NR clientVersion from the HAR's HTML responses.
    /// Looks for <c>clientVersion:"X.Y"</c> in Nuxt's __NUXT_CONFIG__.
    /// </summary>
    public static string? ExtractClientVersion(string harFilePath)
    {
        var json = File.ReadAllText(harFilePath);
        var doc = JsonNode.Parse(json);
        var entries = doc?["log"]?["entries"]?.AsArray();
        if (entries is null)
        {
            return null;
        }

        foreach (var entry in entries)
        {
            var text = entry?["response"]?["content"]?["text"]?.GetValue<string>();
            if (text is null)
            {
                continue;
            }

            var match = System.Text.RegularExpressions.Regex.Match(text, @"clientVersion:""([^""]+)""");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        return null;
    }

    /// <summary>
    /// Finds the frozen HAR file by searching upward from the given directory.
    /// </summary>
    public static string? FindFrozenHarFile(string? startDir = null)
    {
        var dir = startDir ?? Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, ".testdata", "newrecruit-har", "newrecruit.har");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}

public record HarMetadata
{
    public DateTimeOffset FrozenAt { get; init; }
    public string SourceUrl { get; init; } = "";
    public string? ClientVersion { get; init; }
    public string Notes { get; init; } = "";
}

[System.Text.Json.Serialization.JsonSerializable(typeof(HarMetadata))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class HarMetadataContext : System.Text.Json.Serialization.JsonSerializerContext;
