using System.Net.Http.Headers;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;
using BattleScribeSpec.Telemetry;
using BattleScribeSpec.Telemetry.Collector;

namespace BattleScribeSpec.Tests.Features;

[Trait("Category", "Unit")]
public sealed class TelemetryCollectorTests
{
    [Fact]
    public async Task PostedProtobufSpans_LandInTheArtifact()
    {
        var artifact = Path.Combine(Path.GetTempPath(), $"bsspec-otlp-{Guid.NewGuid():N}.pb");
        try
        {
            await using (var collector = await HarnessCollector.StartAsync(artifact, TestContext.Current.CancellationToken))
            {
                Assert.StartsWith("http://127.0.0.1:", collector.Endpoint, StringComparison.Ordinal);

                using var client = new HttpClient();
                var request = MakeRequest("spec-under-test");
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
                r.ResourceSpans.SelectMany(rs => rs.ScopeSpans.SelectMany(ss => ss.Spans))));
            Assert.Equal("spec-under-test", span.Name);
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
        var artifact = Path.Combine(Path.GetTempPath(), $"bsspec-otlp-{Guid.NewGuid():N}.pb");
        // HarnessTelemetry's ActivitySource is a process-wide static, and the xUnit run has other
        // tests emitting spans on it concurrently (e.g. adapter-command spans from unrelated
        // collections). ParentProviders.Attach subscribes to the source BY NAME, so this test's
        // artifact can legitimately contain spans from those other tests too — a unique span name
        // is what lets us pick "ours" out of that noise deterministically, rather than assuming
        // the artifact holds exactly one span.
        var specId = $"parent-spec-under-test-{Guid.NewGuid():N}";
        try
        {
            await using (var collector = await HarnessCollector.StartAsync(artifact, TestContext.Current.CancellationToken))
            {
                Assert.True(collector.Enabled);

                using var activity = HarnessTelemetry.StartSpec(specId, "category", "domain");
                HarnessTelemetry.SetVerdict(activity, "passed");
                ResourceMetrics.Acquired("test-resource");
                ResourceMetrics.Released("test-resource");

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
