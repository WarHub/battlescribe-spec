# NDJSON adapter-protocol correlation + timeout hierarchy — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make the NDJSON adapter protocol (bs-spec CLI ↔ bs-engine-host / any adapter) immune to request/response desync, and fix the inverted timeout hierarchy that triggers it — so BattleScribe-UI roster warm-reuse passes at scale and this class of bug cannot resurface at any protocol layer.

**Architecture:** We already fixed this bug class in `AgentClient` (host ↔ Java agent) via a reader loop + JSON-RPC id correlation. The SAME flaw exists one layer up in the NDJSON protocol: `AdapterProcess.SendAsync` (`:75-77`) writes a command then reads exactly one line — **purely positional, the protocol carries no id** — while `JsonProtocolEngine.SendCommand` (`:245-246`) wraps that read in a **30s** timeout that abandons the stream. BS-UI roster operations legitimately take up to ~90s host-side (`BsUiRosterEngine`: `AgentClient.CallTimeout = 90s`, `ActionTimeout` 60s, `MaxRetries` 1), so the CLI gives up while the host is still working; the host's late response is then consumed as the answer to the NEXT command and every subsequent read is shifted by one — a permanent cascade (observed: 4 passed / 98 failed, 96 × `Unexpected response type: teardownResult`). Fast engines (gamedata, NR) rarely trip it; slow UI roster ops trip it reliably. Fix = (1) add an `id` to NDJSON commands, echo it in responses, and correlate in a reader loop (desync structurally impossible), and (2) make the CLI's per-request timeout exceed the host's max operation time (so a timeout means "dead", not "busy").

**Tech Stack:** .NET 10, xUnit, NDJSON over stdio, source-generated `System.Text.Json` (`ProtocolJsonContext`), JSON Schema (`docs/protocol-schema.json`).

## Global Constraints

- **Backward compatible.** `id` is OPTIONAL on the wire. A response WITHOUT an `id` is correlated positionally to the single oldest outstanding request (exactly today's behavior), so legacy/third-party adapters keep working. Our own adapters (via `AdapterHandler`) always echo it and get the structural guarantee.
- **Only one request is outstanding at a time** in all current callers — the reader loop must still be correct if that ever changes.
- **Every new/changed protocol DTO must be registered in `ProtocolJsonContext`** and reflected in `docs/protocol-schema.json`, or `ProtocolSchemaDriftTests` fails (this gate is real — see repo conventions).
- **Timeouts must be a hierarchy, outermost-longest:** CLI per-request timeout > host per-operation timeout (BS-UI ≈ 122s worst case: 60s `ActionTimeout` × (1 + `MaxRetries`) + retry delay) > engine internals. A timeout at the CLI must mean the adapter is genuinely unresponsive.
- **The gate is the roster scale run**: `--engine battlescribe-ui --roster --filter "force,cost"` (~102 specs) warm must show NO desync cascade and verdicts matching cold.
- Repo conventions: `dotnet build` before `--no-build`; `TreatWarningsAsErrors=true`; analyzers-as-errors; xUnit1051 → `TestContext.Current.CancellationToken`.

---

### Task 1: Correlate NDJSON responses by id (+ deterministic desync regression test)

**Files:**
- Modify: `src/BattleScribeSpec.TestKit/Protocol/ProtocolMessages.cs` (add `Id` to command + response base types)
- Modify: `src/BattleScribeSpec.TestKit/Protocol/ProtocolSerializer.cs` / `ProtocolJsonContext` (register/serialize `id`)
- Modify: `src/BattleScribeSpec.TestKit/Protocol/AdapterProcess.cs` (reader loop + correlation)
- Modify: `src/BattleScribeSpec.TestKit/Protocol/AdapterHandler.cs` (echo the request's id on every response)
- Modify: `docs/protocol-schema.json`, `docs/adapter-protocol.md`
- Test: `tests/Features/AdapterProcessCorrelationTests.cs` (create)

**Context:** `AdapterProcess` implements `IAdapterConnection.SendCommandAsync`. `JsonProtocolEngine`/`JsonProtocolGameDataEngine` call it with a per-request timeout. The desync happens entirely inside `AdapterProcess` (positional read) once a caller abandons a request.

- [ ] **Step 1: Write the failing regression test**

Create `tests/Features/AdapterProcessCorrelationTests.cs`. Use the existing `InMemoryAdapterConnection` pattern where possible, but this test needs a *fake adapter* that replies LATE to a timed-out command and then to the next one. Model it on `tests/Features/AgentClientTests.cs`'s `FakeAgentServer` (same shape, but stdio/NDJSON instead of TCP — drive `AdapterProcess` against a scripted process-like reader/writer, or refactor the test to target the correlation logic through `IAdapterConnection`).

The essential assertion (this is what must go red then green):

```
// Adapter takes longer than the client's timeout on command #1, then replies to it LATE;
// command #2 must receive ITS OWN response, not the stale response to #1.
await Assert.ThrowsAsync<TimeoutException>(() => engine.SendWithShortTimeout(cmd1));
var r2 = await engine.Send(cmd2);
Assert.IsType<SetupResult>(r2);      // NOT a TeardownResult/stale response for cmd1
```

Also assert: a response with NO `id` still resolves the single outstanding request (legacy-adapter fallback).

- [ ] **Step 2: Run it — confirm it FAILS (reproduces the cascade in miniature)**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "FullyQualifiedName~AdapterProcessCorrelationTests"`
Expected: the late-response test FAILS — command #2 receives command #1's stale response (`Unexpected response type`), exactly the production cascade.

- [ ] **Step 3: Add an optional `id` to the protocol messages**

Add a nullable `Id` (int?) to the base `ProtocolCommand` and base `ProtocolResponse` (serialized as `"id"`, omitted when null via `JsonIgnore(WhenWritingNull)`). Register any new/changed types in `ProtocolJsonContext`. Keep every existing field unchanged — this is purely additive.

- [ ] **Step 4: Echo the id in `AdapterHandler`**

In `AdapterHandler.RunAsync`, capture the deserialized command's `Id` and set it on the `ProtocolResponse` before serializing (every response path, including `ProtocolError` and the `catch` fallback). If the command had no id, the response has none.

- [ ] **Step 5: Rewrite `AdapterProcess` transport with a reader loop + correlation**

Mirror the `AgentClient` fix (`src/BattleScribeSpec.BsRosterUiDriver/AgentClient.cs`, already merged on this branch — read it as the reference implementation):
- A single background read loop owns `_stdout`, reads lines to completion (never cancelled mid-line), parses each response, and completes the matching pending request from a `ConcurrentDictionary<int, TaskCompletionSource<...>>` by `id`.
- **Legacy fallback:** a response with no `id` completes the single oldest outstanding request (preserves today's behavior for adapters that don't echo ids).
- `SendCommandAsync` assigns the next id, registers a waiter, writes the command (writes serialized by a `SemaphoreSlim`), and awaits the waiter with the caller's timeout/token. On timeout it removes its waiter and throws — **it never touches the stream**, so the adapter's late response is later discarded by id.
- Stream close / process exit faults all pending waiters (no hangs). Preserve the existing `GetStderrTail()` diagnostics on failure.
- `IAdapterConnection.SendCommandAsync`'s public signature is unchanged.

- [ ] **Step 6: Update the protocol docs + schema**

`docs/protocol-schema.json`: add the optional `id` to command/response definitions. `docs/adapter-protocol.md`: document `id` — clients SHOULD send it, adapters MUST echo it when present; explain that it makes the stream resilient to a client-side timeout (a late response is discarded rather than desyncing the stream), and that omitting it falls back to strict positional ordering. Bump the doc's protocol version note if the repo's convention requires it.

- [ ] **Step 7: Run the tests — confirm GREEN**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "Category!=Conformance"`
Expected: the correlation tests pass; `ProtocolSchemaDriftTests` passes (schema updated); the whole offline suite is green.

- [ ] **Step 8: Commit**

```bash
git add src/BattleScribeSpec.TestKit/Protocol docs/protocol-schema.json docs/adapter-protocol.md tests/Features/AdapterProcessCorrelationTests.cs
git commit -m "fix(protocol): correlate NDJSON responses by id; late responses can no longer desync the stream (#271)"
```

---

### Task 2: Fix the inverted timeout hierarchy

**Files:**
- Modify: `src/BattleScribeSpec.TestKit/Protocol/JsonProtocolEngine.cs`, `JsonProtocolGameDataEngine.cs`

**Context:** `JsonProtocolEngine`'s default per-request timeout is **30s** (`:18`), but a single BS-UI roster action can legitimately take ~122s host-side (`ActionTimeout` 60s × (1 + `MaxRetries` 1) + 2s retry delay), and `AgentClient.CallTimeout` is 90s for actions. With correlation (Task 1) a premature CLI timeout no longer corrupts the stream — but it still **fails the spec spuriously**. The CLI must be the OUTERMOST timeout.

- [ ] **Step 1: Raise the default per-request timeout above any host-side operation**

Set the default request timeout so the CLI is strictly the outermost layer (e.g. `TimeSpan.FromMinutes(3)`), and document the hierarchy in an XML comment: CLI per-request (3 min) > BS-UI action worst case (~122s) > `AgentClient.CallTimeout` (90s) > FX dispatch (60s). Keep the existing longer `setup` window (2 min) and the 5-min dataSource window — raise them if they'd now be shorter than the new default. Apply the same to `JsonProtocolGameDataEngine`.

Rationale to capture in the comment: a CLI timeout must mean "the adapter is genuinely unresponsive," never "the adapter is still working." Slower detection of a truly hung adapter is an acceptable trade for not failing/aborting live work.

- [ ] **Step 2: Build + offline suite**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "Category!=Conformance"` → green.

- [ ] **Step 3: Commit**

```bash
git add src/BattleScribeSpec.TestKit/Protocol/JsonProtocolEngine.cs src/BattleScribeSpec.TestKit/Protocol/JsonProtocolGameDataEngine.cs
git commit -m "fix(protocol): CLI request timeout must exceed host operation time (timeout hierarchy) (#271)"
```

---

### Task 3: Prove it — BS-UI roster warm-reuse at scale (the gate)

**Files:** none new. The roster warm-reuse changes (`BsUiRosterEngine`/`HostEngineFactory`/`ServeCommand`/`RosterActions.java`) are already in the working tree from the prior task and are committed here once the gate passes.

- [ ] **Step 1: Rebuild the Java agent jar** (`pwsh -File src/bs-ui-java-agent/build.ps1`) so `RosterActions.java`'s unsaved-roster dialog fix is live. Then `dotnet build`.

- [ ] **Step 2: WARM roster scale run (the gate)**

```bash
dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll run --all \
  --engine battlescribe-ui --roster --filter "force,cost" \
  --expected-failures battlescribe-ui --workers 1 > /tmp/rw2-warm.stdout 2> /tmp/rw2-warm.stderr
grep -c "Unexpected response type\|teardownResult" /tmp/rw2-warm.stdout   # MUST be 0
```
REQUIREMENT: **zero** desync-cascade failures, and a pass rate consistent with the engine's known conformance (not 4/102).

- [ ] **Step 3: COLD comparison + verdict diff**

Re-run the same filter with `BSSPEC_DISABLE_WARM_REUSE=1` if that toggle exists yet, else by temporarily setting `KeepAlive=false` on the BS-UI roster engine (rebuild, run, then REVERT + rebuild). Compare per-spec verdicts warm vs cold — they must MATCH. Record both wall-times (feeds the measurements task).

- [ ] **Step 4: Commit the roster warm-reuse enablement (only if the gate passed)**

```bash
git add src/BattleScribeSpec.BsRosterUiDriver/BsUiRosterEngine.cs src/BattleScribeSpec.EngineHost/HostEngineFactory.cs src/BattleScribeSpec.EngineHost/ServeCommand.cs src/bs-ui-java-agent/src/bsspec/uiagent/RosterActions.java
git commit -m "feat(host): warm-reuse the BattleScribe Roster Editor across specs (#271)"
```

If the cascade persists, STOP and report BLOCKED with the failing spec index and messages — do not commit.

---

## Final verification

- [ ] Offline suite green incl. the new correlation tests and `ProtocolSchemaDriftTests`.
- [ ] The NDJSON desync regression test fails on the pre-fix commit and passes on HEAD.
- [ ] Roster + gamedata warm scale runs: no cascade, warm verdicts == cold.
- [ ] Then proceed to the measurements + per-engine docs task and refresh PR #302.

## Out of scope

- `AdapterProcess` stderr forwarding (#303) — would have made this far easier to diagnose; still worth doing separately.
- Pipelining more than one outstanding NDJSON request (correlation makes it possible, but nothing needs it).
