# New Recruit: Data Loading via `loadSystemFromFs`

## Overview

NR's internal `systemsStore.loadSystemFromFs()` API loads custom BattleScribe XML
(`.gst`/`.cat` files) as local game systems. The NR adapter uses this single API for
**both** synthetic specs (inline YAML → generated XML) and real-world DataSource specs
(git-cloned BSData repos → raw XML files). Between them those two modes cover every roster
spec: all 380 roster specs run through this path. The GameData specs under `specs/gamedata/`
do not — they drive the NR Editor app rather than the NR roster app, and load their data
through it.

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

## How the Adapter Uses This

### Synthetic specs (inline YAML data)

1. `CatXmlGenerator` converts the spec's inline game system + catalogue YAML into
   BattleScribe XML (`.gst` + `.cat`)
2. XML strings are loaded via `loadSystemFromFs`
3. `costIndex` is populated manually (see below)
4. Roster is created and force inserted via Pinia store API

### Real-world DataSource specs (e.g., wh40k-10e)

1. `DataSourceResolver` resolves `github:BSData/wh40k-10e@v10.14.0` → git clone
   to `~/.battlescribe-spec/datasource-cache/`
2. All `.gst`/`.cat` files are read as raw XML strings
3. Loaded via the same `loadSystemFromFs` call
4. All playable books are loaded; name-based entry selection is used (since there
   are no C# models to resolve indices against)

### Other data loading approaches (investigated, not used)

- **Mock `showDirectoryPicker`**: Works but more complex than `loadSystemFromFs`
- **`addGithubSystem`**: NR's built-in GitHub download; not used because
  `loadSystemFromFs` with local git clone gives us version pinning and offline support
- **Real OS folder picker**: Requires native UI interaction, impractical for automation
