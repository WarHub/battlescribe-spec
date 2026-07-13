using BattleScribeSpec.Telemetry;
using BattleScribeSpec.Telemetry.Collector;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// Proves the xUnit path's assembly-wide telemetry (#271 Task 9) does its one job: a
/// <see cref="TelemetryAssemblyFixture"/> is actually wired into the whole test assembly (proven
/// by taking it as a constructor parameter — the exact mechanism any test class here can use to
/// reach it), and <c>harness.resource.count</c> yields a genuine PEAK — a value strictly above
/// the final (zero) reading — rather than only the single dead snapshot a bare
/// <c>MeterProvider.ForceFlush</c> would produce.
/// </summary>
/// <remarks>
/// <para>
/// Reading the ASSEMBLY fixture's own artifact from inside a test is not possible:
/// <c>OtlpArtifactWriter</c> holds its files open (<c>FileShare.None</c>) until
/// <see cref="TelemetryAssemblyFixture.DisposeAsync"/> runs at the very end of the whole run — see
/// the Task 9 report for that number, read back after a real <c>dotnet test</c> run completes.
/// What this test proves instead is the MECHANISM that number depends on: that a short
/// <c>OTEL_METRIC_EXPORT_INTERVAL</c> turns <c>harness.resource.count</c> into a readable time
/// series whose max exceeds its last value — exactly what makes the real
/// <c>artifacts/telemetry/xunit-&lt;timestamp&gt;.metrics.pb</c> artifact's peak recoverable.
/// </para>
/// <para>
/// Shares <c>HarnessCollectorEnv</c> with <c>TelemetryCollectorTests</c>/<c>EndToEndTraceTests</c>:
/// like them, this test mutates the process-wide <c>OTEL_METRIC_EXPORT_INTERVAL</c> environment
/// variable, and xUnit serializes tests within one collection while running different collections
/// in parallel.
/// </para>
/// </remarks>
[Collection("HarnessCollectorEnv")]
[Trait("Category", "Integration")]
public sealed class XunitTelemetryTests
{
    private readonly TelemetryAssemblyFixture _assemblyFixture;

    public XunitTelemetryTests(TelemetryAssemblyFixture assemblyFixture)
    {
        _assemblyFixture = assemblyFixture;
    }

    [Fact]
    public async Task AssemblyCollector_IsWiredUp_AndResourceCountSeries_YieldsAPeakAboveTheFinalValue()
    {
        // (1) The assembly fixture is reachable via constructor injection — the same way any
        // other test class in this assembly reaches it — and actually started a collector.
        Assert.SkipWhen(_assemblyFixture.Collector is not { Enabled: true },
            "assembly-wide telemetry collector did not start (fail-open) — skipping");

        var childEnv = _assemblyFixture.Collector!.ChildEnvironment;
        Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT", childEnv.Keys);

        // (2) The mechanism the real xunit-<timestamp> artifact's peak depends on: a short
        // OTEL_METRIC_EXPORT_INTERVAL turns harness.resource.count into a time series, not a
        // single dead reading. This uses its own short-lived collector/artifact rather than the
        // assembly-wide one, precisely because that one can't be read until the whole run ends.
        var ct = TestContext.Current.CancellationToken;
        var artifact = Path.Combine(Path.GetTempPath(), $"bsspec-xunit-telemetry-{Guid.NewGuid():N}");
        var kind = $"xunit-test-kind-{Guid.NewGuid():N}";
        var previousInterval = Environment.GetEnvironmentVariable("OTEL_METRIC_EXPORT_INTERVAL");
        try
        {
            Environment.SetEnvironmentVariable("OTEL_METRIC_EXPORT_INTERVAL", "200");

            await using (var collector = await HarnessCollector.StartAsync(artifact, ct))
            {
                Assert.True(collector.Enabled);

                ResourceMetrics.Acquired(kind);
                ResourceMetrics.Acquired(kind); // two alive at once: the peak this test expects
                await Task.Delay(600, ct); // let the 200ms periodic reader export "2" more than once
                ResourceMetrics.Released(kind);
                ResourceMetrics.Released(kind);
                await Task.Delay(600, ct); // and export "0" at least once before the final ForceFlush

                // Disposing the collector below force-flushes one last (zero) snapshot too.
            }

            var series = OtlpArtifactReader.ReadMetrics(artifact)
                .SelectMany(r => r.ResourceMetrics)
                .SelectMany(rm => rm.ScopeMetrics)
                .SelectMany(sm => sm.Metrics)
                .Where(m => m.Name == "harness.resource.count")
                .SelectMany(m => m.Sum.DataPoints)
                .Where(dp => dp.Attributes.Any(a => a.Key == "harness.resource.kind" && a.Value.StringValue == kind))
                .Select(dp => (int)dp.AsInt)
                .ToList();

            // The series exists...
            Assert.NotEmpty(series);

            // ...and a peak is computable from it, and it is exactly the thing a single final
            // snapshot could never show: strictly above the last (post-release) reading.
            var peak = series.Max();
            Assert.Equal(2, peak);
            Assert.Equal(0, series[^1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_METRIC_EXPORT_INTERVAL", previousInterval);
            File.Delete(artifact + ".traces.pb");
            File.Delete(artifact + ".metrics.pb");
            File.Delete(artifact + ".logs.pb");
        }
    }
}
