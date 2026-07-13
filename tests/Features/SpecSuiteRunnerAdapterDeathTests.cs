using System.Diagnostics.Metrics;
using BattleScribeSpec.Batch;
using BattleScribeSpec.Telemetry;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// #304: an adapter process dying mid-batch used to fail EVERY remaining spec on that worker
/// ("Adapter process has exited with code N.") — the motivating incident was a single crash
/// cascading into 98 of 102 specs failing. These tests drive the reference adapter's
/// <c>BSSPEC_TEST_FORCE_KILL</c> hook (analogous to <c>BSSPEC_TEST_FORCE_FAIL</c>, but the process
/// kills itself instead of just returning an error) to reproduce a real adapter death
/// deterministically, and assert <see cref="SpecSuiteRunner"/>'s recovery policy: retry once on a
/// fresh process, record the death regardless of outcome, fail with a clear reason if the retry
/// also dies, and cap the number of deaths tolerated per run.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SpecSuiteRunnerAdapterDeathTests
{
    private const string KillEnvVar = "BSSPEC_TEST_FORCE_KILL";

    /// <summary>
    /// Scopes <see cref="ResourceMetrics"/> observations to THIS test's own emissions. xUnit runs
    /// collections in parallel and both instruments used below live on one process-wide static
    /// <see cref="Meter"/>, so an unscoped listener would also see every other concurrently running
    /// suite that drives an <c>AdapterProcess</c>. Set true immediately before calling
    /// <see cref="SpecSuiteRunner.RunAsync"/> and false immediately after — it flows through the
    /// awaited call tree (including <c>Parallel.ForEachAsync</c>'s internally scheduled work) via
    /// <see cref="ExecutionContext"/>, the same pattern <c>ResourceMetricsTests</c> established.
    /// </summary>
    private static readonly AsyncLocal<bool> IsThisTest = new();

    /// <summary>
    /// <see cref="SpecSuiteResult.ReportResults"/> carries a "skipped" entry for EVERY spec in the
    /// domain that didn't match <c>FilterPatterns</c> (pre-existing behavior, unrelated to #304) —
    /// so tests that filter down to a handful of specs must exclude those to get the count of specs
    /// that actually ran.
    /// </summary>
    private static List<SpecResultSummary> Executed(SpecSuiteResult result) =>
        [.. result.ReportResults.Where(r => r.Status != "skipped")];

    /// <summary>
    /// Runs <see cref="SpecSuiteRunner.RunAsync"/> under a bounded wall-clock timeout so that a
    /// regression in the adapter-process pool's write-back invariant (see the class remarks on
    /// <c>AdapterDeathCap_Exceeded_Parallel_...</c> below) fails the TEST fast instead of hanging
    /// CI forever. <see cref="SpecSuiteRunner.RunAsync"/> takes no <see cref="CancellationToken"/>,
    /// so a timeout here cannot cancel a truly hung run — it only bounds how long THIS TEST waits
    /// for it, which is the CI-hygiene property that matters.
    /// </summary>
    private static async Task<SpecSuiteResult> RunWithTimeout(SpecSuiteOptions options, TimeSpan timeout, CancellationToken ct)
    {
        var runTask = SpecSuiteRunner.RunAsync(options);
        var winner = await Task.WhenAny(runTask, Task.Delay(timeout, ct));
        Assert.True(ReferenceEquals(winner, runTask),
            $"SpecSuiteRunner.RunAsync did not complete within {timeout} — likely a pool deadlock " +
            "(a channel slot never written back on some exit path).");
        return await runTask;
    }

    [Fact]
    public async Task AdapterDies_Once_Sequential_IsRetriedAndPasses()
    {
        var callCount = 0;
        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            FilterPatterns = ["protocol/protocol-kitchen-sink"],
            EngineFilter = "battlescribe",
            AssertionEngine = "battlescribe",
            AdapterFactory = _ =>
            {
                callCount++;
                var env = callCount == 1
                    ? new Dictionary<string, string> { [KillEnvVar] = "protocol-kitchen-sink" }
                    : null;
                return AdapterTestHost.StartReferenceAdapter(env);
            },
        });

        Assert.Equal(0, result.ExitCode);
        var entry = Assert.Single(result.ReportResults, r => r.SpecId == "protocol-kitchen-sink");
        Assert.Equal("passed", entry.Status);
        Assert.Equal(1, entry.AdapterDeaths);
        Assert.Equal(2, callCount); // the original (dead) process + one replacement
    }

    [Fact]
    public async Task AdapterDies_Twice_Sequential_FailsWithAdapterDeathReason_AndBatchContinues()
    {
        // The kill switch is scoped to "protocol-kitchen-sink" only — catalogue-cost-types must be
        // unaffected, proving the SECOND spec still runs (the actual #304 bug: a cascade would have
        // failed it too).
        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            FilterPatterns = ["protocol/protocol-kitchen-sink", "catalogue/catalogue-cost-types"],
            EngineFilter = "battlescribe",
            AssertionEngine = "battlescribe",
            AdapterFactory = _ => AdapterTestHost.StartReferenceAdapter(
                new Dictionary<string, string> { [KillEnvVar] = "protocol-kitchen-sink" }),
        });

        Assert.Equal(1, result.ExitCode);

        var killed = Assert.Single(result.ReportResults, r => r.SpecId == "protocol-kitchen-sink");
        Assert.Equal("failed", killed.Status);
        Assert.Equal(2, killed.AdapterDeaths);
        Assert.Contains(killed.Failures, f => f.Contains("ADAPTER DEATH", StringComparison.Ordinal));

        var unaffected = Assert.Single(result.ReportResults, r => r.SpecId == "catalogue-cost-types");
        Assert.Equal("passed", unaffected.Status);
        Assert.Equal(0, unaffected.AdapterDeaths);
    }

    [Fact]
    public async Task SpecsAfterAnAdapterDeath_StillRun_NoCascade()
    {
        // Three specs; only the first ever gets killed (once). Proves the cascade is gone for the
        // WHOLE remainder of the run, not just the immediately-next spec.
        var callCount = 0;
        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            FilterPatterns =
            [
                "protocol/protocol-kitchen-sink",
                "catalogue/catalogue-cost-types",
                "category/category-entry-hidden",
            ],
            EngineFilter = "battlescribe",
            AssertionEngine = "battlescribe",
            AdapterFactory = _ =>
            {
                callCount++;
                var env = callCount == 1
                    ? new Dictionary<string, string> { [KillEnvVar] = "protocol-kitchen-sink" }
                    : null;
                return AdapterTestHost.StartReferenceAdapter(env);
            },
        });

        Assert.Equal(0, result.ExitCode);
        var executed = Executed(result);
        Assert.Equal(3, executed.Count);
        Assert.All(executed, r => Assert.Equal("passed", r.Status));
    }

    [Fact]
    public async Task AdapterDies_Once_Parallel_IsRetriedAndPasses()
    {
        var callCount = 0;
        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            FilterPatterns = ["protocol/protocol-kitchen-sink"],
            EngineFilter = "battlescribe",
            AssertionEngine = "battlescribe",
            Workers = 2,
            AdapterFactory = _ =>
            {
                var n = Interlocked.Increment(ref callCount);
                var env = n == 1
                    ? new Dictionary<string, string> { [KillEnvVar] = "protocol-kitchen-sink" }
                    : null;
                return AdapterTestHost.StartReferenceAdapter(env);
            },
        });

        Assert.Equal(0, result.ExitCode);
        var entry = Assert.Single(result.ReportResults, r => r.SpecId == "protocol-kitchen-sink");
        Assert.Equal("passed", entry.Status);
        Assert.Equal(1, entry.AdapterDeaths);
    }

    [Fact]
    public async Task AdapterDies_Twice_Parallel_FailsWithAdapterDeathReason_AndOtherWorkerSpecsContinue()
    {
        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            FilterPatterns =
            [
                "protocol/protocol-kitchen-sink",
                "catalogue/catalogue-cost-types",
                "category/category-entry-hidden",
                "condition/condition-at-least",
            ],
            EngineFilter = "battlescribe",
            AssertionEngine = "battlescribe",
            Workers = 2,
            AdapterFactory = _ => AdapterTestHost.StartReferenceAdapter(
                new Dictionary<string, string> { [KillEnvVar] = "protocol-kitchen-sink" }),
        });

        Assert.Equal(1, result.ExitCode);
        var executed = Executed(result);
        Assert.Equal(4, executed.Count);

        var killed = Assert.Single(executed, r => r.SpecId == "protocol-kitchen-sink");
        Assert.Equal("failed", killed.Status);
        Assert.Equal(2, killed.AdapterDeaths);

        Assert.All(
            executed.Where(r => r.SpecId != "protocol-kitchen-sink"),
            r => Assert.Equal("passed", r.Status));
    }

    [Fact]
    public async Task AdapterDeathCap_Exceeded_StopsReplacing_AndReportsTheCap()
    {
        var callCount = 0;
        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            FilterPatterns =
            [
                "protocol/protocol-kitchen-sink",
                "catalogue/catalogue-cost-types",
                "category/category-entry-hidden",
            ],
            EngineFilter = "battlescribe",
            AssertionEngine = "battlescribe",
            MaxAdapterDeaths = 1,
            AdapterFactory = _ =>
            {
                Interlocked.Increment(ref callCount);
                // Unconditional kill: EVERY process this factory produces dies on its very first
                // spec — simulating a deterministically-crashing engine so the cap must trip.
                return AdapterTestHost.StartReferenceAdapter(new Dictionary<string, string> { [KillEnvVar] = "1" });
            },
        });

        Assert.Equal(1, result.ExitCode);
        var executed = Executed(result);
        Assert.Equal(3, executed.Count);

        // Exactly one spec burns through both attempts (2 deaths — the retry also died); the
        // other two hit the cap immediately (1 death each, no retry spent) once it's spent.
        var byDeaths = executed.ToLookup(r => r.AdapterDeaths);
        Assert.Single(byDeaths[2]);
        Assert.Equal(2, byDeaths[1].Count());
        Assert.All(executed, r => Assert.Equal("failed", r.Status));

        var cappedMessage = byDeaths[1].SelectMany(r => r.Failures)
            .FirstOrDefault(f => f.Contains("cap", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(cappedMessage);
        Assert.Contains("1", cappedMessage); // the configured cap value is reported, not a silent truncation

        // Stopped replacing: only the original process per spec + one retry replacement for the
        // single spec that spent both attempts = 2 factory calls total, never more.
        Assert.Equal(2, callCount);
    }

    /// <summary>
    /// Code review follow-up (IMPORTANT 1): the cap-exceeded test above runs with the default
    /// <c>Workers = 1</c> — it never drives cap-exceeded through the parallel channel-pool path,
    /// which is the single highest-risk seam in #304. The pool pulls a process out of a bounded
    /// <c>Channel&lt;AdapterProcess&gt;</c> and must write exactly one back in a <c>finally</c>, on
    /// EVERY exit path including cap-exceeded — if a slot is ever lost, every worker eventually
    /// blocks forever on <c>Reader.ReadAsync</c>, turning an intermittent crash into a hung CI job
    /// (strictly worse than the cascade #304 fixes, which at least fails loudly). This drives the
    /// cap past its limit through 2 concurrent workers and asserts the run still completes, reports
    /// the cap, fails post-cap specs with the cap message, and leaks no process.
    /// </summary>
    [Fact]
    public async Task AdapterDeathCap_Exceeded_Parallel_CompletesWithoutHanging_AndReportsTheCap()
    {
        var resourceCountDeltas = new List<int>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HarnessTelemetry.MeterName && instrument.Name == "harness.resource.count")
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
                    lock (resourceCountDeltas)
                    {
                        resourceCountDeltas.Add(measurement);
                    }

                    break;
                }
            }
        });
        listener.Start();

        var callCount = 0;
        IsThisTest.Value = true;
        var result = await RunWithTimeout(new SpecSuiteOptions
        {
            FilterPatterns =
            [
                "protocol/protocol-kitchen-sink",
                "catalogue/catalogue-cost-types",
                "category/category-entry-hidden",
                "condition/condition-at-least",
            ],
            EngineFilter = "battlescribe",
            AssertionEngine = "battlescribe",
            Workers = 2,
            MaxAdapterDeaths = 1,
            AdapterFactory = _ =>
            {
                Interlocked.Increment(ref callCount);
                // Unconditional kill: EVERY process this factory produces dies on its very first
                // spec — simulating a deterministically-crashing engine so the cap must trip.
                return AdapterTestHost.StartReferenceAdapter(new Dictionary<string, string> { [KillEnvVar] = "1" });
            },
        }, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        IsThisTest.Value = false;

        // Completed at all (RunWithTimeout already asserted this didn't time out) — the batch did
        // not hang.
        Assert.Equal(1, result.ExitCode);
        var executed = Executed(result);
        Assert.Equal(4, executed.Count);
        Assert.All(executed, r => Assert.Equal("failed", r.Status));

        var cappedMessage = executed.SelectMany(r => r.Failures)
            .FirstOrDefault(f => f.Contains("cap", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(cappedMessage);
        Assert.Contains("1", cappedMessage); // the configured cap value is reported

        // No process leaked: every Acquired(+1) is matched by a Released(-1) once the run (and its
        // final `finally` disposal of every process ever created, originals + replacements) has
        // completed — a net-zero sum proves the pool's `allProcesses` bookkeeping tracked and
        // disposed every process this run ever spawned.
        Assert.True(resourceCountDeltas.Count > 0, "expected at least one adapter-process resource-count observation");
        Assert.Equal(0, resourceCountDeltas.Sum());
    }

    /// <summary>
    /// Code review follow-up (IMPORTANT 2): <c>ReplaceProcess</c> (now <c>TryReplaceProcess</c>)
    /// disposes the dead process and calls <c>AdapterFactory</c> to spawn a replacement. If THAT
    /// throws — plausible right after a crash: the engine binary is gone, the JVM won't start, the
    /// machine is out of memory — the exception used to propagate out of <c>RunOneSpec</c> uncaught,
    /// faulting the batch. This proves: the batch does not crash, the affected spec fails with a
    /// clear reason naming the replacement failure, the death is counted, and — once the factory
    /// recovers — the very next spec's own rescue retry succeeds, proving the batch really continues
    /// rather than being permanently wedged.
    /// </summary>
    [Fact]
    public async Task ReplacementFactoryThrows_Transiently_FailsSpecClearly_AndBatchContinues()
    {
        // Unconditional kill (env value "1", not a spec-name match): dies on whichever of the two
        // filtered specs happens to run FIRST. Spec run order here is <c>SpecLoader.DiscoverSpecs</c>'s
        // filesystem enumeration order, NOT `FilterPatterns`' list order — so the test deliberately
        // does not assume which of the two spec ids is "the killed one" and instead identifies it
        // from the result (AdapterDeaths == 2). The mechanism under test — the replacement-failure
        // recovery — is exercised identically regardless of which spec triggers it.
        var callCount = 0;
        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            FilterPatterns = ["protocol/protocol-kitchen-sink", "catalogue/catalogue-cost-types"],
            EngineFilter = "battlescribe",
            AssertionEngine = "battlescribe",
            AdapterFactory = _ =>
            {
                var n = Interlocked.Increment(ref callCount);
                return n switch
                {
                    1 => AdapterTestHost.StartReferenceAdapter(new Dictionary<string, string> { [KillEnvVar] = "1" }),
                    2 => throw new InvalidOperationException("simulated: engine binary missing"),
                    _ => AdapterTestHost.StartReferenceAdapter(),
                };
            },
        });

        Assert.Equal(1, result.ExitCode);
        var executed = Executed(result);
        Assert.Equal(2, executed.Count);

        var killed = Assert.Single(executed, r => r.AdapterDeaths == 2);
        Assert.Equal("failed", killed.Status);
        Assert.Contains(killed.Failures, f =>
            f.Contains("ADAPTER DEATH", StringComparison.Ordinal) &&
            f.Contains("replacement process could not be started", StringComparison.Ordinal) &&
            f.Contains("simulated: engine binary missing", StringComparison.Ordinal));

        // The OTHER spec — whichever ran second — still ran to completion and PASSED, proving the
        // throwing factory neither crashed the batch nor permanently wedged the process slot: the
        // next recovery attempt (retrying the corpse left behind by the killed spec) got a working
        // factory call this time.
        var recovered = Assert.Single(executed, r => r.SpecId != killed.SpecId);
        Assert.Equal("passed", recovered.Status);

        Assert.Equal(3, callCount);
    }

    /// <summary>
    /// The other extreme of IMPORTANT 2: the replacement factory NEVER recovers (the engine binary
    /// is really gone for the rest of the run). The batch must still terminate cleanly — via the
    /// existing death cap treating each failed replacement as its own adapter-death-class event —
    /// rather than hang or let the factory's exception escape <see cref="SpecSuiteRunner.RunAsync"/>.
    /// </summary>
    [Fact]
    public async Task ReplacementFactoryThrows_Permanently_CapTripsAndBatchCompletesCleanly()
    {
        var callCount = 0;
        var result = await RunWithTimeout(new SpecSuiteOptions
        {
            FilterPatterns =
            [
                "protocol/protocol-kitchen-sink",
                "catalogue/catalogue-cost-types",
                "category/category-entry-hidden",
            ],
            EngineFilter = "battlescribe",
            AssertionEngine = "battlescribe",
            MaxAdapterDeaths = 1,
            AdapterFactory = _ =>
            {
                var n = Interlocked.Increment(ref callCount);
                if (n == 1)
                {
                    return AdapterTestHost.StartReferenceAdapter(new Dictionary<string, string> { [KillEnvVar] = "1" });
                }

                throw new InvalidOperationException("simulated: engine binary permanently missing");
            },
        }, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        var executed = Executed(result);
        Assert.Equal(3, executed.Count);
        Assert.All(executed, r => Assert.Equal("failed", r.Status));

        // The original process + exactly one failed replacement attempt trip the cap
        // (MaxAdapterDeaths=1: 2 death-class events) — no further factory calls for the remainder.
        Assert.Equal(2, callCount);
    }

    /// <summary>
    /// Code review follow-up (MINOR 3): <c>RecordDeath</c> used to be skipped whenever
    /// <c>deathBudget.IsExceeded</c> was ALREADY true, which suppressed counting a genuinely new,
    /// distinct process crashing on a DIFFERENT worker after some other worker's crash had already
    /// tripped the cap — the aggregate <c>harness.resource.death.count</c> telemetry undercounted
    /// the true crash count. With <c>MaxAdapterDeaths = 0</c> the cap trips on the very first death,
    /// so no replacement is EVER attempted for the rest of the run — meaning both workers' initial,
    /// pool-seeded processes crashing on their own first spec is a fully deterministic count of
    /// exactly 2 distinct crashes, regardless of which worker's crash is detected first (unlike a
    /// higher cap, this doesn't depend on winning a race between the two workers).
    /// </summary>
    [Fact]
    public async Task AdapterDeathCounter_CountsEveryDistinctCrash_NotUndercountedAfterCapTrips()
    {
        var deathObservations = new List<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HarnessTelemetry.MeterName && instrument.Name == "harness.resource.death.count")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (!IsThisTest.Value)
            {
                return; // noise from a concurrently running, unrelated test
            }

            foreach (var tag in tags)
            {
                if (tag.Key == "harness.resource.kind" && Equals(tag.Value, "adapter-process"))
                {
                    lock (deathObservations)
                    {
                        deathObservations.Add("adapter-process");
                    }

                    break;
                }
            }
        });
        listener.Start();

        var callCount = 0;
        IsThisTest.Value = true;
        var result = await RunWithTimeout(new SpecSuiteOptions
        {
            FilterPatterns =
            [
                "protocol/protocol-kitchen-sink",
                "catalogue/catalogue-cost-types",
                "category/category-entry-hidden",
                "condition/condition-at-least",
            ],
            EngineFilter = "battlescribe",
            AssertionEngine = "battlescribe",
            Workers = 2,
            MaxAdapterDeaths = 0,
            AdapterFactory = _ =>
            {
                Interlocked.Increment(ref callCount);
                return AdapterTestHost.StartReferenceAdapter(new Dictionary<string, string> { [KillEnvVar] = "1" });
            },
        }, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        IsThisTest.Value = false;

        Assert.Equal(1, result.ExitCode);
        var executed = Executed(result);
        Assert.Equal(4, executed.Count);
        Assert.All(executed, r => Assert.Equal("failed", r.Status));

        // Exactly the 2 pool-seeded processes (one per worker) are ever created — the cap trips on
        // the very first death, so no replacement is ever attempted for the rest of the run.
        Assert.Equal(2, callCount);

        // Both of those processes genuinely crash exactly once each: this must be exactly 2 — not 1
        // (the undercount bug: a second, genuinely new crash silently going unrecorded because the
        // cap had already tripped elsewhere) and not more than 2 (a repeat re-detection of the same
        // already-dead corpse must stay suppressed).
        Assert.Equal(2, deathObservations.Count);
    }
}
