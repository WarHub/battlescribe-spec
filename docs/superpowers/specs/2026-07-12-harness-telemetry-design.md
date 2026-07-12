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
  │   ├── OTLP/HTTP receiver on 127.0.0.1:<eph>    │   (in-process sink only,
  │   ├── in-process sink                          │    no port needed — but may
  │   └── OTLP-JSON artifact writer                │    bind one for the JVM agent)
  │                                                └── fixtures / pools feed it directly
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

There is **no stock OTLP receiver in .NET** — the SDK exports only. And OpenTelemetry .NET's exporter implements **gRPC and HTTP/protobuf only; it does not implement `http/json`.** So the parent cannot simply forward a JSON body verbatim to the artifact; it must decode OTLP protobuf.

Consequences:

- Take a `Google.Protobuf` dependency plus the OpenTelemetry `.proto` types (vendored + generated at build).
- The receiver **content-type sniffs** and accepts `application/x-protobuf` *and* `application/json`. .NET children send protobuf; Python/JS adapters may send `http/json`, and accepting both is what keeps third-party adapters trivial.
- The artifact is canonical **OTLP-JSON**, produced by `Google.Protobuf`'s `JsonFormatter` — a serializer we do not write or maintain.

### Semantic conventions

Emit OTel's experimental test and CI/CD conventions so off-the-shelf backends render conformance runs with no adapter on their side:

- `test.case.name` (spec id), `test.suite.name` (category), `test.case.result.status` (`pass` / `fail`)
- `cicd.pipeline.name`, `cicd.pipeline.run.id` when running in CI

These conventions are **experimental and will churn**. They are pinned to a stated version in one place and are additive — nothing in the harness reads them back, so churn cannot break a run. Our resource-lifecycle vocabulary (below) is ours; OTel has no convention for "engine cold start."

### Trace context: env vs. protocol

These carry different things, and conflating them collapses the trace:

- **Child env** → `OTEL_EXPORTER_OTLP_ENDPOINT` (where to send). Optionally a `traceparent` for the host process's **own lifetime** span.
- **Adapter protocol** → a **per-request `traceparent`**, alongside the existing `corrId`.

A single `bs-engine-host` serves *many* specs. An env-level `traceparent` would pin every one of them under one static parent, flattening hundreds of specs into a single trace. Per-spec correlation must ride the protocol.

This makes the protocol change load-bearing, so it passes through `docs/protocol-schema.json` and `ProtocolSchemaDriftTests`. The field is **optional**: adapters that ignore it still work, exactly as with `corrId`.

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

- **Receiver:** unit tests posting canned OTLP protobuf *and* JSON payloads; assert both decode and land in the artifact.
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
