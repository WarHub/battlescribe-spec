using System.Globalization;
using BattleScribeSpec.Batch;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Telemetry.Collector;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// End-to-end proof of the whole telemetry chain (#271 Task 7): parent span -&gt; <c>traceparent</c>
/// over the NDJSON wire -&gt; child span nested underneath -&gt; both landing in one artifact. This is
/// the property the entire OTel investment rests on: without it, a foreign adapter process's
/// spans would just be orphan roots in whatever backend a user points the harness at.
/// </summary>
// Shares a collection with TelemetryCollectorTests: see the comment there on why
// OTEL_EXPORTER_OTLP_ENDPOINT-mutating tests must never run concurrently with a plain
// HarnessCollector.StartAsync call like this test's.
[Collection("HarnessCollectorEnv")]
[Trait("Category", "Integration")]
public sealed class EndToEndTraceTests
{
    private static string FindHostDll()
    {
        // Tests run from artifacts/bin/BattleScribeSpec.Tests/<pivot>/ — walk up to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BattleScribeSpec.slnx")))
        {
            dir = dir.Parent!;
        }

        Assert.NotNull(dir);
        var dll = Path.Combine(dir.FullName, "artifacts", "bin",
            "BattleScribeSpec.EngineHost", "debug", "bs-engine-host.dll");
        Assert.True(File.Exists(dll), $"Engine host not built: {dll}");
        return dll;
    }

    [Fact]
    public async Task RunAsync_ChildProcessSpans_NestUnderTheParentsSpans_InOneArtifact()
    {
        var ct = TestContext.Current.CancellationToken;
        var hostDll = FindHostDll();
        var artifact = Path.Combine(Path.GetTempPath(), $"bsspec-e2e-{Guid.NewGuid():N}");

        try
        {
            SpecSuiteResult result;
            await using (var collector = await HarnessCollector.StartAsync(artifact, ct))
            {
                Assert.True(collector.Enabled);
                Assert.True(collector.HasLocalArtifact);

                // A real bs-engine-host child process (NOT the in-process reference adapter) is
                // required here: the child span's resource carries service.name = bs-engine-host
                // ONLY because bs-engine-host/Program.cs initializes the OTel SDK from the
                // OTEL_* env vars in ChildEnvironment. The reference adapter never does that.
                result = await SpecSuiteRunner.RunAsync(
                    new SpecSuiteOptions
                    {
                        FilterPatterns = ["protocol/protocol-kitchen-sink"],
                        EngineFilter = "battlescribe",
                        AssertionEngine = "battlescribe",
                        Workers = 1,
                        AdapterFactory = workerIndex =>
                        {
                            var index = workerIndex.ToString(CultureInfo.InvariantCulture);
                            var env = new Dictionary<string, string>(collector.ChildEnvironment)
                            {
                                ["BSSPEC_WORKER_INDEX"] = index,
                            };
                            return AdapterProcess.Start("dotnet", $"{hostDll} serve --engine battlescribe", env);
                        },
                    },
                    progressWriter: Console.Error);

                // Disposing the collector below force-flushes the parent's TracerProvider, which
                // drives the parent-side spans (setup/action/teardown CLIENT spans, spec spans)
                // into the artifact over OTLP. The child's own OTel SDK (bs-engine-host) flushes
                // on its own process exit, which AdapterProcess/SpecSuiteRunner already waited for.
            }

            Assert.True(result.TotalSpecs > 0);

            var allSpans = OtlpArtifactReader.ReadTraces(artifact)
                .SelectMany(r => r.ResourceSpans)
                .SelectMany(rs => rs.ScopeSpans.SelectMany(ss => ss.Spans)
                    .Select(span => (Span: span, rs.Resource)))
                .ToList();
            Assert.NotEmpty(allSpans);

            // (a) spec spans exist — HarnessTelemetry.StartSpec tags every spec span with
            // test.case.name (see SpecSuiteRunner/HarnessTelemetryTests).
            Assert.Contains(allSpans, x => x.Span.Attributes.Any(a => a.Key == "test.case.name"));

            // (b) at least one span was produced by the CHILD PROCESS: its resource has
            // service.name = bs-engine-host (set via HarnessCollector.ChildEnvironment's
            // OTEL_SERVICE_NAME, read by the child's stock SDK — never overridden in code).
            var childSpans = allSpans
                .Where(x => x.Resource.Attributes.Any(
                    a => a.Key == "service.name" && a.Value.StringValue == "bs-engine-host"))
                .ToList();
            Assert.NotEmpty(childSpans);

            // Parent-side span ids: everything NOT from the child (i.e. bs-spec's own resource).
            var parentSpanIds = allSpans
                .Where(x => x.Resource.Attributes.Any(
                    a => a.Key == "service.name" && a.Value.StringValue == "bs-spec"))
                .Select(x => x.Span.SpanId)
                .ToHashSet();
            Assert.NotEmpty(parentSpanIds);

            // (c) THE property the whole design rests on: a child span's parent_span_id is
            // non-zero and matches a parent-side span id — proof that traceparent, carried over
            // the NDJSON wire, really nests a foreign process's spans under ours.
            Assert.Contains(childSpans, x =>
                !x.Span.ParentSpanId.IsEmpty && parentSpanIds.Contains(x.Span.ParentSpanId));
        }
        finally
        {
            File.Delete(artifact + ".traces.pb");
            File.Delete(artifact + ".metrics.pb");
            File.Delete(artifact + ".logs.pb");
        }
    }
}
