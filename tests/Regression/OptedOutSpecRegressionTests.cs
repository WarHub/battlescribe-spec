using System.Diagnostics;
using BattleScribeSpec.GameData;
using BattleScribeSpec.Roster;
using Xunit.Sdk;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Guards that a per-spec lane reports a row it does not run as skipped, never as passed: a spec the lane's
/// engine opts out of (<c>engines: {&lt;engine&gt;: skip}</c>), and a lane with no engine to drive.
/// </summary>
/// <remarks>
/// <para>
/// Both bases used to log and <c>return</c>, and xunit records a returned test as Passed. Under the strict
/// zero-tests policy a Passed row counts as executed, so a lane whose real rows all skipped still passed on
/// its opted-out ones: a filter for the since-deleted per-spec frozen NR roster class, whose engine was gated off
/// by default, selected 403 rows, skipped 401, "passed" <c>modifier/modifier-repeat-cost-mutual-reference</c> and
/// <c>customization/customization-category</c> (both <c>engines: {newrecruit: skip}</c>) and exited 0. The
/// engine-composition check would have counted the same two rows as the lane having run.
/// </para>
/// <para>
/// Mutation-checked when written: each base's skip put back to <c>return</c> turns its tests here red.
/// </para>
/// </remarks>
public sealed class OptedOutSpecRegressionTests
{
    private const string RosterSpecYaml = """
        id: opted-out
        category: regression
        description: A spec the BattleScribe engine opts out of
        engines:
          battlescribe: skip

        setup:
          gameSystem:
            forceEntries:
              - id: fe-1
                name: Patrol
          catalogues:
            - id: cat-1

        steps:
          - expectedState:
              forceCount: 0
        """;

    [Fact]
    public void RosterLane_ReportsAnOptedOutSpecAsSkipped()
    {
        var skip = RunRoster(new RosterLane("battlescribe", "battlescribe", engine: null), RosterSpecYaml);
        Assert.Contains("regression/opted-out is not applicable to the battlescribe engine", skip.Message, StringComparison.Ordinal);
    }

    /// <summary>A UI lane inherits its base engine's opt-out — the same resolution as its expectations.</summary>
    [Fact]
    public void UiRosterLane_ReportsItsBaseEnginesOptOutAsSkipped()
    {
        var skip = RunRoster(new RosterLane("battlescribe-ui", "battlescribe", engine: null), RosterSpecYaml);
        Assert.Contains("not applicable to the battlescribe-ui engine (the spec says engines: {battlescribe: skip})", skip.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RosterLane_WithNoEngine_ReportsTheRowAsSkipped()
    {
        var skip = RunRoster(new RosterLane("newrecruit", "newrecruit", engine: null), RosterSpecYaml);
        Assert.Contains("no newrecruit engine on this machine", skip.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GameDataLane_ReportsAnOptedOutSpecAsSkipped()
    {
        // A corpus spec that opts newrecruit out: GameData lanes run specs by name only.
        var skip = SkipOf(() => new GameDataLane("newrecruit").Run("load/load-malformed-catalogue"));
        Assert.Contains("load/load-malformed-catalogue is not applicable to the newrecruit engine", skip.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GameDataLane_WithNoEngine_ReportsTheRowAsSkipped()
    {
        var skip = SkipOf(() => new GameDataLane("battlescribe").Run("load/load-malformed-catalogue"));
        Assert.Contains("no battlescribe engine on this machine", skip.Message, StringComparison.Ordinal);
    }

    private static SkipException RunRoster(RosterLane lane, string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"opted-out-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        try
        {
            return SkipOf(() => lane.Run(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The skip <paramref name="row"/> ends in. Caught by hand: xunit's <c>Assert.Throws</c> lets a skip escape on
    /// purpose, which would report this test itself as skipped rather than check the row.
    /// </summary>
    private static SkipException SkipOf(Action row)
    {
        try
        {
            row();
        }
        catch (SkipException skip)
        {
            return skip;
        }

        Assert.Fail("The row returned, which xunit records as Passed; a row that drives no engine must skip.");
        throw new UnreachableException();
    }

    /// <summary>A roster lane under a chosen identity whose engine is gated off (<see cref="GetEngine"/> returns none).</summary>
    private sealed class RosterLane(string engineName, string baseEngineName, IRosterEngine? engine)
        : ConformanceTestBase(TestContext.Current.TestOutputHelper!)
    {
        protected override string EngineName => engineName;

        protected override string BaseEngineName => baseEngineName;

        protected override IRosterEngine? GetEngine() => engine;

        public void Run(string specPath) => RunSpec(specPath, "regression/opted-out");
    }

    /// <summary>A GameData lane under a chosen identity, with no engine.</summary>
    private sealed class GameDataLane(string engineName) : GameDataConformanceTestBase(TestContext.Current.TestOutputHelper!)
    {
        protected override string EngineName => engineName;

        protected override IGameDataEngine? GetEngine() => null;

        public void Run(string specName) => RunSpec(specName);
    }
}
