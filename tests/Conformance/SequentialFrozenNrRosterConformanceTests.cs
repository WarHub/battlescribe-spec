using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Sequential version of frozen NR conformance tests (one spec at a time).
/// Useful for debugging individual spec failures without parallel noise.
/// Skipped by default — set NR_SEQUENTIAL=true to run.
/// </summary>
[Collection("SequentialFrozenNrRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrRoster")]
[Trait("Mode", "Sequential")]
public sealed class SequentialFrozenNrRosterConformanceTests : ConformanceTestBase
{
    private readonly SequentialFrozenNrRosterFixture _fixture;

    public SequentialFrozenNrRosterConformanceTests(ITestOutputHelper output, SequentialFrozenNrRosterFixture fixture)
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
    public void FrozenNrRosterEngine(string specPath, string specName) => RunSpec(specPath, specName);
}
