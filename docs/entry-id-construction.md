# Entry ID Construction Rules

This document describes how `entryId` and `entryGroupId` are constructed on
selection objects in BattleScribe rosters. These rules were determined
empirically by testing the BattleScribe Java engine.

## Core Principle

**Every entry link in the resolution path prepends its own ID with `::` as separator.**

Direct (non-linked) entries and groups do NOT contribute composite segments.
Only links (both entry links and group links) add `::` segments.

## entryId Rules

| Scenario | entryId format | Example |
|----------|---------------|---------|
| Direct entry (no links) | plain entry ID | `se-unit` |
| Direct child entry (no links) | plain child ID | `se-child` |
| Entry via entry link | `linkId::targetId` | `el-weapon::sse-weapon` |
| Direct child inside link target | `linkId::childId` | `el-vehicle::se-hull` |
| Entry link inside entry link (2-hop chain) | `outerLink::innerLink::targetId` | `el-weapon::el-ammo::sse-ammo` |
| Entry link 3-hop chain | `l1::l2::l3::targetId` | `el-1::el-2::el-3::sse-deep` |
| Entry via group link* | `groupLinkId::entryId` | `el-group::se-opt-a` |
| Entry link inside group link target | `groupLink::entryLink::targetId` | `el-group::el-power::sse-power` |
| Direct child in nested groups (no links) | plain entry ID | `se-deep` |

*Group link = entry link with `type: selectionEntryGroup`

### Pattern

```
entryId = link1Id :: link2Id :: ... :: linkNId :: actualEntryId
```

Where `link1` is the outermost link and `linkN` is the innermost. If there are
no links in the path, entryId is just the plain entry definition ID.

## entryGroupId Rules

| Scenario | entryGroupId format | Example |
|----------|--------------------| --------|
| Entry NOT in a group | not set (null) | — |
| Entry in direct group (no links) | plain group ID | `seg-weapons` |
| Entry link in direct group | plain group ID | `seg-traits` |
| Entry in group via group link | `groupLinkId::targetGroupId` | `el-group::sseg-options` |
| Entry in nested groups (no links) | immediate parent group ID only | `seg-inner` |
| Entry in group inside link target | `linkId::groupId` | `el-warrior::seg-weapons` |
| Entry link in group inside group link | `groupLinkId::targetGroupId` | `el-group::sseg-abilities` |

### Pattern

```
entryGroupId = link1Id :: link2Id :: ... :: linkNId :: actualGroupId
```

Same rule: only links contribute `::` segments. The group ID itself is always
the immediate parent group (not a chain of nested groups).

## Key Insights

1. **Links compose, groups don't.** Nested groups (group-in-group) do NOT create
   multi-segment entryGroupId. Only the immediate parent group's ID is used.

2. **The outer link prefix propagates to all descendants.** If entry A is accessed
   via link L, then ALL children of A (whether direct children, link targets, or
   group members) will have L's ID in their prefix.

3. **Group links are just entry links with `type: selectionEntryGroup`.** They
   follow the same `::` prefixing rule as regular entry links.

4. **Segment order is outermost-first.** The leftmost segment is the outermost
   link in the resolution path; the rightmost segment is the actual entry/group ID.

## Example: Complex Nested Structure

```
Unit (se-unit)
├── Shared Warrior (via el-warrior → sse-warrior)
│   ├── entryId: el-warrior::sse-warrior
│   └── Blade (in group seg-weapons, via min=1)
│       ├── entryId: el-warrior::se-blade
│       └── entryGroupId: el-warrior::seg-weapons
└── Shared Power (via el-grp[group link] → sseg-abilities → el-power → sse-power)
    ├── entryId: el-grp::el-power::sse-power
    └── entryGroupId: el-grp::sseg-abilities
```

## Engine Differences

### NewRecruit

NR **does** support composite `::` IDs via `getBattleScribePath()` — the same method
used by NR's own `.ros`/`.rosz` export. However, the simpler `getId()` API returns
only the plain target entry definition ID.

#### Key APIs on selection nodes

| Method | Returns | Use for |
|--------|---------|---------|
| `sel.getBattleScribePath()` | Full composite `entryId` | e.g. `"el-relics-group::el-relic::sse-relic"` |
| `sel.getBattleScribePath(true)` | Full composite `entryGroupId` | e.g. `"el-relics-group::sseg-relics"` |
| `sel.getId()` | Plain target entry definition ID | e.g. `"sse-relic"` |
| `sel.getOptionIds()` | Array of immediate link + target IDs | e.g. `["el-relic","sse-relic"]` |
| `sel.getNarrowId()` | Immediate link ID (or entry ID if direct) | e.g. `"el-relic"` |
| `sel.selector.ids` | Same as `getOptionIds()` | Partial — only immediate link |

#### `getBattleScribePath()` algorithm (deobfuscated)

```javascript
getBattleScribePath(forGroup = false) {
    const node = forGroup ? this.getParent() : this;
    if (forGroup && !node.isGroup()) return "";
    const ids = [...node.getOptionIds()];
    let currentIds = ids, parent = node.getParent();
    while (parent.selector.isQuantifiable || parent.isGroup()) {
        const src = parent.source;
        if (src.isLink()) {
            // prepend link id if not already in the chain
            let alreadyPresent = false;
            for (const u of src.localSelectionsIterator())
                if (currentIds.includes(u.id)) { alreadyPresent = true; break; }
            if (!alreadyPresent) ids.unshift(src.id);
        }
        currentIds = parent.getOptionIds();
        parent = parent.getParent();
    }
    return ids.join("::");
}
```

The algorithm walks up the parent chain, prepending link IDs from ancestor sources
that are links, producing the same `::` segments as BattleScribe's Java engine.

#### Internal data model vs export format

NR maintains **two different representations** of entry identity:

1. **Runtime model** (used by `getId()`, `selector.ids`):
   - `selector.ids` only tracks the **immediate** link, not ancestor chains
   - No propagation of link prefixes to descendants inside link targets
   - Group links do NOT contribute to `selector.ids`
   - Properties `entryId`, `entryGroupId`, `linkId`, `targetId`, `compositeId` are all
     `undefined` on NR selection objects

2. **BattleScribe-compatible export** (used by `getBattleScribePath()` and `.ros` export):
   - Walks the full ancestor chain to construct composite `::` paths
   - Handles entry links, group links, and chained links correctly
   - Used by NR's `.ros`, `.rosz`, and JSON (roster schema) export buttons

#### Export formats available in NR

| Export | Format | ID style |
|--------|--------|----------|
| `.ros` button | BattleScribe roster XML | Composite `::` via `getBattleScribePath()` |
| `.rosz` button | Compressed `.ros` (ZIP/DEFLATE) | Same composite `::` |
| `json` button | BS roster JSON schema (XML→JSON) | Same composite `::` |
| `exportArmy(format)` | Human-readable text (GW/Tournament/NR/SHORT/Warscroll/AoS4/WTC-Compact) | No IDs in output |
| `toJsonObject()` | NR internal save format | Plain `option_id` + separate `link_id` field |
| Yellowscribe | BS `.rosz` sent to API | Same composite `::` |
| NR Yellowscribe | NR-native unit data to API | No BS IDs |

#### Export implementation (deobfuscated)

NR's export pipeline is built around a central `.ros` XML serializer. All
BattleScribe-compatible formats (`.ros`, `.rosz`, JSON) share the same serialization
code path:

```
              ┌──────────────────────────────┐
              │ _b(army, name) → iX(roster)  │  Core .ros XML generator
              └──────────────┬───────────────┘
                             │
              ┌──────────────┼──────────────────────────┐
              │              │                           │
        ┌─────▼────┐   ┌────▼─────┐             ┌──────▼──────┐
        │ .ros btn │   │ .rosz btn│             │  json btn   │
        │  b4e()   │   │  v4e()   │             │  w4e()      │
        └─────┬────┘   └────┬─────┘             └──────┬──────┘
              │              │                          │
              │         FF() = JSZip            CU() = fast-xml-parser
              │         DEFLATE compress         XML → JSON object
              │              │                          │
              │              │                    cw() = flatten/clean
              │              │                          │
              ▼              ▼                          ▼
         W_(name.ros)   W_(name.rosz)           W_(name.json)
         saveAs blob    saveAs blob             saveAs blob
```

##### Core XML generator: `_b()` → `iX()`

```javascript
// Public entry point
function _b(roster, rosterName) { return iX(roster, rosterName); }

// Validates roster, builds XML document
function iX(roster, rosterName, XmlBuilder = Es) {
    if (!sX(roster)) throw Error("isBsExportable(roster) returned false");
    const builder = new XmlBuilder();
    builder.line('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>');
    const book = roster.getBook();
    const gstBook = book.system.books.array.find(b => b.bsid === book.catalogue.gameSystemId);
    const attrs = {
        id: roster.uid,
        name: rosterName || roster.getName(),
        battleScribeVersion: "2.03",
        generatedBy: "https://newrecruit.eu",
        gameSystemId: gstBook.bsid,
        gameSystemName: gstBook.name,
        gameSystemRevision: gstBook.nrversion,
        xmlns: "http://www.battlescribe.net/schema/rosterSchema"
    };
    builder.begin("roster", builder.formatAttrs(attrs));
    builder.child(N_(Object.values(roster.getTotalCosts())));  // <costs>
    builder.child(nX(roster.getMaxCosts()));                   // <costLimits>
    builder.child(WU(roster.getForces(false)));                // <forces>
    builder.end("roster");
    return builder.toString();
}
```

##### Selection serializer: `JU()` (recursive)

This is where `getBattleScribePath()` is called to produce composite `::` IDs:

```javascript
function JU(selections, XmlBuilder = Es) {
    const builder = new XmlBuilder();
    selections = selections.filter(s => s.amount);
    if (!selections?.length) return builder.toString();
    builder.begin("selections");
    for (const sel of selections) {
        const attrs = {
            id: sel.uid,                              // unique instance ID
            name: sel.getName(),
            entryId: sel.getBattleScribePath(),        // ← composite :: ID
            entryGroupId: sel.getBattleScribePath(true) || undefined,  // ← composite :: group ID
            number: sel.getSelectionCount("root"),
            type: sel.source.getType(),
            from: sel.getParent().isGroup() ? "group" : "entry"
        };
        if (sel.getParent().isGroup()) {
            // "group" attr = parent group names joined with ::
            attrs.group = sel.getParentGroups()
                .map(g => g.getName().trim()).reverse().join("::");
        }
        if (sel.getCustomName()) attrs.customName = sel.getCustomName();
        const costs = Object.values(sel.getCosts());
        costs.forEach(c => c.value = c.value * attrs.number);
        builder.begin("selection", builder.formatAttrs(attrs));
        builder.child(YU(sel.getModifiedRules()));      // <rules>
        builder.child(KU(profiles));                     // <profiles>
        builder.child(tX(sel.getAssociations()));        // <associations>
        builder.child(rX(sel.getIncomingAssociations())); // <incomingAssociations>
        builder.child(JU(sel.getSelections()));           // <selections> (recursive)
        builder.child(N_(costs));                         // <costs>
        builder.child(qU(sel.getSelectionCategories())); // <categories>
        builder.end("selection");
    }
    builder.end("selections");
    return builder.toString();
}
```

##### Force serializer: `WU()` with simpler `fP()` for entryId

Forces use a simpler single-level composite for `entryId`:

```javascript
function fP(node) {
    const src = node.source;
    return src.isLink() ? `${src.id}::${src.targetId}` : src.id;
}
```

##### Download helper: `W_()`

```javascript
function W_(filename, mimeType, data) {
    const blob = new Blob([data], { type: mimeType });
    AL(blob, filename);  // AL = platform-aware saveAs (Capacitor native or browser)
}
```

##### JSON export pipeline: `w4e()`

The JSON button does **not** have a separate JSON serializer. It generates
`.ros` XML first, then converts to JSON:

```javascript
function w4e(r) {
    const army = r.army;
    if (!army) return;
    const jsonObj = CU(_b(army, r.row.name));  // XML → JSON via fast-xml-parser
    cw(jsonObj.roster);                         // flatten wrapper elements, clean empties
    const jsonStr = JSON.stringify(jsonObj);
    W_(`${r.row.name}.json`, "application/json", jsonStr);
}
```

`CU()` uses `fast-xml-parser` with options to preserve attributes as plain keys
(no `@_` prefix), and `cw()` recursively flattens single-child wrapper elements
(e.g., `{selections: {selection: [...]}}` → `{selections: [...]}`).

##### Text export: `exportArmy(format)`

Produces human-readable HTML/text. Each format has its own dedicated function:
- NR: `cz(army, options)` (default)
- Warscroll: `mK(army, options)`
- AoS4: `Fq(army)`
- WTC-Compact: `gz(army, options)`
- GW/Tournament/SHORT: handled by dedicated formatters

No IDs appear in text export output.

##### Internal save format: `toJsonObject()`

Used for IndexedDB persistence and server sync. **Completely separate** from
the BattleScribe export pipeline:

```javascript
toJsonObject(includeUid = false) {
    const obj = {
        name: this.getName(),
        option_id: this.source.getId(),    // plain entry def ID
        options: this.getChildInstances()
            .filter(i => /* has content */)
            .map(i => i.toJsonObject(includeUid))
    };
    if (this.source.isLink()) obj.link_id = this.source.id;
    if (includeUid) obj.uid = this.uid;
    if (this.customName) obj.customName = this.customName;
    if (this.selector.isQuantifiable) obj.amount = this.amount;
    // ... force-specific: catalogue_id, maxCosts
    return obj;
}
```

Example `.ros` export output:
```xml
<selection name="Shared Relic"
           entryId="el-relics-group::el-relic::sse-relic"
           entryGroupId="el-relics-group::sseg-relics"
           type="upgrade" from="group" group="Shared Relics"/>
```

Example `toJsonObject()` output (internal save):
```json
{ "name": "Shared Relic", "option_id": "sse-relic", "link_id": "el-relic", "amount": 1 }
```

#### Adapter implications

The adapter uses `sel.getBattleScribePath()` instead of `sel.getId()` to produce
composite IDs matching the BattleScribe engine. This is NR's own native API — the same
one used by their `.ros` export — so using it is not "cheating" or synthesizing IDs.

All 15 entry-id specs now pass on both BattleScribe and NewRecruit engines.
