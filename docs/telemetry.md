# Telemetry

The harness emits [OpenTelemetry](https://opentelemetry.io/) (OTel) traces and metrics for every
`bs-spec run --all`, `bs-spec compare`, and `dotnet test` invocation. This is not a bolted-on
logging feature: it is how the repo answers questions it used to have no way to answer at
all — "how many browsers/JVMs were alive at once during this test run?", "did warm-reuse actually
change which specs failed?", "which specs are the slowest 10 in this batch?" — without adding a
single print statement to a test.

## What is emitted

### Spans

| Span | Emitted by | Notes |
|---|---|---|
| `run` | `bs-spec run --all` / `compare` (per arm) | Wraps the whole batch; carries `cicd.*`/`vcs.*` (below) when run in GitHub Actions. |
| one span per spec, named for the spec ID | `SpecSuiteRunner` | Tagged with the `test.*` semantic conventions (below). Parent of everything that spec does. |
| adapter protocol command spans (e.g. `describe`, `setup`, `step`) | `AdapterProcess`/`AdapterHandler` | `CLIENT` on the sending side, `SERVER` on the handling side — this is what gives Jaeger/Tempo a service-graph edge between `bs-spec` and `bs-engine-host`/an external adapter, rather than two disconnected blobs. |

Every span the *parent* process starts uses `HarnessTelemetry.StartOp`/`StartSpec`
(`src/BattleScribeSpec.Telemetry/HarnessTelemetry.cs`), which only depends on
`System.Diagnostics.ActivitySource` — no OTel SDK reference, so it stays AOT/trim-safe and usable
from the analyzer-strict `Cli`/`TestKit` projects.

**Semantic conventions used:**

- `test.case.name`, `test.suite.name`, `test.case.result.status` (`pass`/`fail`) — OTel's test
  semantic conventions (Development stability). The harness's own richer four-way verdict
  (`passed`/`failed`/`expected-failure`/`unexpected-pass`) lives alongside as `bsspec.verdict`,
  because `test.case.result.status` only admits `pass`/`fail`.
- `cicd.pipeline.name`, `cicd.pipeline.run.id`, `cicd.pipeline.run.url.full`,
  `cicd.pipeline.task.type`, `vcs.repository.url.full`, `vcs.ref.head.name`,
  `vcs.ref.head.revision` — set on the `run` span, only when the standard GitHub Actions env vars
  (`GITHUB_WORKFLOW`, `GITHUB_RUN_ID`, `GITHUB_SHA`, ...) are present (`RunBatch.cs`). Release
  Candidate stability.

### Metrics

| Metric | Kind | Description |
|---|---|---|
| `harness.resource.count` | UpDownCounter, `{resource}` | Expensive resources currently alive, by `harness.resource.kind` (`jvm`, `browser`, `browser-context`, `adapter-process`, ...). The signal that makes the harness's parallelism visible — nothing else in the system can answer "how many are alive right now?" |
| `harness.engine.start.duration` | Histogram, seconds | Engine acquisition cost, tagged `harness.resource.kind` and `harness.engine.reused` (cold start vs warm reuse). |

Both are defined in `src/BattleScribeSpec.Telemetry/ResourceMetrics.cs` and recorded directly by
the engine pools (browser-context pools, the JVM/BS-UI pool, `AdapterProcess`) — independent of
whether any spec span exists around them.

## The parent-as-collector architecture, and why

The CLI process (`bs-spec`) binds a small OTLP/HTTP receiver on an **ephemeral loopback port**
(`HarnessCollector`, `src/BattleScribeSpec.Telemetry.Collector/HarnessCollector.cs`) and hands
every child process (`bs-engine-host`, or any external adapter) the standard OTel environment —
`OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`, `OTEL_SERVICE_NAME`,
etc. — via `HarnessCollector.ChildEnvironment`.

This is the whole design, and it is deliberate: **the child exports with its own stock OTel SDK
and needs zero harness-specific code.** A child written in any language, by anyone — a Python
adapter, a Node adapter, a third-party `.NET` adapter — only needs the standard OTel SDK for its
language pointed at `OTEL_EXPORTER_OTLP_ENDPOINT`, and its spans show up correctly parented under
the parent's spec span (propagated via the adapter protocol's `traceparent`/`tracestate` fields).
That is why the harness speaks **real OTLP** — length-delimited protobuf, the wire format every
OTel SDK already knows how to emit — rather than inventing a private telemetry format that would
require every adapter author to special-case this repo.

The parent's own spans/metrics reach the SAME destination via its own OTel SDK
(`ParentProviders.Attach`), so the artifact ends up with both the parent's spec/run spans and
every child's protocol spans in one place, correctly parented to each other.

## Viewing a run in Jaeger

The harness honors an **externally-set** `OTEL_EXPORTER_OTLP_ENDPOINT` instead of self-hosting its
own receiver — this is the one environment variable the harness *honors* rather than *owns*,
because it is an industry standard rather than a bespoke dial of ours. Point it at a local Jaeger
(or Tempo, or any OTLP/HTTP backend):

```bash
docker run -d --name jaeger -p 4318:4318 -p 16686:16686 jaegertracing/all-in-one:latest

OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318 \
  bs-spec run --all --engine battlescribe --workers 2
```

Open `http://localhost:16686` and look for the `bs-spec`/`bs-engine-host` services. In this mode
there is **no local `.traces.pb` artifact** — the externally-set collector owns the data, and
`HarnessCollector.HasLocalArtifact` is false (so `TraceSummary`/CI upload have nothing local to
read; the run still prints normally).

## The artifact format, and how to read it

When no external collector is configured (the common case, including every CI job), the harness
writes its own artifact: three sibling files per run, one per OTLP signal —

```
artifacts/telemetry/run-<id>.traces.pb
artifacts/telemetry/run-<id>.metrics.pb
artifacts/telemetry/run-<id>.logs.pb
```

(`compare` writes `compare-a-<id>`/`compare-b-<id>`; the `dotnet test` assembly fixture writes
`xunit-<timestamp>`, anchored at the repo root explicitly — VSTest runs the test host with its
working directory set to the test assembly's own output folder, not the repo root, so a bare
relative path would silently land somewhere under `artifacts/bin/.../`.)

Each file is a **length-delimited stream of protobuf messages** — the same
`ExportTraceServiceRequest`/`ExportMetricsServiceRequest`/`ExportLogsServiceRequest` messages the
receiver got over HTTP, one per line the collector received, written verbatim
(`OtlpArtifactWriter`). It is lossless: what the collector received is exactly what is on disk.
Three separate files rather than one interleaved stream, because a length-delimited protobuf
stream is only self-describing when every message in it has the same type.

Read it back with `OtlpArtifactReader.ReadTraces`/`ReadMetrics`/`ReadLogs`
(`src/BattleScribeSpec.Telemetry.Collector/OtlpArtifactReader.cs`), which yields the generated OTLP
types directly — or, for the common case of "just show me the numbers", use
`TraceSummary.FromArtifact(basePath)`
(`src/BattleScribeSpec.Telemetry.Collector/TraceSummary.cs`). It turns the artifact into a compact
record: spec count, wall time, p50/p95 spec duration, cold-starts vs warm-reuses, peak live
resources (overall and by kind), and the 10 slowest specs. `TraceSummary.WriteTable` renders it as
plain text (what `run --all`/`compare` print to stderr); `AppendToGitHubStepSummary` renders the
same table as a markdown-fenced block and appends it to `$GITHUB_STEP_SUMMARY` when that env var
is set — which is how every PR gets wall time, cold-starts vs reuses, and peak live resources on
the checks page, without downloading anything.

## `bs-spec compare`

```bash
bs-spec compare --engine battlescribe-ui --gamedata \
  --config-a "" --config-b "BSSPEC_DISABLE_WARM_REUSE=1"
```

`compare` runs the **same spec set twice**, once per `--config-*` arm, each arm's child adapter
processes getting their own extra environment. Before reporting any timing, it asserts that the
two arms' **per-spec verdicts are identical**. The command's entire reason to exist is this
guarantee: **a configuration change that alters conformance results is not an optimization, it is
a regression.** A speedup that also changes which specs pass or fail has not been validated — it
has only been timed. `compare` exits non-zero the moment a verdict diverges, before printing a
single timing number, so a regression can never hide behind an attractive speedup figure. See
`docs/warm-reuse.md` for the measurements this command produced (e.g. `battlescribe-ui` gamedata
warm-reuse: 54 specs, verdicts identical, 2.20× faster).

## Known limitations, stated honestly

- **A hard-killed child loses its buffered spans.** `OTEL_BSP_SCHEDULE_DELAY=500` keeps the export
  batch window short specifically to limit this, but a child that dies hard (e.g. the BattleScribe
  JVM taking its own process down) can still lose whatever spans were buffered and not yet
  exported. This is tolerable: the spans that *prove* the death happened are parent-side —
  `AdapterProcess` (which lives in the surviving CLI process) records the failed command and the
  process exit. The child's own spans would only have added detail about what it was doing at the
  moment it died, not the evidence that it died.
- **Any peak read off `harness.resource.count` is a LOWER BOUND, not an exact maximum.** The
  metric only exists in the artifact at each periodic export tick; a concurrency spike that both
  rises and falls entirely between two export ticks never appears at all. A peak of N read off
  this counter only proves "at least N were alive at once, at some point" — the true peak may have
  been higher. **This caveat must travel with the number wherever it is used for tuning** — do not
  quote a peak from this counter as an exact ceiling. `TraceSummary.WriteTable` labels the number
  `>= N` and marks it "not sampled" (rather than a misleading `0`) when a run finished faster than
  one export interval and no data point was ever recorded.
- **Loopback assumes a same-host child.** `HarnessCollector` binds `127.0.0.1`; a child on a
  separate host or in an isolated container network (no route to the parent's loopback interface)
  cannot reach the receiver and simply will not export. Point such a setup at an external collector
  via `OTEL_EXPORTER_OTLP_ENDPOINT` instead (see "Viewing a run in Jaeger" above) — that path works
  regardless of network topology.
- **A `dotnet test` run's artifact has no spec spans.** Individual xUnit `[Fact]`/`[Theory]` tests
  call `GameDataRunner`/`RosterRunner` directly, not through `SpecSuiteRunner` (the only emitter of
  spec spans) — so `TraceSummary.FromArtifact` on a `xunit-<timestamp>` artifact always reports
  `SpecCount == 0`, and its "slowest specs" list is always empty. The engine pools still emit
  `harness.resource.count`/`harness.engine.start.duration` directly, so cold-start/reuse counts and
  peak live resources are still real and meaningful for that artifact — only per-spec duration data
  is unavailable outside the CLI batch path.

## In CI

`.github/workflows/ci.yml`'s `checks`, `thorough-conformance`, and `thorough-ui-bs` jobs upload
`artifacts/telemetry/` as a build artifact — `if: always()`, so a **failed** run's trace (the one
you most want) is captured too, not just a green one. `if-no-files-found: ignore` matters:
`artifacts/` is gitignored and a lane where telemetry never started (an unrelated early failure)
should not fail the upload step. There are **no performance gates** — a slow lane does not fail
the build; shared CI runners are too noisy for that to be anything but a flaky red, and a flaky
red is worse than an invisible regression. The numbers are published (job summary + artifact);
nothing is enforced on them.
