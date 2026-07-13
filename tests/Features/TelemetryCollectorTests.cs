using System.Net.Http.Headers;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;
using BattleScribeSpec.Telemetry;
using BattleScribeSpec.Telemetry.Collector;

namespace BattleScribeSpec.Tests.Features;

// Shares a collection with EndToEndTraceTests: both mutate/depend on the process-wide
// OTEL_EXPORTER_OTLP_ENDPOINT environment variable that HarnessCollector.StartAsync reads to
// decide self-hosted vs. externally-set-collector mode. xUnit runs different test collections in
// parallel but serializes tests WITHIN one collection, so grouping them here is what stops
// ExternallySetEndpoint_IsHonored (which sets that env var) from racing a concurrent StartAsync
// call in EndToEndTraceTests and flipping it into external mode unexpectedly.
[Collection("HarnessCollectorEnv")]
[Trait("Category", "Unit")]
public sealed class TelemetryCollectorTests
{
    [Fact]
    public async Task PostedProtobufSpans_LandInTheArtifact()
    {
        var artifact = Path.Combine(Path.GetTempPath(), $"bsspec-otlp-{Guid.NewGuid():N}.pb");

        // HarnessCollector.StartAsync's ParentProviders subscribes to HarnessTelemetry.SourceName
        // BY NAME, process-wide — so while this collector is alive, any OTHER concurrently-running
        // test in this xUnit run that dispatches an adapter command in-process (e.g. AdapterHandler
        // tests) also gets captured into THIS artifact. A unique span name is what lets "ours" be
        // picked out of that legitimate noise deterministically (see the sibling test below,
        // ParentTelemetry_ReachesTheArtifact_ThroughTheStockOtlpExporter, for the same pattern) —
        // asserting the artifact holds exactly one span of any name is not a safe assumption here.
        var specId = $"spec-under-test-{Guid.NewGuid():N}";
        try
        {
            await using (var collector = await HarnessCollector.StartAsync(artifact, TestContext.Current.CancellationToken))
            {
                Assert.StartsWith("http://127.0.0.1:", collector.Endpoint, StringComparison.Ordinal);

                using var client = new HttpClient();
                var request = MakeRequest(specId);
                using var content = new ByteArrayContent(request.ToByteArray());
                content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

                var response = await client.PostAsync(
                    new Uri($"{collector.Endpoint}/v1/traces"), content, TestContext.Current.CancellationToken);

                Assert.True(response.IsSuccessStatusCode);

                // OpenTelemetry .NET never deserializes the response body, so an empty 200 would
                // pass this test either way — while Python/JS SDKs log deserialization errors
                // against a body that isn't a real ExportTraceServiceResponse. Assert both the
                // wire-level content-type AND that the body actually parses as the generated
                // response type, so a regression to Results.Ok()/NoContent() is caught here.
                Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
                var responseBytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
                var parsedResponse = ExportTraceServiceResponse.Parser.ParseFrom(responseBytes);
                Assert.Equal(new ExportTraceServiceResponse(), parsedResponse);
            }

            // Artifact is flushed on dispose; read it back with the same generated types.
            var received = OtlpArtifactReader.ReadTraces(artifact).ToList();
            var span = Assert.Single(received.SelectMany(r =>
                r.ResourceSpans.SelectMany(rs => rs.ScopeSpans.SelectMany(ss => ss.Spans))), s => s.Name == specId);
            Assert.Equal(specId, span.Name);
        }
        finally
        {
            File.Delete(artifact);
        }
    }

    [Fact]
    public async Task JsonBody_IsRejectedLoudly()
    {
        var artifact = Path.Combine(Path.GetTempPath(), $"bsspec-otlp-{Guid.NewGuid():N}.pb");
        try
        {
            await using var collector = await HarnessCollector.StartAsync(artifact, TestContext.Current.CancellationToken);

            using var client = new HttpClient();
            using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync(
                new Uri($"{collector.Endpoint}/v1/traces"), content, TestContext.Current.CancellationToken);

            // OTLP/JSON is not supported. An unsupported encoding must be loud, never silently dropped.
            Assert.Equal(System.Net.HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        }
        finally
        {
            File.Delete(artifact);
        }
    }

    [Fact]
    public async Task ParentTelemetry_ReachesTheArtifact_ThroughTheStockOtlpExporter()
    {
        // Regression test for a bug where ParentProviders.Attach() never set
        // OtlpExporterOptions.Protocol: on net10.0 the SDK default is gRPC (HttpProtobuf is the
        // default only on NETFRAMEWORK/NETSTANDARD2_0), but our receiver only maps HTTP routes —
        // no gRPC service exists. The parent's exporter would 404 against a nonexistent gRPC
        // path and, because OTLP export is fail-open, fail silently: the parent's own spans
        // (verdicts, test.case.name, etc.) would never reach the artifact. Unlike the other
        // tests here, this one goes through the real path — HarnessTelemetry -> ParentProviders
        // -> AddOtlpExporter -> the receiver — rather than POSTing raw protobuf directly.
        //
        // This also covers the METRICS half of the same bug: ParentProviders.Attach sets
        // o.Protocol on both the trace AND the metric exporter's AddOtlpExporter call. Reverting
        // only the metrics one would still 404-and-vanish silently, and nothing else here would
        // notice — harness.resource.count is the up-down counter the whole telemetry design calls
        // the single most important signal, so it gets its own artifact assertion below.
        var artifact = Path.Combine(Path.GetTempPath(), $"bsspec-otlp-{Guid.NewGuid():N}.pb");
        // HarnessTelemetry's ActivitySource / ResourceMetrics's Meter are process-wide statics, and
        // the xUnit run has other tests emitting spans/metrics on them concurrently (e.g.
        // adapter-command spans from unrelated collections). ParentProviders.Attach subscribes to
        // both BY NAME, so this test's artifact can legitimately contain spans and metric points
        // from those other tests too — a unique id is what lets us pick "ours" out of that noise
        // deterministically, rather than assuming the artifact holds exactly one of anything.
        var specId = $"parent-spec-under-test-{Guid.NewGuid():N}";
        var resourceKind = $"test-resource-{Guid.NewGuid():N}";
        try
        {
            await using (var collector = await HarnessCollector.StartAsync(artifact, TestContext.Current.CancellationToken))
            {
                Assert.True(collector.Enabled);

                using var activity = HarnessTelemetry.StartSpec(specId, "category", "domain");
                HarnessTelemetry.SetVerdict(activity, "passed");
                ResourceMetrics.Acquired(resourceKind);
                ResourceMetrics.Released(resourceKind);

                // Disposing the collector below force-flushes the parent's TracerProvider (and
                // MeterProvider), which is what actually drives the OTLP export over HTTP into
                // the receiver and, from there, into the artifact.
            }

            var received = OtlpArtifactReader.ReadTraces(artifact).ToList();
            var allSpans = received.SelectMany(r =>
                r.ResourceSpans.SelectMany(rs => rs.ScopeSpans.SelectMany(ss => ss.Spans)));
            var span = Assert.Single(allSpans, s => s.Name == specId);
            Assert.Contains(
                span.Attributes,
                a => a.Key == "test.case.name" && a.Value.StringValue == specId);
            Assert.Contains(
                span.Attributes,
                a => a.Key == "bsspec.verdict" && a.Value.StringValue == "passed");

            var receivedMetrics = OtlpArtifactReader.ReadMetrics(artifact).ToList();
            var dataPoints = receivedMetrics
                .SelectMany(r => r.ResourceMetrics)
                .SelectMany(rm => rm.ScopeMetrics)
                .SelectMany(sm => sm.Metrics)
                .Where(m => m.Name == "harness.resource.count")
                .SelectMany(m => m.Sum.DataPoints)
                .Where(dp => dp.Attributes.Any(
                    a => a.Key == "harness.resource.kind" && a.Value.StringValue == resourceKind))
                .ToList();
            Assert.NotEmpty(dataPoints);
        }
        finally
        {
            File.Delete(artifact + ".traces.pb");
            File.Delete(artifact + ".metrics.pb");
            File.Delete(artifact + ".logs.pb");
        }
    }

    [Fact]
    public async Task FailedStartAsync_DoesNotLeakArtifactFileHandles()
    {
        // Regression test: OtlpArtifactWriter opens three exclusive FileStreams BEFORE
        // app.StartAsync() runs. If StartAsync throws — forced here with an already-cancelled
        // token, which is the same "writer succeeded, then something later in the try failed"
        // shape as the original bug — the fail-open catch must dispose the writer (and the
        // partially-built app). Otherwise the handles leak for the process lifetime and, on
        // Windows, the artifact files stay locked so even a retry at the same path fails.
        var artifact = Path.Combine(Path.GetTempPath(), $"bsspec-otlp-leak-{Guid.NewGuid():N}.pb");
        try
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var collector = await HarnessCollector.StartAsync(artifact, cts.Token);
            Assert.False(collector.Enabled);
            await collector.DisposeAsync();

            // The writer must have created the three artifact files before StartAsync failed...
            foreach (var suffix in new[] { ".traces.pb", ".metrics.pb", ".logs.pb" })
            {
                var path = artifact + suffix;
                Assert.True(File.Exists(path));

                // ...and, if its FileStreams were properly disposed, opening them again
                // exclusively must succeed. Before the fix this throws IOException ("The
                // process cannot access the file because it is being used by another process").
                using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        }
        finally
        {
            File.Delete(artifact + ".traces.pb");
            File.Delete(artifact + ".metrics.pb");
            File.Delete(artifact + ".logs.pb");
        }
    }

    [Fact]
    public async Task ExternallySetEndpoint_IsHonored_WithNoLocalArtifact_AndParentSpansStillExport()
    {
        // A second real HarnessCollector plays the role of "the user's own Jaeger/Tempo": a real
        // OTLP/HTTP receiver, independent of the one under test, so this exercises the exact wire
        // path (stock SDK -> HTTP -> receiver) rather than asserting against a bespoke double.
        var fakeJaegerArtifact = Path.Combine(Path.GetTempPath(), $"bsspec-otlp-fakejaeger-{Guid.NewGuid():N}.pb");
        var artifact = Path.Combine(Path.GetTempPath(), $"bsspec-otlp-external-{Guid.NewGuid():N}.pb");
        var specId = $"external-spec-{Guid.NewGuid():N}";
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await using (var fakeJaeger = await HarnessCollector.StartAsync(fakeJaegerArtifact, ct))
            {
                Assert.True(fakeJaeger.Enabled);

                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", fakeJaeger.Endpoint);
                try
                {
                    await using var collector = await HarnessCollector.StartAsync(artifact, ct);

                    // Honored: same endpoint, still "flowing", but no local receiver/artifact —
                    // "their collector owns the data" in this mode.
                    Assert.True(collector.Enabled);
                    Assert.False(collector.HasLocalArtifact);
                    Assert.Equal(fakeJaeger.Endpoint, collector.Endpoint);
                    Assert.Equal(fakeJaeger.Endpoint, collector.ChildEnvironment["OTEL_EXPORTER_OTLP_ENDPOINT"]);

                    // The parent must still export its own spans (test.*/cicd.* live here) rather
                    // than going dark just because the receiver is someone else's.
                    using var activity = HarnessTelemetry.StartSpec(specId, "category", "domain");
                    HarnessTelemetry.SetVerdict(activity, "passed");

                    // Disposing `collector` below force-flushes its ParentProviders, POSTing the
                    // span to fakeJaeger.Endpoint over the stock OTLP exporter.
                }
                finally
                {
                    Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
                }
            } // fakeJaeger disposed: its writer's FileStreams close, so its artifact is now readable.

            Assert.False(File.Exists(artifact + ".traces.pb"));

            var received = OtlpArtifactReader.ReadTraces(fakeJaegerArtifact)
                .SelectMany(r => r.ResourceSpans.SelectMany(rs => rs.ScopeSpans.SelectMany(ss => ss.Spans)))
                .ToList();
            Assert.Contains(received, s => s.Name == specId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
            File.Delete(fakeJaegerArtifact + ".traces.pb");
            File.Delete(fakeJaegerArtifact + ".metrics.pb");
            File.Delete(fakeJaegerArtifact + ".logs.pb");
            File.Delete(artifact + ".traces.pb");
            File.Delete(artifact + ".metrics.pb");
            File.Delete(artifact + ".logs.pb");
        }
    }

    private static ExportTraceServiceRequest MakeRequest(string spanName)
    {
        var span = new Span
        {
            Name = spanName,
            TraceId = ByteString.CopyFrom(new byte[16]),
            SpanId = ByteString.CopyFrom(new byte[8]),
        };
        var scopeSpans = new ScopeSpans();
        scopeSpans.Spans.Add(span);
        var resourceSpans = new ResourceSpans();
        resourceSpans.ScopeSpans.Add(scopeSpans);
        var request = new ExportTraceServiceRequest();
        request.ResourceSpans.Add(resourceSpans);
        return request;
    }
}
