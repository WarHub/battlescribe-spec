# Harness Telemetry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the harness observable across every process and both entry points (CLI and xUnit) using OpenTelemetry, and ship `bs-spec compare` as the verdict-equality rail that any future tuning change must pass.

**Architecture:** Instrument with the BCL-native OTel API (`ActivitySource` + `Meter`). The parent process hosts an OTLP/HTTP receiver on an ephemeral `127.0.0.1` port and injects `OTEL_EXPORTER_OTLP_ENDPOINT` into child adapter processes, which export with the stock OTel SDK. The parent writes everything to a length-delimited OTLP protobuf artifact.

**Tech Stack:** .NET 10, `System.Diagnostics.ActivitySource`/`Meter` (BCL), `Google.Protobuf` + `Grpc.Tools` (message codegen only — no gRPC), ASP.NET Core minimal API (receiver), OpenTelemetry SDK + OTLP exporter (child side only), xUnit v3.

**Spec:** `docs/superpowers/specs/2026-07-12-harness-telemetry-design.md`

## Global Constraints

These bind **every** task. Violating any one of them fails the build or the review.

- **TFM `net10.0`**, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild=true`, `AnalysisLevel=latest-recommended`, `GenerateDocumentationFile=true`. **An analyzer warning is a build error.** Public types need XML doc comments (CS1591 is suppressed, but style analyzers are not).
- **`IsAotCompatible=true` is set on `src/BattleScribeSpec.Cli` and `src/BattleScribeSpec.TestKit`.** These two projects run the trim/AOT analyzers. **Any reflection-based code they call that is annotated `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]` produces IL2026/IL3050 = build error.** `ActivitySource` and `Meter` are AOT-safe and may be used freely. The OpenTelemetry **SDK** is not — it must never be referenced from `Cli` or `TestKit`. It belongs in `BattleScribeSpec.Telemetry.Collector` (not AOT-marked) and in `bs-engine-host`, and is reached from `Cli` only through the non-annotated `HarnessCollector` facade.
- **`bs-engine-host` (`src/BattleScribeSpec.EngineHost`) is NOT AOT-marked.** The OTel SDK + OTLP exporter go there.
- **Central package management.** New packages require a `<PackageVersion>` entry in `Directory.Packages.props`, never a `Version=` on the `PackageReference`.
- **`RestorePackagesWithLockFile=true`.** Any package change requires `dotnet restore --force-evaluate` to regenerate `packages.lock.json`, or CI restore fails.
- **`stdout` is the adapter protocol wire.** In `bs-engine-host serve`, telemetry must NEVER write to `Console.Out`. Only `Console.Error` is free.
- **Telemetry is fail-open.** A receiver that is down, a port that won't bind, an export that errors — none of these may fail a run, change a verdict, or add material wall-clock. Log once, never per-span.
- **Adding any wire field to `ProtocolCommand`/`ProtocolResponse` breaks `tests/Infrastructure/ProtocolSchemaDriftTests.cs`** until `docs/protocol-schema.json` is hand-edited for **all 15 command `$defs` and 12 response `$defs`**. There is no schema generator.
- Artifact output goes under `artifacts/` (repo uses `UseArtifactsOutput=true`).
- Commit after every task. Never commit to `main`; work on `perf/harness-reuse-and-parallelism`.

## File Structure

**New projects** (two, because the AOT boundary is real):

| Project | AOT? | Responsibility |
|---|---|---|
| `src/BattleScribeSpec.Telemetry` | **`IsAotCompatible=true`** | Instrumentation API only: `ActivitySource`, `Meter`, span/event helpers, `traceparent` parse/format. BCL only, zero packages. Referenced by TestKit, EngineHost, drivers. |
| `src/BattleScribeSpec.Telemetry.Collector` | **no** | Vendored OTel `.proto` + codegen, the OTLP/HTTP receiver, the `.otlp.pb` artifact writer/reader. Referenced by Cli and tests **only**. |

This split is the whole point: the hot instrumentation path is AOT-safe and can be called from anywhere; the heavy receiver is quarantined away from `Cli`'s AOT analyzer.

**Files created:**

- `src/BattleScribeSpec.Telemetry/BattleScribeSpec.Telemetry.csproj`
- `src/BattleScribeSpec.Telemetry/HarnessTelemetry.cs` — `ActivitySource`/`Meter` singletons + span helpers
- `src/BattleScribeSpec.Telemetry/ResourceMetrics.cs` — live-resource up-down counter + cold-start/reuse histograms
- `src/BattleScribeSpec.Telemetry.Collector/BattleScribeSpec.Telemetry.Collector.csproj`
- `src/BattleScribeSpec.Telemetry.Collector/opentelemetry/proto/**` — 8 vendored `.proto` files
- `src/BattleScribeSpec.Telemetry.Collector/HarnessCollector.cs` — receiver + lifecycle
- `src/BattleScribeSpec.Telemetry.Collector/OtlpArtifactWriter.cs` — length-delimited protobuf writer
- `src/BattleScribeSpec.Telemetry.Collector/OtlpArtifactReader.cs` — reader for `compare`/summary
- `src/BattleScribeSpec.Telemetry.Collector/ParentProviders.cs` — the parent's own OTel SDK TracerProvider/MeterProvider
- `src/BattleScribeSpec.Cli/Commands/CompareCommand.cs` — `bs-spec compare`
- `THIRD-PARTY-NOTICES.txt`
- `docs/telemetry.md`
- Tests: `tests/Features/TelemetryCollectorTests.cs`, `tests/Features/OtlpArtifactTests.cs`, `tests/Features/TraceparentPropagationTests.cs`, `tests/Features/SpecSuiteRunnerTelemetryTests.cs`, `tests/BattleScribeSpec.Cli.Tests/CompareCommandTests.cs`

**Files modified:** `Directory.Packages.props`, `BattleScribeSpec.slnx`, `SpecSuiteRunner.cs`, `SpecSuiteOptions.cs`, `SuiteJsonContext.cs`, `AdapterProcess.cs`, `ProtocolMessages.cs`, `AdapterHandler.cs`, `docs/protocol-schema.json`, `EngineSpec.cs`, `EngineHostLocator.cs`, `RunBatch.cs`, `RunCommand.cs`, `VerifyCommand.cs`, `CommandFactory.cs`, `Program.cs` (EngineHost), the 3 diagnostics classes, the 11 xUnit fixtures, `.github/workflows/ci.yml`, `docs/warm-reuse.md`.

**Files deleted:** `scripts/bench-warm-reuse.ps1` (Task 12).

---

### Task 1: Telemetry projects, vendored protos, green build

This task ships **no features**. It exists to prove the riskiest thing in the plan — that protobuf codegen, two new projects, and new packages survive `TreatWarningsAsErrors` + `IsAotCompatible` + lock files — *before* any feature work is built on top. If this task is hard, everything after it would have been harder.

**Files:**
- Create: `src/BattleScribeSpec.Telemetry/BattleScribeSpec.Telemetry.csproj`
- Create: `src/BattleScribeSpec.Telemetry.Collector/BattleScribeSpec.Telemetry.Collector.csproj`
- Create: `src/BattleScribeSpec.Telemetry.Collector/opentelemetry/proto/**` (8 files)
- Create: `THIRD-PARTY-NOTICES.txt`
- Modify: `Directory.Packages.props`, `BattleScribeSpec.slnx`
- Test: `tests/Features/OtlpArtifactTests.cs`

**Interfaces:**
- Produces: the generated protobuf types in namespaces `OpenTelemetry.Proto.Collector.Trace.V1` (`ExportTraceServiceRequest`), `OpenTelemetry.Proto.Trace.V1` (`Span`, `ResourceSpans`, `ScopeSpans`), `OpenTelemetry.Proto.Common.V1` (`KeyValue`, `AnyValue`), `OpenTelemetry.Proto.Resource.V1` (`Resource`), and the metrics/logs equivalents.

- [ ] **Step 1: Vendor the 8 `.proto` files**

Copy from the upstream `open-telemetry/opentelemetry-proto` repo (Apache-2.0), preserving the directory layout exactly — the `import` statements inside the files depend on it:

```
src/BattleScribeSpec.Telemetry.Collector/opentelemetry/proto/common/v1/common.proto
src/BattleScribeSpec.Telemetry.Collector/opentelemetry/proto/resource/v1/resource.proto
src/BattleScribeSpec.Telemetry.Collector/opentelemetry/proto/trace/v1/trace.proto
src/BattleScribeSpec.Telemetry.Collector/opentelemetry/proto/logs/v1/logs.proto
src/BattleScribeSpec.Telemetry.Collector/opentelemetry/proto/metrics/v1/metrics.proto
src/BattleScribeSpec.Telemetry.Collector/opentelemetry/proto/collector/trace/v1/trace_service.proto
src/BattleScribeSpec.Telemetry.Collector/opentelemetry/proto/collector/metrics/v1/metrics_service.proto
src/BattleScribeSpec.Telemetry.Collector/opentelemetry/proto/collector/logs/v1/logs_service.proto
```

Get them with (run from repo root):

```bash
tmp=$(mktemp -d) && git clone --depth 1 https://github.com/open-telemetry/opentelemetry-proto.git "$tmp"
dest=src/BattleScribeSpec.Telemetry.Collector/opentelemetry/proto
mkdir -p "$dest"
cp -r "$tmp"/opentelemetry/proto/{common,resource,trace,logs,metrics,collector} "$dest"/
find "$dest" -name '*.proto' | sort   # expect exactly the 8 files above
rm -rf "$tmp"
```

**Do not edit these files, ever.** They already carry `option csharp_namespace = "OpenTelemetry.Proto...."`, which is what produces the C# namespaces above.

- [ ] **Step 2: Add package versions**

In `Directory.Packages.props`, add to the `<ItemGroup>` (keep alphabetical order):

```xml
    <PackageVersion Include="Google.Protobuf" Version="3.34.1" />
    <PackageVersion Include="Grpc.Tools" Version="2.80.0" />
```

`Grpc.Tools` is build-time only (the `PrivateAssets="all"` goes on the `PackageReference` in the csproj, below). We are **not** adding `Grpc.AspNetCore` — we generate message types only, no gRPC services.

- [ ] **Step 3: Create the instrumentation project**

`src/BattleScribeSpec.Telemetry/BattleScribeSpec.Telemetry.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <!-- Referenced by TestKit and Cli, which are AOT-analyzed. This project must stay
         AOT-clean: BCL only (ActivitySource/Meter), no OpenTelemetry SDK, no reflection. -->
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>

</Project>
```

Zero package references. That is deliberate and load-bearing.

- [ ] **Step 4: Create the collector project**

`src/BattleScribeSpec.Telemetry.Collector/BattleScribeSpec.Telemetry.Collector.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <!-- NOT AOT-compatible on purpose: hosts ASP.NET Core and Google.Protobuf reflection.
         Never reference this from Cli's or TestKit's AOT-analyzed code paths except through
         the non-annotated HarnessCollector facade. -->
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Google.Protobuf" />
    <PackageReference Include="Grpc.Tools" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <!-- Message types only (GrpcServices="None"): we receive OTLP over HTTP/protobuf and do
         not serve gRPC, so no service base classes or client stubs are needed. -->
    <Protobuf Include="opentelemetry/**/*.proto" GrpcServices="None" ProtoRoot="." />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\BattleScribeSpec.Telemetry\BattleScribeSpec.Telemetry.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 5: Add both projects to the solution**

```bash
dotnet sln BattleScribeSpec.slnx add src/BattleScribeSpec.Telemetry/BattleScribeSpec.Telemetry.csproj
dotnet sln BattleScribeSpec.slnx add src/BattleScribeSpec.Telemetry.Collector/BattleScribeSpec.Telemetry.Collector.csproj
```

- [ ] **Step 6: Add `THIRD-PARTY-NOTICES.txt` at the repo root**

```
This product includes software developed by third parties.

--------------------------------------------------------------------------------
OpenTelemetry Protocol (OTLP) .proto definitions
Source: https://github.com/open-telemetry/opentelemetry-proto
License: Apache License 2.0
Location: src/BattleScribeSpec.Telemetry.Collector/opentelemetry/proto/

Vendored verbatim. There is no official NuGet package exposing the OTLP protobuf
types for .NET (OpenTelemetry.Exporter.OpenTelemetryProtocol generates them as
`internal`), so server-side OTLP consumers must vendor the .proto files and run
Grpc.Tools. This is the pattern used by .NET Aspire's dashboard.

Full license text: https://www.apache.org/licenses/LICENSE-2.0
--------------------------------------------------------------------------------
```

- [ ] **Step 7: Regenerate lock files**

```bash
dotnet restore --force-evaluate
```

Expected: succeeds, and `git status` shows modified `packages.lock.json` files. **If you skip this, CI restore fails.**

- [ ] **Step 8: Write the failing round-trip test**

The artifact format is a length-delimited protobuf stream. Prove the generated types exist and that write→read round-trips, because everything downstream (`compare`, the summary) depends on it.

Create `tests/Features/OtlpArtifactTests.cs`:

```csharp
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
```

Add the project reference to `tests/BattleScribeSpec.Tests.csproj`:

```xml
    <ProjectReference Include="..\src\BattleScribeSpec.Telemetry.Collector\BattleScribeSpec.Telemetry.Collector.csproj" />
```

- [ ] **Step 9: Run the test — it must fail to COMPILE first, then pass**

```bash
dotnet build
```

Expected on a correct setup: **build succeeds**. If it fails with `CS0246: The type or namespace name 'OpenTelemetry' could not be found`, the `Protobuf` item glob or `ProtoRoot` is wrong — codegen did not run.

Then:

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~OtlpArtifactTests"
```

Expected: **1 passed**.

- [ ] **Step 10: Verify the AOT boundary actually holds**

This is the step that de-risks the whole plan. Confirm `Cli` and `TestKit` still build clean:

```bash
dotnet build src/BattleScribeSpec.Cli/BattleScribeSpec.Cli.csproj
dotnet build src/BattleScribeSpec.TestKit/BattleScribeSpec.TestKit.csproj
```

Expected: **0 errors, 0 warnings.** If you see `IL2026`/`IL3050`, something AOT-hostile leaked into an AOT-analyzed project — do not suppress it, fix the reference direction.

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "feat(telemetry): scaffold Telemetry + Telemetry.Collector projects, vendor OTLP protos (#271)"
```

---

### Task 2: `HarnessTelemetry` — the instrumentation API

**Files:**
- Create: `src/BattleScribeSpec.Telemetry/HarnessTelemetry.cs`
- Create: `src/BattleScribeSpec.Telemetry/ResourceMetrics.cs`
- Test: `tests/Features/HarnessTelemetryTests.cs`

**Interfaces:**
- Produces (every later task consumes these exact names):
  - `HarnessTelemetry.SourceName` = `"BattleScribeSpec.Harness"` (const string)
  - `HarnessTelemetry.MeterName` = `"BattleScribeSpec.Harness"` (const string)
  - `Activity? HarnessTelemetry.StartSpec(string specId, string category, string domain)`
  - `Activity? HarnessTelemetry.StartOp(string name, string? traceparent = null, ActivityKind kind = ActivityKind.Internal, string? tracestate = null)`
  - `void HarnessTelemetry.SetVerdict(Activity? activity, string status)` — writes `bsspec.verdict` (four-way) AND `test.case.result.status` (`pass`/`fail` only)
  - `string? HarnessTelemetry.CurrentTraceparent()` — W3C format of `Activity.Current`
  - `ResourceMetrics.Acquired(string kind)` / `ResourceMetrics.Released(string kind)` — up-down counter `harness.resource.count`
  - `ResourceMetrics.RecordEngineStart(string kind, bool reused, double seconds)` — histogram `harness.engine.start.duration`, unit `s`

- [ ] **Step 1: Write the failing test**

Create `tests/Features/HarnessTelemetryTests.cs`:

```csharp
using System.Diagnostics;
using BattleScribeSpec.Telemetry;

namespace BattleScribeSpec.Tests.Features;

[Trait("Category", "Unit")]
public sealed class HarnessTelemetryTests
{
    [Fact]
    public void StartSpec_EmitsTestSemanticConventions()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == HarnessTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Add,
        };
        ActivitySource.AddActivityListener(listener);

        using (var activity = HarnessTelemetry.StartSpec("entry/entry-basic", "entry", "roster"))
        {
            HarnessTelemetry.SetVerdict(activity, "expected-failure");
        }

        var span = Assert.Single(captured);
        Assert.Equal("entry/entry-basic", span.GetTagItem("test.case.name"));
        Assert.Equal("entry", span.GetTagItem("test.suite.name"));

        // OTel's test.case.result.status admits ONLY "pass" and "fail". Our four-way verdict
        // rides bsspec.verdict; emitting "expected-failure" into the standard attribute would
        // make us unreadable by the backends we adopted OTel to satisfy.
        Assert.Equal("pass", span.GetTagItem("test.case.result.status"));
        Assert.Equal("expected-failure", span.GetTagItem("bsspec.verdict"));
    }

    [Fact]
    public void StartOp_WithTraceparent_NestsUnderTheGivenParent()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == HarnessTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        // A well-formed W3C traceparent: version-traceid-spanid-flags.
        const string Traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

        using var child = HarnessTelemetry.StartOp("setup", Traceparent);

        Assert.NotNull(child);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", child.TraceId.ToHexString());
        Assert.Equal("b7ad6b7169203331", child.ParentSpanId.ToHexString());
    }

    [Fact]
    public void CurrentTraceparent_RoundTripsThroughStartOp()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == HarnessTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using var parent = HarnessTelemetry.StartOp("run");
        var traceparent = HarnessTelemetry.CurrentTraceparent();

        Assert.NotNull(traceparent);
        using var child = HarnessTelemetry.StartOp("spec", traceparent);

        Assert.NotNull(child);
        Assert.Equal(parent!.TraceId, child.TraceId);
        Assert.Equal(parent.SpanId, child.ParentSpanId);
    }
}
```

- [ ] **Step 2: Run it to confirm it fails**

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~HarnessTelemetryTests"
```

Expected: FAILS to compile — `The type or namespace name 'Telemetry' does not exist`.

- [ ] **Step 3: Implement `HarnessTelemetry`**

Create `src/BattleScribeSpec.Telemetry/HarnessTelemetry.cs`:

```csharp
using System.Diagnostics;

namespace BattleScribeSpec.Telemetry;

/// <summary>
/// The harness's instrumentation API. Uses only BCL primitives (<see cref="ActivitySource"/>,
/// <see cref="System.Diagnostics.Metrics.Meter"/>) so it stays AOT-safe and can be called from
/// the trim-analyzed Cli and TestKit projects. Emitting is free when nothing is listening.
/// </summary>
public static class HarnessTelemetry
{
    /// <summary>Name of the harness <see cref="ActivitySource"/>; listeners subscribe by this.</summary>
    public const string SourceName = "BattleScribeSpec.Harness";

    /// <summary>Name of the harness meter.</summary>
    public const string MeterName = "BattleScribeSpec.Harness";

    private static readonly ActivitySource Source = new(
        SourceName,
        typeof(HarnessTelemetry).Assembly.GetName().Version?.ToString());

    /// <summary>
    /// Start the span for one spec execution, tagged with OpenTelemetry's test semantic
    /// conventions (stability: Development) so off-the-shelf backends render conformance runs
    /// without an adapter. The span is named for the spec so a trace list is readable — OTel
    /// publishes no span-name convention for tests, so this is our choice, not a standard.
    /// </summary>
    public static Activity? StartSpec(string specId, string category, string domain)
    {
        var activity = Source.StartActivity(specId, ActivityKind.Internal);
        activity?.SetTag("test.case.name", specId);
        activity?.SetTag("test.suite.name", category);
        activity?.SetTag("bsspec.domain", domain);
        return activity;
    }

    /// <summary>
    /// Start an operation span. When <paramref name="traceparent"/> is a valid W3C trace-context
    /// header the span is parented to it — this is how a child process nests its work under the
    /// parent's spec span.
    /// </summary>
    /// <param name="kind">
    /// An adapter command is a remote call, so the sending side passes <see cref="ActivityKind.Client"/>
    /// and the handling side passes <see cref="ActivityKind.Server"/>. Jaeger's dependency graph and
    /// Tempo's servicegraph processor derive edges EXCLUSIVELY from CLIENT→SERVER pairs; with
    /// Internal on both sides there is no edge between bs-spec and bs-engine-host at all.
    /// </param>
    public static Activity? StartOp(
        string name,
        string? traceparent = null,
        ActivityKind kind = ActivityKind.Internal,
        string? tracestate = null)
    {
        if (traceparent is not null && ActivityContext.TryParse(traceparent, tracestate, out var parent))
        {
            return Source.StartActivity(name, kind, parent);
        }

        return Source.StartActivity(name, kind);
    }

    /// <summary>
    /// Record a spec's verdict: one of "passed", "failed", "expected-failure", "unexpected-pass".
    /// </summary>
    /// <remarks>
    /// OTel's <c>test.case.result.status</c> admits ONLY the values <c>pass</c> and <c>fail</c>, so the
    /// harness's four-way verdict lives on <c>bsspec.verdict</c> and is mapped down for the standard
    /// attribute. Emitting our richer vocabulary into the convention would make conformance runs
    /// unreadable to the backends we adopted OpenTelemetry in order to satisfy.
    /// </remarks>
    public static void SetVerdict(Activity? activity, string status)
    {
        activity?.SetTag("bsspec.verdict", status);
        activity?.SetTag("test.case.result.status", status is "passed" or "expected-failure" ? "pass" : "fail");

        if (status is "failed" or "unexpected-pass")
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }
    }

    /// <summary>
    /// The W3C <c>traceparent</c> for <see cref="Activity.Current"/>, or null when untraced.
    /// Send this over the adapter protocol so the child parents its spans correctly.
    /// </summary>
    public static string? CurrentTraceparent() => Activity.Current?.Id;
}
```

Note `Activity.Id` is already the W3C `traceparent` string when `Activity.DefaultIdFormat` is `W3C` — which is the default on .NET. No hand-formatting.

- [ ] **Step 4: Implement `ResourceMetrics`**

Create `src/BattleScribeSpec.Telemetry/ResourceMetrics.cs`:

```csharp
using System.Diagnostics.Metrics;

namespace BattleScribeSpec.Telemetry;

/// <summary>
/// Metrics for expensive, pooled resources (JVMs, browsers, browser contexts, adapter processes).
/// </summary>
/// <remarks>
/// <para>
/// <c>harness.resource.count</c> is the signal that makes the harness's unbounded parallelism
/// visible. Three in-process browser-context pools and a JVM can currently be alive at once
/// (xUnit's <c>maxParallelThreads</c> is unset, so collections run up to CPU-count wide) and
/// nothing in the system reports it. A span cannot express "how many are alive right now" —
/// only an up-down counter can.
/// </para>
/// </remarks>
public static class ResourceMetrics
{
    private static readonly Meter Meter = new(HarnessTelemetry.MeterName);

    // OTel naming: UpDownCounter names SHOULD NOT be pluralized -> "resource.count", not
    // "resources.live". The "{resource}" unit annotation is correct as a singular.
    private static readonly UpDownCounter<int> Live =
        Meter.CreateUpDownCounter<int>("harness.resource.count", unit: "{resource}",
            description: "Expensive resources currently alive, by kind.");

    // OTel: "When instruments are measuring durations, seconds (i.e. `s`) SHOULD be used."
    // The SDK's default explicit buckets ([0,5,10,25,...,10000]) are millisecond-tuned, so a
    // seconds-valued histogram would land EVERY engine start in a single bucket and make p50/p95
    // meaningless. Supply boundaries fitted to what we actually observe: ~1.6s for a Chromium
    // relaunch, considerably more for a JVM + JavaFX cold start.
    private static readonly Histogram<double> EngineStart =
        Meter.CreateHistogram<double>(
            "harness.engine.start.duration",
            unit: "s",
            description: "Engine acquisition cost, split by whether it was a cold start or a warm reuse.",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [0.01, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60],
            });

    /// <summary>Record that a resource of <paramref name="kind"/> became alive (e.g. "jvm", "browser", "browser-context", "adapter-process").</summary>
    public static void Acquired(string kind) =>
        Live.Add(1, new KeyValuePair<string, object?>("harness.resource.kind", kind));

    /// <summary>Record that a resource of <paramref name="kind"/> was released.</summary>
    public static void Released(string kind) =>
        Live.Add(-1, new KeyValuePair<string, object?>("harness.resource.kind", kind));

    /// <summary>
    /// Record what an engine cost to obtain, in <b>seconds</b>. <paramref name="reused"/> distinguishes
    /// a warm reuse from a cold start — this is the warm-reuse question, asked continuously rather
    /// than by a one-off benchmark script.
    /// </summary>
    public static void RecordEngineStart(string kind, bool reused, double seconds) =>
        EngineStart.Record(seconds,
            new KeyValuePair<string, object?>("harness.resource.kind", kind),
            new KeyValuePair<string, object?>("harness.engine.reused", reused));
}
```

- [ ] **Step 5: Run the tests — they must pass**

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~HarnessTelemetryTests"
```

Expected: **3 passed.**

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(telemetry): HarnessTelemetry instrumentation API (spans + resource metrics) (#271)"
```

---

### Task 3: Unify `SpecSuiteRunner`'s four per-spec paths, then instrument once

`SpecSuiteRunner.RunAsync` currently duplicates the per-spec body **four times**: the parallel roster lambda (`SpecSuiteRunner.cs:171-188`), the parallel gamedata lambda (`:194-209`), the sequential roster loop (`:248-259`) and the sequential gamedata loop (`:261-271`). Instrumenting all four would be four places to get wrong. **Unify first, instrument once.**

**Files:**
- Modify: `src/BattleScribeSpec.TestKit/Batch/SpecSuiteRunner.cs`
- Modify: `src/BattleScribeSpec.TestKit/Batch/SuiteJsonContext.cs` (add `durationMs`)
- Modify: `src/BattleScribeSpec.TestKit/Batch/SpecSuiteOutput.cs` (populate `durationMs`)
- Modify: `src/BattleScribeSpec.TestKit/BattleScribeSpec.TestKit.csproj` (reference Telemetry)
- Test: `tests/Features/SpecSuiteRunnerTelemetryTests.cs`

**Interfaces:**
- Consumes: `HarnessTelemetry.StartSpec`, `HarnessTelemetry.SetVerdict` (Task 2).
- Produces: `SpecResultSummary` gains a `DurationMs` value; `JsonSpecEntry` gains `durationMs`.

- [ ] **Step 1: Add the project reference**

In `src/BattleScribeSpec.TestKit/BattleScribeSpec.TestKit.csproj`, inside the existing `ItemGroup` of project references:

```xml
    <ProjectReference Include="..\BattleScribeSpec.Telemetry\BattleScribeSpec.Telemetry.csproj" />
```

TestKit is `IsAotCompatible=true`; `BattleScribeSpec.Telemetry` is too, so this is safe. It must **never** reference `.Collector`.

- [ ] **Step 2: Write the failing test**

Create `tests/Features/SpecSuiteRunnerTelemetryTests.cs`. This asserts one `spec` span per spec, carrying the verdict — the property Task 12's `compare` and the CI summary both depend on.

```csharp
using System.Diagnostics;
using BattleScribeSpec.Batch;
using BattleScribeSpec.Telemetry;

namespace BattleScribeSpec.Tests.Features;

[Trait("Category", "Unit")]
public sealed class SpecSuiteRunnerTelemetryTests
{
    [Fact]
    public async Task RunAsync_EmitsOneSpecSpanPerSpec_WithVerdict()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == HarnessTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) { spans.Add(a); } },
        };
        ActivitySource.AddActivityListener(listener);

        // NOTE: RunAsync's second parameter is a TextWriter progress sink, NOT a CancellationToken.
        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            AdapterFactory = _ => AdapterTestHost.StartReferenceAdapter(),
            Domains = ["roster"],
            FilterPatterns = ["protocol/protocol-kitchen-sink"],
            Workers = 1,
        });

        var specSpans = spans.Where(s => s.OperationName == "spec").ToList();
        Assert.Equal(result.Results.Count, specSpans.Count);
        Assert.All(specSpans, s => Assert.NotNull(s.GetTagItem("test.case.result.status")));
        Assert.All(specSpans, s => Assert.True(s.Duration > TimeSpan.Zero));
    }
}
```

> **Implementer note:** `AdapterTestHost.StartReferenceAdapter()` is a helper you must add to `tests/Infrastructure/` if no equivalent exists — it should return an `AdapterProcess` running `bs-reference-adapter` (the in-repo reference engine, `src/BattleScribeSpec.ReferenceAdapter`, `MaxParallel = 0`). Look at how `tests/Features/SpecSuiteRunnerTests.cs` already builds an adapter and reuse that mechanism rather than inventing a second one.

- [ ] **Step 3: Run it to confirm it fails**

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~SpecSuiteRunnerTelemetryTests"
```

Expected: FAILS — `specSpans` is empty (0 != N), because nothing emits spans yet.

- [ ] **Step 4: Extract the single per-spec method**

In `src/BattleScribeSpec.TestKit/Batch/SpecSuiteRunner.cs`, add this private method (place it next to `ComputeStatus`). It is the **only** place a spec is executed from now on:

```csharp
    /// <summary>
    /// Execute one spec against an adapter. This is the single per-spec execution path — the
    /// sequential and parallel loops, roster and gamedata, all funnel through here, so timing,
    /// tracing and verdict computation exist exactly once.
    /// </summary>
    private static (SpecResult Result, string Status, double DurationMs) RunOneSpec(
        AdapterProcess proc,
        SpecFileBase spec,
        bool isGameData,
        string? assertionEngine,
        string? engineFilter,
        string? expectedFailuresEngine)
    {
        using var activity = HarnessTelemetry.StartSpec(
            spec.Id,
            spec.Category,
            isGameData ? "gamedata" : "roster");

        var sw = Stopwatch.StartNew();
        SpecResult result;
        if (isGameData)
        {
            using var engine = new JsonProtocolGameDataEngine(proc, null);
            var runner = new GameDataRunner(engine, assertionEngine ?? engineFilter);
            result = runner.Run((GameDataSpecFile)spec);
        }
        else
        {
            var rosterSpec = (SpecFile)spec;
            var timeout = rosterSpec.Setup.DataSource is not null ? TimeSpan.FromMinutes(5) : (TimeSpan?)null;
            using var engine = new JsonProtocolEngine(proc, timeout);
            var runner = new RosterRunner(engine, new DataSourceResolver(), assertionEngine ?? engineFilter);
            result = runner.Run(rosterSpec);
        }

        sw.Stop();

        var status = ComputeStatus(result, spec, expectedFailuresEngine);
        HarnessTelemetry.SetVerdict(activity, status);
        return (result, status, sw.Elapsed.TotalMilliseconds);
    }
```

> **Implementer note:** `SpecFileBase` must expose `Id` and `Category`. Ground truth shows the runner currently gets these from the *source tuple* `(IdForLoad, Id, Category, Loader)`, not from the spec object. If `SpecFileBase` lacks them, pass `id` and `category` into `RunOneSpec` as parameters instead of reading them off `spec` — do **not** add properties to the spec model just for this.

- [ ] **Step 5: Rewrite all four call sites to use it**

Replace the four duplicated bodies. Parallel roster lambda becomes:

```csharp
                    async (item, ct) =>
                    {
                        var (id, category, spec) = item;
                        var proc = await processPool.Reader.ReadAsync(ct);
                        try
                        {
                            var (result, status, durationMs) = RunOneSpec(
                                proc, spec, isGameData: false, assertionEngine, engineFilter, expectedFailuresEngine);
                            concurrentResults.Add((result, spec, false, status, durationMs));
                        }
                        finally
                        {
                            processPool.Writer.TryWrite(proc);
                        }
                    });
```

The parallel gamedata lambda is identical but with `isGameData: true` and `concurrentResults.Add((result, spec, true, status, durationMs))`.

Widen the bag's tuple to carry the duration:

```csharp
                var concurrentResults = new System.Collections.Concurrent.ConcurrentBag<(SpecResult Result, SpecFileBase Spec, bool IsGameData, string Status, double DurationMs)>();
```

The sequential roster loop becomes:

```csharp
            foreach (var (id, category, spec) in filteredSpecs)
            {
                var (result, status, durationMs) = RunOneSpec(
                    adapterProcess!, spec, isGameData: false, assertionEngine, engineFilter, expectedFailuresEngine);
                results.Add(result);
                specsByResult[result] = spec;
                reportResults.Add(new SpecResultSummary(
                    result.SpecId, result.Category, result.Description, status, [.. result.Failures], spec.Tags, durationMs));
            }
```

and the sequential gamedata loop mirrors it with `isGameData: true` and `gameDataSpecsByResult[result] = spec;`.

Update the parallel collection loop to pass `durationMs` into `SpecResultSummary` the same way.

- [ ] **Step 6: Thread the duration into the report models**

In `src/BattleScribeSpec.TestKit/ConformanceReport.cs`, add a trailing optional parameter to `SpecResultSummary` so nothing else breaks:

```csharp
public record SpecResultSummary(
    string SpecId,
    string Category,
    string Description,
    string Status,
    List<string> Failures,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    List<string>? Tags = null,
    double DurationMs = 0);
```

In `src/BattleScribeSpec.TestKit/Batch/SuiteJsonContext.cs`, add to `JsonSpecEntry`:

```csharp
    [JsonPropertyName("durationMs")]
    public double DurationMs { get; init; }
```

In `src/BattleScribeSpec.TestKit/Batch/SpecSuiteOutput.cs`, `WriteJson` builds `JsonSpecEntry` from `result.Results` (a `SpecResult`, which has no duration). Change the `Specs` projection to source duration from `result.ReportResults` by matching `SpecId`, or — simpler and less fragile — have `SpecSuiteResult.Create` keep a `Dictionary<SpecResult, double>` of durations. **Pick one and state which in your report.**

- [ ] **Step 7: Run the test — it must pass**

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~SpecSuiteRunnerTelemetryTests"
```

Expected: **1 passed.**

- [ ] **Step 8: Run the existing runner tests to prove the refactor is behavior-neutral**

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~SpecSuiteRunnerTests"
dotnet test -p:TestProfile=core
```

Expected: **all green, same counts as before the refactor.** This unification touches every spec execution in the product; if `core` regresses, stop and fix rather than proceeding.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "refactor(runner): single per-spec execution path; emit spec spans + per-spec duration (#271)"
```

---

### Task 4: Give `AdapterProcess` an environment, and thread a worker index

Today `AdapterProcess.Start(string executable, string? arguments)` cannot set child env, and `SpecSuiteOptions.AdapterFactory` is a `Func<AdapterProcess>` with no parameters. Both must change before a child can be told where the collector is, or which worker it is.

**Files:**
- Modify: `src/BattleScribeSpec.TestKit/Protocol/AdapterProcess.cs`
- Modify: `src/BattleScribeSpec.TestKit/Batch/SpecSuiteOptions.cs`
- Modify: `src/BattleScribeSpec.TestKit/Batch/SpecSuiteRunner.cs`
- Modify: `src/BattleScribeSpec.Cli/EngineSpec.cs`
- Modify: `src/BattleScribeSpec.Cli/Commands/RunBatch.cs`, `RunCommand.cs`, `VerifyCommand.cs`
- Test: `tests/Features/AdapterProcessEnvTests.cs`

**Interfaces:**
- Produces:
  - `AdapterProcess.Start(string executable, string? arguments = null, IReadOnlyDictionary<string, string>? environment = null)`
  - `EngineSpec.StartProcess(IReadOnlyDictionary<string, string>? environment = null)`
  - `SpecSuiteOptions.AdapterFactory` becomes `Func<int, AdapterProcess>` — **the int is the zero-based worker index.**

- [ ] **Step 1: Write the failing test**

Create `tests/Features/AdapterProcessEnvTests.cs`:

```csharp
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests.Features;

[Trait("Category", "Unit")]
public sealed class AdapterProcessEnvTests
{
    [Fact]
    public void Start_PassesEnvironmentToChild()
    {
        // `dotnet --info` is not useful here; use a shell that echoes an env var back on stdout.
        // The child must see BSSPEC_TEST_ENV=hello.
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c echo %BSSPEC_TEST_ENV%")
            : ("/bin/sh", "-c \"echo $BSSPEC_TEST_ENV\"");

        using var proc = AdapterProcess.Start(exe, args, new Dictionary<string, string>
        {
            ["BSSPEC_TEST_ENV"] = "hello",
        });

        Assert.Equal("hello", proc.ReadRawStdoutLineForTest());
    }
}
```

> **Implementer note:** `AdapterProcess` wraps stdout in `NdjsonLineConnection` at construction, so you cannot read raw stdout from the outside. Rather than adding a test-only leak, **prefer this instead**: assert the env dictionary is applied to `ProcessStartInfo`. Extract the `ProcessStartInfo` construction into an `internal static ProcessStartInfo BuildStartInfo(string executable, string? arguments, IReadOnlyDictionary<string,string>? environment)`, make it visible to tests via the existing `InternalsVisibleTo`, and assert on it directly. That is a real test of the real logic with no production seam. Delete the sketch above and write that.

- [ ] **Step 2: Run it to confirm it fails**

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~AdapterProcessEnvTests"
```

Expected: FAILS to compile — `Start` takes 2 args.

- [ ] **Step 3: Add the env parameter**

In `src/BattleScribeSpec.TestKit/Protocol/AdapterProcess.cs`, change `Start` (currently line ~217):

```csharp
    /// <summary>
    /// Start an adapter process from the given executable path and optional arguments.
    /// </summary>
    /// <param name="executable">Executable or "dotnet" when launching a .dll.</param>
    /// <param name="arguments">Command-line arguments, verbatim.</param>
    /// <param name="environment">
    /// Extra environment variables for the child, layered on top of the inherited environment.
    /// This is how the OTLP collector endpoint and the worker index reach the child.
    /// </param>
    public static AdapterProcess Start(
        string executable,
        string? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = BuildStartInfo(executable, arguments, environment);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start adapter process: {executable}");
        var stderrLines = new ConcurrentQueue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                stderrLines.Enqueue(e.Data);
            }
        };
        process.BeginErrorReadLine();

        return new AdapterProcess(process, stderrLines);
    }

    internal static ProcessStartInfo BuildStartInfo(
        string executable,
        string? arguments,
        IReadOnlyDictionary<string, string>? environment)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments ?? "",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        return psi;
    }
```

Add `[assembly: InternalsVisibleTo("BattleScribeSpec.Tests")]` to TestKit if it is not already present (check `src/BattleScribeSpec.TestKit/` for an existing `AssemblyInfo` or csproj `InternalsVisibleTo` item; the Cli already does this for `BattleScribeSpec.Cli.Tests`).

- [ ] **Step 4: Widen `EngineSpec.StartProcess`**

`src/BattleScribeSpec.Cli/EngineSpec.cs:32-37`:

```csharp
    /// <summary>Start the adapter process for this selection, with optional extra child environment.</summary>
    public Protocol.AdapterProcess StartProcess(IReadOnlyDictionary<string, string>? environment = null)
    {
        var launch = EngineHostLocator.Resolve(Entry, Headed, KeepAlive);
        return Protocol.AdapterProcess.Start(launch.Executable, launch.Arguments, environment);
    }
```

The default argument keeps `RunCommand.cs:304`, `RunCommand.cs:532` and `VerifyCommand.cs:176` compiling unchanged.

- [ ] **Step 5: Change `AdapterFactory` to take a worker index**

`src/BattleScribeSpec.TestKit/Batch/SpecSuiteOptions.cs`:

```csharp
    /// <summary>
    /// Creates one adapter process per worker; the argument is the zero-based worker index.
    /// Disposed by the runner. The index lets callers give each child a distinct identity —
    /// a per-worker diagnostics directory, a worker tag on its telemetry.
    /// </summary>
    public required Func<int, AdapterProcess> AdapterFactory { get; init; }
```

In `SpecSuiteRunner.RunAsync`, update the two construction sites:

```csharp
        using var adapterProcess = workers <= 1 ? options.AdapterFactory(0) : null;
```

and in the parallel path:

```csharp
                for (var w = 0; w < workers; w++)
                {
                    adapterProcesses.Add(options.AdapterFactory(w));
                }
```

- [ ] **Step 6: Update the CLI call site**

`src/BattleScribeSpec.Cli/Commands/RunBatch.cs`, the `SpecSuiteOptions` construction (currently `AdapterFactory = selection.StartProcess`):

```csharp
                    AdapterFactory = workerIndex => selection.StartProcess(new Dictionary<string, string>
                    {
                        ["BSSPEC_WORKER_INDEX"] = workerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }),
```

`BSSPEC_WORKER_INDEX` is consumed in Task 10 to give each worker its own diagnostics directory. The collector endpoint is added to this same dictionary in Task 6.

> Use `CultureInfo.InvariantCulture` explicitly — this repo builds with `AnalysisLevel=latest-recommended` and `TreatWarningsAsErrors`, and CA1305 ("specify IFormatProvider") is an **error** here.

- [ ] **Step 7: Fix the other `AdapterFactory` users**

Grep and fix every construction of `SpecSuiteOptions`:

```bash
grep -rn "AdapterFactory" src/ tests/
```

Each must now supply a `Func<int, AdapterProcess>`. In tests, `_ => AdapterProcess.Start(...)` is the minimal change.

- [ ] **Step 8: Build and run the affected suites**

```bash
dotnet build
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~AdapterProcessEnvTests"
dotnet test -p:TestProfile=core
```

Expected: build clean; new test passes; `core` unchanged.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat(protocol): AdapterProcess child environment + worker-indexed AdapterFactory (#271)"
```

---

### Task 5: `traceparent` on the adapter protocol

One `bs-engine-host` process serves **many** specs. An env-level traceparent would pin all of them under a single static parent and flatten the trace, so per-spec correlation must ride the wire.

**Files:**
- Modify: `src/BattleScribeSpec.TestKit/Protocol/ProtocolMessages.cs`
- Modify: `src/BattleScribeSpec.TestKit/Protocol/AdapterProcess.cs` (stamp outgoing commands)
- Modify: `src/BattleScribeSpec.TestKit/Protocol/AdapterHandler.cs` (consume it)
- Modify: `docs/protocol-schema.json` (**all 15 command `$defs`**)
- Modify: `docs/adapter-protocol.md`, `docs/adapter-guide.md`
- Test: `tests/Features/TraceparentPropagationTests.cs`

**Interfaces:**
- Produces: `ProtocolCommand.Traceparent` (`string?`, wire name `traceparent`, omitted when null).

- [ ] **Step 1: Write the failing test**

This asserts the exact property that makes third-party nesting work. It is the single most important test in the plan — if it passes, an adapter in any language can join our traces.

Create `tests/Features/TraceparentPropagationTests.cs`:

```csharp
using System.Diagnostics;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Telemetry;

namespace BattleScribeSpec.Tests.Features;

[Trait("Category", "Unit")]
public sealed class TraceparentPropagationTests
{
    [Fact]
    public void Command_CarriesTraceparent_OverTheWire()
    {
        var command = new GetStateCommand { Traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" };

        var json = ProtocolSerializer.SerializeCommand(command);
        var round = ProtocolSerializer.DeserializeCommand(json);

        Assert.Contains("traceparent", json, StringComparison.Ordinal);
        Assert.Equal(command.Traceparent, round!.Traceparent);
    }

    [Fact]
    public void Command_WithoutTraceparent_OmitsItFromTheWire()
    {
        var json = ProtocolSerializer.SerializeCommand(new GetStateCommand());

        // Optional field: adapters that never heard of it must not see it. Same contract as corrId.
        Assert.DoesNotContain("traceparent", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterHandler_ParentsItsSpanToTheCommandsTraceparent()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == HarnessTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) { spans.Add(a); } },
        };
        ActivitySource.AddActivityListener(listener);

        const string TraceId = "0af7651916cd43dd8448eb211c80319c";
        const string ParentSpanId = "b7ad6b7169203331";

        // Drive one command through the in-process adapter loop.
        using var connection = InMemoryAdapterConnection.Start();
        connection.SendCommandAsync(new GetStateCommand
        {
            Traceparent = $"00-{TraceId}-{ParentSpanId}-01",
        }).GetAwaiter().GetResult();

        var handled = spans.Single(s => s.OperationName == "getState");
        Assert.Equal(TraceId, handled.TraceId.ToHexString());
        Assert.Equal(ParentSpanId, handled.ParentSpanId.ToHexString());
    }
}
```

> **Implementer note:** `tests/Infrastructure/InMemoryAdapterConnection.cs` already runs `AdapterHandler` in-process over channels. Use it; check its actual construction API and adapt the third test to it rather than inventing `Start()`.

- [ ] **Step 2: Run it to confirm it fails**

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~TraceparentPropagationTests"
```

Expected: FAILS to compile — `Traceparent` does not exist.

- [ ] **Step 3: Add the wire field**

In `src/BattleScribeSpec.TestKit/Protocol/ProtocolMessages.cs`, on `ProtocolCommand` (next to `CorrId`):

```csharp
    /// <summary>
    /// Optional W3C trace-context header (protocol v1.1+), wire name <c>traceparent</c>.
    /// Clients SHOULD send it so the adapter can parent its spans under the client's spec span,
    /// producing one distributed trace across the runner and the engine process.
    /// </summary>
    /// <remarks>
    /// Per-request rather than per-process on purpose: one adapter process serves many specs, so
    /// a process-level parent would collapse every spec into a single trace. Adapters that ignore
    /// this field remain fully conformant — same optional contract as <see cref="CorrId"/>.
    /// </remarks>
    [JsonPropertyName("traceparent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Traceparent { get; set; }

    /// <summary>
    /// Optional W3C <c>tracestate</c>, the companion of <see cref="Traceparent"/>.
    /// </summary>
    /// <remarks>
    /// W3C requires a vendor that receives <c>tracestate</c> to forward it on outgoing requests.
    /// Without it, a third-party adapter sitting behind a vendor backend loses its vendor context —
    /// which is precisely the cross-language case this field exists to serve. Together the two
    /// fields form a W3C trace-context carrier, so an adapter in any language can feed them
    /// straight into its stock propagator.
    /// </remarks>
    [JsonPropertyName("tracestate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tracestate { get; set; }
```

Do **not** add either to `ProtocolResponse` — the trace flows one way.

- [ ] **Step 4: Stamp it on outgoing commands**

In `NdjsonLineConnection.SendCommandAsync` (`AdapterProcess.cs:141`), next to the existing corrId assignment:

```csharp
        var corrId = Interlocked.Increment(ref _nextId);
        command.CorrId = corrId;

        // The sending side of a remote call is a CLIENT span. Jaeger's dependency graph and Tempo's
        // servicegraph processor derive edges EXCLUSIVELY from CLIENT->SERVER pairs — with Internal
        // on both sides there is no bs-spec -> bs-engine-host edge at all.
        using var activity = HarnessTelemetry.StartOp(command.Type, kind: ActivityKind.Client);

        command.Traceparent ??= HarnessTelemetry.CurrentTraceparent();
        command.Tracestate ??= Activity.Current?.TraceStateString;
```

`??=` so an explicit caller-supplied value always wins. When nothing is listening, `CurrentTraceparent()` is null and the field is omitted — zero wire cost for untraced runs.

Add `using System.Diagnostics;` and `using BattleScribeSpec.Telemetry;` to the file.

> **Watch this trap.** `StartActivity` returns **null when no listener is attached**, so `Activity.Current?.Id` is null and **the traceparent is silently never sent** — the child's spans then become orphan roots instead of nesting. The `ActivityListener` must therefore be attached *unconditionally* (sampling-only), independent of whether the collector managed to bind a port. Otherwise the fail-open path produces a trace that looks fine in the propagation test and is broken in production.

- [ ] **Step 5: Consume it in the adapter**

In `src/BattleScribeSpec.TestKit/Protocol/AdapterHandler.cs`, wrap the dispatch. Just inside the `try` after `commandCorrId = command?.CorrId;`:

```csharp
                    var command = ProtocolSerializer.DeserializeCommand(line);
                    commandCorrId = command?.CorrId;

                    // SERVER: the handling side of a remote call. Pairs with the parent's CLIENT span
                    // to form the one edge a service graph can actually draw.
                    using var activity = command is null
                        ? null
                        : HarnessTelemetry.StartOp(
                            command.Type,
                            command.Traceparent,
                            ActivityKind.Server,
                            command.Tracestate);
                    response = command switch
                    {
```

Add `using System.Diagnostics;` and `using BattleScribeSpec.Telemetry;`.

> `ProtocolCommand.Type` is the `[JsonIgnore]` discriminator (`"setup"`, `"action"`, `"getState"`, `"teardown"`, …), so span names match the protocol vocabulary exactly.

- [ ] **Step 6: Update `docs/protocol-schema.json`**

Add **both** properties to **each of the 15 command `$defs`** (they all have `"additionalProperties": false`, so a missing entry fails validation), right after the existing `corrId` line:

```json
"traceparent": { "type": "string", "description": "Optional W3C trace-context header (protocol v1.1+). Lets the adapter parent its spans under the client's span. Per-request, not per-process: one adapter process serves many specs." },
"tracestate": { "type": "string", "description": "Optional W3C tracestate, companion to traceparent. Together they form a trace-context carrier an adapter can feed to its stock propagator." },
```

The command `$defs` are the 15 entries listed under `$defs/command/oneOf`. Do **not** add them to the 12 response `$defs`.

- [ ] **Step 7: Run the drift test — it gates this change**

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~ProtocolSchemaDriftTests"
```

Expected: **all passing.** If it fails with `C# type 'XCommand' has JSON properties not in schema def 'x': Missing: traceparent`, you missed a `$def` — the message names exactly which.

- [ ] **Step 8: Run the propagation tests**

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~TraceparentPropagationTests"
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~ProtocolSchemaTests"
```

Expected: **all passing.**

- [ ] **Step 9: Document the field**

In `docs/adapter-protocol.md` and `docs/adapter-guide.md`, document `traceparent` alongside `corrId`: optional, per-request, W3C trace-context format; an adapter that ignores it is fully conformant; an adapter that honors it (by configuring its native OTel SDK with `OTEL_EXPORTER_OTLP_ENDPOINT` from its environment and using `traceparent` as the parent context) appears nested in the harness's trace with no harness-specific code.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(protocol): per-request traceparent for cross-process span nesting (#271)"
```

---

### Task 6: `HarnessCollector` — the OTLP receiver and the artifact

**Files:**
- Create: `src/BattleScribeSpec.Telemetry.Collector/HarnessCollector.cs`
- Create: `src/BattleScribeSpec.Telemetry.Collector/OtlpArtifactWriter.cs`
- Create: `src/BattleScribeSpec.Telemetry.Collector/ParentProviders.cs`
- Test: `tests/Features/TelemetryCollectorTests.cs`

**Interfaces:**
- Produces:
  - `static Task<HarnessCollector> HarnessCollector.StartAsync(string artifactPath, CancellationToken ct = default)`
  - `string HarnessCollector.Endpoint { get; }` — e.g. `http://127.0.0.1:53411`
  - `IReadOnlyDictionary<string, string> HarnessCollector.ChildEnvironment { get; }` — `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`, `OTEL_SERVICE_NAME=bs-engine-host`
  - `ValueTask HarnessCollector.DisposeAsync()` — flushes and closes the artifact
- Consumes: the generated OTLP types (Task 1), `HarnessTelemetry.SourceName` (Task 2).

- [ ] **Step 1: Write the failing test**

Create `tests/Features/TelemetryCollectorTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run it to confirm it fails**

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~TelemetryCollectorTests"
```

Expected: FAILS to compile — `HarnessCollector` does not exist.

- [ ] **Step 3: Implement the artifact writer**

Create `src/BattleScribeSpec.Telemetry.Collector/OtlpArtifactWriter.cs`:

```csharp
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
    public Task WriteAsync(ExportTraceServiceRequest request) => AppendAsync(_traces, request);

    /// <summary>Append a metrics export request.</summary>
    public Task WriteAsync(ExportMetricsServiceRequest request) => AppendAsync(_metrics, request);

    /// <summary>Append a logs export request.</summary>
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
```

Also create `src/BattleScribeSpec.Telemetry.Collector/OtlpArtifactReader.cs`:

```csharp
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
```

- [ ] **Step 4: Implement the collector**

Create `src/BattleScribeSpec.Telemetry.Collector/HarnessCollector.cs`:

```csharp
using System.Diagnostics;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
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
    private readonly ActivityListener? _listener;

    private HarnessCollector(WebApplication? app, OtlpArtifactWriter? writer, ActivityListener? listener, string endpoint)
    {
        _app = app;
        _writer = writer;
        _listener = listener;
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
            : new Dictionary<string, string>();

    /// <summary>
    /// Bind a receiver on <c>127.0.0.1:0</c> and begin recording to <paramref name="artifactPath"/>
    /// (which gains <c>.traces.pb</c> / <c>.metrics.pb</c> / <c>.logs.pb</c>).
    /// </summary>
    public static async Task<HarnessCollector> StartAsync(string artifactPath, CancellationToken ct = default)
    {
        OtlpArtifactWriter writer;
        WebApplication app;
        try
        {
            writer = new OtlpArtifactWriter(artifactPath);

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.ListenLocalhost(0, listen => listen.Protocols = HttpProtocols.Http1AndHttp2);
            });

            app = builder.Build();
            MapOtlp(app, writer);

            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fail-open. A run must never die because telemetry could not start.
            Console.Error.WriteLine($"[telemetry] collector disabled: {ex.Message}");
            return new HarnessCollector(app: null, writer: null, listener: null, endpoint: "");
        }

        var endpoint = app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));

        // The parent's own spans and metrics must reach the artifact too. Use the STOCK SDK,
        // pointed at our own loopback receiver — see Step 5 for why hand-rolling this was a mistake.
        var providers = ParentProviders.Attach(endpoint, serviceName: "bs-spec");

        return new HarnessCollector(app, writer, providers, endpoint);
    }

    private static void MapOtlp(WebApplication app, OtlpArtifactWriter writer)
    {
        app.MapPost("/v1/traces", (HttpContext ctx) => ReceiveAsync(
            ctx,
            body => writer.WriteAsync(ExportTraceServiceRequest.Parser.ParseFrom(body)),
            new ExportTraceServiceResponse()));

        app.MapPost("/v1/metrics", (HttpContext ctx) => ReceiveAsync(
            ctx,
            body => writer.WriteAsync(ExportMetricsServiceRequest.Parser.ParseFrom(body)),
            new ExportMetricsServiceResponse()));

        app.MapPost("/v1/logs", (HttpContext ctx) => ReceiveAsync(
            ctx,
            body => writer.WriteAsync(ExportLogsServiceRequest.Parser.ParseFrom(body)),
            new ExportLogsServiceResponse()));
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
            await parse(ctx.Request.Body).ConfigureAwait(false);
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
        _listener?.Dispose();

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
```

- [ ] **Step 5: Export the parent's own spans and metrics with the STOCK SDK**

The parent's `Activity` and `Meter` data never travels over HTTP — it is in-process. It still has to reach the artifact.

**Do not hand-roll an `Activity` → protobuf converter.** An earlier draft of this plan did, reasoning that the OTel SDK is AOT-hostile. That is true of `Cli` and `TestKit` — and **false of this project**, which is deliberately not AOT-marked and is already referenced by `Cli`. The bespoke exporter was quietly broken in five ways:

- `Activity.Tags` yields **only `string`-valued tags**, so `SetTag("bsspec.workers", 4)` (an `int`) would never have reached the artifact.
- Span **status** was dropped — `SetVerdict` calls `SetStatus(Error)`, so **failed specs would have rendered green**.
- Span **kind** was dropped, silently undoing the CLIENT/SERVER work that makes service graphs render.
- Span **events** were dropped.
- The `Resource` was left empty — no `service.name`, which Jaeger keys everything on.

The SDK gets all five right. Add to `Directory.Packages.props` and reference from **`.Collector` only**:

```xml
    <PackageVersion Include="OpenTelemetry" Version="1.16.0" />
    <PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.16.0" />
```

Create `src/BattleScribeSpec.Telemetry.Collector/ParentProviders.cs`:

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BattleScribeSpec.Telemetry.Collector;

/// <summary>
/// The parent process's own OpenTelemetry providers, exporting over OTLP to <paramref name="endpoint"/>
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

    public static ParentProviders Attach(string endpoint, string serviceName)
    {
        var resource = ResourceBuilder.CreateDefault().AddService(serviceName);

        var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(HarnessTelemetry.SourceName)
            .SetResourceBuilder(resource)
            .SetSampler(new AlwaysOnSampler())
            .AddOtlpExporter(o =>
            {
                // MUST set Protocol explicitly. On net10.0 OpenTelemetry .NET's OtlpExporterOptions
                // defaults to Grpc (HttpProtobuf is the default ONLY on netfx/netstandard2.0). Our
                // receiver is HTTP-only (GrpcServices="None"; three MapPost routes, no gRPC service),
                // so a gRPC export hits a path that does not exist, 404s, and — because export is
                // fail-open — vanishes SILENTLY, taking the parent's spans and harness.resource.count
                // with it.
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
                o.Endpoint = new Uri($"{endpoint}/v1/traces");
            })
            .Build();

        var meter = Sdk.CreateMeterProviderBuilder()
            .AddMeter(HarnessTelemetry.MeterName)
            .SetResourceBuilder(resource)
            .AddOtlpExporter(o =>
            {
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
                o.Endpoint = new Uri($"{endpoint}/v1/metrics");
            })
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
```

> **Note the explicit `/v1/traces` path.** OpenTelemetry .NET only appends the signal path when the endpoint arrives via the *environment variable*; assigning `OtlpExporterOptions.Endpoint` programmatically sets `AppendSignalPathToEndpoint = false`, so the full path must be given. Getting this wrong sends everything to `/` and the receiver 404s — silently, because export is fail-open.

Update `HarnessCollector`'s field and `DisposeAsync` accordingly: hold a `ParentProviders?` instead of an `ActivityListener?`, and dispose it **before** stopping the web app, so the final flush has somewhere to land.

This step also resolves the parent-metrics gap: without a `MeterProvider`, `harness.resource.count` — which the spec calls the single most important thing the telemetry must make visible — would have been emitted into the void, and Tasks 9 and 11 could not have been completed at all.

- [ ] **Step 6: Run the tests — they must pass**

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~TelemetryCollectorTests"
```

Expected: **2 passed.**

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(telemetry): OTLP/HTTP receiver on loopback + protobuf run artifact (#271)"
```

---

### Task 7: Wire the collector into `bs-spec run --all`, and export from the child

**Files:**
- Modify: `src/BattleScribeSpec.Cli/BattleScribeSpec.Cli.csproj` (reference `.Collector`)
- Modify: `src/BattleScribeSpec.Cli/Commands/RunBatch.cs`
- Modify: `src/BattleScribeSpec.EngineHost/BattleScribeSpec.EngineHost.csproj` (OTel SDK)
- Modify: `src/BattleScribeSpec.EngineHost/Program.cs`
- Modify: `Directory.Packages.props`
- Test: `tests/Features/EndToEndTraceTests.cs`

**Interfaces:**
- Consumes: `HarnessCollector.StartAsync`, `.ChildEnvironment` (Task 6); `AdapterProcess` env (Task 4); `traceparent` (Task 5).

- [ ] **Step 1: Reference the OTel SDK from the engine host**

`OpenTelemetry` and `OpenTelemetry.Exporter.OpenTelemetryProtocol` already have `PackageVersion` entries (added in Task 6 Step 5). Reference them from `src/BattleScribeSpec.EngineHost/BattleScribeSpec.EngineHost.csproj`, and add the runtime instrumentation that Spec 2 needs:

```xml
    <PackageReference Include="OpenTelemetry" />
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Runtime" />
    <ProjectReference Include="..\BattleScribeSpec.Telemetry\BattleScribeSpec.Telemetry.csproj" />
```

with `<PackageVersion Include="OpenTelemetry.Instrumentation.Runtime" Version="1.16.0" />` in `Directory.Packages.props`.

**Never add any of these to `Cli` or `TestKit`** — both are `IsAotCompatible=true` and the SDK's reflection becomes a build error there. `.Collector` and `EngineHost` are the only homes.

> Runtime instrumentation (CPU, GC, thread pool) is not decoration: *"are we actually CPU-saturated at N workers, or merely I/O-blocked?"* is the question **Spec 2's auto-tuner** must answer, and it is a free OTel metric. `OpenTelemetry.Instrumentation.Process` would add more, but it is pre-release — skip it; the runtime package answers the saturation question on its own.

Then `dotnet restore --force-evaluate`.

- [ ] **Step 2: Initialize the SDK in the host, but only when the parent asked for it**

In `src/BattleScribeSpec.EngineHost/Program.cs`, before the command dispatch:

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using BattleScribeSpec.Telemetry;

// The parent injects OTEL_EXPORTER_OTLP_ENDPOINT when it is collecting. Absent -> no exporter, no
// cost. Everything else (protocol, service name, resource attributes, batch delay, sampler) is read
// by the SDK from the standard OTEL_* env vars the parent set. There is deliberately NO bespoke
// configuration here: a third-party adapter in any language must be able to do exactly this with
// its own stock SDK, so our own host is held to the same contract.
var collecting = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") is { Length: > 0 };

using var tracerProvider = collecting
    ? Sdk.CreateTracerProviderBuilder()
        .AddSource(HarnessTelemetry.SourceName)
        .AddOtlpExporter()
        .Build()
    : null;

using var meterProvider = collecting
    ? Sdk.CreateMeterProviderBuilder()
        .AddMeter(HarnessTelemetry.MeterName)
        .AddRuntimeInstrumentation()
        .AddOtlpExporter()
        .Build()
    : null;
```

Note there is **no `ConfigureResource(...AddService(...))`** call: `AddService` would *override* the `OTEL_SERVICE_NAME` the parent set, and relying on the env var is exactly what a third-party adapter would do. Keeping our own host on the same path keeps us honest about the contract we advertise.

`using var` guarantees a flush on normal exit. A hard kill still loses the in-flight batch — the accepted limitation in the spec, tolerable because the spans that *prove* a death (`setup`/`action`/`teardown`, process exit) are emitted parent-side.

- [ ] **Step 3: Start the collector in the batch runner**

In `src/BattleScribeSpec.Cli/Commands/RunBatch.cs`, in `ExecuteAsync`, before `SpecSuiteRunner.RunAsync`:

```csharp
        var runId = Guid.NewGuid().ToString("N")[..8];
        var artifactPath = Path.Combine("artifacts", "telemetry", $"run-{runId}");
        await using var collector = await HarnessCollector.StartAsync(artifactPath);
        if (collector.Enabled)
        {
            Ui.Info($"Telemetry: {artifactPath}.traces.pb");
        }
```

and merge the collector env into the adapter factory from Task 4:

```csharp
                    AdapterFactory = workerIndex =>
                    {
                        var index = workerIndex.ToString(CultureInfo.InvariantCulture);
                        var env = new Dictionary<string, string>(collector.ChildEnvironment)
                        {
                            ["BSSPEC_WORKER_INDEX"] = index,
                            // Without a per-worker service.instance.id, all N workers collapse into one
                            // resource in any backend and question 4 ("which worker ran this spec?")
                            // stays unanswerable — which is the whole point of attribution.
                            ["OTEL_RESOURCE_ATTRIBUTES"] = $"service.instance.id={index}",
                        };
                        return selection.StartProcess(env);
                    },
```

Wrap the whole suite in a run span so every spec nests under it:

```csharp
        using var runSpan = HarnessTelemetry.StartOp("run");
        runSpan?.SetTag("bsspec.engine", engineLabel);
        runSpan?.SetTag("bsspec.workers", workers);
```

Add the project reference in `src/BattleScribeSpec.Cli/BattleScribeSpec.Cli.csproj`:

```xml
    <ProjectReference Include="..\BattleScribeSpec.Telemetry.Collector\BattleScribeSpec.Telemetry.Collector.csproj" />
```

- [ ] **Step 4: Respect an externally-set collector, and tag CI runs**

Two additions to `HarnessCollector`.

**(a) Never hijack a user's own collector — and never stop exporting the parent's spans.** If `OTEL_EXPORTER_OTLP_ENDPOINT` is already set in the parent's environment, the user is pointing the harness at their own Jaeger/Tempo. Honor it: skip binding a port, pass that endpoint to children unchanged — **and still stand up the parent's own `ParentProviders` against it.**

```csharp
        // An externally-set endpoint means the user is pointing us at their own collector. This is
        // the ONE environment variable the harness honors rather than owns, because it is an
        // industry standard rather than a bespoke dial of ours.
        if (Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") is { Length: > 0 } external)
        {
            // The parent MUST still export. Its spans are the ones carrying test.* and cicd.* —
            // drop them and the user sees engine-host protocol spans with no test context at all,
            // which would make the design's headline claim ("point it at Jaeger and it just works")
            // simply false. No local artifact in this mode: their collector owns the data.
            var external Providers = ParentProviders.Attach(external, serviceName: "bs-spec");
            return new HarnessCollector(app: null, writer: null, externalProviders, endpoint: external);
        }
```

(Fix the typo when you write it — `externalProviders`, one identifier.)

Make `ChildEnvironment` return the endpoint dictionary whenever `Endpoint` is non-empty, not only when the receiver is self-hosted, and let `Enabled` mean "telemetry is flowing" — true in both the self-hosted and external cases. State in your report how you distinguished the two internally.

**(b) CI/CD and VCS semantic conventions.** On the `run` span, when the standard GitHub Actions env vars are present:

```csharp
        if (Environment.GetEnvironmentVariable("GITHUB_WORKFLOW") is { Length: > 0 } workflow)
        {
            var server = Environment.GetEnvironmentVariable("GITHUB_SERVER_URL");
            var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
            var runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");

            runSpan?.SetTag("cicd.pipeline.name", workflow);
            runSpan?.SetTag("cicd.pipeline.run.id", runId);
            runSpan?.SetTag("cicd.pipeline.run.url.full", $"{server}/{repo}/actions/runs/{runId}");
            runSpan?.SetTag("cicd.pipeline.task.type", "test");
            runSpan?.SetTag("vcs.repository.url.full", $"{server}/{repo}");
            runSpan?.SetTag("vcs.ref.head.name", Environment.GetEnvironmentVariable("GITHUB_REF_NAME"));
            runSpan?.SetTag("vcs.ref.head.revision", Environment.GetEnvironmentVariable("GITHUB_SHA"));
        }
```

and after the suite completes, the run-level status:

```csharp
        runSpan?.SetTag("test.suite.run.status", result.Failed > 0 ? "failure" : "success");
```

> Stability differs and it is worth knowing which: `cicd.*` and `vcs.*` are **Release Candidate** (near-stable); `test.*` is **Development** (expect churn). All are additive and nothing reads them back, so churn cannot break a run — but keep them in this one place so a convention bump is a single edit.

- [ ] **Step 5: Verify the AOT analyzer is still quiet**

```bash
dotnet build src/BattleScribeSpec.Cli/BattleScribeSpec.Cli.csproj
```

Expected: **0 warnings.** `Cli` now references `.Collector` (which is not AOT-clean), so this is the moment the boundary is actually tested. If `IL2026`/`IL3050` appear, the offending call must be moved behind a non-annotated facade method on `HarnessCollector` — **do not add a suppression**.

- [ ] **Step 6: Write the end-to-end test**

Create `tests/Features/EndToEndTraceTests.cs` asserting the full chain: run a batch against the reference adapter with the collector on, then read the artifact and assert (a) `spec` spans exist, (b) at least one span was produced **by the child process** (its resource has `service.name = bs-engine-host`), and (c) that child span's `parent_span_id` is non-zero and matches a parent-side span id. **(c) is the property the whole design rests on** — it is the proof that `traceparent` really nests a foreign process's spans under ours.

- [ ] **Step 7: Run it**

```bash
dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~EndToEndTraceTests"
```

Expected: **passing.** If child spans are missing, check that `bs-engine-host` actually saw `OTEL_EXPORTER_OTLP_ENDPOINT` — `AdapterProcess.BuildStartInfo` must be applying the dictionary.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(telemetry): host the collector in bs-spec run; export child spans over OTLP (#271)"
```

---

### Task 8: Resource lifecycle instrumentation in the engine drivers

Make cold-start-vs-reuse and live-resource counts real, in the four places expensive resources are actually born.

**Files:**
- Modify: `src/BattleScribeSpec.TestKit/Protocol/AdapterHandler.cs` (`engine.cold_start` / `engine.reuse` around `HandleSetup`, `HandleGameDataSetup`, `ResetOrDispose<T>`)
- Modify: `src/BattleScribeSpec.BsRosterUiDriver/BsRosterApp.cs` (`jvm` acquire/release; poison → restart)
- Modify: `src/BattleScribeSpec.NewRecruit/NrBrowserHost.cs` (`browser` acquire/release)
- Modify: `src/BattleScribeSpec.NewRecruit/NewRecruitBrowser.cs` (`browser-context` acquire/release)
- Modify: `src/BattleScribeSpec.TestKit/Protocol/AdapterProcess.cs` (`adapter-process` acquire/release)
- Add project reference to `BattleScribeSpec.Telemetry` in each driver csproj
- Test: `tests/Features/ResourceMetricsTests.cs`

- [ ] **Step 1: Write the failing test**

Use `MeterListener` to assert that running two specs on a warm-reuse engine produces exactly **one** `reused: false` and one `reused: true` observation on `harness.engine.start.duration`, and that `harness.resource.count` returns to zero after teardown. Prefer the in-process reference adapter so the test is fast and hermetic.

- [ ] **Step 2: Run it to confirm it fails**

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~ResourceMetricsTests"
```

Expected: FAILS — no observations recorded.

- [ ] **Step 3: Instrument `AdapterHandler`'s engine acquisition**

In `HandleSetup` (`AdapterHandler.cs:158`), the reuse decision already exists (`reuseEngine` + `ResetOrDispose`). Time both branches and record which happened:

```csharp
        var sw = Stopwatch.StartNew();
        var reused = engine is not null && reuseEngine;
        // ... existing acquisition logic, unchanged ...
        sw.Stop();
        // Seconds, per OTel's duration-unit convention — NOT milliseconds.
        ResourceMetrics.RecordEngineStart("roster-engine", reused, sw.Elapsed.TotalSeconds);
```

Mirror it in `HandleGameDataSetup` with kind `"gamedata-engine"`.

> This is the warm-reuse question — *was it reused, and what did it cost* — asked on every single spec, instead of by a benchmark script run by hand once a month.

- [ ] **Step 4: Instrument the resource owners**

In each of `BsRosterApp` (JVM launch/exit), `NrBrowserHost` (Chromium launch/shutdown), `NewRecruitBrowser` (context create/close), and `AdapterProcess` (`Start`/`Dispose`), call `ResourceMetrics.Acquired(kind)` when the resource comes up and `ResourceMetrics.Released(kind)` when it goes down, with kinds `"jvm"`, `"browser"`, `"browser-context"`, `"adapter-process"`. Add the `BattleScribeSpec.Telemetry` project reference to each driver csproj.

Release must be in a `finally` (or the dispose path) so a throwing teardown does not leak the counter — a counter that drifts upward is worse than no counter, because it silently invents resources that do not exist.

- [ ] **Step 5: Run the test and the core suite**

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~ResourceMetricsTests"
dotnet test -p:TestProfile=core
```

Expected: new test passes; `core` unchanged.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(telemetry): engine cold-start/reuse + live-resource metrics in the drivers (#271)"
```

---

### Task 9: The xUnit path — telemetry where the unbounded parallelism actually lives

The CLI path is now instrumented. The **xUnit path is not**, and that is where the real problem is: `parallelizeTestCollections: true` with `maxParallelThreads` unset means the 11 collection fixtures can bring up multiple browser-context pools *and* the JVM concurrently, with nothing bounding the product. Task 8's `harness.resource.count` counter can prove it — but only if something hosts a collector inside `dotnet test`.

**Files:**
- Create: `tests/Infrastructure/TelemetryAssemblyFixture.cs`
- Modify: `tests/Infrastructure/*Fixture.cs` (the 11 fixtures — pool size + acquire spans)
- Modify: `tests/BattleScribeSpec.Tests.csproj`
- Test: `tests/Features/XunitTelemetryTests.cs`

- [ ] **Step 1: Write the failing test**

Assert that a `dotnet test` run emits a `harness.resource.count` series, and that the peak value is recorded. The test's real job is to make the number *exist*; asserting a specific bound is Spec 2's business, not this one's.

- [ ] **Step 2: Add an assembly-level collector**

xUnit v3 supports `[assembly: AssemblyFixture(typeof(T))]`. Create `tests/Infrastructure/TelemetryAssemblyFixture.cs` which starts a `HarnessCollector` for the whole test assembly (artifact `artifacts/telemetry/xunit-<timestamp>`) and disposes it at the end. There is precedent for process-wide init here: `tests/Infrastructure/IkvmAssemblyResolver.cs` already uses a `[ModuleInitializer]`.

Add the `.Collector` project reference to `tests/BattleScribeSpec.Tests.csproj`.

- [ ] **Step 3: Instrument the fixtures**

Each of the 11 fixtures owns exactly one expensive resource. In `InitializeAsync`, record the pool size as a span tag; wrap each pooled `Acquire` in a short span so acquire-wait becomes visible. `ResourceMetrics.Acquired/Released` already fire from the drivers (Task 8), so the live counter works here **for free** — that is the payoff of instrumenting at the resource owner rather than at the call site.

- [ ] **Step 4: Run and verify the number is real**

```bash
dotnet test -p:TestProfile=core
```

Then read the artifact and report the **peak `harness.resource.count`**. Put the number in your task report — it is the first direct measurement of the concurrency this repo has been running blind.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(telemetry): assembly-level collector + fixture instrumentation for the xUnit path (#271)"
```

---

### Task 10: Fix the diagnostics-directory collision, and forward host stderr (#303)

Two pre-existing correctness bugs that sit directly under the observability work.

**Files:**
- Modify: `src/BattleScribeSpec.BsRosterUiDriver/BsUiDiagnostics.cs`
- Modify: `src/BattleScribeSpec.BsGameDataUiDriver/BsGameDataUiDiagnostics.cs`
- Modify: `src/BattleScribeSpec.NrGameDataUiDriver/NrGameDataUiDiagnostics.cs`
- Modify: `src/BattleScribeSpec.Cli/Commands/RunCommand.cs` (`ReportDiagnosticDumps`, ~line 628)
- Modify: `src/BattleScribeSpec.TestKit/Protocol/AdapterProcess.cs` (stderr forwarding)
- Test: `tests/Features/DiagnosticsIsolationTests.cs`

- [ ] **Step 1: Write the failing test**

Assert that two `BsUiDiagnostics.DiagnosticsDirectory` values resolved under different `BSSPEC_WORKER_INDEX` values are **different paths**. Today they are identical, so parallel workers overwrite each other's dumps.

- [ ] **Step 2: Run it to confirm it fails**

Expected: FAILS — both resolve to `artifacts/bs-ui-diagnostics`.

- [ ] **Step 3: Suffix the directory per worker**

In all three diagnostics classes, append a worker discriminator when one is present:

```csharp
    private static string WorkerSuffix =>
        Environment.GetEnvironmentVariable("BSSPEC_WORKER_INDEX") is { Length: > 0 } index
            ? $"-w{index}"
            : "";

    /// <summary>
    /// Where diagnostic dumps are written. Suffixed per worker: N parallel adapter processes
    /// otherwise resolve the same directory and overwrite each other's dumps — the NR variant
    /// collides at whole-second precision and then writes fixed filenames into the shared folder.
    /// </summary>
    public static string DiagnosticsDirectory { get; set; } =
        Environment.GetEnvironmentVariable("BS_UI_DIAGNOSTICS_DIR")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", $"bs-ui-diagnostics{WorkerSuffix}");
```

Apply the equivalent change to `BsGameDataUiDiagnostics` and `NrGameDataUiDiagnostics` (the latter also builds a per-report subdirectory at whole-second precision — add the worker suffix there too, since two workers on the same spec in the same second currently overwrite `screenshot.png` / `console.txt` / `dom.html`).

`RunCommand.ReportDiagnosticDumps` hardcodes the un-suffixed path; make it enumerate `artifacts/bs-ui-diagnostics*` so it still finds dumps from every worker.

- [ ] **Step 4: Forward host stderr (#303)**

`AdapterProcess` buffers stderr into an unbounded `ConcurrentQueue<string>` that only `GetStderrTail(10)` ever reads — so host-side diagnostics are invisible during a run. This actively obstructed the NR-UI roster diagnosis.

In `AdapterProcess.Start`'s `ErrorDataReceived` handler, also forward the line to the parent's stderr, prefixed with the worker index so N workers stay legible:

```csharp
        var workerTag = Environment.GetEnvironmentVariable("BSSPEC_WORKER_INDEX") is { Length: > 0 } w
            ? $"[host:{w}] "
            : "[host] ";
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                stderrLines.Enqueue(e.Data);
                Console.Error.WriteLine(workerTag + e.Data);
            }
        };
```

Keep the queue — `GetStderrTail` still enriches exception messages. Note the tag must come from the env the **parent** set on the child (Task 4), read here in the parent; if that is awkward, thread the worker index into `AdapterProcess.Start` as an explicit parameter instead. **State which you chose in your report.**

- [ ] **Step 5: Run the tests**

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~DiagnosticsIsolationTests"
dotnet test -p:TestProfile=core
```

- [ ] **Step 6: Commit and close #303**

```bash
git add -A
git commit -m "fix(diag): per-worker diagnostics dirs; forward engine-host stderr (closes #303) (#271)"
```

---

### Task 11: Trace summary — turn the artifact into an answer

An artifact nobody reads is not observability.

**Files:**
- Create: `src/BattleScribeSpec.Telemetry.Collector/TraceSummary.cs`
- Modify: `src/BattleScribeSpec.Cli/Commands/RunBatch.cs` (print the summary after a run)
- Test: `tests/Features/TraceSummaryTests.cs`

**Interfaces:**
- Produces: `TraceSummary.FromArtifact(string basePath)` → a record with `SpecCount`, `TotalWall`, `P50SpecMs`, `P95SpecMs`, `ColdStarts`, `Reuses`, `PeakLiveResources`, and `SlowestSpecs` (top 10 by duration).

- [ ] **Step 1: Write the failing test** — build a synthetic artifact with three known spec spans and assert p50/p95 and the slowest-spec ordering.

- [ ] **Step 2: Run it to confirm it fails.**

- [ ] **Step 3: Implement `TraceSummary`** — read the trace artifact via `OtlpArtifactReader`, select spans named `spec`, group by `test.case.name`, compute the statistics.

- [ ] **Step 4: Print it after `bs-spec run --all`** as a compact table on stderr (stdout is reserved for `--output json`).

- [ ] **Step 5: Run the tests, then run a real batch and paste the summary into your report.**

```bash
dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll run --all --engine battlescribe --filter entry/ --workers 2
```

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(telemetry): trace summary (p50/p95, cold-start vs reuse, peak live resources) (#271)"
```

---

### Task 12: `bs-spec compare` — the verdict-equality rail

This is the safety rail Spec 2's auto-tuning must pass. It is built **before** the thing it guards, deliberately: building the guard alongside the guarded change is exactly how NR-UI roster warm-reuse shipped "verified" while silently changing 6 verdicts.

**Files:**
- Create: `src/BattleScribeSpec.Cli/Commands/CompareCommand.cs`
- Modify: `src/BattleScribeSpec.Cli/CommandFactory.cs`
- Delete: `scripts/bench-warm-reuse.ps1`
- Modify: `docs/warm-reuse.md`, `src/BattleScribeSpec.EngineHost/ServeCommand.cs` (comments referencing the script)
- Test: `tests/BattleScribeSpec.Cli.Tests/CompareCommandTests.cs`

**Interfaces:**
- Produces:
```
bs-spec compare --engine <name> [--roster|--gamedata] [--filter <f>] [--workers <n>]
                --config-a <k=v,...> --config-b <k=v,...>
```
Each `--config-*` is a comma-separated list of `KEY=VALUE` environment settings applied to that arm's child processes. Warm-vs-cold becomes `--config-a "" --config-b "BSSPEC_DISABLE_WARM_REUSE=1"`, which is exactly what the retired script hard-coded — but now any config pair works, including the parallelism levels Spec 2 will tune.

- [ ] **Step 1: Write the failing test**

The critical one is **red on divergence**:

```csharp
[Fact]
public async Task Compare_ExitsNonZero_WhenAVerdictDiverges()
{
    // Two configurations that produce different verdicts must FAIL the comparison.
    // A configuration change that alters conformance results is not an optimization,
    // it is a regression — and this assertion is the only thing that catches it.
    var exitCode = await RunCompare(
        configA: "",
        configB: "BSSPEC_TEST_FORCE_FAIL=1");   // reference adapter honours this; see note

    Assert.NotEqual(0, exitCode);
}
```

> **Implementer note:** you need a way to make one arm deliberately diverge. Add a tiny, clearly-named test hook to `src/BattleScribeSpec.ReferenceAdapter` — e.g. an env var that makes it fail one named spec. Keep it in the reference adapter (a test double), never in a real engine.

Also test the happy path: identical configs → exit 0, and a reported speedup near 1.0×.

- [ ] **Step 2: Run it to confirm it fails** — `compare` does not exist.

- [ ] **Step 3: Implement `CompareCommand`**

Run the same spec set twice, each arm with its own child environment, reusing `SpecSuiteRunner`. Then:

1. Build `id → status` maps for both arms.
2. **Assert they are identical.** Any spec whose status differs is a divergence: print `spec-id: A=passed B=failed` for each, and **exit non-zero**.
3. Only if verdicts match, report the timing delta: wall for each arm, absolute saving, per-spec saving, speedup — plus cold-starts vs reuses and peak live resources from each arm's trace artifact.

Register it in `CommandFactory.cs` next to `RunCommand`.

- [ ] **Step 4: Run the tests**

```bash
dotnet test tests/BattleScribeSpec.Cli.Tests
```

- [ ] **Step 5: Reproduce the documented warm-reuse measurements with the new command**

This proves `compare` is a true replacement, not merely a new command:

```bash
dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll compare \
  --engine battlescribe-ui --gamedata --filter "entry/,export/" \
  --config-a "" --config-b "BSSPEC_DISABLE_WARM_REUSE=1"
```

Expected: verdicts identical, and a speedup in the neighbourhood of the **2.20×** recorded in `docs/warm-reuse.md`. **If the verdicts diverge, stop and report it** — that would mean warm-reuse is not actually verdict-neutral, and that finding matters far more than this task does.

- [ ] **Step 6: Retire the script**

```bash
git rm scripts/bench-warm-reuse.ps1
```

Update `docs/warm-reuse.md`'s "Reproducing" section and the two comments in `src/BattleScribeSpec.EngineHost/ServeCommand.cs` (lines ~46 and ~67) to reference `bs-spec compare` instead.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(cli): bs-spec compare — verdict-equality rail; retire bench-warm-reuse.ps1 (#271)"
```

---

### Task 13: CI integration and docs

**Files:**
- Modify: `.github/workflows/ci.yml`
- Create: `docs/telemetry.md`
- Modify: `src/BattleScribeSpec.TestKit/Engines/EngineHostLocator.cs` (delete the false doc comment)
- Modify: `tests/Infrastructure/BsGameDataUiFixture.cs` (delete the `BS_UI_PATH` comment)
- Modify: `AGENTS.md`, `README.md`

- [ ] **Step 1: Delete the two documentation lies**

`EngineHostLocator.cs:26-28` claims the CLI sets `BSSPEC_HEADED` / `BSSPEC_KEEP_ALIVE` on the child env for launchable adapters. **No code sets or reads them.** Delete the claim. Do **not** implement the behavior here — actually honoring `--headed`/`--keep-alive` for `exec:`/`dotnet:` adapters is a behavioral change that belongs to Spec 2. File it as an issue instead, and reference the issue number in the comment you leave behind.

`BsGameDataUiFixture.cs:28` documents `BS_UI_PATH`, also read nowhere. Delete.

- [ ] **Step 2: Upload the trace artifact in CI**

In `.github/workflows/ci.yml`, add to the `checks`, `thorough-conformance` and `thorough-ui-bs` jobs:

```yaml
      - name: Upload telemetry
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: telemetry-${{ github.job }}-${{ strategy.job-index || '0' }}
          path: artifacts/telemetry/
          if-no-files-found: ignore
```

`if: always()` matters — a failed run's trace is the one you most want.

- [ ] **Step 3: Print the trace summary into the CI job summary**

After each conformance step, append the `TraceSummary` table to `$GITHUB_STEP_SUMMARY` so wall time, cold-starts vs reuses and peak live resources are visible on every PR without downloading anything.

**No perf gates.** A slow lane must not fail the build — shared runners are too noisy, and a flaky red is worse than an invisible regression.

- [ ] **Step 4: Write `docs/telemetry.md`**

Cover: what is emitted (spans, metrics, semconv); the parent-as-collector architecture and why (children use their stock exporter, so a third-party adapter in any language needs zero harness code); how to view a run in Jaeger (`OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318 bs-spec run --all …`); the artifact format and how to read it; `bs-spec compare`; and the known limitation that a hard-killed child loses its buffered spans, with the reason it is tolerable (the spans proving the death are parent-side).

- [ ] **Step 5: Full verification**

```bash
dotnet build
dotnet test -p:TestProfile=pre-push
dotnet format whitespace --verify-no-changes
```

Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "docs(telemetry): CI artifacts + job summary; docs/telemetry.md; delete two false env-var docs (#271)"
```

---

## Verification

The plan is done when all of the following hold:

1. `dotnet test -p:TestProfile=pre-push` is green.
2. `bs-spec run --all --engine battlescribe --workers 2` writes `artifacts/telemetry/run-*.traces.pb` and prints a summary.
3. Reading that artifact shows `spec` spans from the **parent** and protocol spans from the **child**, with the child's spans correctly parented — the property that makes the harness open to third-party adapters.
4. `bs-spec compare --engine battlescribe-ui --gamedata --config-a "" --config-b "BSSPEC_DISABLE_WARM_REUSE=1"` reports identical verdicts and reproduces the ~2.20× from `docs/warm-reuse.md`.
5. `bs-spec compare` exits non-zero when verdicts diverge.
6. `harness.resource.count` has a real peak value from a `dotnet test` run — the first direct measurement of the concurrency the repo has been running blind.
7. `dotnet build src/BattleScribeSpec.Cli` emits **zero** IL2026/IL3050 warnings.
