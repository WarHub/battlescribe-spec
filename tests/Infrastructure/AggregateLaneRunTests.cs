using Xunit.Sdk;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>What an aggregate lane prints and refuses (<see cref="AggregateLaneRun"/>)</b>: the machine-readable
/// selection line, one progress line per spec, and where it stopped when its run was cancelled.
/// </summary>
/// <remarks>
/// Mutation-checked when written: the empty-selection check deleted, and the stop check made a no-op, each turn
/// this red. <c>TestProfileRegistryTests.EveryAggregateLane_ReportsItsSelectionAndProgress</c> holds the lane
/// classes to calling it.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class AggregateLaneRunTests
{
    [Fact]
    public void Start_PrintsTheSelectionLine()
    {
        var output = new Recorder();
        AggregateLaneRun.Start(output, "[X] ", "FrozenNrRoster", AggregateMode.KitchenSink, selected: 1, applicable: 401);
        AggregateLaneRun.Start(output, "[X] ", "FrozenNrUiRoster", AggregateMode.OtherSpecs, selected: 399, applicable: 400);
        AggregateLaneRun.Start(output, "[X] ", "LiveNrRoster", AggregateMode.Full, selected: 401, applicable: 401);
        Assert.Equal(
            [
                "[lane] FrozenNrRoster mode=kitchen-sink selected=1 applicable=401",
                "[lane] FrozenNrUiRoster mode=other-specs selected=399 applicable=400",
                "[lane] LiveNrRoster mode=full selected=401 applicable=401",
            ],
            output.Lines);
    }

    /// <summary>
    /// <b>A test that selected no spec fails, in every mode</b> — an empty corpus included, where <c>selected</c> and
    /// <c>applicable</c> are both 0 and so agree: it would pass having driven nothing, and the engine-composition
    /// check would count that pass as the lane having run.
    /// </summary>
    [Theory]
    [InlineData(nameof(AggregateMode.Full), "full", 0, "selected no spec in full mode, and no spec in the corpus applies to it: the spec corpus (specs/) was not found")]
    [InlineData(nameof(AggregateMode.KitchenSink), "kitchen-sink", 401, "selected no spec in kitchen-sink mode of the 401 that apply to it.")]
    [InlineData(nameof(AggregateMode.OtherSpecs), "other-specs", 400, "selected no spec in other-specs mode of the 400 that apply to it.")]
    public void Start_ALaneThatSelectedNothing_Fails(string mode, string printed, int applicable, string expected)
    {
        var output = new Recorder();
        var empty = Assert.ThrowsAny<XunitException>(() =>
            AggregateLaneRun.Start(output, "[X] ", "FrozenNrRoster", Enum.Parse<AggregateMode>(mode), selected: 0, applicable: applicable));
        Assert.Contains($"[lane] FrozenNrRoster {expected}", empty.Message, StringComparison.Ordinal);
        Assert.Equal($"[lane] FrozenNrRoster mode={printed} selected=0 applicable={applicable}", Assert.Single(output.Lines));
    }

    [Fact]
    public void Completed_PrintsOneNumberedLinePerSpec()
    {
        var output = new Recorder();
        var lane = AggregateLaneRun.Start(output, "[FROZEN-UI] ", "FrozenNrUiRoster", AggregateMode.Full, selected: 2, applicable: 2);
        lane.Completed("protocol/protocol-kitchen-sink", AggregateLaneRun.Passed, TimeSpan.FromSeconds(14.72));
        lane.Completed("cost/cost-x", AggregateLaneRun.ExpectedFailure, TimeSpan.FromMilliseconds(40));
        Assert.Equal(
            [
                "[FROZEN-UI] [1/2] protocol/protocol-kitchen-sink passed 14.7s",
                "[FROZEN-UI] [2/2] cost/cost-x expected-failure 0.0s",
            ],
            output.Lines.Skip(1));
    }

    [Fact]
    public void ThrowIfStopped_SaysWhereTheRunStopped()
    {
        var output = new Recorder();
        var lane = AggregateLaneRun.Start(output, "[FROZEN-UI] ", "FrozenNrUiRoster", AggregateMode.Full, selected: 378, applicable: 378);
        lane.ThrowIfStopped(CancellationToken.None);
        lane.Completed("protocol/protocol-kitchen-sink", AggregateLaneRun.Passed, TimeSpan.FromSeconds(1));

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var stopped = Assert.Throws<OperationCanceledException>(() => lane.ThrowIfStopped(stop.Token));
        Assert.Equal("[FROZEN-UI] stopped after 1/378; last completed protocol/protocol-kitchen-sink", stopped.Message);
        Assert.Equal(stopped.Message, output.Lines[^1]);
    }

    /// <summary>An <see cref="ITestOutputHelper"/> that keeps what is written to it.</summary>
    private sealed class Recorder : ITestOutputHelper
    {
        public List<string> Lines { get; } = [];

        public string Output => string.Join("\n", Lines);

        public void Write(string message) => Lines.Add(message);

        public void Write(string format, params object[] args) => Lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));

        public void WriteLine(string message) => Lines.Add(message);

        public void WriteLine(string format, params object[] args) => Lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));
    }
}
