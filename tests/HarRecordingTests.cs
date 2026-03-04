using BattleScribeSpec.NewRecruit;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Utility test for recording a new HAR snapshot from the live NR website.
/// Run to update the frozen snapshot:
///   dotnet test tests/BattleScribeSpec.Tests.csproj --filter "HarRecordingTests" -e NR_ENGINE_URL=https://newrecruit.eu
/// </summary>
public sealed class HarRecordingTests
{
    private readonly ITestOutputHelper _output;

    public HarRecordingTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public async Task RecordFrozenSnapshot()
    {
        var baseUrl = Environment.GetEnvironmentVariable("NR_ENGINE_URL");
        Skip.If(string.IsNullOrEmpty(baseUrl),
            "NR_ENGINE_URL not set — set to NR base URL to record a HAR snapshot");

        var frozenDir = FindFrozenDirectory()
            ?? throw new InvalidOperationException(
                "Could not find 'frozen/newrecruit' directory. Run from the repo root.");

        var harPath = Path.Combine(frozenDir, "newrecruit.har");
        var metadataPath = Path.Combine(frozenDir, "metadata.json");

        _output.WriteLine($"Recording HAR from {baseUrl} to {harPath}...");

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        await HarRecorder.RecordAsync(harPath, metadataPath, baseUrl, headless);

        var harSize = new FileInfo(harPath).Length;
        _output.WriteLine($"HAR recorded: {harPath} ({harSize / 1024} KB)");
        _output.WriteLine($"Metadata: {metadataPath}");
        _output.WriteLine("Review and commit the updated files.");

        Assert.True(File.Exists(harPath), "HAR file should exist after recording");
    }

    private static string? FindFrozenDirectory()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "frozen", "newrecruit");
            if (Directory.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
