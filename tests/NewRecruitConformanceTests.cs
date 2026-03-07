using BattleScribeSpec.NewRecruit;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs all declarative YAML spec files against the New Recruit web engine via Playwright.
/// Tests are skipped if the NR_ENGINE_URL environment variable is not set.
/// </summary>
[Collection("NewRecruit")]
[Trait("Category", "Conformance")]
[Trait("Engine", "NewRecruit")]
public sealed class NewRecruitConformanceTests : ConformanceTestBase
{
    private readonly NewRecruitFixture _fixture;

    public NewRecruitConformanceTests(ITestOutputHelper output, NewRecruitFixture fixture)
        : base(output)
    {
        _fixture = fixture;
    }

    protected override string EngineName => "newrecruit";

    protected override IRosterEngine? GetEngine()
    {
        Skip.If(!_fixture.Available,
            "NR_ENGINE_URL not set — skipping New Recruit conformance tests");
        return _fixture.Engine!;
    }

    [SkippableTheory]
    [MemberData(nameof(AllSpecs))]
    public void NewRecruitEngine(string specPath, string specName) => RunSpec(specPath, specName);
}
