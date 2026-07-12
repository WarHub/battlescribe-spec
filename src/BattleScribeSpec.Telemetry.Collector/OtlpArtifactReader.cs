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
    public static IEnumerable<ExportTraceServiceRequest> ReadTraces(string path)
    {
        var file = path.EndsWith(".traces.pb", StringComparison.Ordinal) ? path : path + ".traces.pb";
        if (!File.Exists(file))
        {
            yield break;
        }

        using var stream = File.OpenRead(file);
        while (stream.Position < stream.Length)
        {
            ExportTraceServiceRequest? request;
            try
            {
                request = ExportTraceServiceRequest.Parser.ParseDelimitedFrom(stream);
            }
            catch (Google.Protobuf.InvalidProtocolBufferException)
            {
                yield break; // truncated tail — the writer died mid-message.
            }

            yield return request;
        }
    }
}
