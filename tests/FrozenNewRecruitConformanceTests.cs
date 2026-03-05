using BattleScribeSpec.NewRecruit;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs declarative YAML spec files against a frozen New Recruit snapshot (HAR replay).
/// 
/// Unlike <see cref="NewRecruitConformanceTests"/> which tests the live website,
/// this runs against a committed HAR file — fully offline and deterministic.
///
/// Tests are skipped if:
/// - The frozen HAR file doesn't exist (.testdata/newrecruit-har/newrecruit.har)
/// - NR_FROZEN_SKIP=true is set
/// - Playwright browsers are not installed
///
/// Uses the same expected-failures as live NR tests since the frozen version
/// matches the NR behavior that was baselined.
/// </summary>
[Collection("FrozenNewRecruit")]
public sealed class FrozenNewRecruitConformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly FrozenNewRecruitFixture _fixture;
    private readonly ExpectedFailures? _expectedFailures;

    public FrozenNewRecruitConformanceTests(ITestOutputHelper output, FrozenNewRecruitFixture fixture)
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
    public void FrozenNewRecruitEngine(string specPath, string specName)
    {
        Skip.If(!_fixture.Available,
            "Frozen HAR file not found or NR_FROZEN_SKIP=true — skipping frozen NR tests");

        var spec = SpecLoader.Load(specPath);

        if (!spec.IsApplicableTo("newrecruit"))
        {
            _output.WriteLine($"Skipping spec: {specName} — not applicable to newrecruit engine");
            return;
        }

        _output.WriteLine($"[FROZEN] Running spec: {specName} — {spec.Description}");

        var engine = _fixture.Engine!;
        var runner = new SpecRunner(engine, new DataSourceResolver());
        var result = runner.Run(spec);

        if (!result.Passed)
        {
            var classification = _expectedFailures?.Classify(result)
                ?? SpecResultClassification.Failed;

            var message = $"[FROZEN] Spec '{specName}' failed with {result.Failures.Count} error(s):\n" +
                string.Join("\n", result.Failures.Select((f, i) => $"  [{i + 1}] {f}"));

            if (classification == SpecResultClassification.ExpectedFailure)
            {
                var entry = _expectedFailures!.GetEntry(result.SpecId);
                _output.WriteLine($"[FROZEN/EXPECTED] {message}");
                _output.WriteLine($"  Reason: {entry?.Reason}");
                return;
            }

            _output.WriteLine(message);
            Assert.Fail(message);
        }
        else if (_expectedFailures?.IsExpectedFailure(result.SpecId) == true)
        {
            _output.WriteLine($"[FROZEN/UNEXPECTED PASS] Spec '{specName}' is in expected failures but now passes!");
        }
    }
}
