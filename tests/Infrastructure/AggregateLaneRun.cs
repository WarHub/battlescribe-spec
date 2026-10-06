using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace BattleScribeSpec.Tests;

/// <summary>Which part of its spec suite an aggregate lane's test drives.</summary>
internal enum AggregateMode
{
    /// <summary>Every spec the lane's engine is not opted out of: the lane, whole, in one test.</summary>
    Full,

    /// <summary>
    /// The kitchen-sink spec(s): a split lane's <c>KitchenSink</c> test, which the smoke profiles select alone,
    /// or all a lane drives yet.
    /// </summary>
    KitchenSink,

    /// <summary>
    /// Every spec but kitchen-sink: a split lane's <c>OtherSpecs</c> test. With <see cref="KitchenSink"/> it
    /// drives each applicable spec once, so selecting the lane's engine runs the lane whole.
    /// </summary>
    OtherSpecs,
}

/// <summary>
/// <b>What every single-<c>[Fact]</c> aggregate test says while it runs</b>: which part of its lane it drives and
/// how many specs that is out of how many apply, one line per spec as it finishes, and where it stopped when it
/// was stopped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an aggregate needs this and a theory does not.</b> An aggregate lane (<c>EngineLane.Aggregate</c>)
/// drives its spec suite inside one test (or two, <c>KitchenSink</c> and <c>OtherSpecs</c>, when split), so
/// everything the platform reports about it reads 1/1/0 whether it ran 378 specs or one. Two failures have hidden
/// behind that. A full lane that silently stopped opting in ran kitchen-sink alone and stayed green
/// (<c>docs/warm-reuse.md</c>: "CI never caught the original bug because the NR-UI roster lane runs a single
/// spec"); and a lane that hangs prints nothing at all — the NR UI lane is 27 minutes of one test, so a hang there
/// was 27 minutes of silence before the step's timeout, with no record of how far it got.
/// </para>
/// <para>
/// So each aggregate test:
/// </para>
/// <list type="bullet">
/// <item><description>
/// prints <c>[lane] &lt;Engine&gt; mode=&lt;full|kitchen-sink|other-specs&gt; selected=N applicable=M</c> first —
/// machine-readable: <c>selected</c> is what this test will drive, <c>applicable</c> is every spec in the corpus the
/// lane's engine is not opted out of, counted from the corpus itself — and fails when it selected nothing (a test
/// that drives no spec still passes, and the engine-composition check counts that pass as the lane having run);
/// </description></item>
/// <item><description>
/// prints <c>[i/N] &lt;spec&gt; &lt;verdict&gt; &lt;seconds&gt;s</c> as each spec finishes (<c>passed</c>, <c>failed</c>,
/// <c>expected-failure</c>, <c>unexpected-pass</c>), which <c>--show-live-output on</c> — set by <c>TestHost</c> in
/// Actions for every profile that claims an aggregate lane — streams to the log as it happens;
/// </description></item>
/// <item><description>
/// checks the test's cancellation token between specs, and when the run is stopped (MTP's <c>--timeout</c>,
/// Ctrl+C) says <c>stopped after k/N; last completed &lt;spec&gt;</c> before it throws.
/// </description></item>
/// </list>
/// <para>
/// <c>TestProfileRegistryTests.EveryAggregateLane_ReportsItsSelectionAndProgress</c> holds every aggregate lane
/// class to calling this.
/// </para>
/// </remarks>
internal sealed class AggregateLaneRun
{
    /// <summary>What the machine-readable selection line starts with.</summary>
    public const string LanePrefix = "[lane]";

    /// <summary>A spec that passed as expected.</summary>
    public const string Passed = "passed";

    /// <summary>A spec that failed and was not expected to.</summary>
    public const string Failed = "failed";

    /// <summary>A spec declared <c>fail</c> for the lane's engine, failing as declared.</summary>
    public const string ExpectedFailure = "expected-failure";

    /// <summary>A spec declared <c>fail</c> for the lane's engine that now passes — a failure of the run.</summary>
    public const string UnexpectedPass = "unexpected-pass";

    private readonly ITestOutputHelper _output;
    private readonly string _logPrefix;
    private readonly Lock _gate = new();
    private int _completed;
    private string? _lastCompleted;

    private AggregateLaneRun(ITestOutputHelper output, string logPrefix, int selected)
    {
        _output = output;
        _logPrefix = logPrefix;
        Selected = selected;
    }

    /// <summary>How many specs this run drives.</summary>
    public int Selected { get; }

    /// <summary>
    /// Whether the spec named <paramref name="spec"/> (<c>category/id</c>) is in the part <paramref name="mode"/>
    /// of a lane's suite: the one definition of kitchen-sink a split lane's two tests partition the corpus by.
    /// </summary>
    public static bool Drives(AggregateMode mode, string spec) => mode switch
    {
        AggregateMode.KitchenSink => spec.Contains("kitchen-sink", StringComparison.Ordinal),
        AggregateMode.OtherSpecs => !spec.Contains("kitchen-sink", StringComparison.Ordinal),
        _ => true,
    };

    /// <summary>Prints the lane's selection line, and fails when it selected no spec.</summary>
    /// <param name="output">The test's output.</param>
    /// <param name="logPrefix">The lane's log prefix (<c>[FROZEN-UI] </c>), put before each progress line.</param>
    /// <param name="engine">The lane's <c>Engine</c> trait.</param>
    /// <param name="mode">Which part of the suite this run drives.</param>
    /// <param name="selected">The specs this run will drive.</param>
    /// <param name="applicable">The specs in the corpus the lane's engine is not opted out of, in every part.</param>
    public static AggregateLaneRun Start(ITestOutputHelper output, string logPrefix, string engine, AggregateMode mode, int selected, int applicable)
    {
        ArgumentNullException.ThrowIfNull(output);
        var name = Name(mode);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{LanePrefix} {engine} mode={name} selected={selected} applicable={applicable}"));
        Assert.False(selected == 0,
            $"{LanePrefix} {engine} selected no spec in {name} mode"
            + (applicable == 0
                ? ", and no spec in the corpus applies to it: the spec corpus (specs/) was not found from the test output folder, or every spec opts its engine out. "
                : $" of the {applicable} that apply to it. ")
            + "A test that drives nothing still reads 1 passed, and the engine-composition check would count that pass as the lane having run.");
        return new AggregateLaneRun(output, logPrefix, selected);
    }

    private static string Name(AggregateMode mode) => mode switch
    {
        AggregateMode.KitchenSink => "kitchen-sink",
        AggregateMode.OtherSpecs => "other-specs",
        _ => "full",
    };

    /// <summary>Prints <c>[i/N] &lt;spec&gt; &lt;verdict&gt; &lt;seconds&gt;s</c> for a spec that just finished.</summary>
    public void Completed(string spec, string verdict, TimeSpan elapsed)
    {
        int index;
        lock (_gate)
        {
            index = ++_completed;
            _lastCompleted = spec;
        }

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{_logPrefix}[{index}/{Selected}] {spec} {verdict} {elapsed.TotalSeconds:F1}s"));
    }

    /// <summary>Between specs: when the run was stopped, says where, and throws.</summary>
    public void ThrowIfStopped(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            Stop(cancellationToken);
        }
    }

    /// <summary>Says where the run stopped — <c>stopped after k/N; last completed &lt;spec&gt;</c> — and throws.</summary>
    [DoesNotReturn]
    public void Stop(CancellationToken cancellationToken)
    {
        string message;
        lock (_gate)
        {
            message = $"{_logPrefix}stopped after {_completed}/{Selected}; last completed {_lastCompleted ?? "(none)"}";
        }

        _output.WriteLine(message);
        throw new OperationCanceledException(message, cancellationToken);
    }
}
