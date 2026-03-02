# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- `ValidationErrorState` record with `Message`, `OwnerType`, `OwnerId`, `OwnerEntryId`, `EntryId`, and `ConstraintId` fields.
- `ProtocolValidationError` structured type in the adapter protocol.
- Compact `errors` assertion format in spec YAML using `on`/`from`/`message` syntax:
  ```yaml
  errors:
    - on: category cat-troops       # ownerType + ownerEntryId
      from: se-unit-a/con-min-1     # entryId/constraintId
    - on: roster
      from: costLimits/ct-pts       # cost limit pseudo-entry
    - on: category cat-troops
      from: se-unit-a/hidden        # hidden entry pseudo-constraint
  ```
- `errors: []` asserts no validation errors; omitting `errors` skips the check.
- `from` format is `{entryId}/{constraintId}` with reserved pseudo-values:
  - `costLimits/{costTypeId}` — cost limit errors (pseudo-entry)
  - `{entryId}/hidden` — hidden entry errors (pseudo-constraint)
- 9 new spec YAML files for structured validation error assertions:
  `constraint-min-violation-linked`, `constraint-max-violation-linked`,
  `constraint-cost-max-linked`, `constraint-cost-min-linked`,
  `constraint-cost-limit-linked`, `constraint-hidden-violation-linked`,
  `constraint-shared-linked`, `constraint-multiple-errors-linked`,
  `constraint-min-on-force-linked`.

### Changed

- **BREAKING:** `RosterState.ValidationErrors` is now `IReadOnlyList<ValidationErrorState>` instead of `IReadOnlyList<string>`.
- **BREAKING:** `IRosterEngine.GetValidationErrors()` returns `IReadOnlyList<ValidationErrorState>`.
- **BREAKING:** Protocol `errors` response now returns structured objects instead of plain strings.
- Oracle error extraction walks the roster tree per-element (mirrors BattleScribe's `rosterManager`).
- Updated `adapter-protocol.md` documentation with structured error format and cost limit convention.

### Removed

- **BREAKING:** `IRosterEngine.HasValidationErrors()` — use `GetValidationErrors().Count > 0` instead.
