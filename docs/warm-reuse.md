# Host Warm-Reuse

`bs-engine-host` runs a built-in engine over the [adapter protocol](adapter-protocol.md) as a
child process spawned by the runner. By default it recreates the underlying engine for every
spec: dispose the old instance, construct a fresh one. For UI-driven engines (a Playwright
browser, or a JavaFX app driven through a Java agent) that means a full cold start — browser or
JVM launch — **per spec**, which dominates wall time for a large batch.

**Warm-reuse** keeps ONE engine instance alive across the batch instead. Between specs the host
calls the engine's `Cleanup()` to reset its state (close the current roster/document, clear cached
lookups, return the app to a neutral screen) rather than disposing it, and the next spec's
`Setup()` reuses the still-running browser/JVM. This mirrors the in-process engine pool the
`battlescribe` engine already gets for free.

Warm-reuse is opt-in per **engine identity** and per **domain** (roster vs gamedata), wired in
`src/BattleScribeSpec.EngineHost/ServeCommand.cs` (`ReuseRosterEngineAcrossSetups` /
`ReuseGameDataEngineAcrossSetups`). For `battlescribe-ui` gamedata it is gated a second time by
`BsGameDataUiEngine.KeepAlive` in `HostEngineFactory.cs` — both must be on for the app to survive
between specs.

It also **self-heals**. An action that leaves the app in an unknown state — an unexpected modal
dialog, or any operation timeout — marks the engine instance *poisoned*. The next `Cleanup` tears
a poisoned instance down unconditionally (even under `KeepAlive`), so the following spec gets a
fresh cold-started instance rather than inheriting corrupted state. One bad spec costs one extra
cold restart; it cannot cascade through the rest of the batch.

## Per-engine applicability

| Engine | Domain | Warm-reuse | Status / reason |
|---|---|---|---|
| `newrecruit` | roster | ✅ enabled | The NR web app loads game data at runtime per spec — no restart needed. Not benchmarked at batch scale here. |
| `newrecruit` | gamedata | ✅ enabled | Same: the NR Editor loads catalogue data at runtime. Not benchmarked here. |
| `newrecruit-ui` | roster | ⚠️ **enabled, but measured BROKEN** | Warm-reuse is on in `ServeCommand`, but the benchmark shows a correctness regression: after any spec that **successfully creates a roster**, every following spec fails at step 0 with `addForce` timing out (`waiting for Locator(".box").First.Locator("select").First`). `NrRosterUiEngine.Cleanup` → `ResetBrowserStateAsync` does not actually clear the previous list, so NR's Create-List dialog no longer exposes the force `select`. Warm is therefore both **slower and wrong** vs cold. Numbers and evidence below. |
| `newrecruit-ui` | gamedata | ✅ enabled | The NR Editor UI reloads data at runtime. Not benchmarked here. |
| `battlescribe-ui` | gamedata (Data Editor) | ✅ enabled, **verified** | The Data Editor loads catalogue/gst files **by path at runtime**: `gamedataLoadFilesAction` (C#) → the Java agent's `openCataloguePath` (`DataEditorActions.java`), a genuine runtime file loader used on every spec. Reusing the JVM and reloading new files between specs is safe — measured identical verdicts warm vs cold, with a real speedup. |
| `battlescribe-ui` | roster (Roster Editor) | ❌ disabled (cold) | **The BattleScribe app terminates itself when kept alive.** A background `TimerThread` in the app polls `https://battlescribe.net/rest/sponsormessage/getMessages`; when that call fails the JVM exits (code -1) and takes the host with it. Cold never trips it — each JVM lives only ~6s, well under the poll interval — but a warm-reused instance survives long enough for the timer to fire. Measured: warm 4/102 specs passed before the host died, vs cold 17/18 passed. Tracked for a proper fix (suppress the app's phone-home from the Java agent). |
| `battlescribe` (in-process) | both | ⚪ N/A | Engine construction is cheap (no external process); there is nothing to save. |

## The protocol correlation fixes (why long warm sessions are safe)

Warm-reuse means one engine process — and for `battlescribe-ui`, one JVM plus one long-lived agent
socket — now serves an entire batch instead of one spec per fresh connection. That exposed a
latent bug at **both** protocol layers this host uses: responses were matched to requests
*positionally* (read a line, assume it answers the most recent command), which is only safe if
every command gets exactly one timely response.

- **NDJSON adapter protocol** (`AdapterProcess`, CLI ↔ host): commands now carry an optional
  `corrId`, which `AdapterHandler` echoes on every response. `AdapterProcess` matches each response
  to its pending request by `corrId` and **discards** a late response whose id no longer has a
  waiter (i.e. one the client already timed out on), instead of handing it to the next command in
  line and permanently desyncing the stream. See [`corrId`](adapter-protocol.md#correlation-id-corrid).
- **JSON-RPC agent protocol** (`AgentClient`, host ↔ Java agent inside the BattleScribe JVM):
  `AgentClient` always assigned a unique `id` per request and the Java server echoed it — the bug
  was purely client-side (read one line, assume positional order). It now runs a dedicated
  background read loop that correlates responses to requests by `id` and discards late/abandoned
  ones, exactly like the NDJSON fix.
- **Timeout hierarchy**: the CLI's per-request timeout was raised from 30s to 3 minutes (`setup`
  gets 5 minutes) so it always *exceeds* every host-side operation: BS-UI's worst-case action retry
  window (~122s), `AgentClient.CallTimeout` (90s), and the Java agent's FX-thread dispatch (60s). A
  CLI timeout now means "the adapter is genuinely unresponsive," never "the adapter is still
  working." Correlation makes a late response harmless; the hierarchy makes it rare.

Together these let a long warm session absorb an occasional slow request without desyncing every
spec that follows — the failure mode that previously made a 107-spec warm batch cascade.

## Measured numbers

Captured on this branch (`feat/271-nr-warm-reuse`), Windows 11, `--workers 1`, via
`scripts/bench-warm-reuse.ps1`. Each row is the **same batch run twice**: warm (default) and cold
(`BSSPEC_DISABLE_WARM_REUSE=1`).

### `battlescribe-ui` gamedata (Data Editor) — ✅ warm wins

`-Filter "condition/,constraint/,cost/"`, 8 specs:

| Metric | Value |
|---|---|
| Spec count | 8 |
| Warm wall | 44.9s |
| Cold wall | 69.8s |
| Absolute saving | 24.9s |
| Per-spec saving | 3.11s |
| Speedup | **1.56×** |
| Verdicts warm == cold | ✅ all 8 identical |

Warm launches the Data Editor JVM **once**; cold launches it 8 times. An earlier measurement on a
different 8-spec filter gave 34.5s warm vs 58.8s cold (1 launch vs 8) — the same ~1.6–1.7× shape.

### `newrecruit-ui` roster — ❌ warm is slower AND wrong

`-Filter "gamesystem/,entry-group/"`, 8 specs:

| Metric | Value |
|---|---|
| Spec count | 8 |
| Warm wall | 258.8s |
| Cold wall | 145.0s |
| Absolute "saving" | **−113.8s** (warm is 113.8s *slower*) |
| Speedup | **0.56×** (a 1.8× slowdown) |
| Verdicts warm == cold | ❌ **6 mismatches, every one warm=FAIL / cold=PASS** |

Warm: **1 passed, 7 failed**. Cold: 7 passed, 1 failed. The single warm pass is
`entry-group/entry-group-collective` — the *first* spec in the batch. All seven warm failures are
identical and occur at **step 0**:

```
Step 0: InvalidOperationException: Action 'addForce' failed: Timeout 30000ms exceeded.
Call log:
  - waiting for Locator(".box").First.Locator("select").First
```

That locator is the force-selection dropdown in NR's Create-List dialog. A separate 3-spec run
isolates the trigger: in a batch whose first two specs failed *before* creating a roster (NR's
"cannot create rosters from library catalogues" limitation), the third spec still **passed** warm.
So warm-reuse survives a spec that never made a roster, and breaks only once a spec has
**successfully created** one. `NrRosterUiEngine.Cleanup` → `ResetBrowserStateAsync` (clears the
Pinia `lists` store, strips `list`-matching `localStorage` keys, re-navigates to `/app`) is
evidently not removing the previous list — precisely the "leftover list rows make the Create List
dialog's controls ambiguous" hazard its own code comment warns about. The 30s Playwright timeout
on each of the 7 failing specs is also what makes the warm run slower than cold.

The in-tree NR-UI roster conformance lane never caught this because it deliberately runs **one
spec** (`protocol/protocol-kitchen-sink`) — see the comment in
`tests/Conformance/FrozenNrUiRosterConformanceTests.cs`. Multi-spec warm reuse through the host is
first exercised by this benchmark.

**This is an open defect, not a shipped win.** The flag is left as-is by this change (the
enablement decision is out of scope for the docs/benchmark pass); it needs either a fix to the
warm reset path so NR's roster-creation UI is genuinely restored between specs, or `newrecruit-ui`
must be dropped from `ReuseRosterEngineAcrossSetups`.

### `newrecruit-ui` gamedata, `newrecruit` (both domains)

Not captured in this pass. Warm-reuse is enabled for them; treat them as unverified at batch scale
until benchmarked with the script below.

### `battlescribe-ui` roster — cold only

Warm is disabled (app self-crash, see the table). Cold baseline: ≈6.6s/spec on a 102-spec batch.

## Reproducing

```powershell
pwsh -File scripts/bench-warm-reuse.ps1 -Engine battlescribe-ui -Domain gamedata -Filter "condition/,constraint/,cost/"
pwsh -File scripts/bench-warm-reuse.ps1 -Engine newrecruit-ui   -Domain roster   -Filter "gamesystem/,entry-group/"
```

The script builds, runs the same `bs-spec run --all` batch twice (warm, then cold via
`BSSPEC_DISABLE_WARM_REUSE=1`), times each with `Measure-Command`, prints spec count / warm wall /
cold wall / absolute saving / per-spec saving / speedup, and **asserts the per-spec PASS/FAIL
verdicts are identical** between the two — exiting non-zero with a loud banner if warm-reuse ever
changes a conformance result. That assertion is how the NR-UI roster defect above was found.

To force cold behavior for any engine/domain without the script (e.g. to confirm a failure is
warm-only), set:

```
BSSPEC_DISABLE_WARM_REUSE=1
```

before invoking `bs-engine-host serve`, or before a `bs-spec run` that spawns it. It overrides both
`ServeCommand`'s per-domain reuse flags and `BsGameDataUiEngine.KeepAlive`.

## Related issues

- [#303](https://github.com/WarHub/battlescribe-spec/issues/303) — adapter stderr is not forwarded
  to the CLI, which makes diagnosing a warm-session failure harder than it should be.
- [#304](https://github.com/WarHub/battlescribe-spec/issues/304) — no recovery from a dead adapter
  process: a crashed host (e.g. the `battlescribe-ui` roster self-crash) aborts the rest of the
  batch instead of restarting the engine.
