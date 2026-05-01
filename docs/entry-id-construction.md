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

For full details on how NR serializes rosters to various export formats, see
[nr-export-internals.md](nr-export-internals.md).

#### Adapter implications

The adapter uses `sel.getBattleScribePath()` instead of `sel.getId()` to produce
composite IDs matching the BattleScribe engine. This is NR's own native API — the same
one used by their `.ros` export — so using it is not "cheating" or synthesizing IDs.

All 15 entry-id specs now pass on both BattleScribe and NewRecruit engines.
