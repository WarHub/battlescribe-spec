---
name: writing-specs
description: >
  Write or edit BattleScribe YAML spec files. Use when creating new conformance specs,
  fixing spec assertions, or adding test coverage for BattleScribe engine features.
  Covers setup data (gameSystem/catalogue), actions, and expectedState assertions.
---

# Writing BattleScribe Specs

## Workflow

1. Create `specs/roster/{category}/{spec-id}.yaml` (a GameData spec goes under `specs/gamedata/`, a
   scenario spec — data editor and roster editor in one run — under `specs/scenario/`; see below)
2. Run it with the one-spec command in [AGENTS.md, "Build & test"](../../../AGENTS.md#build--test), your spec id
   in place of `my-spec-id` (what it runs: [docs/running-tests.md](../../../docs/running-tests.md#selecting-tests-by-name))
3. Fix failures and re-run until BattleScribe passes
4. Run: `pwsh -File tools/format-specs.ps1`
5. Run: `dotnet test --project tests/BattleScribeSpec.Tests.csproj --filter "DisplayName~SpecLint"`

Each domain has a JSON Schema that VS Code applies to its folder as you type (`.vscode/settings.json`):
`docs/roster-spec-schema.json`, `docs/gamedata-spec-schema.json` and `docs/scenario-spec-schema.json`, the
last reusing the other two by `$ref`. They refuse unknown fields and unknown actions, as the loaders do;
`SpecSchemaTests` validates every spec against its schema. Adding a field to a spec model, or an action to
`SpecValidator`'s table or `GameDataActions`, means adding it to the schema too — `SpecSchemaDriftTests` fails
until you do.

## Spec structure

```yaml
id: my-spec-id              # MUST match filename without .yaml
category: constraint         # MUST match parent directory name
description: What this tests
tags: []                     # MUST use known tags (see references/KNOWN-TAGS.md)
engines:                     # optional: per-engine expectations
  newrecruit: fail           # pass (default) | fail | skip — covers newrecruit-ui too (see below)

setup:
  gameSystem:
    id: test-gs
    name: Test GS
    # See references/PROTOCOL-TYPES.md for all fields
  catalogues:
    - id: cat-1
      name: Cat
      gameSystemId: test-gs

steps:
  - id: add-patrol
    action: addForce
    forceEntryId: fe-1

  - action: selectEntry
    forceId: ${{ steps.add-patrol.forceId }}
    entryId: se-1

  - expectedState:           # MUST be the last step
      forces:
        - selectionCount: 1
```

## Critical assertion rules

**Selections match by INDEX, not name.** List expected selections in exact roster order.
Auto-selected entries (min≥1 constraint) appear first.

**Profiles, rules, categories** match by **name** if specified.

**Costs** match by **typeId** if specified, then by **name**.

**Omitted fields are not checked.** Only non-null fields are asserted.

**Errors default to zero.** Any `expectedState` without explicit error fields asserts 0 errors.
If the roster has validation errors at that point, the step fails. Use `errorsContain` or
`errors` to assert expected errors.

## Actions

All addressing is **ID-based**. Definition IDs (e.g., `forceEntryId`, `entryId`) come from
setup data. Instance IDs (e.g., `forceId`, `selectionId`) are returned as step outputs
and referenced via `${{ steps.<id>.<field> }}`.

| Action | Key params | Outputs |
|--------|-----------|---------|
| `addForce` | `forceEntryId`. Optional: `catalogueId` | `forceId`, `selections` |
| `addChildForce` | `forceId`, `forceEntryId`. Optional: `catalogueId` | `forceId`, `selections` |
| `removeForce` | `forceId` | — |
| `selectEntry` | `forceId`, `entryId` | `selectionId`, `selections` |
| `selectChildEntry` | `forceId`, `selectionId`, `entryId` | `selectionId`, `selections` |
| `deselectSelection` | `forceId`, `selectionId` | — |
| `setSelectionCount` | `forceId`, `selectionId`, `count` | — |
| `duplicateSelection` | `forceId`, `selectionId` | `selectionId` |
| `duplicateForce` | `forceId` | `forceId`, `categories` |
| `setCostLimit` | `costTypeId`, `value` | — |
| `dump` | (none) — triggers state dump in debugger; no-op in test runner | — |

### Step ID and output expressions

Steps that need their outputs referenced must have an `id` field. Later steps use
`${{ steps.<id>.<field> }}` to refer to outputs like `forceId`, `selectionId`, or
entries in the `selections` map (e.g., `${{ steps.add-patrol.selections.se-required }}`).

`selections` and `categories` are keyed by **catalogue entry id**, and each key holds every node the
step minted from that entry, in roster order. The bare form is the first; add `[n]` for the rest —
`${{ steps.add-patrol.selections.se-unit-a[1] }}` is the second Unit A a `min: 2` auto-add created.
An index past the end fails and says how many there are.

```yaml
# Add a force and name the step
- id: add-patrol
  action: addForce
  forceEntryId: fe-patrol

# Use the force ID from the previous step
- id: select-unit
  action: selectEntry
  forceId: ${{ steps.add-patrol.forceId }}
  entryId: se-infantry

# Use auto-selected child's selection ID
- action: setSelectionCount
  forceId: ${{ steps.add-patrol.forceId }}
  selectionId: ${{ steps.select-unit.selections.se-trooper }}
  count: 3

# Add a child force under the first force
- action: addChildForce
  forceId: ${{ steps.add-patrol.forceId }}
  forceEntryId: fe-vanguard
```

## Error assertions

```yaml
errors:          # exact match — every error must be listed, no extras allowed
  - on: category ${{ steps.add-patrol.categories.cat-troops }}
    from: se-unit/con-min-1

errorsContain:   # subset match — extra actual errors are OK
  - on: roster
    from: costLimits/ct-pts

errorCount: 3    # just count, no specifics
```

`on` names the roster NODE the engine raised the error on. Node ids are minted per run, so they
are always written as `${{ steps.… }}` references: `force ${{ steps.<id>.forceId }}`,
`category ${{ steps.<id>.categories.<categoryEntryId> }}`,
`selection ${{ steps.<id>.selectionId }}`. `roster` and `group` are written **bare** — neither node
has an id a spec can name. Where the two engines raise on different nodes (they do, on 24 of the 38
assertions both evaluate) the spec records both, base plus an `engines:` block.
A literal second token is the retired pre-#423 entry-addressed form: it names a catalogue ENTRY,
which is a set of nodes rather than one. Both the schema pattern and the linter reject it. Full
contract: `docs/error-assertions.md`.

`from` format: `{entryId}/{constraintId}` — pseudo-values: `costLimits/{costTypeId}`,
`{entryId}/hidden`.

## Engine-specific expectedState overrides

```yaml
- expectedState:
    forces:
      - selections:
          - name: Unit A
            page: "42"
    engines:
      newrecruit:
        forces:
          - selections:
              - name: Unit A
              # page omitted — NR doesn't expose page on selections
```

Non-null fields in the engine override replace the base fields for that engine.

## Which engine entry applies: its own, else its family's

Every per-engine declaration — spec-level `engines:`, the `engines:` overrides on `expectedState`,
`expectFailure`, `expectedFile` and action inputs, a step's `skipEngines`, and `expectedFile` side-files —
is read by **one rule, in every runner and every domain**: the engine's own entry, else its family's. A
UI driver's family is its name without `-ui`, so `battlescribe: skip` covers `battlescribe-ui`, and a
`newrecruit:` override applies on `newrecruit-ui` unless the spec gives `newrecruit-ui` one of its own.

- **Don't restate the family's entry** under the driver's name (`battlescribe-ui: skip` beside
  `battlescribe: skip`). It says nothing, it drifts, and the spec lint refuses it.
- **Do give the driver an entry where it differs.** `pass` is a real value here: `newrecruit-ui: pass`
  under `newrecruit: skip` keeps a spec on the UI driver that the store-direct adapter cannot run
  (`specs/gamedata/load/`). When a UI driver and its store-direct sibling disagree, the UI is the
  normative answer (AGENTS.md, "What is normative") — so before writing a driver entry that records the
  UI *failing* where the store-direct engine passes, check which of the two is wrong.

The rule lives in one place, `EngineIdentity` (`src/BattleScribeSpec.TestKit/EngineIdentity.cs`).

## Scenario specs (data editor + roster editor in one spec)

A scenario spec (`specs/scenario/{category}/{id}.yaml`) follows one engine family's data editor and
roster editor through a single run. Use one when the thing under test is the **hand-off**: an edit
reaching a roster, or a saved roster reopened against changed data. Design:
[docs/adr/002-scenario-specs.md](../../../docs/adr/002-scenario-specs.md).

```yaml
setup:                       # the gamedata setup, verbatim: these files seed the data directory
  edit: cat-1                # the data editor's open file (required)
  gameSystem: {...}
  catalogues: [...]

steps:                       # each item is exactly ONE of: data / roster / action
  - action: newRoster        # roster editor reads the data directory, starts an empty roster
  - roster:                  # roster-spec steps, unchanged vocabulary
      - action: addForce
        id: add-detachment
        forceEntryId: fe-1
  - action: saveRoster       # roster editor saves; `file` names the saved roster
    file: my-list
  - data:                    # gamedata-spec steps, unchanged vocabulary
      - action: setFields
        entryId: se-squad
        costs: {ct-pts: "15"}
  - action: saveData         # data editor saves its OPEN file into the data directory
  - action: openRoster       # roster editor re-reads the data directory, opens `file`
    file: my-list
  - roster:
      - expectedState: {...} # the last item is a block ending in an assertion
```

**The one rule:** the roster editor sees data only as of the last `saveData`, and only at
`newRoster`/`openRoster`. Unsaved edits stay in the data editor. `saveData` saves the open file only;
to save another, `openFile` it in a `data:` block first.

- Step ids are one namespace across the spec; expressions resolve within their editor. A roster step
  names an entry a data step made by its **declared** id (`addEntry` with `entryId:`), not by expression.
- A roster block or `saveRoster` before any `newRoster`/`openRoster` is a spec error.
- Any hand-off can carry `expectFailure` (an `openRoster` the edited data can no longer carry).
- **Migration** (`specs/scenario/migration/`): save a roster, change one thing in the data, reopen it;
  one change per spec ([ADR 003](../../../docs/adr/003-roster-migration.md)).
- **Live reload** (`reloadRoster`, `specs/scenario/reload/`): the one exception to the rule above. After a
  `saveData`, the open roster comes back as it stands — unsaved changes included, not its file — against
  the new data, as BattleScribe's "A file was modified outside" prompt does on YES. It needs a `saveData`
  since the roster editor last read the data, and an open roster (not after an open that may be refused).
  NewRecruit has no live reload: opt it out (`newrecruit: skip`).
- Per-engine overrides use one rule in both kinds of block: the running engine's entry
  (`battlescribe-ui`) wins, else its base engine's (`battlescribe`).
- Expectations come from `battlescribe-ui` (the desktop apps): run the `bs-ui-scenario` profile.

## Reference files

- [PROTOCOL-TYPES.md](references/PROTOCOL-TYPES.md) — all setup data types and their fields
- [KNOWN-TAGS.md](references/KNOWN-TAGS.md) — allowed tag values
- `specs/protocol/protocol-kitchen-sink.yaml` — comprehensive example exercising all types
