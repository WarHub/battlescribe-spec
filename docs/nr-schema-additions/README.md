# NewRecruit schema additions over original BattleScribe

NewRecruit (NR) reimplemented the abandoned BattleScribe data engine and then **evolved** the
catalogue/gameSystem data format — adding new enum values, new node types, new child collections,
and new attributes. This directory catalogues those additions.

**Framing:** every NR addition is treated as part of the **base spec**; only the original
BattleScribe engine is excluded/skipped as not supporting it. The baseline we diff against is the
faithful model of original BattleScribe **v2.03** vendored in this repo at `.deps/wham`
(`src/dataformat/xml/schema/v2_03/Catalogue.xsd` + `src/WarHub.ArmouryModel.Source/*Kind.cs`).

> Scope: additive schema surface (this directory) **plus** runtime behavioural divergences, which
> are catalogued separately in [`../nr-behavioral-differences.md`](../nr-behavioral-differences.md)
> and cross-referenced here rather than duplicated.

## Files

| File | Contents |
|------|----------|
| [`new-nodes.md`](new-nodes.md) | New node types / child collections (associations, attributes, formatRules, localConditionGroups, sharedForceEntries …) |
| [`new-enum-values.md`](new-enum-values.md) | New values on existing enums (modifier/condition/conditionGroup/constraint types, selection-entry types) |
| [`new-fields.md`](new-fields.md) | New attributes/fields and query vocabularies (constraint flags, modifier fields, query field/scope sets) |
| [`roster-additions.md`](roster-additions.md) | Roster-instance file-format additions (mostly needs-live-confirmation) |

## How this was discovered (reproducible)

Discovery is automated by the `bs-spec discover` command group (added for this effort), which drives
the frozen NR Editor (`.testdata/nr-editor`, v1.4.6) headlessly via Playwright and dumps machine
artifacts under `artifacts/discover/<specId>/`. Seed: [`tools/discovery/seed.yaml`](../../tools/discovery/seed.yaml).

```sh
bs-spec discover nodes tools/discovery/seed.yaml   # → nodes.json   (creatable node types per context menu)
bs-spec discover enums tools/discovery/seed.yaml   # → enums.json   (every dropdown's option list) + scaffold-*.cat/.gst
bs-spec discover xml   <spec>                       # → <files>.cat/.gst (exact XML NR emits, via NR's own serializer)
```

- **`discover nodes`** right-clicks every tree section + entry and records the context-menu "add"
  items and submenus → which node types the editor can create, and where.
- **`discover enums`** creates one of each selector/type node and dumps every `<select>` option list
  and icon-select/autocomplete vocabulary (the modifier-type dropdown is gated on the chosen field,
  so it is captured once per field data-type). It then exports the built-up catalogue to XML
  (`scaffold-*.cat`) to recover exact element/attribute names.
- **`discover xml`** captures the real `.cat`/`.gst` a spec's data serialises to, by calling NR's
  bundled `convertToXml` in-page (the editor's own "Download" serialiser) with the Electron file
  bridge stubbed so the bytes are captured instead of written to disk.

Why driving the editor (not reading source): NR's model + serializer live in the **private**
`giloushaker/nr-shared` submodule, which we cannot read. The running editor's bundle contains the
compiled serializer and renders every dropdown, so it is the authoritative source.

## Evidence & status convention

Each record cites: the **NR evidence** (CLI artifact, and/or readable nr-editor UI component at
`D:\repos\nr-editor`), the **baseline absence** (XSD line / `*Kind.cs` — the value/element is absent
there), and a **status**:

- **confirmed** — captured directly by a `discover` artifact from the running editor (≥1 structural
  source) and absent from the baseline. Most records.
- **uncertain (source-only)** — seen in the readable nr-editor UI components but not yet captured by
  a `discover` artifact (e.g. type-definition panels the UI dump did not reach). Needs a live-confirm
  pass; tracked as the backlog at the bottom of each file.

## Baseline (original BattleScribe v2.03) — for reference

From `.deps/wham/src/WarHub.ArmouryModel.Source/*Kind.cs` (the closed sets NR extends):

| Enum | Baseline values |
|------|-----------------|
| `ModifierKind` | set, increment, decrement, append, add, remove, set-primary, unset-primary |
| `ConditionKind` | lessThan, greaterThan, equalTo, notEqualTo, atLeast, atMost, instanceOf, notInstanceOf |
| `ConditionGroupKind` | and, or |
| `ConstraintKind` | min, max |
| `SelectionEntryKind` | unit, model, upgrade |
| `EntryLinkKind` | selectionEntry, selectionEntryGroup |
| `InfoLinkKind` | infoGroup, profile, rule |
| `CatalogueLinkKind` | catalogue |

Baseline shared root collections: `sharedSelectionEntries`, `sharedSelectionEntryGroups`,
`sharedProfiles`, `sharedRules`, `sharedInfoGroups` (note: **no** `sharedForceEntries`,
**no** `sharedAssociations`).

## Coverage / completeness

The audit walked every baseline node type / enum (the spine in the plan). Automated capture is
complete for: selection-entry types, constraint/condition/conditionGroup/modifier types, modifier
`field` vocabulary, query `field`/`scope` vocabularies, and the `associations` node (exact XML).
Outstanding `uncertain` items (type-definition panel additions: `attributeTypes`, `formatRules`,
characteristicType `kind`/`defaultValue`; `localConditionGroups`; constraint `message`; the
roster-format axis) are listed per-file as the live-confirm backlog — they were not reachable by the
current seed/driver path (the game-system type-def add-paths and roster runtime are next).

## Encoded as executable specs (Phase 2 — complete for the catalogue/gameSystem axis)

The NR-superset is now modelled end-to-end (wham AST + `CatXmlGenerator` + Protocol + both NR
engines' read paths) and exercised by specs under `specs/gamedata/nr/`, each with
`engines: { battlescribe: skip, battlescribe-ui: skip }` and **passing on both `newrecruit` and
`newrecruit-ui`**:

| Spec | Additions covered |
|------|-------------------|
| `association-and-exactly` | `associations`/`association` node; constraint `exactly` (note: NR serializes it to a min+max pair on export) |
| `selection-entry-types` | entry types `unit-group`, `mount`, `crew` |
| `type-def-additions` | `attributeType`; characteristicType `defaultValue`; profile/characteristic `kind` |
| `modifier-local-condition-group` | `localConditionGroup` node; constraint `negative`/`automatic` |
| `condition-group-types` | conditionGroup kinds `not`, `count`, `greaterOrEqual` (representative of the boolean/numeric/comparison set) |
| `modifier-extended-types` | modifier types `multiply`, `divide`, `cumulative-add`, `prepend`, `replace` |
| `condition-types` | condition types `always`, `never` |
| `condition-before` | positional condition type `before` (only valid inside a `localConditionGroup`) |
| `query-vocab` | query `field=associations`, `scope=root-entry`, `field=limit::<costType>` |
| `shared-collections` | catalogue-root `sharedForceEntries`, `sharedAssociations` |

All enum vocabularies (modifier/condition/conditionGroup/constraint/selection-entry types) are added
to the wham `*Kind.cs` enums and the spec/protocol JSON schemas; `CatXmlGenerator` maps them via a
generic `[XmlEnum]` reflection helper.

All confirmed **gamedata/catalogue** additions are now encoded and green on both NR engines —
including the positional condition `before` (`condition-before`, serialises as `type="before"` on the
`<localConditionGroup>`) and `field=limit::<costType>` (`query-vocab`, serialises verbatim as
`field="limit::pts"`), both confirmed byte-for-byte via `discover xml`.

The **roster-format** (`.ros`) axis has been audited (see [`roster-additions.md`](roster-additions.md)):
there are **no confirmed `.ros` format additions** — `customName`/`customNotes` are baseline original
BattleScribe roster format (the NR difference is behavioural and already covered), and runtime ops like
`duplicateForce` are exercised by `specs/roster/protocol/protocol-kitchen-sink.yaml`. The one open
question (whether roster *selections* serialise chosen-association state) is deferred with reason — it
needs a live army-builder export spike, as the catalogue `discover` trick drives the editor, not the
army builder.

wham changes are committed upstream (PR WarHub/wham#308) and `.deps/wham` is pinned to that branch;
re-pin to the merged wham SHA before the parent PR (WarHub/battlescribe-spec#267) merges.

## Remaining

The catalogue/gameSystem axis is fully encoded, modelled, and green on both NR engines. What is left:

- **Re-pin `.deps/wham`** to the merged wham SHA once WarHub/wham#308 lands (currently pinned to its
  branch), then the parent PR can merge.
- **Roster association-state spike** (the single deferred `.ros` question) — see
  [`roster-additions.md`](roster-additions.md). Only worth pursuing if NR is found to serialise
  chosen-association state on roster selections; everything else on the roster axis is either baseline
  format or a behavioural difference already covered.
