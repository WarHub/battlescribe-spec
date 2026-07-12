using OpenTelemetry;
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
        var resource = ResourceBuilder.CreateDefault().AddService(serviceName);

        var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(HarnessTelemetry.SourceName)
            .SetResourceBuilder(resource)
            .SetSampler(new AlwaysOnSampler())
            .AddOtlpExporter(o => o.Endpoint = new Uri($"{endpoint}/v1/traces"))
            .Build();

        var meter = Sdk.CreateMeterProviderBuilder()
            .AddMeter(HarnessTelemetry.MeterName)
            .SetResourceBuilder(resource)
            .AddOtlpExporter(o => o.Endpoint = new Uri($"{endpoint}/v1/metrics"))
            .Build();

        return new ParentProviders(tracer, meter);
    }

    /// <summary>Flush and shut down. Disposal order matters: providers first, then the receiver.</summary>
    public void Dispose()
    {
        _tracer.ForceFlush();
        _meter.ForceFlush();
        _tracer.Dispose();
        _meter.Dispose();
    }
}
