# Harness Telemetry Design (Spec 1 of 2)

**Status:** proposed
**Supersedes:** the observability half of the closed PR #302
**Followed by:** Spec 2 — *Harness concurrency & reuse model* (adaptive parallelism, retiring the tuning env vars, CI re-scoping)

## Goal

Make the harness observable — across every process it spawns and both of its entry points — using OpenTelemetry, so that the concurrency/reuse decisions in Spec 2 are made from evidence rather than from benchmark scripts written after the fact.

## Why this comes first

Every wrong turn in this effort came from tuning something we could not see:

- Warm-reuse was built on the premise that NewRecruit's per-spec Chromium cold start was the dominant cost. **It is not** — Chromium relaunches in ~1.6s and NR gains nothing (0.92×). The real win was BattleScribe UI (JVM + JavaFX), which nobody was looking at. The premise survived for weeks because no measurement contradicted it.
- NR-UI roster warm-reuse shipped "verified" because the *mechanism* worked — one browser launch instead of N. Nobody measured whether the *verdicts* were still correct. They were not: 6 specs silently changed result. A wall-clock number said success while conformance regressed.
- `NR_PARALLEL=6` was measured optimal **in isolation**, on a lane where nothing else runs. It is not a global truth, and nothing in the system knows that.

The through-line is that the harness measures wall time and nothing else. Spec 2 proposes to make the harness *tune itself*. Auto-tuning without observability is not an improvement; it is a new and better-hidden place for exactly these bugs to live.

## Baseline: what exists today

### Three parallelism mechanisms, mutually unaware

| Mechanism | Multiplies | Bounded by |
|---|---|---|
| `--workers N` (CLI path) | adapter **host processes** | registry `MaxParallel`, then the `describe` handshake |
| `NR_PARALLEL` (xUnit path) | **browser contexts** in an in-process pool | nothing — a raw env var |
| xUnit `parallelizeTestCollections: true` | **test collections** (11 of them) | **nothing** — `maxParallelThreads` is unset, so it defaults to CPU count |

They compose **multiplicatively and unbounded**. One `dotnet test` may legally bring up the `FrozenNrRoster` pool (5 contexts), the `FrozenNrGameDataUi` pool (5 contexts), `FrozenNrUiRoster`, and the BattleScribe JVM concurrently. `MaxParallel = 1` on `battlescribe-ui` protects the CLI path only; the xUnit path is unprotected.

This is the single most important thing the telemetry must make visible, and it is the reason the tuning knobs feel arbitrary: they are not three dials on one system, they are three systems with no shared budget.

### Knobs in play

~30 environment variables gate parallelism, reuse, timeouts and diagnostics. The load-bearing ones:

| Knob | Layer | Default |
|---|---|---|
| `--workers N` | N adapter host processes | 1 |
| `NR_PARALLEL` | N browser contexts in an in-process pool | 5 (frozen), 10 (live), 6 in CI |
| `BSSPEC_DISABLE_WARM_REUSE` | forces every domain cold | unset (reuse on for `battlescribe-ui`) |
| `BS_UI_KEEP_ALIVE` | BattleScribe JVM survives between specs (xUnit path) | false; `true` in CI |
| `AdapterOptions.Reuse{Roster,GameData}EngineAcrossSetups` | engine survives setup/teardown | true only for `battlescribe-ui` |
| `EngineRegistry` `MaxParallel` | CLI worker ceiling | `battlescribe-ui` = 1; all others 0 (unlimited) |
| xUnit `maxParallelThreads` | concurrent test collections | **unset → CPU count** |

Spec 2 retires the tuning knobs. This spec does not touch them; it makes their effects measurable.

### Two pre-existing correctness bugs this spec absorbs

1. **Diagnostics directories collide.** `BS_UI_DIAGNOSTICS_DIR`, `BS_GAMEDATA_UI_DIAGNOSTICS_DIR`, `NR_GAMEDATA_UI_DIAGNOSTICS_DIR` are single, shared and unsuffixed. At `--workers > 1`, concurrent workers overwrite each other's diagnostics. Observability work sits directly on top of this, so it is fixed here rather than filed.
2. **#303 — host stderr is buffered, not forwarded.** `AdapterProcess` swallows the engine host's stderr, so host-side diagnostics are invisible during a run. This actively obstructed the NR-UI roster diagnosis. Fixed here; #303 is closed by this spec rather than separately.

### Dead knobs (delete)

- `BSSPEC_HEADED` / `BSSPEC_KEEP_ALIVE` — documented in `EngineHostLocator.cs:26-28`, **read nowhere**. `--headed` and `--keep-alive` are therefore silently dropped for `exec:`/`dotnet:` adapters. A knob that does not exist is worse than one misconfigured. This spec **deletes the false doc comment only**; actually honoring `--headed`/`--keep-alive` for launchable adapters is a behavioral change that belongs to Spec 2, and is filed as an issue rather than smuggled in here.
- `BS_UI_PATH` — documented in `BsGameDataUiFixture.cs:28`, read nowhere. Delete the comment.

## The five questions the telemetry must answer

The design is driven by these, not by "collect telemetry":

1. **Where does a spec's wall time go?** setup / action / getState / teardown / waiting to acquire an engine.
2. **How many expensive resources are alive at once, of what kind?** The question that catches the multiplicative blowup.
3. **Was this resource cold-started or reused, and what did each cost?** The warm-reuse question, asked continuously rather than by a one-off script.
4. **Which worker / process / context ran this spec?** Attribution — impossible today.
5. **Did the verdicts change between two configurations?** The correctness rail. Today only `bench-warm-reuse.ps1` asks, ad hoc.

Questions 1, 3, 4 are **traces**. Question 2 is a **metric** (an up-down counter) — a span cannot express "three pools and a JVM are alive simultaneously." Question 5 is a **tool** (see `bs-spec compare`).

## Architecture

### Parent-as-collector, over real OTLP

```
  bs-spec (CLI)                                    dotnet test (xUnit)
  ├── HarnessCollector                             ├── HarnessCollector
  │   ├── OTLP receiver on 127.0.0.1:<eph>         │   (in-process sink only,
  │   │   (gRPC + HTTP/protobuf)                   │    no port needed — but may
  │   ├── in-process sink                          │    bind one for the JVM agent)
  │   └── .otlp.pb artifact writer                 └── fixtures / pools feed it directly
  ├── ActivitySource + Meter (own spans)
  └── spawns N children:
      OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:<eph>   ← env
      bs-engine-host  ──stock OTLP exporter──▶ parent receiver
        └── per-request `traceparent` arrives over the adapter protocol
```

**The decisive property: children use the stock exporter.** Every OTel SDK — .NET, Python, JS, Java, Go — honors `OTEL_EXPORTER_OTLP_ENDPOINT` out of the box. A third-party adapter therefore needs **zero harness-specific code** to appear in our traces. That is a genuine openness guarantee; a private file format would be a private format wearing a standard's clothes.

### Why not per-process trace files

The rejected alternative was "each process writes its own NDJSON; merge at the end." It is simpler and crash-resilient, but it requires every third-party adapter, in every language, to learn *our* file layout and *our* naming convention. Since the harness's premise is an open adapter ecosystem, that cost lands in exactly the wrong place. Real OTLP moves the cost to us (we must build a receiver) and removes it from every adapter author.

### What we must build, and the .NET constraint

There is **no stock OTLP receiver in .NET** — the SDK exports only. And OpenTelemetry .NET's exporter implements **gRPC and HTTP/protobuf only**; its `OtlpExportProtocol` enum has exactly two members (`Grpc = 0`, `HttpProtobuf = 1`). **`HttpJson` does not exist.** (Verified against `opentelemetry-dotnet` source, not from memory.)

**Protobuf-only receiver — this covers every language we care about.** Chase the constraint through: .NET and Java *cannot* send `http/json`; Python and JS *default to* `http/protobuf`. So a receiver speaking gRPC + HTTP/protobuf accepts 100% of stock SDK exporters out of the box. JSON *ingest* is therefore **deferred**, not designed in — it is a bounded add (~1,000 LOC, see below) the day an adapter author actually needs it.

There is also **no official OTel proto NuGet for .NET.** `OpenTelemetry.Exporter.OpenTelemetryProtocol` generates the types but marks them `internal`. Every .NET project needing server-side OTLP types vendors the `.proto` files and runs `Grpc.Tools` — this is the sanctioned pattern, and it is what Aspire does.

So:

- **Vendor** the OpenTelemetry `.proto` files (Apache-2.0, ~1,700 lines, never edited) and generate at build via `Grpc.Tools`.
- **One dependency:** `Grpc.AspNetCore` (transitively brings `Google.Protobuf`).
- Two Kestrel listeners on `127.0.0.1:0` — gRPC needs `HttpProtocols.Http2`, HTTP/protobuf takes `Http1AndHttp2` — with the real port read back and injected into children.

Estimated ~170 LOC of endpoints + wiring.

**The receiver must be spec-compliant, not merely functional.** On success OTLP requires a protobuf-encoded `Export<signal>ServiceResponse` body with the same `Content-Type` as the request — an empty `200 OK` violates a MUST.

This is worth stating explicitly because the failure is invisible from where we stand: **OpenTelemetry .NET never deserializes the response body**, so an empty 200 works perfectly for .NET children and every test we would write. Python and JS SDKs *do* parse it and log deserialization errors. A receiver that is compliant only for the language we happen to use is the one thing this design cannot afford, since its entire justification is third-party adapters in *other* languages. It is a three-line fix.

**Metric units follow the convention, not our convenience.** Durations are recorded in **seconds** (OTel: "when instruments are measuring durations, seconds SHOULD be used"), with explicit bucket boundaries supplied — the SDK's default buckets are millisecond-tuned, so a seconds-valued histogram would collapse every engine start into one bucket and make p50/p95 meaningless. Instrument and attribute names are namespaced (`harness.engine.reused`, not `reused`), and UpDownCounter names are not pluralized (`harness.resource.count`).

### Artifact format

The parent writes received OTLP as a **length-delimited protobuf stream** (`Google.Protobuf`'s `WriteDelimitedTo`) to `run-<id>.otlp.pb`. Lossless, exact, ~10 LOC, and our summary and `compare` tooling reads it back using the same generated types.

**Rejected: writing OTLP-JSON directly.** An earlier draft of this spec claimed `Google.Protobuf`'s `JsonFormatter` yields canonical OTLP-JSON for free. **That claim is false**, and it is worth recording why, because it inverted a cost estimate. OTLP-JSON deliberately deviates from the proto3 JSON mapping:

1. `traceId` / `spanId` are **hex** strings — `JsonFormatter` emits base64.
2. Enum values must be **integers** — `JsonFormatter` emits names by default.

This is precisely why Aspire hand-wrote ~940 lines of JSON⇄protobuf conversion plus ~1,400 lines of source-generated DTOs rather than calling `JsonFormatter`. A spec-exact OTLP-JSON writer is a **~500 LOC component**, not a two-liner.

We do not need it, because **what makes off-the-shelf backends render our runs is the semantic conventions, not our file format.** Anyone wanting Jaeger/Tempo points `OTEL_EXPORTER_OTLP_ENDPOINT` at it and gets live ingest with no file in the loop. The artifact exists for CI archival and for `compare` — both of which are our own readers.

A `bs-spec trace export --json` converter is therefore **deferred until someone needs the file itself**, at which point Aspire's MIT-licensed OTLP-JSON DTOs are available to copy.

### Prior art: why not reuse Aspire

The .NET Aspire dashboard *is* an OTLP receiver (gRPC + HTTP/protobuf + HTTP/JSON), and was evaluated directly.

- **Not consumable as a package.** `Aspire.Dashboard` is an app (`Microsoft.NET.Sdk.Web`), not a library; the published `Aspire.Dashboard.Sdk.*` packages ship an executable, not types. Microsoft themselves reuse this code by *linking source*. Vendoring is the only reuse path.
- **The standalone dashboard cannot do the job.** Its telemetry is **in-memory only and never persisted** — it physically cannot produce our artifact — and using it would drag Docker, Blazor, FluentUI and an AI assistant into CI.
- **What we take from it:** the `.proto` file set and layout, and the pattern for binding `127.0.0.1:0` and resolving the real port. Licensing is clean — MIT (Aspire C#) and Apache-2.0 (protos), attribution only; record both in `THIRD-PARTY-NOTICES.txt`.

### Use the stock SDK in the collector — do not hand-roll an exporter

`BattleScribeSpec.Telemetry.Collector` hosts a real OpenTelemetry `TracerProvider` and `MeterProvider`. It is **not** AOT-marked, so the SDK's reflection is harmless there, and `Cli` reaches it through a facade.

An earlier draft hand-rolled an `Activity` → protobuf converter here, on the grounds that "the OTel SDK is not AOT-safe." That reasoning is correct for `Cli` and `TestKit` and **was wrongly extended to the collector**. The bespoke exporter silently dropped every non-string tag (`Activity.Tags` yields only `string` values), dropped span kind, dropped span **status** (so a failed spec would have rendered green), dropped events, and emitted an empty `Resource` (no `service.name` — Jaeger keys on it). Using the SDK deletes that code and fixes all of it, because the SDK already gets it right.

The rule stands where it belongs: **the OTel SDK must never be referenced from `Cli` or `TestKit`.** Those two use only the BCL `ActivitySource`/`Meter`.

### Semantic conventions

Emit OTel's test, CI/CD and VCS conventions so off-the-shelf backends render conformance runs with no adapter on their side:

- **Test** (stability: *Development* — expect churn): `test.case.name` (spec id), `test.suite.name` (category), `test.case.result.status`, `test.suite.run.status`.
- **CI/CD + VCS** (stability: *Release Candidate* — near-stable): `cicd.pipeline.name`, `cicd.pipeline.run.id`, `cicd.pipeline.run.url.full`, `cicd.pipeline.task.type`, `vcs.repository.url.full`, `vcs.ref.head.name`, `vcs.ref.head.revision`.

**`test.case.result.status` admits only `pass` and `fail`.** The harness has a four-way verdict (`passed`, `failed`, `expected-failure`, `unexpected-pass`), so it carries its own value on `bsspec.verdict` and maps down to the two the convention allows. Emitting our richer vocabulary into the standard attribute would make us unreadable by the very backends we adopted OTel to satisfy.

Conventions are pinned to a stated version in one place and are additive — nothing in the harness reads them back, so churn cannot break a run. Our resource-lifecycle vocabulary is ours; OTel has no convention for "engine cold start."

### Span kind: CLIENT and SERVER, not INTERNAL

An adapter command is a remote call: the parent writes it over the NDJSON wire and awaits a response; the child handles it. So the parent's `setup`/`action`/`getState`/`teardown` spans are **`CLIENT`** and the child's handler span is **`SERVER`**.

This is not pedantry. Jaeger's dependency graph and Tempo's `servicegraph` processor derive edges **exclusively** from CLIENT→SERVER pairs. With `INTERNAL` on both sides there is **no edge at all** between `bs-spec` and `bs-engine-host` — precisely the picture this design exists to produce.

For the same reason parent and child carry **different** `service.name` values (`bs-spec` and `bs-engine-host`): that is what makes them two nodes with an edge rather than one anonymous blob. Each worker additionally sets `service.instance.id`, without which per-worker attribution is unachievable in any backend.

### Trace context: env vs. protocol

These carry different things, and conflating them collapses the trace:

- **Child env** → `OTEL_EXPORTER_OTLP_ENDPOINT` (where to send), `OTEL_RESOURCE_ATTRIBUTES` (worker identity).
- **Adapter protocol** → a **per-request `traceparent`** (and `tracestate`), alongside the existing `corrId`.

A single `bs-engine-host` serves *many* specs. An env-level `traceparent` would pin every one of them under one static parent, flattening hundreds of specs into a single trace. Per-spec correlation must ride the protocol.

`tracestate` travels with `traceparent` because W3C requires a vendor that receives it to forward it — without it, a third-party adapter behind a vendor backend loses its vendor context, which is exactly the cross-language case this design is built for.

This makes the protocol change load-bearing, so it passes through `docs/protocol-schema.json` and `ProtocolSchemaDriftTests`. Both fields are **optional**: adapters that ignore them still work, exactly as with `corrId`.

> `OTEL_EXPORTER_OTLP_ENDPOINT` is a **base URL** — the SDK appends `v1/traces` / `v1/metrics` / `v1/logs`, which is why the receiver maps exactly those paths. This holds only when the endpoint arrives via the environment variable: OpenTelemetry .NET sets `AppendSignalPathToEndpoint = false` whenever `OtlpExporterOptions.Endpoint` is assigned programmatically.

## Instrumentation points

Both entry points are covered. The xUnit path is not optional — it is where the unbounded parallelism lives.

| Layer | Spans | Resource events / metrics |
|---|---|---|
| CLI `SpecSuiteRunner` | `run`, `spec` (carries `test.*` attrs + verdict) | worker count; queue wait |
| `AdapterProcess` (parent side) | `setup`, `action`, `getState`, `teardown` | adapter process spawn / exit, incl. **unexpected death** |
| `bs-engine-host` / `AdapterHandler` (child side) | child spans, nested via the per-request `traceparent` | `engine.cold_start` vs `engine.reuse`, with duration for each |
| Engine drivers (BS-UI, NR, NR-UI) | — | `jvm.start` / `restart` / `poison`; `browser.launch`; `context.create` / `destroy` |
| xUnit fixtures + pools | `spec` per pooled iteration | pool size; acquire wait; **live-resource up-down counter** |

The live-resource counter is process-wide. If three pools and a JVM are alive simultaneously, the number says so — which is precisely what no existing signal can express.

Take the stock **runtime/process metrics** instrumentation (CPU, GC, thread pool) as well. "Are we actually CPU-saturated at N workers, or merely I/O-blocked?" is the question Spec 2's auto-tuner must answer, and it is a free OTel metric.

## `bs-spec compare`

Promote verdict-equality from a PowerShell script to a first-class command.

```
bs-spec compare --engine <e> [--roster|--gamedata] [--filter <f>] \
                --config-a <k=v,...> --config-b <k=v,...>
```

Runs the same spec set under two configurations, then:

1. **Asserts per-spec verdicts are identical.** A configuration change that alters conformance results is not an optimization; it is a regression. This assertion is the whole point — it is the check that caught NR-UI roster warm-reuse silently changing 6 verdicts while the stopwatch reported success.
2. Reports the timing delta, cold-start vs reuse counts, and peak live resources, from the traces both runs emit.

Exit non-zero on any verdict divergence. Retires `scripts/bench-warm-reuse.ps1`.

This is the safety rail Spec 2 must pass: **no tuning change ships without a verdict-neutral `compare` run.** It is deliberately built *before* the thing it guards, because building the guard alongside the guarded change is how the broken warm path shipped in the first place.

## Failure modes

Telemetry must never be able to fail a run or slow it materially.

- **Export is fail-open.** If the receiver is unreachable or export errors, the run proceeds. Short export timeout; failures are logged once, not per span.
- **No network.** Loopback only. CI stays offline. `OTEL_EXPORTER_OTLP_ENDPOINT` is honored if externally set, so a user may point at their own collector — this is the one environment variable retained, and it is an industry standard rather than a bespoke dial of ours.
- **Sampling is AlwaysOn.** We want every span; the volume is small and bounded by spec count.
- **Hard-killed children lose buffered spans.** `BatchSpanProcessor` buffers; a SIGKILLed `bs-engine-host` — or the BattleScribe JVM taking its process down — drops whatever is in flight. Mitigation: short batch delay (~500ms) plus explicit `ForceFlush` on teardown and on unhandled exception.

  **This is an accepted limitation, not a solved problem.** It is tolerable because *the spans that prove a death are parent-side*: `AdapterProcess` lives in the surviving CLI, so the `setup`/`action`/`teardown` spans and the process-exit event are emitted regardless. The child's spans add detail, not evidence. A hard kill costs sub-second of child-side detail.

- **Loopback assumes same-host children.** `exec:` / `dotnet:` adapters are child processes on this machine, so `127.0.0.1` reaches them. A containerized adapter on a separate network would not be able to reach the receiver. Out of scope; documented.

## Deliberately deferred

**The OTel Java agent alongside our own `-javaagent` in the BattleScribe app.** It would give JVM metrics, and two agents can technically coexist. But the BS app's intermittent self-termination is still unexplained, and our own `sun.misc.Unsafe` usage in `EngineAccessor` remains an open suspect. Adding a second bytecode-rewriting agent into an unsolved native crash trades a small win for a large confound. The JVM can instead export over OTLP from our existing agent once the port is bound — same benefit, no new agent.

## Testing

- **Receiver:** unit tests posting canned OTLP protobuf payloads over both gRPC and HTTP/protobuf; assert both decode and land in the artifact. A JSON body must be rejected with a clear 415 rather than silently dropped — an unsupported encoding should be loud.
- **Round-trip:** write the `.otlp.pb` artifact and read it back with the generated types; assert span identity and attributes survive. This is the property `compare` depends on.
- **Propagation:** an end-to-end test asserting that a child-emitted span's `parent_span_id` matches the `traceparent` the parent sent over the protocol — the property that makes third-party nesting work, so it is tested directly rather than assumed.
- **Fail-open:** run with the receiver deliberately unbound; assert the run completes and verdicts are unchanged.
- **`compare`:** a red test — two configurations with a deliberately divergent verdict must exit non-zero.
- **Protocol:** `traceparent` is optional; an adapter that ignores it still passes conformance (same contract as `corrId`).
- **No regression:** `bs-spec compare` between telemetry-on and telemetry-off must show identical verdicts and no material wall-clock delta.

## Out of scope (→ Spec 2)

The concurrency/reuse model itself: a shared budget across the three mechanisms, adaptive parallelism derived from the environment, retiring `NR_PARALLEL` / `BSSPEC_DISABLE_WARM_REUSE` / `--workers` guesswork, and re-scoping the CI jobs and spec sets.

## Findings this spec does not fix

Recorded here because they surfaced while gathering the baseline and would otherwise be lost.

**`nr-ui-frozen` runs 1 of 477 specs it can run.** The lane hard-codes `protocol/protocol-kitchen-sink`, justified by a comment claiming the frozen HAR supports only a single roster-creation flow per run. That was true in the shared-context era; it is no longer true. Measured on the current code — full NR-UI frozen driver, all 477 specs, 8 workers: **431 passed / 46 failed in 831s.**

The 46 failures cluster (`force` 11, `selection` 7, `modifier` 7) rather than spreading uniformly, which is the shape of **genuine NR-UI conformance gaps**, not flakes. These are engine conformance bugs, not harness bugs, and belong in neither spec — they need their own triage. The *lane scoping* question (831s at 8 workers is a thorough-tier cost, not a per-push one) belongs to Spec 2.
