# Unified `bs-spec` CLI — design (#271)

Status: approved design, pre-implementation.
Issue: [#271](https://github.com/WarHub/battlescribe-spec/issues/271) (part of epic #73).
Related: #272 (protocol diagnostics — a parity subset is pulled in here), #276 (snapshot verb — unblocked by this), #159 (protocol JSON Schema).

## Goal

One entry point. `bs-spec` becomes the single CLI for running, inspecting, and
formatting conformance specs; `bs-spec-runner` (`src/BattleScribeSpec.Runner`) is
deleted. In the same move, the CLI becomes an **AOT-publishable orchestrator**
that drives every engine — built-in or third-party — as a child process over the
NDJSON adapter protocol. "Built-in" engines are just adapters that ship in-box.

## Architecture

```
bs-spec  (AOT-publishable; references TestKit only)
  └─ launches engines as child processes, NDJSON protocol v1.1 over stdio
       ├─ bs-engine-host --engine battlescribe|battlescribe-ui|newrecruit|newrecruit-ui
       │     new project, NOT AOT; hosts today's in-process engines and UI
       │     drivers (Playwright, Java agent), both domains
       ├─ bs-reference-adapter          (existing)
       └─ any exec:/dotnet: adapter     (wham, phalanx, future engines)
```

### `bs-spec` (BattleScribeSpec.Cli)

- Keeps all verbs: `run`, `verify`, `probe`, `export-xml`, `format`, `discover`.
- `format` is engine-free and unchanged. `export-xml` is pure serialization
  but depends on `CatXmlGenerator` (XmlSerializer/reflection-based, today in
  the NewRecruit project); that type moves to a new small shared
  **`BattleScribeSpec.XmlGen`** project (not AOT-flagged) referenced by the
  CLI, the engine host, and the drivers.
- `run`, `verify` interact with engines **only** via the adapter protocol.
  All engine/driver project references are dropped from the csproj.
- `probe` and `discover` drive engine-specific surfaces (scene dumps, raw JS
  eval, widget probing) the protocol does not carry — they become
  **host verbs**: their implementations move wholesale into `bs-engine-host`
  (`bs-engine-host probe|discover …`), and the `bs-spec` verbs resolve the
  engine via the registry and forward (spawn the host with passthrough
  stdio, so interactive REPLs keep working). Protocol-native probing stays
  #272 territory. The engine-specific bootstrap (jar resolution, frozen
  static dirs) moves into `bs-engine-host` with `EngineFactory`.
- `IsAotCompatible=true` (analyzer-enforced on the CLI's own code); actual
  `PublishAot` is **best-effort** — attempted and smoke-tested, but the
  XmlSerializer-based `BattleScribeSpec.XmlGen` dependency may keep the
  published binary framework-dependent initially (matching the docker hedge
  below). Document the outcome either way.
- Known risk: Spectre.Console under AOT. `Ui.cs` is the single console wrapper;
  if the AOT analyzer flags Spectre, replace the implementation with plain ANSI
  output without changing call sites.

### `bs-engine-host` (new project, non-AOT)

- Absorbs `EngineFactory` and the references to
  `BattleScribeSpec.BattleScribe`, `BattleScribeSpec.NewRecruit`, and the four
  UI driver projects.
- Speaks adapter protocol v1.1 on stdin/stdout: roster and gamedata domains,
  plus the parity extensions below (default `serve` verb).
- Also carries the interactive `probe` and `discover` verbs (moved wholesale
  from the CLI), since their engine-specific surfaces have no protocol
  representation yet.
- Engine-shaping CLI options forward as host argv for built-ins
  (`--headed`, `--keep-alive`, worker count); ad-hoc/config-registered adapters
  receive them as documented `BSSPEC_*` environment variables.
- `--keep-alive` (BattleScribe desktop) stays a host-side concern: the host
  process may exit while the app + Java agent stay up; the next host attaches.

## Engine selection: registry + connectables

Prior art: bowtie's connectables (`image:`/`container:`/`direct:` schemes on one
flag), eshost/test262-harness named-host registry (`.eshostrc`), wpt product
registry. Synthesis:

`--engine` is the single selector in **all modes** (single-spec run, batch,
verify, probe). `--adapter` is retired. Three accepted forms:

```
--engine newrecruit-ui                                # registry name (built-in)
--engine wham                                         # registry name (engines.json)
--engine exec:"node adapters/wham.js"                 # ad-hoc connectable, anonymous
--engine dotnet:path/to/adapter.dll                   # sugar for exec:"dotnet …"
--engine battlescribe=dotnet:bs-reference-adapter.dll # name=connectable:
                                                      # run THIS adapter AS that identity
```

- **Registry**: built-in entries for `battlescribe`, `battlescribe-ui`,
  `newrecruit`, `newrecruit-ui` resolve to `bs-engine-host` (located as a
  sibling artifact of the bs-spec executable, fallback `PATH`). A repo-level
  `engines.json` config registers additional engines:
  `{"wham": {"exec": "node adapters/wham.js"}}`. Static metadata in config is
  optional — `describe` (below) is the primary capability source.
- **Identity**: the registry name keys spec applicability (`engines:` YAML
  field), assertion overrides, expected-failures, and report labels.
  `name=connectable` separates identity from launch (the CI case: reference
  adapter running *as* `battlescribe`). Anonymous `exec:` has no identity —
  no applicability filtering or expectations apply.
- `--ui` remains sugar: `--engine newrecruit --ui` ⇒ `newrecruit-ui`
  (idempotent on names already ending in `-ui`; error for engines where no
  `-ui` variant is registered).
- New connectable kinds (e.g. `container:`) can be added later without new flags.

## Protocol v1.1 (parity subset of #272)

- **`describe`** — sent first after process start. Response: engine
  name/version, supported domains (`roster`, `gamedata`), capabilities
  (`screenshot`, `record`, `rosterXml`, `maxParallel`). A legacy v1.0 adapter
  that answers with an error (or an unknown-type failure) is treated as
  roster-only with no optional capabilities — existing adapters keep working.
- **`screenshot`** → `{png: base64}`; **`exportRosterXml`** → `{xml}`;
  **`recordStart`/`recordStop`** → recorded actions JSON. These back
  `--screenshots`, `--timeline`, `--save-roster`, `--record`. Capability gating
  replaces today's C# type checks, keeping the existing warn-and-skip UX.
- **`--break` REPL** goes protocol-native: getState/getErrors/screenshot
  commands plus a raw-JSON passthrough prompt. Typed `eval`/introspection stays
  in #272.
- **Gamedata over the wire**: the NDJSON adapter protocol gains gamedata
  commands (`gamedataSetup`, `gamedataAction`, `gamedataGetState`,
  `gamedataGetErrors`) modeled 1:1 on the `IGameDataEngine` operation table in
  `docs/adapter-protocol.md` (the doc's existing `gamedata*Action` wire is
  JSON-RPC between the BS UI driver and its Java agent — a shape precedent, not
  the adapter protocol). A new `JsonProtocolGameDataEngine` in TestKit
  implements `IGameDataEngine` over these messages, and `AdapterHandler` +
  the reference adapter serve them. Both domains work in every mode; a spec
  whose domain the engine does not describe is skipped with a stated reason.
- `docs/adapter-protocol.md` is updated to v1.1; #159's JSON Schema covers the
  new messages when it lands.

## `run` verb: three modes

```
bs-spec run <spec> [--engine …] [--headed --break N --screenshots <dir> …]   # single
bs-spec run --all [--specs <dir>] [--filter --tags --report …] [--workers N] # batch
bs-spec run --matrix <dir>                                                   # matrix
```

- Exactly one of `<spec>`, `--all`, `--matrix` (validated; `spec` argument
  becomes optional).
- **Batch** ports the Runner pipeline: discovery (`--specs` dir or embedded
  fallback), pre-filter (`--filter`, `--tags`, engine applicability), execute,
  report (`--report` conformance JSON, `--expected-failures`,
  `--assertion-engine`). Discovery covers **both domains**; `--roster` /
  `--gamedata` narrow it. Exit-code semantics preserved (failures ⇒ 1,
  expected failures excluded, unexpected passes count as failures).
- **`--output` is modal**: `tree|json` (state dump) single-spec;
  `summary|json|github-actions` (results report) in `--all` mode; wrong value
  for the mode is a clear error.
- **`--matrix`** prints the markdown compatibility matrix from
  `*-conformance.json` files; runs nothing.
- Interactive/artifact flags in `--all` mode follow the existing convention:
  warn once and ignore (`--headed` stays honored).

## Parallelism: one `--workers N`

`--workers N` = N concurrent engine instances working the spec queue
(channel-based pool, as in the Runner today), for **any** connectable:

- Adapter processes: N child processes.
- `bs-engine-host` NR engines: instances map onto the existing `NR_PARALLEL`
  browser-context machinery.
- Engines describing `maxParallel: 1` (BattleScribe desktop UI): clamped with a
  warning.

No adapter-only carve-out; `NR_PARALLEL` remains an internal knob of the test
suite, with `--workers` the single user-facing control in the CLI.

## Code placement

The batch/orchestration pipeline — engine registry + connectable resolution,
spec discovery/filtering, worker pool, both protocol engines, report and matrix
output (`JsonRunReport` moves in from the Runner) — lives in **TestKit**
(already `IsAotCompatible=true`), so its AOT-compatibility stays continuously
analyzer-verified. The CLI project keeps only verb wiring.

## Consumers, migration, deletion

- `ci.yml` (2 call sites): `dotnet …/bs-spec.dll run --all
  --engine battlescribe=dotnet:…/bs-reference-adapter.dll …` (other flags map
  1:1).
- Docker: orchestrator image builds `bs-spec` (AOT or framework-dependent —
  keep framework-dependent initially); full image adds `bs-engine-host`;
  compose command updated.
- Docs: README (usage, architecture diagram, project tree), `docs/ci-guide.md`,
  `docs/adapter-guide.md`, `docs/adapter-protocol.md`; ADR 001 gets a
  "superseded in part by #271" note.
- Delete `src/BattleScribeSpec.Runner` and its solution entry.

## Testing

- `Cli.Tests`: connectable/registry parsing, `name=connectable` identity,
  modal `--output` and mode mutual-exclusion validation, `--ui` sugar on the
  string `--engine`.
- TestKit tests: `describe` negotiation incl. legacy fallback,
  `JsonProtocolGameDataEngine` mapping, worker-pool clamping.
- E2E: `run --all` against the reference adapter compared for parity with a
  pre-deletion `bs-spec-runner` run (same pass/fail counts); `--matrix` golden
  test; per-engine smoke of `bs-engine-host` over the wire.
- `dotnet test` conformance suites are untouched — they use engines in-process
  directly and do not go through the CLI.

## PR slicing

1. **TestKit foundations**: registry + connectables, protocol v1.1
   (`describe` + parity messages), `JsonProtocolGameDataEngine`, batch pipeline
   extraction (Runner stays alive and green throughout).
2. **`bs-engine-host` + CLI rewire**: new host project, verbs moved onto the
   protocol, engine references dropped, AOT publish enabled, tests updated.
3. **Consumers**: CI/docker/docs migration, delete
   `src/BattleScribeSpec.Runner`.

## Follow-ups (out of scope)

- #272: typed `eval`/`diagnostic`/introspection protocol actions (the REPL's
  raw passthrough is the stopgap).
- #276: `bs-spec snapshot` verb, now unblocked.
- User-level `engines.json` (repo-level only in this issue).
- `container:` connectable kind.
