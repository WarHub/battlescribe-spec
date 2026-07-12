using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace BattleScribeSpec.Telemetry.Collector;

/// <summary>Reads back the artifact written by <see cref="OtlpArtifactWriter"/>.</summary>
public static class OtlpArtifactReader
{
    /// <summary>
    /// Stream the trace export requests from a run artifact. Accepts either the base path or the
    /// <c>.traces.pb</c> file itself. A truncated final message (a hard-killed writer) is ignored
    /// rather than thrown — a partial trace is still evidence.
    /// </summary>
    /// <param name="path">The base artifact path, or the <c>.traces.pb</c> file itself.</param>
    public static IEnumerable<ExportTraceServiceRequest> ReadTraces(string path) =>
        ReadDelimited(path, ".traces.pb", ExportTraceServiceRequest.Parser.ParseDelimitedFrom);

    /// <summary>
    /// Stream the metrics export requests from a run artifact. Accepts either the base path or the
    /// <c>.metrics.pb</c> file itself. A truncated final message (a hard-killed writer) is ignored
    /// rather than thrown — a partial metrics snapshot is still evidence.
    /// </summary>
    /// <param name="path">The base artifact path, or the <c>.metrics.pb</c> file itself.</param>
    public static IEnumerable<ExportMetricsServiceRequest> ReadMetrics(string path) =>
        ReadDelimited(path, ".metrics.pb", ExportMetricsServiceRequest.Parser.ParseDelimitedFrom);

    private static IEnumerable<T> ReadDelimited<T>(string path, string suffix, Func<Stream, T> parseDelimitedFrom)
    {
        var file = path.EndsWith(suffix, StringComparison.Ordinal) ? path : path + suffix;
        if (!File.Exists(file))
        {
            yield break;
        }

        using var stream = File.OpenRead(file);
        while (stream.Position < stream.Length)
        {
            T request;
            try
            {
                request = parseDelimitedFrom(stream);
            }
            catch (Google.Protobuf.InvalidProtocolBufferException)
            {
                yield break; // truncated tail — the writer died mid-message.
            }

            yield return request;
        }
    }
}
