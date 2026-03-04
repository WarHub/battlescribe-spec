using System.Text.Json;
using Microsoft.Playwright;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Records NR website traffic to a HAR file for frozen (offline) testing.
/// Navigates through the NR app to capture all assets needed by the adapter.
/// </summary>
public static class HarRecorder
{
    /// <summary>
    /// Records a HAR file by navigating the NR web app and capturing all network traffic.
    /// </summary>
    public static async Task RecordAsync(
        string harFilePath,
        string? metadataFilePath = null,
        string baseUrl = "https://newrecruit.eu",
        bool headless = true)
    {
        var harDir = Path.GetDirectoryName(harFilePath);
        if (harDir is not null)
            Directory.CreateDirectory(harDir);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
        });

        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            RecordHarPath = harFilePath,
            RecordHarUrlFilter = $"{baseUrl.TrimEnd('/')}/**",
        });

        var page = await context.NewPageAsync();

        // Navigate to landing page — captures HTML + JS/CSS bundles
        await page.GotoAsync(baseUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 60_000,
        });

        // Dismiss consent dialog if present
        try
        {
            var consentButton = page.GetByRole(AriaRole.Button, new() { Name = "Do not consent" });
            if (await consentButton.IsVisibleAsync())
            {
                await consentButton.ClickAsync();
                await page.WaitForTimeoutAsync(1000);
            }
        }
        catch { /* consent dialog may not appear */ }

        // Navigate to /app — captures app-specific assets
        await page.GotoAsync($"{baseUrl.TrimEnd('/')}/app", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 60_000,
        });

        // Wait for NR to fully initialize
        await page.WaitForTimeoutAsync(3000);

        // Close context to finalize the HAR file
        await context.CloseAsync();

        if (metadataFilePath is not null)
        {
            var metadata = new HarMetadata
            {
                FrozenAt = DateTimeOffset.UtcNow,
                SourceUrl = baseUrl,
                Notes = "Recorded by HarRecorder for offline testing.",
            };
            var json = JsonSerializer.Serialize(metadata, HarMetadataContext.Default.HarMetadata);
            await File.WriteAllTextAsync(metadataFilePath, json);
        }
    }

    /// <summary>
    /// Finds the frozen HAR file by searching upward from the given directory.
    /// </summary>
    public static string? FindFrozenHarFile(string? startDir = null)
    {
        var dir = startDir ?? Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "frozen", "newrecruit", "newrecruit.har");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}

public record HarMetadata
{
    public DateTimeOffset FrozenAt { get; init; }
    public string SourceUrl { get; init; } = "";
    public string Notes { get; init; } = "";
}

[System.Text.Json.Serialization.JsonSerializable(typeof(HarMetadata))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class HarMetadataContext : System.Text.Json.Serialization.JsonSerializerContext;
