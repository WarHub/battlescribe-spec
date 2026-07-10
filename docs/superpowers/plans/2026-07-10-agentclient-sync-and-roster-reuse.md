# AgentClient response correlation + BS-UI roster warm-reuse — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make the BattleScribe UI agent protocol immune to request/response desync (the root cause that blocked warm-reuse at scale), then re-enable and verify warm-reuse for the BattleScribe **Roster Editor**, and land the warm-reuse performance measurements + per-engine docs.

**Architecture:** The JSON-RPC agent protocol already assigns a unique `id` per request (`AgentClient.cs:35`) and the Java server echoes it (`JsonRpcServer.java:185`), and the server is strictly sequential (one request fully handled before the next is read, `JsonRpcServer.java:101-106`). The bug is purely client-side: `CallAsync` reads one line and assumes it's the matching response, never checking the `id`. When a call times out (client `CallTimeout` 30s fires before the Java FX 60s timeout), request N is still in flight; its late response lands on the socket and every subsequent positional read is shifted by one — permanent desync. Cold runs never hit it (fresh socket per spec); warm reuse across hundreds of specs turns one timeout into a cascade (observed 103/107 failing). Fix: a dedicated background read loop that correlates responses by `id` and discards late/abandoned ones — the canonical concurrent JSON-RPC client. No Java change. This makes warm-reuse robust for both BS-UI domains and unblocks roster (the New-Roster dialog was already confirmed to rescan the data directory).

**Tech Stack:** .NET 10, xUnit, `System.Net.Sockets` (a fake TCP JSON-RPC server for the regression test), the BattleScribe desktop app + `bs-ui-java-agent`, PowerShell for the benchmark harness.

## Global Constraints

- **`AgentClient`'s public API is unchanged** (`CallAsync` + all wrapper methods keep their signatures) — no caller edits required, only the transport internals change.
- **No Java change for the correlation fix** (the server already echoes ids and is sequential). The only Java change in this plan is the roster "unsaved roster" dialog dismissal in Task 2.
- **The desync must be reproduced in a deterministic unit test that fails before the fix and passes after** — it is the gate. It must not depend on the live BattleScribe app (use an in-process fake TCP server).
- **Correctness first**: warm-reuse must give identical conformance verdicts to cold, at scale. The 107-spec roster batch that previously cascaded must pass warm≈cold after Task 2.
- Repo conventions: `dotnet build` before `--no-build`; `TreatWarningsAsErrors=true`; analyzers-as-errors (`new()`, IDE0055 formatting); xUnit1051 → `TestContext.Current.CancellationToken`.
- Continues PR #302 on branch `feat/271-nr-warm-reuse` (per-domain flags + gamedata warm-reuse already landed).

---

### Task 1: Response-id correlation in `AgentClient` (the desync fix) + deterministic regression test

**Files:**
- Modify: `src/BattleScribeSpec.BsRosterUiDriver/AgentClient.cs`
- Test: `tests/Features/AgentClientTests.cs` (create)

**Interfaces:**
- `AgentClient(TcpClient)`, `Task<JsonNode?> CallAsync(string, JsonObject?, CancellationToken)`, `CallTimeout`, `Dispose()` — all unchanged externally.

**Context:** `CallAsync` currently: increments `_nextId`, writes the request, then `await _reader.ReadLineAsync(effectiveToken)` and returns `response["result"]` with no id check (`AgentClient.cs:33-90`). The rewrite moves reading into one background loop and correlates by id.

- [ ] **Step 1: Write the failing regression test**

Create `tests/Features/AgentClientTests.cs` with an in-process fake JSON-RPC server on `TcpListener` (loopback, port 0) whose per-connection handler is a scripted callback so tests control timing. Include at minimum:

```csharp
[Fact]
public async Task LateResponseAfterTimeout_DoesNotDesyncNextCall()
{
    // Server: for id=1 ("slow"), wait past the client's CallTimeout before replying;
    // for id=2 ("fast"), reply immediately. Both replies echo their request id.
    await using var server = FakeAgentServer.Start(async (req, respond, ct) =>
    {
        var id = req["id"]!.GetValue<int>();
        var method = req["method"]!.GetValue<string>();
        if (method == "slow")
        {
            await Task.Delay(TimeSpan.FromMilliseconds(600), ct);   // > CallTimeout below
            await respond($$"""{"jsonrpc":"2.0","id":{{id}},"result":"slow-result"}""");
        }
        else
        {
            await respond($$"""{"jsonrpc":"2.0","id":{{id}},"result":"fast-result"}""");
        }
    });

    using var client = new AgentClient(server.Connect()) { CallTimeout = TimeSpan.FromMilliseconds(200) };
    var ct = TestContext.Current.CancellationToken;

    await Assert.ThrowsAsync<TimeoutException>(() => client.CallAsync("slow", cancellationToken: ct));

    // The late "slow" response (id=1) will arrive on the socket AFTER we time out.
    // The next call (id=2) must get ITS OWN result, not the stale id=1 result.
    var fast = await client.CallAsync("fast", cancellationToken: ct);
    Assert.Equal("fast-result", fast!.GetValue<string>());
}

[Fact]
public async Task NormalCall_ReturnsResult() { /* immediate echo → result */ }

[Fact]
public async Task ErrorResponse_ThrowsAgentException() { /* {"error":{"code":-1,"message":"boom"}} → AgentException */ }

[Fact]
public async Task ConnectionClosed_FaultsPendingCall() { /* server closes mid-wait → InvalidOperationException, not a hang */ }
```

Implement `FakeAgentServer` in the same test file: a `TcpListener` accepting one client, reading newline-delimited JSON requests, and invoking the scripted handler with a `respond(string)` that writes a line. It must let the handler reply out of band / late. Give it an `IAsyncDisposable` that stops the listener.

- [ ] **Step 2: Run the test to confirm it fails against the current positional client**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "FullyQualifiedName~AgentClientTests"`
Expected: `LateResponseAfterTimeout_DoesNotDesyncNextCall` FAILS — the `fast` call returns `"slow-result"` (the stale, positionally-read response), proving the desync.

- [ ] **Step 3: Rewrite `AgentClient`'s transport to a reader loop + id correlation**

Replace the fields and `CallAsync`/`Dispose` (keep every wrapper method — `PingAsync`, `FindNodeAsync`, etc. — exactly as-is; they call `CallAsync`). New transport:

```csharp
private readonly TcpClient _client;
private readonly StreamReader _reader;
private readonly StreamWriter _writer;
private readonly SemaphoreSlim _writeLock = new(1, 1);
private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
private readonly CancellationTokenSource _readLoopCts = new();
private readonly Task _readLoop;
private int _nextId;
private volatile Exception? _fault;

public AgentClient(TcpClient client)
{
    _client = client;
    var stream = client.GetStream();
    _reader = new StreamReader(stream, Encoding.UTF8);
    _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };
    _readLoop = Task.Run(ReadLoopAsync);
}

private async Task ReadLoopAsync()
{
    try
    {
        while (!_readLoopCts.IsCancellationRequested)
        {
            var line = await _reader.ReadLineAsync(_readLoopCts.Token);
            if (line is null) { break; } // stream closed
            JsonNode? response;
            try { response = JsonNode.Parse(line); }
            catch { continue; } // ignore unparseable line
            if (response?["id"] is not JsonNode idNode) { continue; } // e.g. parse-error response (id null) — discard
            int id;
            try { id = idNode.GetValue<int>(); } catch { continue; }
            if (_pending.TryRemove(id, out var tcs)) { tcs.TrySetResult(response); }
            // else: late/abandoned response for a timed-out call — discard. THIS is the desync fix.
        }
    }
    catch (OperationCanceledException) { /* disposing */ }
    catch (Exception ex) { _fault = ex; }
    finally { FaultAllPending(_fault ?? new InvalidOperationException("Agent connection closed.")); }
}

private void FaultAllPending(Exception ex)
{
    foreach (var key in _pending.Keys)
    {
        if (_pending.TryRemove(key, out var tcs)) { tcs.TrySetException(ex); }
    }
}

public async Task<JsonNode?> CallAsync(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default)
{
    if (_fault is { } fault) { throw new InvalidOperationException("Agent connection is faulted.", fault); }

    var id = Interlocked.Increment(ref _nextId);
    var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
    _pending[id] = tcs;
    try
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["id"] = id };
        if (parameters is not null) { request["params"] = parameters; }
        var json = request.ToJsonString();

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _writer.FlushAsync(cancellationToken);
        }
        finally { _writeLock.Release(); }

        using var timeoutCts = CallTimeout != Timeout.InfiniteTimeSpan
            ? new CancellationTokenSource(CallTimeout) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        JsonNode response;
        try
        {
            response = await tcs.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Agent did not respond to '{method}' within {CallTimeout.TotalSeconds:F0}s. " +
                "The JavaFX thread may be blocked (deadlock).");
        }

        if (response["error"] is JsonNode error)
        {
            var code = error["code"]?.GetValue<int>() ?? -1;
            var message = error["message"]?.GetValue<string>() ?? "Unknown error";
            throw new AgentException(code, message);
        }
        return response["result"];
    }
    finally
    {
        _pending.TryRemove(id, out _); // no waiter leak on timeout/cancel/error
    }
}

public void Dispose()
{
    _readLoopCts.Cancel();
    try { _writer.Dispose(); } catch { }
    try { _reader.Dispose(); } catch { }
    _client.Dispose();
    FaultAllPending(new ObjectDisposedException(nameof(AgentClient)));
}
```

Key properties to preserve: unique id per request; the read loop is the SOLE reader and is never cancelled mid-line (so no partial-line corruption); a timed-out call removes its waiter so the late response is discarded by id; stream close/dispose faults all waiters (no hangs).

- [ ] **Step 4: Run the tests to confirm they pass**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "FullyQualifiedName~AgentClientTests"`
Expected: all 4 pass, including `LateResponseAfterTimeout_DoesNotDesyncNextCall` (fast call now returns `"fast-result"`).

- [ ] **Step 5: Run the broader offline suite for regressions**

Run: `dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "Category!=Conformance"`
Expected: green (nothing depended on the old positional internals; the API is unchanged).

- [ ] **Step 6: Commit**

```bash
git add src/BattleScribeSpec.BsRosterUiDriver/AgentClient.cs tests/Features/AgentClientTests.cs
git commit -m "fix(bs-ui): correlate agent responses by id via a reader loop (no positional desync) (#271)"
```

---

### Task 2: Re-enable and verify BattleScribe Roster Editor warm-reuse

**Files:**
- Modify: `src/BattleScribeSpec.BsRosterUiDriver/BsUiRosterEngine.cs`
- Modify: `src/BattleScribeSpec.EngineHost/HostEngineFactory.cs`
- Modify: `src/BattleScribeSpec.EngineHost/ServeCommand.cs`
- Modify: `src/bs-ui-java-agent/src/bsspec/uiagent/RosterActions.java`

**Interfaces:** consumes `ReuseRosterEngineAcrossSetups` (already exists) and `BsUiRosterEngine.KeepAlive`.

**Context:** The spike established that (a) the New-Roster dialog re-enumerates game systems from the data directory on open (so a newly-staged system appears — roster data-reload IS feasible), and (b) reusing the app with a roster already open pops BattleScribe's native "Continue? Roster has not been saved…" confirm dialog that `createRosterAction` must dismiss. With Task 1's fix, the desync that made the 107-spec run cascade is gone. The warm path already exists in `BsUiRosterEngine.SetupAsync` (`:209-227`): ping the live app, `CloseCurrentRosterIfOpenAsync`, restage files, return — the next `createRosterAction` opens the New-Roster dialog (which rescans). It must handle the unsaved-roster confirm.

- [ ] **Step 1: Dismiss the unsaved-roster confirm dialog in `createRosterAction`**

In `RosterActions.java`'s `createRosterAction` (~`:205-269`), before/while opening the New Roster dialog, detect and dismiss BattleScribe's native "Continue? Roster has not been saved…" confirmation (title `Confirm`, negative button) that appears when a roster is already open from the previous spec. Mirror the existing dialog-handling patterns in the file (e.g. `btnNegative`/`btnPositive` firing) — pick the option that discards without saving so the new roster can be created. Rebuild the agent jar (`pwsh -File src/bs-ui-java-agent/build.ps1`) so the fix is exercised; the jar is gitignored (CI rebuilds it), so commit only the `.java` change.

- [ ] **Step 2: Enable roster warm-reuse in the host**

- `HostEngineFactory.cs` `battlescribe-ui` roster case (`:40-45`): set `KeepAlive = true` on the `BsUiRosterEngine` (so `Cleanup` preserves the app between specs).
- `ServeCommand.cs`: add `battlescribe-ui` to the `ReuseRosterEngineAcrossSetups` line so it becomes `name is "newrecruit" or "newrecruit-ui" or "battlescribe-ui"`.
- In `BsUiRosterEngine.SetupAsync`'s warm branch (`:209-227`), confirm the flow after `CloseCurrentRosterIfOpenAsync()` + restage leaves the app ready for `createRosterAction` to open a fresh New-Roster dialog. Adjust only if the spike's findings require it (e.g. ensuring the dialog is re-opened rather than reusing a stale combo). Keep changes minimal.

- [ ] **Step 3: Small warm-vs-cold correctness check (different game systems)**

Run 4 roster specs spanning ≥2 different game systems through the warm host and confirm identical verdicts vs cold (toggle `KeepAlive`/rebuild for the cold run, then restore). Confirm from stderr the app launches ONCE warm.

- [ ] **Step 4: SCALE re-verification (the gate — this previously cascaded)**

Run the same large roster batch that failed before Task 1 — `bs-spec run --all --engine battlescribe-ui --roster --filter "force,cost" --expected-failures battlescribe-ui --workers 1` (~100+ specs) — WARM, and a COLD comparison (KeepAlive off). The gate: **no desync cascade**, and warm per-spec verdicts MATCH cold (allowing only known expected-failures). If any cascade of `Unexpected response type`-style failures reappears, Task 1's fix is incomplete — STOP and report BLOCKED with the spec index where it starts. Record warm vs cold wall-times (feeds Task 3).

If the BS app cannot launch in this environment, record that Steps 3–4 could not run here and that CI `thorough-ui-bs` is the gate; commit the code (correct by construction given Task 1) but state clearly it was not verified live.

- [ ] **Step 5: Commit**

```bash
git add src/BattleScribeSpec.BsRosterUiDriver/BsUiRosterEngine.cs src/BattleScribeSpec.EngineHost/HostEngineFactory.cs src/BattleScribeSpec.EngineHost/ServeCommand.cs src/bs-ui-java-agent/src/bsspec/uiagent/RosterActions.java
git commit -m "feat(host): warm-reuse the BattleScribe Roster Editor across specs (#271)"
```

---

### Task 3: Performance measurements + per-engine applicability docs

**Files:**
- Modify: `src/BattleScribeSpec.EngineHost/ServeCommand.cs` (ablation env toggle)
- Create: `scripts/bench-warm-reuse.ps1`
- Create: `docs/warm-reuse.md`
- Modify: `README.md`

**Interfaces:** none.

**Context:** With all UI engines now warm-reusable (NR both domains, BS-UI both domains; in-process `battlescribe` excluded), capture real numbers and document per-engine applicability. Resolves the dangling `docs/warm-reuse.md` reference already committed in `ServeCommand.cs`.

- [ ] **Step 1: Ablation env toggle**

In `ServeCommand.BuildOptions`, gate the reuse flags so `BSSPEC_DISABLE_WARM_REUSE=1` forces cold (used by the benchmark and for diagnosis):

```csharp
var reuseDisabled = Environment.GetEnvironmentVariable("BSSPEC_DISABLE_WARM_REUSE") == "1";
...
ReuseRosterEngineAcrossSetups = !reuseDisabled && name is "newrecruit" or "newrecruit-ui" or "battlescribe-ui",
ReuseGameDataEngineAcrossSetups = !reuseDisabled && name is "newrecruit" or "newrecruit-ui" or "battlescribe-ui",
```

(If Task 2 did NOT enable roster `battlescribe-ui`, leave it out of the roster line and reflect that in the docs.)

- [ ] **Step 2: Benchmark script**

Create `scripts/bench-warm-reuse.ps1` taking `-Engine`, `-Filter`, and `-Domain` (roster|gamedata), that builds, then runs the same `bs-spec run --all … --workers 1` batch twice — warm (default) and cold (`$env:BSSPEC_DISABLE_WARM_REUSE='1'`) — timing each with `Measure-Command`, and prints a table: spec count, warm time, cold time, absolute + per-spec saving, speedup. Fail loudly if the engine can't launch (distinguish driver/launch failure from spec failures). No external modules.

- [ ] **Step 3: Capture real numbers**

Run for: `newrecruit-ui --roster` (~8 specs), `battlescribe-ui --gamedata` (~8 specs, ≥2 systems), and `battlescribe-ui --roster` (~8 specs, ≥2 systems). Record actual output. Where a suite ran at scale in Task 2 Step 4, reuse those wall-times too. If an engine can't run locally, mark those rows "to be captured on CI" — do not invent numbers.

- [ ] **Step 4: `docs/warm-reuse.md`**

Document: (1) what host warm-reuse is; (2) the **response-correlation fix** (why long warm sessions are now safe — brief, links Task 1); (3) the **per-engine applicability table** (`newrecruit`/`newrecruit-ui` both; `battlescribe-ui` gamedata; `battlescribe-ui` roster per Task 2 outcome; `battlescribe` in-process = no benefit); (4) the **measured numbers** from Step 3; (5) the `BSSPEC_DISABLE_WARM_REUSE` toggle + `scripts/bench-warm-reuse.ps1`. Add a one-line `README.md` pointer.

- [ ] **Step 5: Commit**

```bash
git add src/BattleScribeSpec.EngineHost/ServeCommand.cs scripts/bench-warm-reuse.ps1 docs/warm-reuse.md README.md
git commit -m "docs,perf: warm-reuse benchmark harness, measurements, and per-engine applicability (#271)"
```

---

## Final verification (before updating the PR)

- [ ] `dotnet build` clean; `dotnet test … --filter "Category!=Conformance"` green (incl. the new `AgentClientTests`).
- [ ] The desync regression test fails on the pre-Task-1 commit and passes on HEAD (the fix is genuinely load-bearing).
- [ ] Roster + gamedata warm scale runs show no desync cascade and warm≈cold verdicts (or explicitly deferred to CI with a reason).
- [ ] Retitle/refresh PR #302 (UI-engine warm-reuse + protocol correlation fix + measurements); update the body with numbers + the per-engine table. Push.

## Out of scope (follow-ups)

- `AdapterProcess` stderr forwarding (issue #303) — orthogonal.
- Cross-process app/browser sharing.
- Simplifying `BsUiRosterEngine`'s now-redundant double timeout layer (`RunWithTimeoutAsync` + `CallTimeout`) — safe to leave; the correlation fix makes the outer retry harmless.
