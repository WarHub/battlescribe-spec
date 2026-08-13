---
name: writing-specs
description: >
  Write or edit BattleScribe YAML spec files. Use when creating new conformance specs,
  fixing spec assertions, or adding test coverage for BattleScribe engine features.
  Covers setup data (gameSystem/catalogue), actions, and expectedState assertions.
---

# Writing BattleScribe Specs

## Workflow

1. Create `specs/{category}/{spec-id}.yaml`
2. Run: `dotnet test tests/BattleScribeSpec.Tests.csproj --filter "DisplayName~{spec-id}"`
3. Fix failures and re-run until BattleScribe passes
4. Run: `pwsh -File tools/format-specs.ps1`
5. Run: `dotnet test tests/BattleScribeSpec.Tests.csproj --filter "DisplayName~SpecLint"`

## Spec structure

```yaml
id: my-spec-id              # MUST match filename without .yaml
category: constraint         # MUST match parent directory name
description: What this tests
tags: []                     # MUST use known tags (see references/KNOWN-TAGS.md)
engines:                     # optional: per-engine expectations
  newrecruit: fail           # pass (default) | fail | skip

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
| `duplicateForce` | `forceId` | `forceId` |
| `setCostLimit` | `costTypeId`, `value` | — |
| `dump` | (none) — triggers state dump in debugger; no-op in test runner | — |

### Step ID and output expressions

Steps that need their outputs referenced must have an `id` field. Later steps use
`${{ steps.<id>.<field> }}` to refer to outputs like `forceId`, `selectionId`, or
entries in the `selections` map (e.g., `${{ steps.add-patrol.selections.se-required }}`).

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
A literal second token is the pre-#423 entry-addressed form, still accepted while the corpus
migrates (#424) — do not write new ones. Full contract: `docs/error-assertions.md`.

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

## Reference files

- [PROTOCOL-TYPES.md](references/PROTOCOL-TYPES.md) — all setup data types and their fields
- [KNOWN-TAGS.md](references/KNOWN-TAGS.md) — allowed tag values
- `specs/protocol/protocol-kitchen-sink.yaml` — comprehensive example exercising all types
