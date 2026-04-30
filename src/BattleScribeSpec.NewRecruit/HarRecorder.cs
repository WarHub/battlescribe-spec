using System.Text.Json;
using System.Text.Json.Nodes;
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
        string baseUrl = "https://newrecruit.eu",
        bool headless = true)
    {
        var harDir = Path.GetDirectoryName(harFilePath);
        if (harDir is not null)
        {
            Directory.CreateDirectory(harDir);
        }

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

        // Close context to finalize the HAR file
        await context.CloseAsync();

        // Post-process: strip non-essential entries (ads, trackers)
        await StripNonEssentialEntriesAsync(harFilePath);

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
