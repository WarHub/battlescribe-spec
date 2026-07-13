using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BattleScribeSpec.Telemetry.Collector;

/// <summary>
/// The parent process's own OpenTelemetry providers, exporting over OTLP to the given endpoint
/// — normally our own loopback receiver, or the user's collector when they set
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> themselves.
/// </summary>
/// <remarks>
/// The parent's <c>service.name</c> is <c>bs-spec</c> and the child's is <c>bs-engine-host</c>.
/// Different names on purpose: that is what makes them two nodes with an edge between them in a
/// service graph, rather than one anonymous blob.
/// </remarks>
internal sealed class ParentProviders : IDisposable
{
    private readonly TracerProvider _tracer;
    private readonly MeterProvider _meter;

    private ParentProviders(TracerProvider tracer, MeterProvider meter)
    {
        _tracer = tracer;
        _meter = meter;
    }

    /// <summary>Build and attach the parent's tracer and meter providers.</summary>
    /// <param name="endpoint">The base OTLP endpoint, e.g. <c>http://127.0.0.1:53411</c>.</param>
    /// <param name="serviceName">The <c>service.name</c> resource attribute for this process.</param>
    public static ParentProviders Attach(string endpoint, string serviceName)
    {
        // A caller-supplied endpoint with a trailing slash (e.g. from a user's
        // OTEL_EXPORTER_OTLP_ENDPOINT) must not become "//v1/traces" below.
        var trimmedEndpoint = endpoint.TrimEnd('/');

        var resource = ResourceBuilder.CreateDefault().AddService(serviceName);

        var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(HarnessTelemetry.SourceName)
            .SetResourceBuilder(resource)
            .SetSampler(new AlwaysOnSampler())
            .AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri($"{trimmedEndpoint}/v1/traces");
                // The default protocol on net10.0 is gRPC (HttpProtobuf is the default only on
                // NETFRAMEWORK/NETSTANDARD2_0). Our receiver is HTTP-only — no gRPC service is
                // mapped — so a gRPC export would 404 silently (OTLP export is fail-open) and the
                // parent's own spans would never reach the artifact.
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
            })
            .Build();

        var meter = Sdk.CreateMeterProviderBuilder()
            .AddMeter(HarnessTelemetry.MeterName)
            .SetResourceBuilder(resource)
            .AddOtlpExporter((exporterOptions, readerOptions) =>
            {
                exporterOptions.Endpoint = new Uri($"{trimmedEndpoint}/v1/metrics");
                exporterOptions.Protocol = OtlpExportProtocol.HttpProtobuf;

                // Without this, the SDK default (60s) applies: `harness.resource.count` is
                // recorded ONLY in this (parent) process for the "adapter-process" kind (see
                // AdapterProcess.Acquired/Released), and for any --workers run shorter than 60s
                // the ONLY exported data point is the final ForceFlush on Dispose — AFTER
                // SpecSuiteRunner has disposed every AdapterProcess. That reports a confident,
                // sampled "adapter-process: 0" instead of "not sampled", for the one resource kind
                // --workers N directly controls. Mirrors ChildEnvironment's
                // OTEL_METRIC_EXPORT_INTERVAL=1000 for children and
                // TelemetryAssemblyFixture's 2000 for the xUnit path — same problem, same fix,
                // applied to the one process neither of those already covers.
                //
                // `??=`, NOT a bare assignment: PeriodicExportingMetricReaderOptions's own
                // constructor already parsed OTEL_METRIC_EXPORT_INTERVAL (if the caller set one —
                // e.g. XunitTelemetryTests' own short-lived collector, or a user's own override)
                // into this same property. A bare `=` here would silently clobber that explicit,
                // more specific choice with this default; `??=` only fills the gap when nothing
                // upstream already asked for a specific interval.
                readerOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds ??= 1000;
            })
            .Build();

        return new ParentProviders(tracer, meter);
    }

    /// <summary>Flush and shut down. Disposal order matters: providers first, then the receiver.</summary>
    public void Dispose()
    {
        // An explicit timeout, not Timeout.Infinite (ForceFlush()'s default): against an
        // unreachable external collector (a user-set OTEL_EXPORTER_OTLP_ENDPOINT that is down),
        // an unbounded flush would only be bounded by the exporter's own retry budget — telemetry
        // must never add material wall-clock to a run. 5s is generous for a loopback receiver and
        // still bounded for an external one.
        var flushTimeoutMs = 5000;
        _tracer.ForceFlush(flushTimeoutMs);
        _meter.ForceFlush(flushTimeoutMs);
        _tracer.Dispose();
        _meter.Dispose();
    }
}
