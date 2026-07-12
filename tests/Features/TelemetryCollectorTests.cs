using System.Net.Http.Headers;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;
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
