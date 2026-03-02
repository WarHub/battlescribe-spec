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

---

## Alternative: Folder-Based Data Loading

NR supports loading game systems from a local folder via the File System Access API.
This is accessible through the "Add from folder" button in the My Games dialog
(`/app/MySystems#install`).

### How it works

1. NR calls `window.showDirectoryPicker()` to let user select a directory
2. NR scans the directory for `.gst`/`.cat`/`.gstz`/`.catz` files via async iterator
3. Files are parsed through the same pipeline as remote downloads
4. System is added to `localLibrary` and becomes selectable
5. NR shows "Hot Reload for local system is working" notification

### Mock `showDirectoryPicker` approach (verified working)

Playwright cannot trigger the native OS directory picker, but we can mock
`showDirectoryPicker()` to return a virtual `FileSystemDirectoryHandle`:

```javascript
// Create mock file handles from generated XML
const files = { 'system.gst': gstXml, 'catalogue.cat': catXml };

const mockFileHandle = (name, content) => ({
    kind: 'file',
    name: name,
    getFile: () => Promise.resolve(new File([content], name, { type: 'text/xml' }))
});

const mockDirHandle = {
    kind: 'directory',
    name: 'spec-data',
    values: function() {
        const entries = Object.entries(files).map(([n, c]) => mockFileHandle(n, c));
        let i = 0;
        return {
            next: () => i < entries.length
                ? Promise.resolve({ value: entries[i++], done: false })
                : Promise.resolve({ done: true }),
            [Symbol.asyncIterator]() { return this; }
        };
    },
    [Symbol.asyncIterator]: function() { return this.values(); },
    requestPermission: () => Promise.resolve('granted'),
    queryPermission: () => Promise.resolve('granted')
};

window.showDirectoryPicker = () => Promise.resolve(mockDirHandle);
// Then click "Add from folder" button, or call the upload handler directly
```

**Result**: NR parses both files, creates a local system, and adds it to the dropdown.
The system is fully functional — books are loaded, rosters can be created.

### "Add from GitHub" feature

NR also has a built-in "Add from GitHub" feature at `/app/MySystems#install`:

```
Input: owner/repo (e.g., "BSData/wh40k-10e")
Version: Latest Release | Latest Commit (Head) | Custom tag/branch
```

This can be triggered programmatically via:

```javascript
const sysStore = pinia._s.get('systemsStore');
await sysStore.addGithubSystem('BSData/wh40k-10e', 'latest'); // or specific version
```

**Implication for Phase 5**: For real-world BSData specs, we may be able to use NR's
built-in GitHub integration instead of building our own DataSource resolver. NR already
knows how to download, parse, and manage BSData repositories.

### Comparison of data loading approaches

| Approach | Synthetic Specs | Real BSData | Hot Reload | Complexity |
|----------|:-:|:-:|:-:|:-:|
| `loadSystemFromFs` (current) | ✅ | ❌ | ❌ | Low |
| Mock `showDirectoryPicker` | ✅ | ❌ | Partial | Medium |
| `addGithubSystem` | ❌ | ✅ | ❌ | Low |
| Real folder (local clone) | ❌ | ✅ | ✅ | High |

**Recommendation**: Keep `loadSystemFromFs` for synthetic specs (simplest, proven).
Use `addGithubSystem` for Phase 5 real-world specs against NR.
