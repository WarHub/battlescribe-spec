# New Recruit: Synthetic Data Loading via `loadSystemFromFs`

## Discovery Summary

NR's internal `systemsStore.loadSystemFromFs()` API can load custom BattleScribe XML
(`.gst`/`.cat` files) as local game systems. This enables running all 217 synthetic spec
tests against NR — not just the 5 real-world specs that use remote BSData repositories.

## API

```javascript
const pinia = document.querySelector('#__nuxt')?.__vue_app__
    ?.config?.globalProperties?.$pinia;
const sysStore = pinia._s.get('systemsStore');

// Each file object: { name: string, path: string, data: string }
// name must end in .gst or .cat (determines parser selection)
// data is the raw XML string
await sysStore.loadSystemFromFs([
    { name: "system.gst", path: "/spec/system.gst", data: gstXml },
    { name: "catalogue.cat", path: "/spec/catalogue.cat", data: catXml },
]);
```

### Important: Load `.gst` and `.cat` together

Both files must be loaded in the same call for the catalogue to be correctly
associated with the game system. If loaded separately, the catalogue appears but
the game system's `books` array won't include it as playable.

### Required: Populate `costIndex` on catalogue

After loading, the catalogue's `costIndex` property is not automatically generated
(unlike remote systems). It must be populated manually before `getCosts()` works:

```javascript
const bookData = await sys.getBook(catalogueId);
const gs = bookData.catalogue.gameSystem;
if (gs?.costTypes) {
    bookData.catalogue.costIndex = {};
    for (const ct of gs.costTypes) {
        bookData.catalogue.costIndex[ct.id] = ct;
    }
}
```

## Full Setup Flow

1. **Navigate to NR app**: `page.GotoAsync("https://www.newrecruit.eu/app")`
2. **Load data**: Call `loadSystemFromFs` with generated XML via `EvaluateAsync`
3. **Select system**: `sysStore.selectSystem(sysStore.localLibrary[systemId])`
4. **Get book & populate costIndex**: Manually set `costIndex` from `gameSystem.costTypes`
5. **Create roster**: `bookData.createRoster(costs)` — this calls `yB(catalogue)` which creates the reactive tree
6. **Insert force**: `roster.insertForce(bookData, forceId)` — note `createRoster` already inserts one force
7. **Add to list store**: `listsStore.addList({row, army: roster, book: bookData})`

## State Reading

After the list is created and opened in the editor, state can be read via:

```javascript
const currentList = listsStore.getCurrentList();
const army = currentList.army;

// Forces
const forces = army.getChildren(); // NOT army.forces.array

// Selections within a force
const selections = forces[0].getChildren();
selections[0].getName(); // "Warrior"
selections[0].getType(); // "unit"

// Total costs
army.calcTotalCosts(); // [{name: "Points", value: 50, typeId: "pts"}]

// Cost limits
army.getMaxCosts(); // [{name: "Points", typeId: "pts", value: 0, defaultCostLimit: -1}]
```

### Key difference from remote systems

For locally-loaded systems, the roster tree uses **NR's internal reactive objects**
(not the simplified Pinia array representation). The adapter's `NewRecruitStateReader`
will need to use `getChildren()`, `getName()`, `getType()`, `getCosts()` methods
instead of reading `.forces.array[].selections[]` properties.

## Internal Implementation Details

### File format detection
- Extensions: `gst`, `gstz`, `cat`, `catz`, `json` (compressed variants auto-decompressed)
- Parser: `Gp(data, extension)` — routes to `jB(data)` for XML, `jB(decompress(data))` for compressed
- `jB` is the XML→JSON parser that produces the internal data structure

### System storage
- `sysStore.localLibrary[gameSystemId]` — stores `Um` class instances (local systems)
- `Um` constructor: `new Um(metadata, "en", new TB)` where `TB` is the data manager
- `TB.setSystem(parsedGst)` and `TB.setCatalogue(parsedCat)` register data

### Book class (`Yw`)
- Created by `sys.getBook(bookId)`
- Properties: `id`, `name`, `revision`, `short`, `version`, `nrversion`, `system`, `catalogue`
- Prototype methods: `getCosts()`, `createRoster(costs)`, `getForces()`, `hasOnlyOneForce()`, etc.

### Roster tree (from `yB(catalogue)`)
- Root: roster node with `getChildren()`, `calcTotalCosts()`, `getMaxCosts()`, `setMaxCosts()`
- Children: force nodes with `getChildren()`, `getName()`, `getId()`
- Grandchildren: selection nodes (units/models) with `getName()`, `getType()`, `getCosts()`
- Each node: `selectors` array (categories), `state` object (reactive state)

## Implications for Adapter

### What changes
1. `NewRecruitRosterEngine.SetupAsync()` — add `loadSystemFromFs` path for inline data specs
2. `NewRecruitStateReader` — use `getChildren()` API instead of `.forces.array` properties
3. Skip the UI click for system selection — use programmatic `selectSystem()` directly

### What stays the same
1. CatXmlGenerator still generates the XML (no change needed)
2. Playwright browser lifecycle (shared fixture)
3. Expected failures for real-world specs (DataSource resolver still needed for those)

### Expected impact
- 217 currently-skipped synthetic specs become runnable against NR
- Estimated run time: ~3-5 min (shared browser, JS-level operations, no network latency for data)
