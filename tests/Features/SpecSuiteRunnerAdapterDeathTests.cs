using BattleScribeSpec.Batch;

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
    /// <see cref="SpecSuiteResult.ReportResults"/> carries a "skipped" entry for EVERY spec in the
    /// domain that didn't match <c>FilterPatterns</c> (pre-existing behavior, unrelated to #304) —
    /// so tests that filter down to a handful of specs must exclude those to get the count of specs
    /// that actually ran.
    /// </summary>
    private static List<SpecResultSummary> Executed(SpecSuiteResult result) =>
        [.. result.ReportResults.Where(r => r.Status != "skipped")];

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
}
