using BattleScribeSpec.NewRecruit;
using Xunit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Sequential version of frozen NR conformance tests (one spec at a time).
/// Useful for debugging individual spec failures without parallel noise.
/// Skipped by default — set NR_SEQUENTIAL=true to run.
/// </summary>
[Collection("SequentialFrozenNewRecruit")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNewRecruit")]
[Trait("Mode", "Sequential")]
public sealed class SequentialFrozenNewRecruitConformanceTests : ConformanceTestBase
{
    private readonly SequentialFrozenNewRecruitFixture _fixture;

    public SequentialFrozenNewRecruitConformanceTests(ITestOutputHelper output, SequentialFrozenNewRecruitFixture fixture)
        : base(output)
    {
        _fixture = fixture;
    }

    protected override string EngineName => "newrecruit";
    protected override string LogPrefix => "[SEQ-FROZEN] ";

    protected override IRosterEngine? GetEngine()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("NR_SEQUENTIAL") is not "true",
            "Sequential tests skipped by default — set NR_SEQUENTIAL=true to run");
        Assert.SkipWhen(!_fixture.Available,
            "Frozen HAR file not found or NR_FROZEN_SKIP=true — skipping frozen NR tests");
        return _fixture.Engine!;
    }

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void FrozenNewRecruitEngine(string specPath, string specName) => RunSpec(specPath, specName);
}
