# NR roster-format additions

Additions to the **roster-instance** file format (`.ros`), as opposed to the catalogue/gameSystem
authoring format covered by the sibling files. This axis is **mostly needs-live-confirmation**: the
`discover` commands drive the NR **Editor** (catalogue authoring), not the army builder, so
roster-format additions surface at runtime rather than in the editor UI.

Status: **backlog**. Confirm each by building a roster in NR, exporting `.ros`, and diffing element/
attribute names against the baseline roster schema in `.deps/wham/.../v2_03/Catalogue.xsd`
(roster types: Roster, Force, Selection, Category, Cost, CostLimit, RosterTag, RosterElementBase).

## Known so far

- **`customName` / customNotes** on selections/forces — NR premium feature (custom display names &
  notes). Surfaced in the roster engine adapter (`IRosterEngine.SetCustomization`,
  `src/BattleScribeSpec.NewRecruit`) where original BattleScribe throws `NotSupportedException`.
  This is recorded as a runtime/behavioural difference in
  [`../nr-behavioral-differences.md`](../nr-behavioral-differences.md); whether NR also serialises a
  new roster attribute for it needs `.ros` confirmation.
- **associations on selections** — the catalogue-level `associations` node (see
  [`new-nodes.md`](new-nodes.md)) implies association state on roster selections; confirm the `.ros`
  serialisation.

## How to confirm (next)

1. Build a roster in the NR army builder exercising the feature.
2. Export `.ros` (NR's own serializer) — extend `bs-spec discover xml` to the roster engine, or use
   the editor's download path as the catalogue side does.
3. Diff element/attribute names vs the baseline roster schema.

No roster-format additions are **confirmed** in this pass; the catalogue/gameSystem additions in the
sibling files are the verified body of work.
