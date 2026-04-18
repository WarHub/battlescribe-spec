using BattleScribeSpec.NewRecruit;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Sequential version of live NR conformance tests (one spec at a time).
/// Useful for debugging individual spec failures without parallel noise.
/// Skipped by default — set NR_SEQUENTIAL=true and NR_ENGINE_URL to run.
/// </summary>
[Collection("SequentialLiveNewRecruit")]
[Trait("Category", "Conformance")]
[Trait("Engine", "LiveNewRecruit")]
[Trait("Mode", "Sequential")]
public sealed class SequentialLiveNewRecruitConformanceTests : ConformanceTestBase
{
    private readonly SequentialLiveNewRecruitFixture _fixture;

    public SequentialLiveNewRecruitConformanceTests(ITestOutputHelper output, SequentialLiveNewRecruitFixture fixture)
        : base(output)
    {
        _fixture = fixture;
    }

    protected override string EngineName => "newrecruit";
    protected override string LogPrefix => "[SEQ-LIVE] ";

    protected override IRosterEngine? GetEngine()
    {
        Skip.If(Environment.GetEnvironmentVariable("NR_SEQUENTIAL") is not "true",
            "Sequential tests skipped by default — set NR_SEQUENTIAL=true to run");
        Skip.If(!_fixture.Available,
            "NR_ENGINE_URL not set — skipping live NR conformance tests");
        return _fixture.Engine!;
    }

    [SkippableTheory]
    [MemberData(nameof(AllSpecs))]
    public void LiveNewRecruitEngine(string specPath, string specName) => RunSpec(specPath, specName);
}
