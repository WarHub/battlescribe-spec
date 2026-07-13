# NR-UI host-side warm-reuse Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop `bs-engine-host` from cold-starting a browser per spec for the NewRecruit engines: reuse one warm engine across setup/teardown cycles, resetting it with the engines' existing `Cleanup()` between specs instead of disposing and recreating.

**Architecture:** The host process is already reused across specs (one per `--workers` slot; `SpecSuiteRunner` loops specs on the same `AdapterProcess`). Today `AdapterHandler` disposes the real engine on every `teardown` (sent by `JsonProtocolEngine.Dispose` after each spec) and recreates it on the next `setup` — two Chromium cold starts per spec. The fix adds an opt-in flag so the handler keeps one engine alive, calling the engine's already-existing `Cleanup()` between specs. This mirrors, byte-for-byte, the proven in-process engine pool (which reuses one browser/context across specs with `Cleanup()` between). No protocol/wire change.

**Tech Stack:** .NET 10, xUnit, the NDJSON adapter protocol (TestKit), Playwright (NR engines, unchanged).

## Global Constraints

- **Opt-in, default off.** `AdapterOptions.ReuseEngineAcrossSetups` defaults `false`. The reference adapter, `battlescribe`, and `battlescribe-ui` keep today's dispose+recreate behavior unchanged (correct for cheap in-process engines and any engine whose `Setup` isn't re-entrant).
- **Enabled only for `newrecruit` and `newrecruit-ui`** (roster + gamedata) — the frozen/live Playwright engines. `battlescribe-ui` is explicitly out of scope (separate `KeepAlive`/`MaxParallel=1` lifecycle).
- **No new protocol message.** Reuse is entirely host-side, keyed off the existing `teardown`/`setup` flow. Do not add wire types, do not touch `docs/protocol-schema.json` / `ProtocolSchemaDriftTests`.
- **`Cleanup()` is the reset primitive** and already exists on `IRosterEngine`/`IGameDataEngine` (default no-op) and is overridden by all four NR engines. It is best-effort/idempotent ("safe to call when partially initialized").
- **Self-heal:** if a reset (`Cleanup`) throws, dispose the engine so the next `setup` recreates a fresh one — a crashed browser must not poison the rest of the batch.
- Repo conventions: `dotnet build` before any `--no-build`; `TreatWarningsAsErrors=true`; xUnit1051 → tests must pass `TestContext.Current.CancellationToken`; central package management.

---

### Task 1: Warm-reuse mechanism in `AdapterHandler` (TestKit) + unit tests

**Files:**
- Modify: `src/BattleScribeSpec.TestKit/Protocol/AdapterHandler.cs`
- Test: `tests/Features/AdapterHandlerTests.cs`

**Interfaces:**
- Produces: `AdapterOptions.ReuseEngineAcrossSetups` (`bool`, default `false`). Task 2 sets it in the host.
- Consumes: `IRosterEngine.Cleanup()` / `IGameDataEngine.Cleanup()` (existing, default no-op); both interfaces are `IDisposable`.

**Context:** `AdapterHandler.RunAsync` holds `IRosterEngine? engine` and `IGameDataEngine? gdEngine` across the read loop, disposing both in `finally` at process end. Today `HandleSetup`/`HandleSetupFromFiles`/`HandleGameDataSetup` do `engine?.Dispose(); engine = factory();`, and `HandleTeardown` disposes+nulls both. This task makes those paths reuse-aware.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Features/AdapterHandlerTests.cs`. First add a counting fake roster engine — copy the **complete** `IRosterEngine` member list from the existing `FakeEngine` nested class in `tests/Regression/RunnerAndProtocolRegressionTests.cs` (it already stubs every action method), renamed to `CountingRosterEngine`, and add the three counters shown below (keep all the action-method stubs it already has):

```csharp
private sealed class CountingRosterEngine : IRosterEngine
{
    public int SetupCalls { get; private set; }
    public int CleanupCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public bool ThrowOnCleanup { get; init; }

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        SetupCalls++;
        return [];
    }

    public void Cleanup()
    {
        CleanupCalls++;
        if (ThrowOnCleanup)
        {
            throw new InvalidOperationException("cleanup boom");
        }
    }

    public void Dispose() => DisposeCalls++;

    // ── copy every remaining IRosterEngine member (SetupFromFiles, AddForce, AddChildForce,
    //    RemoveForce, SelectEntry, SelectChildEntry, DeselectSelection, SetSelectionCount,
    //    DuplicateSelection, DuplicateForce, SetCostLimit, SetCustomization, GetRosterState,
    //    GetValidationErrors) verbatim from FakeEngine in RunnerAndProtocolRegressionTests.cs —
    //    they are never called by a setup/teardown-only script but must exist to compile. ──
}
```

Then the tests. `InMemoryAdapterConnection.DisposeAsync` completes stdin → the handler loop exits → `finally` disposes the engine, so the counts are deterministic after `await connection.DisposeAsync()`:

```csharp
[Fact]
public async Task Reuse_KeepsOneEngine_AcrossSetupTeardownCycles()
{
    CountingRosterEngine? created = null;
    var factoryCalls = 0;
    var connection = new InMemoryAdapterConnection(
        (input, output, ct) => AdapterHandler.RunAsync(
            new AdapterOptions
            {
                RosterEngineFactory = () => { factoryCalls++; return created = new CountingRosterEngine(); },
                Name = "newrecruit-ui",
                ReuseEngineAcrossSetups = true,
            },
            input, output, ct));

    var ct = TestContext.Current.CancellationToken;
    var gs = new ProtocolGameSystem { Id = "gs", Name = "GS" };

    // Two specs: setup → teardown → setup → teardown, on the same connection (same host loop).
    Assert.IsType<SetupResult>(await connection.SendCommandAsync(new SetupCommand { GameSystem = gs }, ct));
    Assert.IsType<TeardownResult>(await connection.SendCommandAsync(new TeardownCommand(), ct));
    Assert.IsType<SetupResult>(await connection.SendCommandAsync(new SetupCommand { GameSystem = gs }, ct));
    Assert.IsType<TeardownResult>(await connection.SendCommandAsync(new TeardownCommand(), ct));

    await connection.DisposeAsync();

    Assert.Equal(1, factoryCalls);            // engine created ONCE, not per spec
    Assert.Equal(2, created!.SetupCalls);     // Setup ran for both specs on the same instance
    Assert.Equal(2, created.CleanupCalls);    // reset between/after specs (per teardown)
    Assert.Equal(1, created.DisposeCalls);    // disposed once, at process end
}

[Fact]
public async Task NoReuse_DisposesAndRecreates_PerSetup()
{
    var engines = new List<CountingRosterEngine>();
    var connection = new InMemoryAdapterConnection(
        (input, output, ct) => AdapterHandler.RunAsync(
            new AdapterOptions
            {
                RosterEngineFactory = () => { var e = new CountingRosterEngine(); engines.Add(e); return e; },
                Name = "battlescribe",
                // ReuseEngineAcrossSetups defaults false
            },
            input, output, ct));

    var ct = TestContext.Current.CancellationToken;
    var gs = new ProtocolGameSystem { Id = "gs", Name = "GS" };

    await connection.SendCommandAsync(new SetupCommand { GameSystem = gs }, ct);
    await connection.SendCommandAsync(new TeardownCommand(), ct);
    await connection.SendCommandAsync(new SetupCommand { GameSystem = gs }, ct);
    await connection.SendCommandAsync(new TeardownCommand(), ct);

    await connection.DisposeAsync();

    Assert.Equal(2, engines.Count);                       // recreated per setup
    Assert.All(engines, e => Assert.Equal(1, e.DisposeCalls)); // each disposed on its teardown
    Assert.All(engines, e => Assert.Equal(0, e.CleanupCalls)); // no warm reset when reuse is off
}

[Fact]
public async Task Reuse_SelfHeals_WhenCleanupThrows()
{
    var engines = new List<CountingRosterEngine>();
    var connection = new InMemoryAdapterConnection(
        (input, output, ct) => AdapterHandler.RunAsync(
            new AdapterOptions
            {
                RosterEngineFactory = () => { var e = new CountingRosterEngine { ThrowOnCleanup = true }; engines.Add(e); return e; },
                Name = "newrecruit-ui",
                ReuseEngineAcrossSetups = true,
            },
            input, output, ct));

    var ct = TestContext.Current.CancellationToken;
    var gs = new ProtocolGameSystem { Id = "gs", Name = "GS" };

    await connection.SendCommandAsync(new SetupCommand { GameSystem = gs }, ct);
    await connection.SendCommandAsync(new TeardownCommand(), ct);   // Cleanup throws → engine disposed
    await connection.SendCommandAsync(new SetupCommand { GameSystem = gs }, ct); // must recreate

    await connection.DisposeAsync();

    Assert.Equal(2, engines.Count);                 // reset failure forced a fresh engine
    Assert.Equal(1, engines[0].CleanupCalls);       // attempted reset
    Assert.Equal(1, engines[0].DisposeCalls);       // then disposed (self-heal)
}
```

- [ ] **Step 2: Run the tests to confirm they fail**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "FullyQualifiedName~AdapterHandlerTests"`
Expected: the three new tests FAIL to compile (no `ReuseEngineAcrossSetups`) or fail assertions.

- [ ] **Step 3: Add the `ReuseEngineAcrossSetups` option**

In `AdapterOptions` (top of `AdapterHandler.cs`), add:

```csharp
/// <summary>
/// When true, keep ONE engine alive across setup/teardown cycles, resetting it with
/// <see cref="IRosterEngine.Cleanup"/> / <see cref="GameData.IGameDataEngine.Cleanup"/> between
/// specs instead of disposing and recreating. For browser-backed engines (newrecruit,
/// newrecruit-ui) this avoids a Chromium cold start per spec when one host process serves a whole
/// batch. Mirrors the in-process engine pool, which reuses one browser/context across specs with
/// Cleanup between. Default false: dispose+recreate — correct for cheap in-process engines and any
/// engine whose Setup is not re-entrant.
/// </summary>
public bool ReuseEngineAcrossSetups { get; init; }
```

- [ ] **Step 4: Thread the flag through the command dispatch**

In `RunAsync`'s `switch`, update these four arms to pass the flag:

```csharp
SetupCommand setup => HandleSetup(setup, engineFactory, options.ReuseEngineAcrossSetups, ref engine, out catalogueIds),
SetupFromFilesCommand setupFiles => HandleSetupFromFiles(setupFiles, engineFactory, options.ReuseEngineAcrossSetups, ref engine, out catalogueIds),
TeardownCommand => HandleTeardown(options.ReuseEngineAcrossSetups, ref engine, ref gdEngine),
GameDataSetupCommand gdSetup => HandleGameDataSetup(gdSetup, options, ref gdEngine),
```

(`HandleGameDataSetup` already receives `options`; it reads the flag internally.)

- [ ] **Step 5: Make the setup handlers reuse-aware**

In `HandleSetup`, replace the signature and the dispose+recreate preamble. Also rewrite its XML doc-comment (which currently describes the cold-start problem as an open follow-up) to describe the implemented behavior:

```csharp
/// <summary>
/// Configures the engine for a spec. When reuse is enabled (browser-backed engines) an
/// already-live engine is kept and reconfigured in place — avoiding a per-spec cold start;
/// otherwise the engine is disposed and recreated. Reset between specs happens in
/// <see cref="HandleTeardown"/> via the engine's Cleanup.
/// </summary>
private static ProtocolResponse HandleSetup(
    SetupCommand cmd, Func<IRosterEngine> factory, bool reuse, ref IRosterEngine? engine, out IReadOnlyList<string> catalogueIds)
{
    if (!reuse || engine is null)
    {
        engine?.Dispose();
        engine = factory();
    }

    catalogueIds = [.. cmd.Catalogues.Select(c => c.Id)];
    if (cmd.SpecId is { Length: > 0 })
    {
        engine.SetTestContext(cmd.SpecId);
    }

    var errors = engine.Setup(cmd.GameSystem, [.. cmd.Catalogues]);
    return new SetupResult { Errors = [.. errors] };
}
```

Apply the identical `if (!reuse || engine is null) { engine?.Dispose(); engine = factory(); }` change (and add the `bool reuse` parameter) to `HandleSetupFromFiles`.

- [ ] **Step 6: Make teardown reset-and-keep (with self-heal), and add the shared helper**

Replace `HandleTeardown` and add `ResetOrDispose`:

```csharp
private static ProtocolResponse HandleTeardown(bool reuse, ref IRosterEngine? engine, ref GameData.IGameDataEngine? gdEngine)
{
    engine = ResetOrDispose(engine, reuse, e => e.Cleanup());
    gdEngine = ResetOrDispose(gdEngine, reuse, e => e.Cleanup());
    return new TeardownResult();
}

/// <summary>
/// End-of-spec engine handling. With <paramref name="reuse"/> true, resets the engine in place and
/// keeps it warm for the next setup; if the reset throws, disposes it so the next setup recreates a
/// fresh one (self-heal against a crashed browser). With reuse false, disposes and clears it.
/// </summary>
private static T? ResetOrDispose<T>(T? engine, bool reuse, Action<T> cleanup) where T : class, IDisposable
{
    if (engine is null)
    {
        return null;
    }

    if (!reuse)
    {
        engine.Dispose();
        return null;
    }

    try
    {
        cleanup(engine);
        return engine;
    }
    catch
    {
        try { engine.Dispose(); } catch { /* best-effort */ }
        return null;
    }
}
```

- [ ] **Step 7: Make gamedata setup reuse-aware**

In `HandleGameDataSetup`, after the `GameDataEngineFactory is null` guard, replace `engine?.Dispose(); engine = options.GameDataEngineFactory();` with the reuse-aware form, and rewrite its doc-comment the same way as `HandleSetup`:

```csharp
if (!options.ReuseEngineAcrossSetups || engine is null)
{
    engine?.Dispose();
    engine = options.GameDataEngineFactory();
}
```

The `finally { engine?.Dispose(); gdEngine?.Dispose(); }` at the end of `RunAsync` stays unchanged — it disposes the warm engine at process shutdown.

- [ ] **Step 8: Run the tests to confirm they pass**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "FullyQualifiedName~AdapterHandlerTests"`
Expected: all AdapterHandlerTests pass (the 3 new + the 6 existing — the existing `Setup_GetState_Teardown_RoundTrips` still passes because `battlescribe` uses the default reuse=false path).

- [ ] **Step 9: Commit**

```bash
git add src/BattleScribeSpec.TestKit/Protocol/AdapterHandler.cs tests/Features/AdapterHandlerTests.cs
git commit -m "feat(host): opt-in warm engine reuse across setups in AdapterHandler (#271)"
```

---

### Task 2: Enable reuse for the NR engines in the host + verify

**Files:**
- Modify: `src/BattleScribeSpec.EngineHost/ServeCommand.cs`

**Interfaces:**
- Consumes: `AdapterOptions.ReuseEngineAcrossSetups` from Task 1.

**Context:** `ServeCommand.BuildOptions(name, headless, keepAlive)` constructs the `AdapterOptions` for a served engine, already keying `Capabilities` off `name`. Add the reuse flag the same way. Enabling it for `newrecruit` and `newrecruit-ui` makes each host process launch its browser once and reuse it across every spec that process serves.

- [ ] **Step 1: Set the flag in `BuildOptions`**

In the `AdapterOptions` object initializer in `ServeCommand.BuildOptions`, add (next to `Capabilities`):

```csharp
// Browser-backed NR engines: keep one warm engine per host process, resetting via Cleanup
// between specs (see AdapterOptions.ReuseEngineAcrossSetups). The in-process (battlescribe) and
// Java-app (battlescribe-ui) engines keep dispose+recreate.
ReuseEngineAcrossSetups = name is "newrecruit" or "newrecruit-ui",
```

- [ ] **Step 2: Build and run the offline suite**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "Category!=Conformance"`
Expected: green (build clean; the AdapterHandler tests from Task 1 pass; nothing else regressed).

- [ ] **Step 3: Verify warm-reuse against the real frozen NR engine (observable single browser launch)**

`HostEngineFactory` logs `NR UI frozen mode: {har}` (roster) / `NR Editor GameData UI (frozen): {dir}` (gamedata) to **stderr** each time it *creates* an engine. With reuse, that line appears **once per host process** instead of once per spec — a direct, observable proof.

Run a small batch of NR-applicable roster specs through the host at `--workers 1` and count the creation lines (frozen NR data is present at `.testdata/newrecruit-har/newrecruit.har`; this needs Playwright browsers installed via `setup.ps1`):

```bash
dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll run --all \
  --engine newrecruit-ui --roster \
  --filter "selection/selection-page,selection/selection-lifecycle,force/force-add" \
  --expected-failures newrecruit-ui --workers 1 \
  2> /tmp/nr-warm.stderr; echo "exit=$?"
grep -c "NR UI frozen mode" /tmp/nr-warm.stderr
```

Expected: the run completes (exit 0 under `--expected-failures`), it executes **more than one** spec, and `grep -c "NR UI frozen mode"` prints **`1`** (one browser launch for the whole batch). Before this change it would equal the spec count.

If Playwright browsers are not installed in this environment (the run fails at browser launch with a Playwright driver/executable error rather than a spec assertion), record that the real-run proof was **not** executed here and note that the deterministic Task 1 tests are the gate; the `nr-conformance` CI lane exercises the live path. Do NOT fabricate a passing run.

- [ ] **Step 4: Commit**

```bash
git add src/BattleScribeSpec.EngineHost/ServeCommand.cs
git commit -m "feat(host): reuse the warm NR engine across specs in bs-engine-host (#271)"
```

---

## Final verification (before opening the PR)

- [ ] `dotnet build` — clean.
- [ ] `dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "Category!=Conformance"` — green.
- [ ] Confirm no protocol schema files changed: `git diff --name-only main...HEAD` lists only `AdapterHandler.cs`, `AdapterHandlerTests.cs`, `ServeCommand.cs`, and this plan — no `docs/protocol-schema.json`.
- [ ] Push and open the PR. The `nr-conformance` CI lane (opt-in / `[nr-test]`) is where the end-to-end speedup is exercised on the live path.

## Out of scope (follow-ups)

- **`battlescribe-ui` warm-reuse** — the Java-app engine also cold-starts per spec but has its own `KeepAlive` mechanism and `MaxParallel=1`; a separate change.
- **Cross-process browser sharing** — `--workers N` still launches N browsers (one per host process). Sharing one browser with N contexts across processes (as the in-process pool does) is a larger architectural change, not needed for the per-spec cold-start win.
