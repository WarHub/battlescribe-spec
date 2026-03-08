# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Changed

- **Codebase reorganization** — every project now has its own subdirectory:
  - Moved root `src/BattleScribeSpec.csproj` + 8 .cs files into `src/BattleScribeSpec.Oracle/`.
  - Organized 32 flat test files into 6 subfolders: `Infrastructure/`, `Conformance/`, `Oracle/`,
    `Features/`, `Integration/`, `Regression/`.
  - Updated solution file, project references, Dockerfile paths.

### Removed

- **Dead code cleanup** (~800 lines of C# + 3,170 lines of debug JSON):
  - 4 `Debug_Probe*` exploratory methods from `NrIntegrationTests.cs` (617 lines).
  - 7 unused diagnostic methods from `BattleScribeOracle.cs` (185 lines).
  - `docs/nr-store-dump.json` debug artifact (insights already in `nr-store-mapping.md`).

### Changed

- **DTO layer reduction** — eliminated 3 redundant model layers (~2,810 lines removed):
  - Deleted `SpecModels.cs` — all `*Spec` record types replaced by Protocol classes.
  - Deleted `ProtocolConverter.cs` — all conversion/mapping code eliminated.
  - Protocol setup types (`ProtocolGameSystem`, `ProtocolCatalogue`, etc.) are now the canonical model
    for YAML deserialization, JSON wire format, and engine input.
  - `EngineTypes.cs` records (`RosterState`, `ForceState`, `SelectionState`, etc.) now serve as both
    runtime state and JSON wire format (added `[JsonPropertyName]` attributes).
  - Deleted 7 Protocol state classes (`ProtocolForce`, `ProtocolSelection`, `ProtocolCost`, etc.)
    and 4 snapshot types (`RosterSnapshot`, `ForceSnapshot`, etc.).
  - `IRosterEngine.Setup()` now takes `(ProtocolGameSystem, ProtocolCatalogue[])` directly.

### Added

- **DataSource spec support** for the BattleScribe oracle engine:
  - `OracleRosterEngine` implements `SetupFromFiles`, `AddForceByName`, `SelectEntryByName`, `SelectChildEntryByName`.
  - `DataSourceResolver` wired into `SpecConformanceTests` for resolving `github:` data source references.
  - XML pre-processing adds missing `value=""` attributes on `<modifier>` elements to work around
    SimpleXML `@Attribute(required=true)` validation on newer data files.
  - Data loading uses DataUtils via reflection to avoid IKVM namespace collision (CS0434).
- `catalogueName` field on spec YAML step actions — resolves faction catalogue by name for `addForce` in DataSource mode.
- `commons-io-2.4.jar` IKVM dependency required by DataUtils.
- 2 real-world wh40k-10e specs using `dataSource: "github:BSData/wh40k-10e@v10.6.0"`:
  - `wh40k-10e-create-army` — minimal smoke test (force creation without catalogue)
  - `wh40k-10e-space-marines-army` — rich multi-step spec: auto-selections, Captain + Intercessor
    Squad with progressive type, cost, and selection count assertions

### Changed

- Cost assertions in `SpecRunner` now match by `name` when `typeId` is not specified, allowing
  specs to use human-readable cost names (e.g., `name: "pts"`) instead of requiring UUIDs.
- `IRosterEngine.AddForceByName` signature updated to accept optional `catalogueName` parameter.
- `BattleScribeOracle` data loading switched from direct SimpleXML deserialization to DataUtils
  loader via reflection, with XML pre-processing for compatibility with modern data files.
- `BattleScribeSpec.Oracle.csproj`: `DataUtils` IKVM reference now includes `BattleScribeEngine.jar`
  dependency; added `CommonsIo` IKVM reference.

### Added

- `ValidationErrorState` record with `Message`, `OwnerType`, `OwnerId`, `OwnerEntryId`, `EntryId`, and `ConstraintId` fields.
- `ValidationErrorState` structured type in the adapter protocol state responses.
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
