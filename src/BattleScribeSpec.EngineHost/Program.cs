using System.CommandLine;
using BattleScribeSpec.EngineHost;
using BattleScribeSpec.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// The parent injects OTEL_EXPORTER_OTLP_ENDPOINT when it is collecting. Absent -> no exporter, no
// cost. Everything else (protocol, service name, resource attributes, batch delay, sampler) is read
// by the SDK from the standard OTEL_* env vars the parent set. There is deliberately NO bespoke
// configuration here: a third-party adapter in any language must be able to do exactly this with
// its own stock SDK, so our own host is held to the same contract.
var collecting = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") is { Length: > 0 };

// Note there is no ConfigureResource(...AddService(...)) call here: AddService would override the
// OTEL_SERVICE_NAME the parent set via HarnessCollector.ChildEnvironment, and relying on the env
// var is exactly what a third-party adapter would do. Keeping our own host on the same path keeps
// us honest about the contract we advertise.
using var tracerProvider = collecting
    ? Sdk.CreateTracerProviderBuilder()
        .AddSource(HarnessTelemetry.SourceName)
        .AddOtlpExporter()
        .Build()
    : null;

// Runtime instrumentation (CPU, GC, thread pool) answers "are we CPU-saturated at N workers, or
// merely I/O-blocked?" — the question a follow-up auto-tuner needs, and it's a free OTel metric.
using var meterProvider = collecting
    ? Sdk.CreateMeterProviderBuilder()
        .AddMeter(HarnessTelemetry.MeterName)
        .AddRuntimeInstrumentation()
        .AddOtlpExporter()
        .Build()
    : null;

var root = new RootCommand("bs-engine-host — built-in BattleScribe/NewRecruit engines behind the NDJSON adapter protocol.");
root.Subcommands.Add(ServeCommand.Create());
root.Subcommands.Add(ProbeCommand.Create());
root.Subcommands.Add(DiscoverCommand.Create());

return await root.Parse(args).InvokeAsync();
