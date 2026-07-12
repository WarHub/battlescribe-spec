using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace BattleScribeSpec.Telemetry.Collector;

/// <summary>
/// Appends received OTLP requests to a length-delimited protobuf stream — the run artifact.
/// Lossless and exact: what the collector received is what lands on disk, and
/// <see cref="OtlpArtifactReader"/> reads it back with the same generated types.
/// </summary>
/// <remarks>
/// Traces, metrics and logs go to three sibling files rather than one interleaved stream, because
/// a length-delimited protobuf stream is only self-describing if every message has the same type.
/// </remarks>
public sealed class OtlpArtifactWriter : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileStream _traces;
    private readonly FileStream _metrics;
    private readonly FileStream _logs;

    /// <summary>Create the writer. <paramref name="basePath"/> gains <c>.traces.pb</c> / <c>.metrics.pb</c> / <c>.logs.pb</c>.</summary>
    /// <param name="basePath">The base path for the run artifact; signal-specific suffixes are appended.</param>
    public OtlpArtifactWriter(string basePath)
    {
        var directory = Path.GetDirectoryName(basePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _traces = File.Create(basePath + ".traces.pb");
        _metrics = File.Create(basePath + ".metrics.pb");
        _logs = File.Create(basePath + ".logs.pb");
    }

    /// <summary>Append a trace export request.</summary>
    /// <param name="request">The trace export request to append.</param>
    public Task WriteAsync(ExportTraceServiceRequest request) => AppendAsync(_traces, request);

    /// <summary>Append a metrics export request.</summary>
    /// <param name="request">The metrics export request to append.</param>
    public Task WriteAsync(ExportMetricsServiceRequest request) => AppendAsync(_metrics, request);

    /// <summary>Append a logs export request.</summary>
    /// <param name="request">The logs export request to append.</param>
    public Task WriteAsync(ExportLogsServiceRequest request) => AppendAsync(_logs, request);

    private async Task AppendAsync(FileStream stream, IMessage message)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            message.WriteDelimitedTo(stream);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _traces.DisposeAsync().ConfigureAwait(false);
        await _metrics.DisposeAsync().ConfigureAwait(false);
        await _logs.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
