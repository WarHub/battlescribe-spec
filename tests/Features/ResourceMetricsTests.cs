using System.Diagnostics.Metrics;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Telemetry;
using BattleScribeSpec.Tests.Infrastructure;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// Proves the two questions Task 8 exists to answer are actually observable:
/// (1) was an engine cold-started or reused, and what did each cost — via
/// <c>harness.engine.start.duration</c>; (2) does a resource's live-count return to zero after
/// teardown — via <c>harness.resource.count</c>.
/// </summary>
/// <remarks>
/// Both <see cref="ResourceMetrics"/> instruments live on a single process-wide static
/// <see cref="Meter"/>, and xUnit runs test collections in parallel — every other suite that
/// drives <c>AdapterHandler</c> or <c>AdapterProcess</c> concurrently emits onto the very same
/// instruments. A <see cref="MeterListener"/> that just filters by instrument name would see that
/// noise and make an "exactly one" assertion flaky. <see cref="IsThisTest"/> is an
/// <see cref="AsyncLocal{T}"/> flag set for the duration of each test's exercised call path: it
/// flows through <c>Task.Run</c> (which captures the calling <c>ExecutionContext</c>, including
/// any <see cref="AsyncLocal{T}"/> values, at schedule time) and through synchronous calls on the
/// test's own thread alike, so the measurement callback can tell "this test's own emission" apart
/// from a concurrent, unrelated test's — without touching production code or existing tests.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class ResourceMetricsTests
{
    private static readonly AsyncLocal<bool> IsThisTest = new();

    [Fact]
    public async Task WarmReuseEngine_RecordsOneColdStartAndOneReuse_AcrossTwoSpecs()
    {
        var observations = new List<(string Kind, bool Reused)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HarnessTelemetry.MeterName &&
                    instrument.Name == "harness.engine.start.duration")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            if (!IsThisTest.Value)
            {
                return; // noise from a concurrently running, unrelated test
            }

            string? kind = null;
            bool? reused = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "harness.resource.kind" && tag.Value is string k)
                {
                    kind = k;
                }
                else if (tag.Key == "harness.engine.reused" && tag.Value is bool r)
                {
                    reused = r;
                }
            }

            if (kind is not null && reused is { } reusedValue)
            {
                lock (observations)
                {
                    observations.Add((kind, reusedValue));
                }
            }
        });
        listener.Start();

        var ct = TestContext.Current.CancellationToken;
        var gameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" };

        // IsThisTest must be set BEFORE the connection is constructed: InMemoryAdapterConnection's
        // handler loop is a Task.Run captured at construction time, so the AsyncLocal has to be
        // true on the calling thread at that point to flow into the loop's ExecutionContext.
        IsThisTest.Value = true;
        var connection = new InMemoryAdapterConnection(
            (input, output, handlerCt) => AdapterHandler.RunAsync(
                new AdapterOptions
                {
                    RosterEngineFactory = () => new BattleScribeSpec.BattleScribeRosterEngine(),
                    Name = "battlescribe",
                    ReuseRosterEngineAcrossSetups = true,
                },
                input, output, handlerCt));

        // Two specs: setup -> teardown -> setup -> teardown. The first setup finds no engine
        // (cold start); the reused engine is reset (not disposed) at teardown, so the second
        // setup reuses it.
        Assert.IsType<SetupResult>(await connection.SendCommandAsync(new SetupCommand { GameSystem = gameSystem }, ct));
        Assert.IsType<TeardownResult>(await connection.SendCommandAsync(new TeardownCommand(), ct));
        Assert.IsType<SetupResult>(await connection.SendCommandAsync(new SetupCommand { GameSystem = gameSystem }, ct));
        Assert.IsType<TeardownResult>(await connection.SendCommandAsync(new TeardownCommand(), ct));

        await connection.DisposeAsync();
        IsThisTest.Value = false;

        var rosterObservations = observations.Where(o => o.Kind == "roster-engine").Select(o => o.Reused).ToList();
        Assert.Equal(2, rosterObservations.Count);
        Assert.Equal(1, rosterObservations.Count(r => !r));
        Assert.Equal(1, rosterObservations.Count(r => r));
    }

    [Fact]
    public async Task AdapterProcessLifecycle_ResourceCountReturnsToZero_AfterDispose()
    {
        var deltas = new List<int>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HarnessTelemetry.MeterName &&
                    instrument.Name == "harness.resource.count")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<int>((_, measurement, tags, _) =>
        {
            if (!IsThisTest.Value)
            {
                return; // noise from a concurrently running, unrelated test
            }

            foreach (var tag in tags)
            {
                if (tag.Key == "harness.resource.kind" && Equals(tag.Value, "adapter-process"))
                {
                    lock (deltas)
                    {
                        deltas.Add(measurement);
                    }

                    break;
                }
            }
        });
        listener.Start();

        // No JVM, no browser: the in-repo reference adapter is a plain .NET child process whose
        // roster engine is the IKVM-translated BattleScribe engine running in-process inside it —
        // fast and hermetic. Acquired()/Released() live in AdapterProcess.Start/Dispose themselves,
        // called synchronously on this thread, so IsThisTest scopes correctly without needing to
        // flow through any Task.Run.
        IsThisTest.Value = true;
        var adapter = AdapterTestHost.StartReferenceAdapter();
        adapter.Dispose();
        IsThisTest.Value = false;

        Assert.Equal([1, -1], deltas);
        Assert.Equal(0, deltas.Sum());
    }
}
