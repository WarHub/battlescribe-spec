# NR new node types & child collections

New nodes/collections NewRecruit added that do not exist in original BattleScribe v2.03.
Baseline absence verified against `.deps/wham/.../v2_03/Catalogue.xsd` (0 hits for the element name)
unless noted. Evidence artifacts under `artifacts/discover/discovery-seed/`.

## associations / association  — **confirmed**

A new child collection on entries (and a new shared root collection), modelling relationships
between selections.

- **Parent(s):** selectionEntry / selectionEntryGroup (child `<associations>`); catalogue/gameSystem
  root (`<sharedAssociations>`).
- **Exact XML** (from `scaffold-cat-1.cat`):
  ```xml
  <associations>
    <association min="1" max="1" scope="parent" childId="any" name="Probe Association" id="…"/>
  </associations>
  ```
- **Attributes:** `min`, `max`, `scope`, `childId`, `name`, `id` (and a query — `field`/`label`/`ids`
  per source).
- **Evidence:** `nodes.json` — "Association" appears as a child-add item on a selectionEntry and as
  the "Shared Associations" root section; `scaffold-cat-1.cat` — exact serialised XML.
- **Baseline:** `association` → 0 hits in v2.03 XSD.

## associationLinks / associationLink  — **confirmed (existence)**

A link node targeting an association (parallel to entryLink/infoLink).

- **Evidence:** `nodes.json` — the entry "Link ❯" submenu lists **Association** alongside
  Entry/Group/Profile/Rule/InfoGroup.
- **XML name:** `<associationLinks>` (inferred by analogy; not yet captured — create one and
  `discover xml` to confirm). Backlog.
- **Baseline:** no association link kind in `*Kind.cs` / XSD.

## sharedForceEntries  — **confirmed (existence)**

A new shared root collection for force entries (baseline has shared collections only for selection
entries/groups, profiles, rules, info groups).

- **Evidence:** `nodes.json` — root section "Shared Force Entries" with a "Force" add item.
- **Baseline:** `sharedForceEntries` → 0 hits in v2.03 XSD.

## sharedAssociations  — **confirmed (existence)**

New shared root collection for associations (see `associations` above).

- **Evidence:** `nodes.json` — root section "Shared Associations".
- **Baseline:** absent (baseline shared set listed in README).

## attributeType  — **confirmed**

A parallel to characteristicType (on profileType), used to carry export-only data.

- **Parent:** profileType (child-add item "Attribute Type", sibling of "Characteristic Type" —
  `nodes.json` profileType child menu).
- **Exact XML** (`scaffold-cat-1.cat`):
  ```xml
  <profileType …>
    <attributeTypes>
      <attributeType name="Probe Attr Type" id="…"/>
    </attributeTypes>
  </profileType>
  ```
- **Baseline:** no `attributeType` (the 92 XSD "attribute" hits are `<xs:attribute>` keywords).
- **Note:** the `attributes` *instance* node (on a profile, parallel to characteristics) is implied
  but not yet round-tripped — set one on a profile + `discover xml`. Small backlog.

## localConditionGroup / localConditionGroups  — **confirmed**

A distinct NR node (not baseline `conditionGroups`): a child of a **modifier** carrying a full query
+ repeats (its own condition logic with a scope/field/childForces query).

- **Parent:** modifier (child-add item "Local Condition Group" — `nodes.json` modifier child menu).
- **Exact XML** (`scaffold-cat-1.cat`):
  ```xml
  <localConditionGroups>
    <localConditionGroup type="atLeast" value="1" scope="parent" field="selections"
      includeChildSelections="true" includeChildForces="true" repeats="1"/>
  </localConditionGroups>
  ```
- **Baseline:** `localConditionGroup` → 0 hits in v2.03 XSD. Editor source: `editorStore.ts:1203`.

## formatRules / formatRule  — **uncertain (source-only)**

Regex match/replace rules attached to a characteristicType/profileType (output formatting).

- **Evidence:** `CharacteristicType.vue:33` mounts `…FieldsFormatRules`; `FormatRules.vue` component.
- **Baseline:** `formatRule` → 0 hits in v2.03 XSD.
- **Type-def editors now reachable** via the catalogue's own Profile Types section; the
  characteristicType/profileType `kind` enum is confirmed (see [`new-fields.md`](new-fields.md)).
  Applying a format rule + `discover xml` to capture the `<formatRules>` element is the remaining step.

---

### Backlog (live-confirm) — small residual, capture during encoding (WS4)

- `associationLink` exact XML (link to an association; add via the entry "Link ❯" → Association).
- `attributes` *instance* node on a profile (attributeType is confirmed).
- `formatRules` applied-rule XML (apply a preset, then `discover xml`).
- characteristicType `defaultValue` serialized XML (set it, then `discover xml`).
- constraint `negative`/`automatic`/`message` serialized XML (set them, then `discover xml`).
- roster `.ros` additions (needs the roster-engine export path — the roster encoding pass).
