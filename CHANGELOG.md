# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- **Cross-engine roster export byte-compare** — roster specs now support `expectedFile`
  (mirroring gamedata), byte-comparing an engine's exported `.ros` XML against a
  per-engine snapshot. Adds `IRosterEngine.ExportRosterXml()` for **both** engines —
  BattleScribe via DataUtils `a(Roster, OutputStream)`; NewRecruit (store-direct) by
  invoking its `exportRos` serializer and capturing the `Blob`; NewRecruit UI by clicking
  the real **Export → .ros** toolbar button with the download mocked (Blob hook + swallowed
  anchor click) — locking each engine's exact roster serialization incl. non-integer cost
  formatting (BattleScribe writes `value="320.0"`, NewRecruit `value="320"`). Per-run
  instance ids are templated rather than blanket-wildcarded: ids a step produced resolve to
  `${{ steps.<id>.forceId|selectionId }}` references, and the remainder (roster/category ids)
  to a `${{ match('…') }}` regex (single-quoted, quote-free — the snapshot stays well-formed
  XML) — so snapshots stay deterministic **and** meaningful.
  NewRecruit's single-line `.ros` export is re-indented to a readable, git-diffable layout as a
  NewRecruit engine-adapter feature (attribute order/values preserved). Exercised by the dedicated
  `roster-fractional-cost-export` spec and by a trailing `expectedFile` step added to
  `protocol-kitchen-sink`, so the frozen NR-UI suite (which runs kitchen-sink) byte-compares the
  real Export-button flow.
- **Per-engine action-step overrides** — a step may carry an `engines:` map overriding its action
  inputs (e.g. a different `value`/`count`) for a given engine, the action-side counterpart to
  `expectedState.engines` / `skipEngines`. Covered by the `engine-action-override` spec.
- **`${{ match('regex') }}` expression** — a template token matching a volatile value by regex,
  used by roster `expectedFile` snapshots for ids no step captures (single-quoted so the token
  embeds in a double-quoted XML attribute without breaking well-formedness).
- **Engine-agnostic snapshot reads + smart writes** — `expectedFile` snapshot resolution now
  always prefers an override matching the running engine, then falls back to the base file,
  regardless of engine (a new engine is held to the base until it gets its own override). The
  base-engine name (`newrecruit`) is consulted **only** when generating/updating snapshots. On
  update, an existing override is rewritten in place; a non-base engine that newly diverges from
  the base prompts (interactively) or defaults to writing its own override — never silently
  clobbering the base. Base gamedata/roster snapshots are the NewRecruit form; BattleScribe carries
  `.battlescribe` overrides where it diverges.
- **Frozen NR replay resilience** — the HAR route now falls back to a benign empty-JSON response
  for un-recorded `/api/*` calls instead of aborting, so the SPA no longer hangs on background
  list-sync RPCs across repeated flows; the NR-UI engine also clears created lists between specs.
- **Non-integer cost conformance** (#277, #285, #286) — new fractional-cost specs
  across gamedata and roster: `cost-fractional-value`, `cost-fractional-modifier`,
  and byte-compare `cost-fractional-export` (gamedata); `cost-fractional-per-model`,
  `cost-fractional-aggregation`, `cost-fractional-over-limit`,
  `modifier-fractional-cost`, and `modifier-repeat-fractional-cost` (roster). Base
  assertions encode exact decimal arithmetic; where an engine's floating-point math drifts, a
  narrow per-engine override records its **raw** value — NewRecruit multiplies `cost × count` in
  JS `double` and surfaces the full-precision result (`0.1 × 3 = 0.30000000000000004`), which is
  exactly what NewRecruit's own `.ros` serializer writes (its JS export applies **no** rounding —
  only its UI display rounds), while BattleScribe's `(decimal)(double)` read-back collapses the
  same product to `0.3`. Adds `cost-fractional-double-divergence`, which documents this as an
  **undefined-default** case: no base value is asserted for the binary-inexact product (neither raw
  double is authoritative) — each engine's output is an override — alongside the binary-exact
  `0.125 × 3 = 0.375` both engines agree on. New research doc `docs/cost-number-formatting.md`
  (incl. a trace of NewRecruit's `fX`/`R_`/`ex` `.ros` serializer).
- **ID-based protocol** — all action addressing now uses definition IDs (`forceEntryId`,
  `entryId`, `catalogueId`) and instance IDs (`forceId`, `selectionId`) instead of
  array indices. Actions return `outputs` with created element IDs. Step expressions
  (`${{ steps.<id>.<field> }}`) allow chaining action results across steps.
- **`addChildForce` action** — add a child force under an existing force.
- **`duplicateForce` action** — deep-copy a force with all selections (NR only;
  BattleScribe Java engine throws NotSupportedException).
- **`duplicateSelection` await fix** — NR's `dupe()` is `async`; now properly awaited
  to get the stable MongoDB ObjectId uid instead of the temporary one.
- **`ForceState.Hidden` field** — both engines now expose the modifier-applied hidden
  state of forces. Oracle uses `_engine.a(forceContext, selection, entry, true)` for
  modifier-applied copies.
- **314 specs migrated** to ID-based protocol format.
- **`protocol-duplicate-force` spec** — tests force duplication (NR only).
- **`protocol-kitchen-sink` expansion** — now exercises deep selection duplication,
  original removal, and comprehensive final state assertions.
- **Hidden validation analysis** — documented in `docs/hidden-validation-analysis.md`.
  Deep-dive into BattleScribe's Java engine (f.java) and live NR Playwright investigation.
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
  Includes `NoEmptyEnginesDeclaration` to flag redundant `engines: {}` (omit instead).
- **Collective flag spec suite** — 12 specs covering all collective behaviors:
  number propagation, per-model constraints, isDuplicate instancing, group defaults,
  sibling replication, deselect, and root-entry ignoring. All pass on both BS and NR
  engines with per-engine overrides documenting structural differences.
- **`docs/collective-flag.md`** — comprehensive deep-analysis document covering the
  BattleScribe collective flag implementation (decompiled Java), NR source code analysis,
  and NR export format comparison.
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

- **`exportRosterXml` silently unsupported for 3 of 4 engines over the protocol** — `bs-engine-host`
  wired its roster-XML exporter as `e is BsUiRosterEngine ? … : null` and advertised
  `capabilities.rosterXml` from a `name is "battlescribe-ui"` match, although **all four** built-ins
  export (three implement `IRosterEngine.ExportRosterXml` directly; `BsUiRosterEngine` is merely
  async-only). Because a null exporter is the adapter's "unsupported" signal, `battlescribe`,
  `newrecruit` and `newrecruit-ui` answered `exportRosterXml` with a `ProtocolError`, which
  `JsonProtocolEngine` maps to `NotSupportedException`, which `RosterRunner.ExecuteFileAssertion`
  catches and `return`s from — so **every roster `expectedFile` byte-compare was a no-op** for those
  three engines whenever specs ran through the protocol (`bs-spec run`, hence all `--report`
  matrices and every external adapter). Nothing failed and nothing warned; the xUnit conformance
  tests construct engines in-process and bypass the protocol, which is why CI stayed green. This
  restores assertions that were already written and believed to be running — it adds no new
  capability. The exporter now falls through to the interface member for every engine and returns
  the unsupported signal **only** on a genuine `NotSupportedException`, so a real export failure
  surfaces as a protocol error instead of a skipped assertion. Same fix un-gates `--save-roster`,
  which `bs-spec run` had been disabling with a warning for every engine but `battlescribe-ui`.
- **Docs referenced a `--update-snapshots` flag that does not exist** — `bs-spec` registers only
  `run`/`compare`/`verify`/`probe`/`export-xml`/`format`/`discover`, and `BSSPEC_UPDATE_SNAPSHOTS=1`
  is the only switch (read by `RosterRunner`/`GameDataRunner`, so it works under both `bs-spec run`
  and `dotnet test`). Corrected in `docs/adapter-protocol.md`, `docs/gamedata-coverage.md`,
  `GameDataRunner`'s docstring and the "no expected file" error message, which had been telling
  people to pass a flag that would have been rejected as unrecognized.
- **BattleScribe gamedata cost parsing locale bug** — `BattleScribeGameDataEngine`
  parsed spec cost strings with the current culture, so on a locale using `,` as the
  decimal separator a value like `"0.5"` silently became `0`. Both numeric parse sites
  now use `NumberStyles.Float` + `InvariantCulture` to match the invariant-format protocol.
- **NR adapter `DeselectSelectionAsync`** — now uses `decrementAmount()` instead of
  `delete()`, matching BattleScribe's deselect semantics (decrement per-model count
  by 1). Previously `delete()` would completely remove the selection regardless of
  current amount, breaking collective entries with scaled counts.
- **NR adapter `getSelectionCount("root")` for number** — the adapter now reads
  selection number via `getSelectionCount("root")` which correctly multiplies through
  the parent chain. This handles collective entries (where internal `amount` stays at 1
  but displayed number should be parent-multiplied).
- **NR adapter `army.getErrors()` for group constraints** — error extraction now
  falls back to `army.getErrors()` to capture constraint errors on entry groups,
  which aren't attached to individual selection nodes.
- **Strict error assertion matching** — `from` field on error assertions is now a
  required string (not nullable). Assertion matching uses exact equality, not
  soft/substring matching.
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

- **`format-specs.ps1` enhanced** — now also removes redundant `engines: {}` lines.
  The formatter keeps specs clean by removing declarations that are semantically
  identical to being omitted.
- **CI verbose test progress** — all `dotnet test` steps now include
  `--logger "console;verbosity=minimal"` alongside the existing GitHubActions logger,
  printing one line per completed test so CI output shows progress instead of silence.
- **Per-engine `expectedState` overrides** replace blunt `engines: {engineName: fail}`
  markers on 29 specs. Each spec now describes the *actual* per-engine behavior via
  `expectedState.engines.{engineName}` blocks, keeping all engines passing while
  documenting behavioral differences precisely. Includes collective specs documenting
  BS double-counting bug (`battlescribe` override on `group-default-scaling`) and NR
  single-node-increment model (`newrecruit` overrides on `is-duplicate`,
  `sibling-replication`). Only 1 real-world spec retains `newrecruit: fail`
  (wh40k-10e-space-marines-army) due to fundamental data incompatibilities that can't
  be expressed as state overrides.
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
