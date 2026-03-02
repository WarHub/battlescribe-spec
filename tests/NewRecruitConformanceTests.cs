using BattleScribeSpec.NewRecruit;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs all declarative YAML spec files against the New Recruit web engine via Playwright.
/// Same pattern as <see cref="SpecConformanceTests"/> but targeting the NR engine.
///
/// Requires Playwright browsers to be installed:
///   pwsh bin/Debug/net10.0/playwright.ps1 install chromium
///
/// Tests are skipped if the NR_ENGINE_URL environment variable is not set.
/// Set NR_ENGINE_URL=https://newrecruit.eu (or a local instance) to enable.
/// </summary>
[Collection("NewRecruit")]
public sealed class NewRecruitConformanceTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private NewRecruitRosterEngine? _engine;

    public NewRecruitConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("NR_ENGINE_URL");
        if (string.IsNullOrEmpty(baseUrl))
            return;

        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        _engine = await NewRecruitRosterEngine.CreateAsync(baseUrl, headless);
    }

    public Task DisposeAsync()
    {
        _engine?.Dispose();
        _engine = null;
        return Task.CompletedTask;
    }

    public static IEnumerable<object[]> AllSpecs()
    {
        var specsDir = SpecLoader.FindSpecsDirectory();
        if (specsDir is null || !Directory.Exists(specsDir))
            yield break;
        foreach (var (path, id, category) in SpecLoader.DiscoverSpecs(specsDir))
        {
            yield return [path, $"{category}/{id}"];
        }
    }

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void NewRecruitEngine(string specPath, string specName)
    {
        var baseUrl = Environment.GetEnvironmentVariable("NR_ENGINE_URL");
        Skip.If(string.IsNullOrEmpty(baseUrl),
            "NR_ENGINE_URL not set — skipping New Recruit conformance tests");

        var spec = SpecLoader.Load(specPath);

        // Skip specs not applicable to the New Recruit engine
        if (!spec.IsApplicableTo("newrecruit"))
        {
            _output.WriteLine($"Skipping spec: {specName} — not applicable to newrecruit engine");
            return;
        }

        _output.WriteLine($"Running spec: {specName} — {spec.Description}");

        Assert.NotNull(_engine);
        var runner = new SpecRunner(_engine);
        var result = runner.Run(spec);

        if (!result.Passed)
        {
            var message = $"Spec '{specName}' failed with {result.Failures.Count} error(s):\n" +
                string.Join("\n", result.Failures.Select((f, i) => $"  [{i + 1}] {f}"));
            _output.WriteLine(message);
            Assert.Fail(message);
        }
    }
}
