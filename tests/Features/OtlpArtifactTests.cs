using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace BattleScribeSpec.Tests.Features;

[Trait("Category", "Unit")]
public sealed class OtlpArtifactTests
{
    [Fact]
    public void DelimitedStream_RoundTripsMultipleRequests()
    {
        var first = MakeRequest("spec-one");
        var second = MakeRequest("spec-two");

        using var stream = new MemoryStream();
        first.WriteDelimitedTo(stream);
        second.WriteDelimitedTo(stream);

        stream.Position = 0;
        var read = new List<ExportTraceServiceRequest>();
        while (stream.Position < stream.Length)
        {
            read.Add(ExportTraceServiceRequest.Parser.ParseDelimitedFrom(stream));
        }

        Assert.Equal(2, read.Count);
        Assert.Equal("spec-one", read[0].ResourceSpans[0].ScopeSpans[0].Spans[0].Name);
        Assert.Equal("spec-two", read[1].ResourceSpans[0].ScopeSpans[0].Spans[0].Name);
    }

    private static ExportTraceServiceRequest MakeRequest(string spanName)
    {
        var span = new Span
        {
            Name = spanName,
            TraceId = ByteString.CopyFrom(new byte[16]),
            SpanId = ByteString.CopyFrom(new byte[8]),
        };
        span.Attributes.Add(new KeyValue
        {
            Key = "test.case.name",
            Value = new AnyValue { StringValue = spanName },
        });

        var scopeSpans = new ScopeSpans();
        scopeSpans.Spans.Add(span);
        var resourceSpans = new ResourceSpans();
        resourceSpans.ScopeSpans.Add(scopeSpans);

        var request = new ExportTraceServiceRequest();
        request.ResourceSpans.Add(resourceSpans);
        return request;
    }
}
