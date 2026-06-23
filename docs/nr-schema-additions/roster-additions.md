# NR roster-format additions

Additions to the **roster-instance** file format (`.ros`), as opposed to the catalogue/gameSystem
authoring format covered by the sibling files. The `discover` commands drive the NR **Editor**
(catalogue authoring), not the army builder, so this axis is confirmed by auditing the modelled roster
schema + the runtime roster specs rather than by an editor dump.

## Audit result: no confirmed `.ros` *format* additions

The baseline roster schema (`.deps/wham/src/dataformat/xml/schema/v2_03/Catalogue.xsd`, roster types
`Roster` / `Force` / `Selection` / `Category` / `Cost` / `CostLimit` / `RosterTag` /
`RosterElementBase`) already covers everything NR is currently observed to write. The items previously
listed here as candidate additions are **not** format additions:

- **`customName` / `customNotes`** — **baseline, not an addition.** Both are in the original v2.03
  roster schema: `customName` is an attribute on `RosterElementBase`
  (`v2_03/Catalogue.xsd:610` → forces/selections/categories) and `customNotes` is an element on
  `RosterElementBase` (`:602`) and `Roster` (`:732`). What is NR-specific is **behavioural**, not
  structural: NR's army builder lets you *set* custom names/notes (a premium feature), whereas the
  original BattleScribe app exposes no UI control and `IRosterEngine.SetCustomization` throws
  `NotSupportedException` on the `battlescribe` engine. That divergence is catalogued in
  [`../nr-behavioral-differences.md`](../nr-behavioral-differences.md) and **exercised** by
  `specs/roster/protocol/protocol-kitchen-sink.yaml` (the `setCustomization` steps: custom name/notes
  on a selection, a force, and a category, with per-engine `expectedState` overrides — e.g. NR ignores
  force-level `customNotes` as there is no UI control for it). No new attribute is serialised.

- **`duplicateForce` / runtime ops** — runtime army-building operations, not format. Already driven in
  `protocol-kitchen-sink.yaml` (`addForce`, `addChildForce`, `removeForce`, `duplicateForce`,
  `selectEntry`, `setCount`, `setCostLimit`, `setCustomization`) with `skipEngines` where BattleScribe
  diverges. No `.ros` element is added.

## Single open question (deferred — needs a live army-builder capture)

The catalogue-level `associations` / `sharedAssociations` nodes (see [`new-nodes.md`](new-nodes.md))
define association *rules*. The one unconfirmed `.ros`-format question is whether a roster **selection**
serialises chosen-association *state* (e.g. an `associations`/`associationLink` element on
`<selection>` pointing at the associated selection) — original BattleScribe has no such concept.

This cannot be confirmed with the existing tooling, and is **deferred** with that reason:

- The catalogue `discover xml` electron-stub trick (capture `convertToXml` via a stubbed
  `globalThis.electron`) drives the NR **Editor** bundle. The roster serializer lives in the separate
  **army builder** app (`www.newrecruit.eu`, driven by `NrRosterUiEngine` / `NewRecruitRosterEngine`),
  whose roster→XML path is **not** currently reachable from the engine (`window.__bsspec.army` exposes
  read accessors like `getForces()`, but no `serialize()`/save path is wired up).
- Confirming it requires a roster-side discovery spike: drive the army builder, build a roster that
  selects an association (needs a catalogue that defines one, and likely a premium account), locate the
  roster export/`saveList` path in the live bundle, and capture the `.ros` bytes (the army-builder
  analogue of `ExportLoadedFilesJsonAsync`). Then diff `<selection>` element/attribute names vs the
  baseline `Selection` type. Add as `bs-spec discover ros <spec>` if a path is found.

If that spike confirms roster-side association state, encode it under `specs/roster/nr/` with
`engines: { battlescribe: skip, battlescribe-ui: skip }`, both NR engines green. Until then there is
nothing to encode: the roster runtime additions are behavioural (covered) and the format is baseline.
