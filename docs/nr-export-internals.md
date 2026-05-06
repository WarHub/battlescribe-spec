# NewRecruit Export Internals

This document describes NR's deobfuscated export pipeline — how roster data is
serialized to `.ros`, `.rosz`, JSON, and text formats.

> **Note:** Function names and implementation details (e.g. `_b()`, `iX()`, `w4e()`,
> `exportArmy()`, `toJsonObject()`) are from NR's live JS bundle, not in-repo source code.
> In-repo implementation references use full paths under `src/`.

## Export Formats

| Export | Format | ID style |
|--------|--------|----------|
| `.ros` button | BattleScribe roster XML | Composite `::` via `getBattleScribePath()` |
| `.rosz` button | Compressed `.ros` (ZIP/DEFLATE) | Same composite `::` |
| `json` button | BS roster JSON schema (XML→JSON) | Same composite `::` |
| `exportArmy(format)` | Human-readable text (GW/Tournament/NR/SHORT/Warscroll/AoS4/WTC-Compact) | No IDs in output |
| `toJsonObject()` | NR internal save format | Plain `option_id` + separate `link_id` field |
| Yellowscribe | BS `.rosz` sent to API | Same composite `::` |
| NR Yellowscribe | NR-native unit data to API | No BS IDs |

## Architecture

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

## Core XML Generator: `_b()` → `iX()`

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

## Selection Serializer: `JU()` (recursive)

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

### `getSelectionCount("root")` and Collective

The `number` attribute uses `getSelectionCount("root")` which multiplies the
selection's own amount through the entire parent chain:

```javascript
getSelectionCount(stopAtId) {
    const t = this.getSelfAmountElseChilds();
    if (!stopAtId) return t;         // no arg → return own amount
    const n = rz(this, t);          // rz() = array of cumulative products up the tree
    let i = 0, s = this.getParent();
    for (; s && s.getId() !== stopAtId; )
        s = s.getParent(), s && i++;
    return n[i] ?? 0;
}
```

For collective entries, this produces the **same result as BattleScribe**:
a Rifle (collective, amount=1) under Trooper (amount=3) exports as `number="3"`
with `costs.value = 5 × 3 = 15`. The collective flag does not affect the export
number — non-collective siblings also get multiplied through the parent chain.
The difference only matters at runtime (NR keeps `amount=1` internally; BS
propagates `number=3` on the selection node).

## Force Serializer: `WU()` with `fP()` for entryId

Forces use a simpler single-level composite for `entryId`:

```javascript
function fP(node) {
    const src = node.source;
    return src.isLink() ? `${src.id}::${src.targetId}` : src.id;
}
```

## Download Helper: `W_()`

```javascript
function W_(filename, mimeType, data) {
    const blob = new Blob([data], { type: mimeType });
    AL(blob, filename);  // AL = platform-aware saveAs (Capacitor native or browser)
}
```

## JSON Export: `w4e()`

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

## Text Export: `exportArmy(format)`

Produces human-readable HTML/text. Each format has its own dedicated function:
- NR: `cz(army, options)` (default)
- Warscroll: `mK(army, options)`
- AoS4: `Fq(army)`
- WTC-Compact: `gz(army, options)`
- GW/Tournament/SHORT: handled by dedicated formatters

No IDs appear in text export output.

## Internal Save Format: `toJsonObject()`

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

## Examples

`.ros` export output:
```xml
<selection name="Shared Relic"
           entryId="el-relics-group::el-relic::sse-relic"
           entryGroupId="el-relics-group::sseg-relics"
           type="upgrade" from="group" group="Shared Relics"/>
```

`toJsonObject()` output (internal save):
```json
{ "name": "Shared Relic", "option_id": "sse-relic", "link_id": "el-relic", "amount": 1 }
```
