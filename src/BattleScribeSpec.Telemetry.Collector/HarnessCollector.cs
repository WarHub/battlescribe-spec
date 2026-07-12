using System.Net;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace BattleScribeSpec.Telemetry.Collector;

/// <summary>
/// An in-process OTLP/HTTP receiver bound to an ephemeral loopback port, plus the run artifact.
/// </summary>
/// <remarks>
/// <para>
/// The parent hosts this and hands children <see cref="ChildEnvironment"/>. Children therefore use
/// their <em>stock</em> OTel SDK exporter and need no harness-specific code — which is what lets a
/// third-party adapter in any language appear in our traces.
/// </para>
/// <para>
/// Protobuf only. OpenTelemetry .NET's exporter implements gRPC and HTTP/protobuf and has no
/// <c>http/json</c>; Python and JS default to <c>http/protobuf</c>. So protobuf covers every stock
/// SDK, and a JSON body is rejected with 415 rather than silently dropped.
/// </para>
/// <para>
/// Fail-open: if the port cannot be bound, <see cref="StartAsync"/> returns a disabled collector
/// with an empty <see cref="ChildEnvironment"/>. Telemetry must never fail a run.
/// </para>
/// </remarks>
public sealed class HarnessCollector : IAsyncDisposable
{
    private readonly WebApplication? _app;
    private readonly OtlpArtifactWriter? _writer;
    private readonly ParentProviders? _providers;

    private HarnessCollector(WebApplication? app, OtlpArtifactWriter? writer, ParentProviders? providers, string endpoint)
    {
        _app = app;
        _writer = writer;
        _providers = providers;
        Endpoint = endpoint;
    }

    /// <summary>The receiver's base URL, e.g. <c>http://127.0.0.1:53411</c>. Empty when disabled.</summary>
    public string Endpoint { get; }

    /// <summary>True when the receiver is listening and telemetry is being recorded.</summary>
    public bool Enabled => _app is not null;

    /// <summary>
    /// Environment to layer onto child adapter processes so their stock OTLP exporter reaches us.
    /// Empty when the collector is disabled — children then simply do not export.
    /// </summary>
    public IReadOnlyDictionary<string, string> ChildEnvironment =>
        Enabled
            ? new Dictionary<string, string>
            {
                // A BASE url — the SDK appends v1/traces, v1/metrics, v1/logs, which is exactly what
                // the receiver maps. (This append only happens for the env var; assigning
                // OtlpExporterOptions.Endpoint in code disables it.)
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = Endpoint,
                ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
                // Different service.name from the parent's "bs-spec" ON PURPOSE: that is what makes
                // them two nodes with an edge in a service graph rather than one anonymous blob.
                ["OTEL_SERVICE_NAME"] = "bs-engine-host",
                // Short batch delay: a hard-killed child (the BattleScribe JVM can take its process
                // down) loses whatever is still buffered, so keep the window small.
                ["OTEL_BSP_SCHEDULE_DELAY"] = "500",
                // Metrics default to a 60s export interval — a short-lived host would emit nothing
                // at all, and a killed one certainly wouldn't.
                ["OTEL_METRIC_EXPORT_INTERVAL"] = "1000",
                ["OTEL_TRACES_SAMPLER"] = "always_on",
            }
            : [];

    /// <summary>
    /// Bind a receiver on <c>127.0.0.1:0</c> and begin recording to <paramref name="artifactPath"/>
    /// (which gains <c>.traces.pb</c> / <c>.metrics.pb</c> / <c>.logs.pb</c>).
    /// </summary>
    /// <param name="artifactPath">The base path for the run artifact.</param>
    /// <param name="ct">Cancellation token for starting the receiver.</param>
    public static async Task<HarnessCollector> StartAsync(string artifactPath, CancellationToken ct = default)
    {
        OtlpArtifactWriter? writer = null;
        WebApplication? app = null;
        try
        {
            writer = new OtlpArtifactWriter(artifactPath);

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            // ListenLocalhost does not support dynamic port 0 ("Dynamic port binding is not
            // supported when binding to localhost"); bind the loopback IPv4 address explicitly.
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1AndHttp2));

            app = builder.Build();
            MapOtlp(app, writer);

            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fail-open. A run must never die because telemetry could not start. But by this
            // point the writer may already have opened three exclusive FileStreams (and app may
            // already be built) — if we return here without disposing them, those handles leak
            // for the process lifetime and, on Windows, keep the artifact files locked so a retry
            // at the same path fails too.
            Console.Error.WriteLine($"[telemetry] collector disabled: {ex.Message}");
            if (app is not null)
            {
                await app.DisposeAsync().ConfigureAwait(false);
            }

            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }

            return new HarnessCollector(app: null, writer: null, providers: null, endpoint: "");
        }

        var endpoint = app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));

        // The parent's own spans and metrics must reach the artifact too. Use the STOCK SDK,
        // pointed at our own loopback receiver — hand-rolling this drops non-string tags, span
        // kind, span status and events, and emits an empty Resource.
        var providers = ParentProviders.Attach(endpoint, serviceName: "bs-spec");

        return new HarnessCollector(app, writer, providers, endpoint);
    }

    private static void MapOtlp(WebApplication app, OtlpArtifactWriter writer)
    {
        // Cast to Delegate: a bare Func<HttpContext, Task<IResult>> lambda is also convertible to
        // RequestDelegate (Func<HttpContext, Task>) via delegate return-type covariance, and the
        // compiler prefers that overload — which discards the IResult instead of writing it to the
        // response. ASP0016 flags this exact trap.
        app.MapPost("/v1/traces", (Delegate)((HttpContext ctx) => ReceiveAsync(
            ctx,
            body => writer.WriteAsync(ExportTraceServiceRequest.Parser.ParseFrom(body)),
            new ExportTraceServiceResponse())));

        app.MapPost("/v1/metrics", (Delegate)((HttpContext ctx) => ReceiveAsync(
            ctx,
            body => writer.WriteAsync(ExportMetricsServiceRequest.Parser.ParseFrom(body)),
            new ExportMetricsServiceResponse())));

        app.MapPost("/v1/logs", (Delegate)((HttpContext ctx) => ReceiveAsync(
            ctx,
            body => writer.WriteAsync(ExportLogsServiceRequest.Parser.ParseFrom(body)),
            new ExportLogsServiceResponse())));
    }

    private static async Task<IResult> ReceiveAsync(HttpContext ctx, Func<Stream, Task> parse, IMessage success)
    {
        var contentType = ctx.Request.ContentType ?? "";
        if (!contentType.StartsWith("application/x-protobuf", StringComparison.OrdinalIgnoreCase))
        {
            // OTLP/JSON is deliberately unsupported — be loud, do not silently drop telemetry.
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        try
        {
            // Google.Protobuf's Parser.ParseFrom(Stream) reads synchronously, but Kestrel's
            // request body disallows synchronous reads by default. Buffer it asynchronously
            // first, then parse from the (sync-safe) in-memory copy.
            using var buffer = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted).ConfigureAwait(false);
            buffer.Position = 0;
            await parse(buffer).ConfigureAwait(false);
        }
        catch (InvalidProtocolBufferException ex)
        {
            return Results.BadRequest(ex.Message);
        }

        // OTLP: "On success ... the response body MUST be a Protobuf-encoded
        // Export<signal>ServiceResponse message" and "the server MUST use the same 'Content-Type'
        // in the response as it received". partial_success stays unset on success.
        //
        // An empty 200 would appear to work: OpenTelemetry .NET never deserializes the response
        // body, so every test here and every .NET child would be perfectly happy — while Python
        // and JS SDKs log deserialization errors. A receiver that is compliant only for the one
        // language we happen to use defeats the entire reason we chose OTLP.
        return Results.Bytes(success.ToByteArray(), "application/x-protobuf");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Providers must flush and dispose BEFORE the web app stops, or the final export has
        // nowhere to land.
        _providers?.Dispose();

        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
