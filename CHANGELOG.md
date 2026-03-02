# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- `ValidationErrorState` record with `Message`, `OwnerType`, `OwnerId`, `OwnerEntryId`, `EntryId`, and `ConstraintId` fields.
- `ProtocolValidationError` structured type in the adapter protocol.
- `ExpectedValidationErrorDef` for structured error matching in spec YAML files.
- Entry/constraint ID enrichment in `OracleRosterEngine` via catalogue spec correlation.
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
- **BREAKING:** Spec YAML `validationErrors` field accepts structured matchers (`message`, `entryId`, `constraintId`, `ownerType`, `ownerEntryId`).
- Updated `adapter-protocol.md` documentation with new error format.

### Removed

- **BREAKING:** `IRosterEngine.HasValidationErrors()` — use `GetValidationErrors().Count > 0` instead.
