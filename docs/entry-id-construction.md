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

The NewRecruit engine does **NOT** implement composite `::` IDs. Specifically:

- **entryId**: NR's `sel.getId()` always returns the target entry's definition ID without
  any link prefixes. Where BS returns `el-weapon::sse-weapon`, NR returns just `sse-weapon`.
- **entryGroupId**: NR does not populate this field at all — it always returns null/empty,
  regardless of whether the entry is inside a group.

#### Internal Structure (from probing)

NR selection nodes have a `selector.ids` array that contains partial link info:

| Scenario | `selector.ids` | BS composite |
|----------|---------------|--------------|
| Direct entry | `["se-unit"]` | `se-unit` |
| 1-hop link target | `["el-weapon","sse-weapon"]` | `el-weapon::sse-weapon` |
| 2-hop chain (inner) | `["el-inner","sse-weapon"]` | `el-outer::el-inner::sse-weapon` |
| Descendant in link target | `["se-ammo"]` | `el-weapon::se-ammo` |
| Entry via group link | `["se-sword"]` | `el-gear::se-sword` |

Key differences from BS:
- `selector.ids` only tracks the **immediate** link, not ancestor chains
- No propagation of link prefixes to descendants inside link targets
- Group links do NOT contribute to `selector.ids`
- No `entryGroupId` equivalent exists anywhere in NR's selection nodes
- Properties `entryId`, `entryGroupId`, `linkId`, `targetId`, `compositeId` are all
  `undefined` on NR selection objects — they are not part of NR's data model

The adapter faithfully reports what NR natively produces (`sel.getId()`), without
synthesizing composite IDs from `selector.ids`.

#### Export Format Investigation

NR does **not** export BattleScribe-format `.ros`/`.rosz` XML. Its save/export capabilities:

- **`toJsonObject()`** — serializes roster as JSON. Entries are stored with `option_id` (plain
  target definition ID) and a separate `link_id` field when accessed through a link. Example:
  ```json
  { "name": "Shared Relic", "option_id": "sse-relic", "link_id": "el-relic", "amount": 1 }
  ```
  There is no `::` composition — the link and target IDs are stored as separate fields.

- **`exportArmy(format)`** — produces human-readable text exports only. Available formats:
  `GW`, `Tournament`, `NR`, `SHORT` — all produce identical HTML text summaries, not XML.

- **No `.rosz` export** — `listsStore` has `importBs` (to import BattleScribe files) but no
  export-to-BS method. Army has 234 methods; none relate to file/download/blob/zip/rosz.

This confirms NR's data model fundamentally stores link and entry IDs separately rather
than composing them with `::`, making composite ID assertions impossible on this engine.

All entry-id specs that assert composite IDs or entryGroupId are marked
`engines: newrecruit: skip`. The two specs with plain assertions (`entry-id-direct`,
`entry-id-shared-entry-child`) run on both engines.
