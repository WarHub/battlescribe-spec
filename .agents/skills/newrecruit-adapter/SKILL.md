---
name: newrecruit-adapter
description: >
  Work with the NewRecruit (NR) browser-based engine adapter. Use when debugging NR
  test failures, modifying Playwright automation, understanding synthetic data loading,
  or working with HAR recording/replay for frozen tests. Covers Pinia store access,
  known NR limitations, and the costIndex population requirement.
---

# NewRecruit Adapter

The NR adapter drives the [NewRecruit](https://newrecruit.eu) web app via Playwright
browser automation, enabling conformance testing against a second independent BattleScribe
engine implementation.

## Architecture

```
Protocol types (spec YAML)
    ↓ CatXmlGenerator
BattleScribe XML (.gst/.cat)
    ↓ loadSystemFromFs (Pinia store)
NR Vue app (Playwright browser)
    ↓ NewRecruitStateReader (JavaScript evaluation)
IRosterEngine state (shared with BattleScribe)
```

**Key files:**

| File | Purpose |
|------|---------|
| `src/BattleScribeSpec.NewRecruit/NewRecruitRosterEngine.cs` | Main IRosterEngine impl |
| `src/BattleScribeSpec.NewRecruit/NewRecruitBrowser.cs` | Playwright lifecycle |
| `src/BattleScribeSpec.NewRecruit/NewRecruitStateReader.cs` | State extraction via JS |
| `src/BattleScribeSpec.NewRecruit/NewRecruitActions.cs` | Roster operations via JS |
| `src/BattleScribeSpec.NewRecruit/CatXmlGenerator.cs` | Protocol → XML generation |
| `src/BattleScribeSpec.NewRecruit/HarRecorder.cs` | HAR recording/filtering |
| `src/BattleScribeSpec.NewRecruit.HarTool/Program.cs` | HAR recording CLI tool |

## Two operational modes

### Live mode — real NR website

```csharp
var engine = await NewRecruitRosterEngine.CreateAsync(
    baseUrl: "https://www.newrecruit.eu", headless: true);
```

Requires `NR_ENGINE_URL` environment variable in test infrastructure.

### Frozen mode — HAR replay (offline, deterministic)

```csharp
var engine = await NewRecruitRosterEngine.CreateFrozenAsync(
    harFilePath: ".testdata/newrecruit-har/newrecruit.har", headless: true);
```

HAR replay intercepts all network requests via `Page.RouteFromHARAsync()`.
No internet needed. Deterministic results.

**Important**: The HAR is a single snapshot of the entire NR web application
(JS bundles, CSS, assets) — NOT per-spec recordings. All specs run against the
same HAR. New specs work immediately against the existing HAR without any
recording step. The HAR is versioned by NR client version (pinned in
`testdata.json`), not by spec content.

## Playwright browser setup

### Pinia store access

NR is a Vue/Nuxt app using Pinia for state management. All interaction goes through
Pinia stores accessed via:

```javascript
document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties
    ?.$pinia?._s?.get('storeName')
```

**Critical stores:**

| Store | Key methods |
|-------|------------|
| `systemsStore` | `loadSystemFromFs(files)`, `selectSystem()`, `localLibrary` |
| `lists` | `getCurrentList()`, `addList(list)`, `removeList(row)`, `findListByKey(key, systemIds)` |

> **There is no `lists.deleteList`.** This table used to claim there was, and both roster engines
> called `listsStore.deleteList?.(key)` — an optional call on a non-existent action, so cleanup
> silently deleted nothing and rows piled up across a suite run. Deletion is `removeList(row)`:
> local-only (splices `listData`, rebuilds `treeData`, deletes from IndexedDB), and it takes the
> **row**, not the key. `removeListVue(row, browserOnly, skipConfirm)` is the UI wrapper (mounts a
> confirm dialog); `deleteListFromServer(row)` issues a server RPC and must never run under a frozen
> HAR. `deleteList` does exist in NR — as a MyLists *page-component* method and as the server RPC
> verb — but not on the store. See `NrListStoreJs`.

### When state is wrong, find *who changed it* — don't reason about it

Four NR bugs in a row (#334, #336, #337, #339) were all the same question: some earlier spec's state
was still present, and the fix depended on knowing *which code put it there*. Snapshot diagnostics
(screenshot, DOM, Pinia dump) answer "what is the state now?" and cannot answer that. Reasoning about
it from the bundle is slow and, in three of those four cases, produced a confident wrong answer first.

**The technique that works — wrap the action, keep the caller stack:**

```javascript
const orig = store.selectSystem.bind(store);
store.selectSystem = function (...args) {
    console.log(args, store.selectedSystem?.id, new Error().stack);  // frames 2+ = the real caller
    return orig(...args);
};
```

The caller frame names the component *and* its lifecycle hook. In #339 that printed
`selectList <- updateRoute <- activated` — `activated` being Vue's **keep-alive** hook — which was
the whole diagnosis, after a lot of theorising had produced nothing.

This is now built in: **`bs-spec run --trace-store`** (see `NrStoreTraceJs`). It wraps the
bug-prone actions, records `{action, args, selectedSystem before/after, lastSelectedListKey, stack}`
into a ring buffer, and writes `store-trace.json` into the failure diagnostic report. Off by
default — wrapping replaces the store's function identities, so it must never be on during
`bs-spec compare`.

**Do not trust a component tree walk to prove "nothing holds this".** A component cached by
`<KeepAlive>` is not in the active subtree, so walking `inst.subTree` returns a false negative for
exactly the case that bites. A caller stack has no such blind spot.

### Navigation

- **Live mode:** Direct `page.GotoAsync(baseUrl)`
- **Frozen mode:** Vue Router client-side navigation (full page nav breaks HAR JS MIME types)
  ```javascript
  const router = document.querySelector('#__nuxt')?.__vue_app__
      ?.config?.globalProperties?.$router;
  router.push('/app');
  ```
- Uses `WaitUntilState.Load` (not `NetworkIdle` — persistent connections cause timeouts)

## Synthetic data loading

The setup flow loads protocol types as synthetic BattleScribe data:

1. Generate XML from protocol objects via `CatXmlGenerator`
2. Build files array: `[{name: "file.gst", path: "/spec/file.gst", data: xmlString}, ...]`
3. Call `sysStore.loadSystemFromFs(files)` to load into NR
4. Select the system: `sysStore.selectSystem(localSys)`
5. **Manually populate costIndex** on each catalogue (see below)
6. Create roster: `primaryBook.createRoster(costs)`
7. Save state to `window.__bsspec` for later access

### costIndex manual population (critical)

NR doesn't auto-populate `catalogue.costIndex` from the game system's cost types.
Without this, child cost calculations fail silently:

```javascript
bd.catalogue.costIndex = {};
if (gs?.costTypes) {
    for (const ct of gs.costTypes) {
        bd.catalogue.costIndex[ct.id] = ct;
    }
}
```

This must be done for **every catalogue** after loading.

## Selection insertion order tracking

**Problem:** BattleScribe displays selections in insertion order; NR sorts alphabetically.

**Solution:** Tag new selections with sequence numbers:

```javascript
// Before adding
const before = new Set(getSelections(force).map(s => s?.__v_raw?.uid || ''));

// After adding
window.__bsspec._selSeq = (window.__bsspec._selSeq || 0) + 1;
for (const s of after) {
    const raw = s?.__v_raw || s;
    if (raw && !before.has(raw.uid || '') && raw.__bsspec_seq === undefined)
        raw.__bsspec_seq = window.__bsspec._selSeq;
}
```

State reader sorts by `__bsspec_seq`, with catalogue entry order as tiebreaker.

## Known limitations

| Limitation | Impact | Workaround |
|-----------|--------|-----------|
| InfoLink publication override | NR uses infoLink's own pub, not target's | Per-engine `expectedState` overrides in specs |
| InfoLink page override | NR uses infoLink's own page, not target's | Per-engine `expectedState` overrides in specs |
| Page modifier not applied | `type: set, field: page` doesn't update selection page | Per-engine `expectedState` override |
| `setAmount()` requires two args | `setAmount(n)` with one arg corrupts: sets `ctx=n, n=undefined` | Always use `sel.setAmount({}, count)` |
| `setSelectionCount` rejects root selections | Protocol validates that `selectionId` targets a child selection | Use `selectEntry`/`deselectSelection` for roots |
| `calcTotalCosts()` omits hidden cost types | Roster cost totals exclude hidden cost types | Uniform manual summation for all types |
| costIndex not auto-populated | Child cost calculations return 0 | Manual population in setup (see above) |
| Child nodes pre-created with amount=0 | selectChildEntry must increment, not addInstance | Increment existing node amount |
| autocheck ignores defaultSelectionEntryId | Selects alphabetically first entry in groups | Use single-option groups for deterministic auto-selection |
| NetworkIdle hangs | Persistent connections prevent WaitUntilState.NetworkIdle | Use WaitUntilState.Load |
| Publication scope resolution | ForceEntry in gameSystem can't resolve catalogue-only publications | Define publications in same file as referencing entries |

## Selection mechanics

NR's roster tree uses **selectors** (templates) and **instances** (selections).
Three distinct APIs exist for changing selection counts:

### setAmount(ctx, n) — Counter Mutation

Called on an existing **instance** to change its count. **This is what NR's
UI spinbutton uses.** The `ctx` arg is a tracker context (pass `{}` for empty).

```javascript
// ✅ NR UI's approach: single node, correct cost propagation
node.setAmount({}, 3);  // sets amount=3, triggers full cost cascade
```

**Warning**: Two args required. `setAmount(3)` with one arg sets `ctx=3, n=undefined`
→ silently corrupts the node's amount to `undefined`.

The adapter's `SetSelectionCountAsync` uses `sel.setAmount({}, count)` which matches
NR's own UI spinbutton behavior. Protocol validation rejects root selections
(`selectionId` must target a child selection) — use `selectEntry`/`deselectSelection` for those.

### addInstance()

Called on a **selector** to create a new instance (selection). Used for
force-level entry selectors — these have an `addInstance` method and create
a fresh child node each time. After creation, the new instance starts with
`amount=0` and child selectors/instances are not yet fully materialized.

**Used by NR UI for**: "Duplicate Unit", "Create Unit (+)", structural operations.
**NOT used by NR UI for** changing child counts (that's `setAmount`).

### setAmount() — incrementing an existing instance

Called on an existing **instance** (child node) to change its count. Used for
child entries that already exist as pre-created nodes with `amount=0` under a
parent selection. Unlike `addInstance`, this doesn't create a new node — it
sets the count on an existing one.

`setAmount(ctx, n)` takes an **absolute** amount, so read `getAmount()` first.
NR's own stepper spells "+1" as `opt.setAmount({}, opt.getAmount() + (opt.getStep() ?? 1))`.

`incrementAmount`/`decrementAmount` existed up to v35.29 and were **deleted in
v35.72**; NR itself never called them. Code written against them fails with
`has no incrementAmount (unexpected node type)`.

### autocheck — cascading auto-selection

After `addInstance()`, the new instance's children with `min` constraints remain at
`amount=0`. Calling **`autocheck()`** on the new instance triggers recursive
auto-selection: it walks child selectors, finds `min>=1` entries, and selects them.

All 5 selection methods in `NewRecruitActions.cs` call `autocheck()` after creating
an instance. Without it, nested min-constraint children aren't populated.

**Caveat:** `autocheck()` ignores `defaultSelectionEntryId` — it picks entries
alphabetically. Specs must use single-option groups for deterministic auto-selection.

See [NR-INTERNALS.md](../bsspec-cli/references/NR-INTERNALS.md) for the full
deobfuscated behavior reference.

## HAR recording and replay

### Recording a fresh HAR

```bash
dotnet run --project src/BattleScribeSpec.NewRecruit.HarTool/ -- \
    --url https://www.newrecruit.eu -o .testdata/newrecruit-har
```

**Output:** `newrecruit.har` + `metadata.json` (timestamp, NR client version)

### HAR filtering

The recorder filters entries to minimize file size:

- **Allowed domains:** newrecruit.eu, fonts.googleapis.com, fonts.gstatic.com,
  raw.githubusercontent.com
- **Deduplication:** First GET/HEAD per URL; all POSTs with unique body
- Non-kept entries are removed

### Frozen test infrastructure

| Class | Purpose |
|-------|---------|
| `FrozenNewRecruitFixture` | Shared browser for frozen tests, finds HAR via `HarRecorder.FindFrozenHarFile()` |
| `NewRecruitFixture` | Shared browser for live tests, uses `NR_ENGINE_URL` |
| `FrozenNewRecruitConformanceTests` | Runs all specs against HAR replay |
| `NewRecruitConformanceTests` | Runs all specs against live NR |

### Environment variables

| Variable | Purpose | Default |
|----------|---------|---------|
| `NR_ENGINE_URL` | Live NR URL | *(required for live tests)* |
| `NR_HEADLESS` | Run browser headless | `true` |
| `NR_FROZEN_SKIP` | Skip frozen tests | `false` |

## Reference files

- [STATE-EXTRACTION.md](references/STATE-EXTRACTION.md) — How roster state is read from NR
