using BattleScribeSpec.NewRecruit;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs declarative YAML spec files against a frozen New Recruit snapshot (HAR replay).
/// 
/// Unlike <see cref="NewRecruitConformanceTests"/> which tests the live website,
/// this runs against a pre-recorded HAR snapshot — fully offline and deterministic.
/// HAR snapshots are published as GitHub Releases in WarHub/newrecruit-har
/// and downloaded into .testdata/newrecruit-har/ before running.
///
/// Tests are skipped if:
/// - The frozen HAR file doesn't exist (.testdata/newrecruit-har/newrecruit.har)
/// - NR_FROZEN_SKIP=true is set
/// - Playwright browsers are not installed
///
/// Uses the same spec-level engine expectations as live NR tests since the frozen version
/// matches the NR behavior that was baselined.
/// </summary>
[Collection("FrozenNewRecruit")]
public sealed class FrozenNewRecruitConformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly FrozenNewRecruitFixture _fixture;

    public FrozenNewRecruitConformanceTests(ITestOutputHelper output, FrozenNewRecruitFixture fixture)
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

        var expectedToFail = spec.IsExpectedToFail("newrecruit");
        _output.WriteLine($"[FROZEN] Running spec: {specName} — {spec.Description}{(expectedToFail ? " [EXPECTED FAILURE]" : "")}");

        var engine = _fixture.Engine!;
        var runner = new SpecRunner(engine, new DataSourceResolver());
        var result = runner.Run(spec);

        if (result.Passed && expectedToFail)
        {
            Assert.Fail($"[FROZEN] Spec '{specName}' was expected to fail on newrecruit but now passes! " +
                "Update the spec's engines field to remove the 'fail' expectation.");
        }

        if (!result.Passed && expectedToFail)
        {
            _output.WriteLine($"[FROZEN/EXPECTED] Spec '{specName}' failed as expected on newrecruit.");
            return;
        }

        if (!result.Passed)
        {
            var message = $"[FROZEN] Spec '{specName}' failed with {result.Failures.Count} error(s):\n" +
                string.Join("\n", result.Failures.Select((f, i) => $"  [{i + 1}] {f}"));
            _output.WriteLine(message);
            Assert.Fail(message);
        }
    }
}
