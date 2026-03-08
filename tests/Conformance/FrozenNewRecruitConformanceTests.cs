using BattleScribeSpec.NewRecruit;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs declarative YAML spec files against a frozen New Recruit snapshot (HAR replay).
/// Fully offline and deterministic. Skipped when the HAR file doesn't exist or NR_FROZEN_SKIP=true.
/// </summary>
[Collection("FrozenNewRecruit")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNewRecruit")]
public sealed class FrozenNewRecruitConformanceTests : ConformanceTestBase
{
    private readonly FrozenNewRecruitFixture _fixture;

    public FrozenNewRecruitConformanceTests(ITestOutputHelper output, FrozenNewRecruitFixture fixture)
        : base(output)
    {
        _fixture = fixture;
    }

    protected override string EngineName => "newrecruit";
    protected override string LogPrefix => "[FROZEN] ";

    protected override IRosterEngine? GetEngine()
    {
        Skip.If(!_fixture.Available,
            "Frozen HAR file not found or NR_FROZEN_SKIP=true — skipping frozen NR tests");
        return _fixture.Engine!;
    }

    [SkippableTheory]
    [MemberData(nameof(AllSpecs))]
    public void FrozenNewRecruitEngine(string specPath, string specName) => RunSpec(specPath, specName);
}
