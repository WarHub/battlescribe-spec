using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests;

/// <summary>
/// The rule that decides what reaches BattleScribe's integer cost-limit spinners.
/// </summary>
/// <remarks>
/// Worth testing offline at all because the alternative is a 12-minute UI lane reporting an absent
/// validation error, several steps from a cast. And worth testing as one rule because it used to be
/// two: the New Roster path refused a fractional default while the Edit Roster path cast the same
/// kind of value to <c>int</c>, so 0.25 became 0 — a limit the game system never declared, which
/// every selection then exceeds.
/// </remarks>
public sealed class BsUiCostLimitsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(2000, 2000)]
    public void SpinnerValueFor_CarriesAWholeNumber(int limit, int expected)
        => Assert.Equal(expected, BsUiCostLimits.SpinnerValueFor(limit));

    [Fact]
    public void SpinnerValueFor_RefusesAFraction_RatherThanTruncatingIt()
    {
        // 0.25 as 0 is not a smaller limit, it is a different question: every selection is then over
        // a limit that was never declared, and the spec sees a violation rather than an absence.
        Assert.Null(BsUiCostLimits.SpinnerValueFor(0.25m));
        Assert.Null(BsUiCostLimits.SpinnerValueFor(0.5m));
        Assert.Null(BsUiCostLimits.SpinnerValueFor(1999.99m));
    }

    [Fact]
    public void SpinnerValueFor_RefusesANegativeLimit()
    {
        // Negative is how the format spells "no limit", which an untouched spinner already means.
        // Entering it would ask the spinner to clamp, and a clamped 0 is a real limit.
        Assert.Null(BsUiCostLimits.SpinnerValueFor(-1));
        Assert.Null(BsUiCostLimits.SpinnerValueFor(-9999));
    }

    [Fact]
    public void SpinnerValueFor_RefusesWhatDoesNotFitTheSpinnersType()
    {
        // Spinner<Integer>. A value past int.MaxValue has no representation there, and wrapping one
        // into a negative is the same class of invented answer as truncating a fraction.
        Assert.Null(BsUiCostLimits.SpinnerValueFor((decimal)int.MaxValue + 1));
        Assert.Equal(int.MaxValue, BsUiCostLimits.SpinnerValueFor(int.MaxValue));
    }

    [Fact]
    public void ForNewRoster_PrefersWhatTheSpecAskedFor()
    {
        var costTypes = new[] { CostType("pts", defaultCostLimit: 100) };

        Assert.Equal(
            500,
            BsUiCostLimits.ForNewRoster(new Dictionary<string, decimal> { ["pts"] = 500 }, costTypes));
    }

    [Fact]
    public void ForNewRoster_FallsBackToTheGameSystemsDefault()
    {
        // Not belt-and-braces: BattleScribe applies a costType default only to a roster created
        // through its ENGINE, so a roster built through this dialog gets no limit unless one is
        // typed in. A spec whose subject is the default then has no violation to report.
        Assert.Equal(
            100,
            BsUiCostLimits.ForNewRoster(
                new Dictionary<string, decimal>(),
                [CostType("pts", defaultCostLimit: 100)]));
    }

    [Fact]
    public void ForNewRoster_RefusesAFractionalDefault_OnTheSamePathAsAFractionalRequest()
    {
        // The pair that used to disagree. Both are the same question about the same spinner.
        Assert.Null(
            BsUiCostLimits.ForNewRoster(
                new Dictionary<string, decimal> { ["pts"] = 0.25m },
                [CostType("pts", defaultCostLimit: 0.25m)]));

        Assert.Null(
            BsUiCostLimits.ForNewRoster(
                new Dictionary<string, decimal>(),
                [CostType("pts", defaultCostLimit: 0.25m)]));
    }

    [Fact]
    public void ForNewRoster_LeavesTheSpinnerAloneWhenItCannotSayWhichTypeIsMeant()
    {
        // One spinner, several limits. Guessing which type it stands for would put a real number in
        // front of BattleScribe under the wrong name, which is worse than no number at all.
        Assert.Null(
            BsUiCostLimits.ForNewRoster(
                new Dictionary<string, decimal> { ["pts"] = 100, ["cp"] = 10 },
                [CostType("pts", 100), CostType("cp", 10)]));

        Assert.Null(
            BsUiCostLimits.ForNewRoster(
                new Dictionary<string, decimal>(),
                [CostType("pts", 100), CostType("cp", 10)]));
    }

    [Fact]
    public void ForNewRoster_IgnoresACostTypeThatDeclaresNoLimit()
    {
        // A negative or absent defaultCostLimit is not a limit, so a system with one limited type
        // and one unlimited type still has exactly one candidate.
        Assert.Equal(
            100,
            BsUiCostLimits.ForNewRoster(
                new Dictionary<string, decimal>(),
                [CostType("pts", 100), CostType("cp", -1), CostType("pl", defaultCostLimit: null)]));
    }

    [Fact]
    public void ForNewRoster_HasAnAnswerWhenThereIsNoGameSystemToAsk()
    {
        // The dataSource setup path has no Protocol objects at all. Real data carries its own
        // limits, and inventing one here would override them.
        Assert.Null(BsUiCostLimits.ForNewRoster(new Dictionary<string, decimal>(), costTypes: null));
    }

    private static ProtocolCostType CostType(string id, decimal? defaultCostLimit)
        => new() { Id = id, Name = id, DefaultCostLimit = defaultCostLimit };
}
