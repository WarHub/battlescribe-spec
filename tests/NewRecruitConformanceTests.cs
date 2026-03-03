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
/// Expected failures: Specs listed in specs/expected-failures/newrecruit.json
/// are still run but don't fail the suite. This allows tracking conformance progress.
/// </summary>
[Collection("NewRecruit")]
public sealed class NewRecruitConformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly NewRecruitFixture _fixture;
    private readonly ExpectedFailures? _expectedFailures;

    public NewRecruitConformanceTests(ITestOutputHelper output, NewRecruitFixture fixture)
    {
        _output = output;
        _fixture = fixture;
        _expectedFailures = fixture.Available ? ExpectedFailures.Load("newrecruit") : null;
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

        _output.WriteLine($"Running spec: {specName} — {spec.Description}");

        var engine = _fixture.Engine!;
        var runner = new SpecRunner(engine, new DataSourceResolver());
        var result = runner.Run(spec);

        if (!result.Passed)
        {
            var classification = _expectedFailures?.Classify(result)
                ?? SpecResultClassification.Failed;

            var message = $"Spec '{specName}' failed with {result.Failures.Count} error(s):\n" +
                string.Join("\n", result.Failures.Select((f, i) => $"  [{i + 1}] {f}"));

            if (classification == SpecResultClassification.ExpectedFailure)
            {
                var entry = _expectedFailures!.GetEntry(result.SpecId);
                _output.WriteLine($"[EXPECTED FAILURE] {message}");
                _output.WriteLine($"  Reason: {entry?.Reason}");
                return; // Don't fail the test
            }

            _output.WriteLine(message);
            Assert.Fail(message);
        }
        else if (_expectedFailures?.IsExpectedFailure(result.SpecId) == true)
        {
            _output.WriteLine($"[UNEXPECTED PASS] Spec '{specName}' is in expected failures but now passes! " +
                "Consider removing it from specs/expected-failures/newrecruit.json");
        }
    }
}
