# Project Context

- **Owner:** Amadeusz Sadowski
- **Project:** battlescribe-spec — declarative conformance test suite for BattleScribe roster engines
- **Stack:** C# / .NET 9, Protocol types (ProtocolMessages.cs), YAML spec format
- **Created:** 2026-05-08

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### Issue #198: JSON Schema Metaschema Validation (2026-05-08)

- **JsonSchema.Net 9.2.0** (already a project dependency) bundles all major draft metaschemas: `MetaSchemas.Draft202012`, `MetaSchemas.Draft201909`, `MetaSchemas.Draft7`, `MetaSchemas.Draft6` — no network access needed
- **Lint profile filter** is in `tests/test-profiles/lint.runsettings` as a `TestCaseFilter` with `FullyQualifiedName~` patterns pipe-separated
- **File discovery**: only files where `$schema` maps to a known JSON Schema metaschema URI should be validated; config files like `xunit.runner.json` reference non-metaschema URIs and are correctly excluded
- **Exclude dirs**: `artifacts\`, `node_modules\`, `.git\`, `.testdata\` should be skipped in repo-wide file scans
- **`[Trait("Category", "Unit")]`** is the standard attribute for lint/unit tests in this project

- Protocol types in ProtocolMessages.cs must remain engine-agnostic — shared between all adapters
- GameData specs reuse ProtocolGameSystem/ProtocolCatalogue as setup data; engines init via IGameDataEngine.Setup()
- The spec format describes WHAT behavior to test, not HOW any engine implements it

### Issue #19 Technical Analysis (2026-05-08)

- **"Roster loading" has two meanings:** (1) file-based loading (.ros files), (2) empty roster creation (already tested)
- **Current protocol gap:** No LoadRosterCommand or SaveRosterCommand; specs can only test inline game data + actions
- **Editor round-trip limitation:** GameData mutation specs (#168-#170) can't verify persistence without save/reload cycle
- **Scope recommendation:** Option 2 (Medium) — add load/save commands to protocol; covers both roster file loading and editor round-trip
- **Engine-agnosticism check:** Load/save interface is neutral; implementation varies by adapter (DB, files, memory)
- **Downstream impact:** Unblocks #18 (data editor epic) and related #168-#172 if round-trip is supported
- **File format decision:** Start with .ros XML only; defer .rosz compression to later backlog
- **Protocol additions needed:** LoadRosterCommand, SaveRosterCommand, RosterPersistenceResult types (draft in decision doc)

### Team Decision: Option 2 Approved (2026-05-08)

**DECISION:** Amadeusz approved Option 2 (Medium scope) for Issue #19.
- Holden's technical analysis was used to evaluate 3 scope options.
- Bobbie's domain analysis clarified roster vs. editor separation.
- **Option 2 chosen:** Roster loading + editor round-trip (load/save protocol support).
- **Rationale:** Unblocks Epic #18; engine-agnostic protocol design; achievable in 2–3 sprints.
- **Next steps:** Begin Epic #18 MVP implementation (Phase 1: 13 priority specs, 4–6 week effort).

### Issue #198 Follow-up: JsonSchemaLintTests Review Resolution (2026-05-10)

- **JsonSchemaLintTests now intentionally supports only JSON Schema draft 2020-12**; older drafts were removed to keep lint behavior narrow and explicit
- **JSON schema discovery is scoped to `docs\` only** and must find at least one supported schema; missing `docs\` or zero matches is treated as a test failure
- **Repo-root discovery for this lint test uses `[CallerFilePath]` first**, with fallback resolution from `AppContext.BaseDirectory` so deterministic path trimming still works in test runs

### Issue #198 Follow-up: Cross-Platform Path Fix (2026-05-12)

- **Repo-root discovery in `JsonSchemaLintTests` should traverse parent directories for `*.slnx` and fail fast on non-rooted `[CallerFilePath]` values**; `AppContext.BaseDirectory` fallback was removed because it masked Linux CI path issues
- **Paths emitted by this lint test should be normalized to forward slashes (`/`)**, including repo-root-derived paths and relative paths shown in assertion/error messages, to keep output cross-platform and stable
- **Use `MetaSchemas.Draft202012.BaseUri` as the source of truth for the supported metaschema URI** instead of duplicating the draft 2020-12 URI as a string constant
