# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- **Visual debug mode** (`NR_VISUAL=true`) — after setup, navigates the browser to the
  roster editor page so the NR UI visually reflects roster state. Invaluable for debugging
  spec failures and demoing the conformance suite with `NR_HEADLESS=false`.
- **SlowMo support** (`NR_SLOW_MO=<ms>`) — adds a Playwright SlowMo delay between
  browser actions, making test execution watchable in real-time.
- **`publicationName` assertion field** on selections — engines now resolve publication
  references and expose the publication's `name`, proving the link was actually resolved
  (not just echoing back the `publicationId` from XML input).
- **`constraint-percent-value-at-limit` spec** — demonstrates percentValue constraint
  at the exact 50% boundary, showing error when over and no error when at limit.
- **`format-specs.ps1` formatting script** in `tools/` — auto-fixes spec YAML
  formatting (blank lines, trailing whitespace, newlines). Run with `-Check` to verify.
- **SpecLintTests** — 11 lint rules enforcing spec YAML formatting conventions.
- **Engine difference tags** on 36 specs classifying behavioral divergences:
  `battlescribe-bug`, `newrecruit-bug`, `newrecruit-missing-feature`,
  `design-difference`, `undefined-behavior`.
- **Positive `instanceOf` specs** demonstrating working scope+childId combinations:
  - `condition-instance-of-self` — scope=self, childId=entry ID
  - `condition-instance-of-self-type` — scope=self, childId=type name
  - `condition-instance-of-self-category` — scope=self, childId=category ID
- **`instanceOf` scope × childId compatibility table** in coverage report and behavioral
  differences docs. Key finding: instanceOf works with self/parent/ancestor scope but NOT
  force (resolves to Force, not Selection) or roster (hardcoded false in c.java:1196-1197).
- **`modifier-field-constraint-value` enhanced** — now selects 3 times to verify the
  constraint value change from max=2 to max=5 is actually observable (no error at 3).

### Fixed

- **Oracle `percentValue` wiring** — `JavaModelFactory.CreateConstraint` was missing
  the `percentValue` parameter, causing BS engine to treat all percent-based constraints
  as flat limits. Conditions and repeats were unaffected (they already passed it).
  The `constraint-percent-value` spec now correctly passes on both engines.
- **`condition-instance-of-ancestor`** — added `selectChildEntry` step so the child
  entry is actually selected and its ancestor condition evaluates. Was previously
  an untested stub.
- **Removed incorrect "synthetic data" comments** from 6 specs. Investigation of the
  decompiled BS Java engine revealed the real causes: instanceOf scope limitations
  (force/roster), not NR synthetic data loading.

### Changed

- **Per-engine `expectedState` overrides** replace blunt `engines: {engineName: fail}`
  markers on 25 specs. Each spec now describes the *actual* per-engine behavior via
  `expectedState.engines.{engineName}` blocks, keeping all engines passing while
  documenting behavioral differences precisely. Only 1 real-world spec retains
  `newrecruit: fail` (wh40k-10e-space-marines-army) due to fundamental data
  incompatibilities that can't be expressed as state overrides.
- **Before/after coverage** added to 13 condition, modifier, and scope specs that
  previously only tested one side of their conditional behavior. Specs now assert
  both the triggered and non-triggered states, verifying the condition truly controls
  the modifier application.
- **Codebase reorganization** — every project now has its own subdirectory:
  - Moved root `src/BattleScribeSpec.csproj` + 8 .cs files into `src/BattleScribeSpec.Oracle/`.
  - Organized 32 flat test files into 6 subfolders: `Infrastructure/`, `Conformance/`, `Oracle/`,
    `Features/`, `Integration/`, `Regression/`.
  - Updated solution file, project references, Dockerfile paths.
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
- Cost assertions in `SpecRunner` now match by `name` when `typeId` is not specified, allowing
  specs to use human-readable cost names (e.g., `name: "pts"`) instead of requiring UUIDs.
- `IRosterEngine.AddForceByName` signature updated to accept optional `catalogueName` parameter.
- `BattleScribeOracle` data loading switched from direct SimpleXML deserialization to DataUtils
  loader via reflection, with XML pre-processing for compatibility with modern data files.
- `BattleScribeSpec.Oracle.csproj`: `DataUtils` IKVM reference now includes `BattleScribeEngine.jar`
  dependency; added `CommonsIo` IKVM reference.
- **BREAKING:** `RosterState.ValidationErrors` is now `IReadOnlyList<ValidationErrorState>` instead of `IReadOnlyList<string>`.
- **BREAKING:** `IRosterEngine.GetValidationErrors()` returns `IReadOnlyList<ValidationErrorState>`.
- **BREAKING:** Protocol `errors` response now returns structured objects instead of plain strings.
- Oracle error extraction walks the roster tree per-element (mirrors BattleScribe's `rosterManager`).
- Updated `adapter-protocol.md` documentation with structured error format and cost limit convention.

### Removed

- **Dead code cleanup** (~800 lines of C# + 3,170 lines of debug JSON):
  - 4 `Debug_Probe*` exploratory methods from `NrIntegrationTests.cs` (617 lines).
  - 7 unused diagnostic methods from `BattleScribeOracle.cs` (185 lines).
  - `docs/nr-store-dump.json` debug artifact (insights already in `nr-store-mapping.md`).
- **BREAKING:** `IRosterEngine.HasValidationErrors()` — use `GetValidationErrors().Count > 0` instead.
- **BREAKING:** Legacy spec YAML assertion fields removed from the loader model:
  `hasValidationErrors`, `noValidationErrors`, `validationErrors`, `validationErrorCount`,
  `assert`, `expected`. Specs must use the new `errors` array format with `on`/`from` syntax.
  SpecStructureTests now rejects any YAML file still using these fields.
