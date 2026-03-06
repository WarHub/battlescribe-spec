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
///
/// Expected failures are encoded in each spec's `engines` YAML field.
/// Specs with `engines: {newrecruit: fail}` are expected to fail on NR.
/// If an expected failure suddenly passes, the test FAILS (behavior change detected).
/// </summary>
[Collection("NewRecruit")]
public sealed class NewRecruitConformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly NewRecruitFixture _fixture;

    public NewRecruitConformanceTests(ITestOutputHelper output, NewRecruitFixture fixture)
    {
        _output = output;
        _fixture = fixture;
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

    [SkippableTheory]
    [MemberData(nameof(AllSpecs))]
    public void NewRecruitEngine(string specPath, string specName)
    {
        Skip.If(!_fixture.Available,
            "NR_ENGINE_URL not set — skipping New Recruit conformance tests");

        var spec = SpecLoader.Load(specPath);

        // Skip specs not applicable to the New Recruit engine
        if (!spec.IsApplicableTo("newrecruit"))
        {
            _output.WriteLine($"Skipping spec: {specName} — not applicable to newrecruit engine");
            return;
        }

        var expectedToFail = spec.IsExpectedToFail("newrecruit");
        _output.WriteLine($"Running spec: {specName} — {spec.Description}{(expectedToFail ? " [EXPECTED FAILURE]" : "")}");

        var engine = _fixture.Engine!;
        var runner = new SpecRunner(engine, new DataSourceResolver());
        var result = runner.Run(spec);

        if (result.Passed && expectedToFail)
        {
            Assert.Fail($"Spec '{specName}' was expected to fail on newrecruit but now passes! " +
                "Update the spec's engines field to remove the 'fail' expectation.");
        }

        if (!result.Passed && expectedToFail)
        {
            _output.WriteLine($"[EXPECTED FAILURE] Spec '{specName}' failed as expected on newrecruit.");
            return;
        }

        if (!result.Passed)
        {
            var message = $"Spec '{specName}' failed with {result.Failures.Count} error(s):\n" +
                string.Join("\n", result.Failures.Select((f, i) => $"  [{i + 1}] {f}"));
            _output.WriteLine(message);
            Assert.Fail(message);
        }
    }
}
