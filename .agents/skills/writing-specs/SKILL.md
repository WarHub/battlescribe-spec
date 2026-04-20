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
3. Fix failures and re-run until Oracle passes
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
  - action: addForce
    forceEntryIndex: 0

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

| Action | Key params |
|--------|-----------|
| `addForce` | `forceEntryIndex` or `forceEntryName`. Optional: `forcePath` (parent), `catalogueIndex` |
| `selectEntry` | `forceIndex` or `forcePath`, `entryIndex` or `entryName` |
| `selectChildEntry` | `forceIndex`/`forcePath`, `selectionIndex`/`selectionPath`, `childEntryIndex` or `childEntryName` |
| `deselectSelection` | `forceIndex`/`forcePath`, `selectionIndex`/`selectionPath` |
| `setSelectionCount` | `forceIndex`/`forcePath`, `selectionPath` (≥2 elements), `count`. Rejects root selections (selectionPath < 2) — lint rule enforced |
| `duplicateSelection` | `forceIndex`/`forcePath`, `selectionIndex`/`selectionPath` |
| `setCostLimit` | `costTypeId`, `value` |
| `removeForce` | `forceIndex` or `forcePath` |
| `dump` | (none) — triggers state dump in debugger; no-op in test runner |

### Path-based addressing (nested forces/selections)

Use `forcePath` and `selectionPath` (integer arrays) instead of `forceIndex`/`selectionIndex`
to target nested forces or selections:

```yaml
# Add a child force under force 0
- action: addForce
  forcePath: [0]
  forceEntryIndex: 0

# Select into a child force (child 0 of force 0)
- action: selectEntry
  forcePath: [0, 0]
  entryIndex: 0

# Deselect a nested selection (child 1 of selection 0)
- action: deselectSelection
  forcePath: [0]
  selectionPath: [0, 1]
```

Legacy `forceIndex: N` / `selectionIndex: N` are converted to `[N]` automatically.

## Error assertions

```yaml
errors:          # exact match — every error must be listed, no extras allowed
  - on: category cat-troops
    from: se-unit/con-min-1

errorsContain:   # subset match — extra actual errors are OK
  - on: roster
    from: costLimits/ct-pts

errorCount: 3    # just count, no specifics
```

`on` format: `{ownerType}` or `{ownerType} {ownerEntryId}` — e.g. `roster`, `force`,
`category cat-troops`, `selection se-unit`.

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
