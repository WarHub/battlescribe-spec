# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- **Roster load + reload (#201, #279)** — the roster domain gains the persistence half of the
  gamedata lifecycle: `IRosterEngine.LoadRoster(xml)` replaces the engine's roster wholesale from
  a `.ros` payload and re-links it against the setup data, and `IRosterEngine.ReloadRoster()`
  serializes the current roster and loads it straight back. (Save already shipped as
  `ExportRosterXml()`.) Both surface as roster **actions** — `loadRoster` (with an inline `content`
  XML payload, mirroring gamedata `openFile`) and `reload` — dispatched inside the existing
  `action` command rather than as new top-level optional protocol commands: optional commands route
  through nullable `AdapterOptions` delegates whose "unsupported" signal is a `NotSupportedException`
  the runner *catches and passes over*, and a round-trip spec that vacuously passes is worse than no
  spec (#309). The runner deliberately does **not** catch `NotSupportedException` for these actions:
  an engine that cannot load makes the spec fail, and engines opt out explicitly via `engines:` /
  `skipEngines`. Implemented for the in-process `battlescribe` reference engine via DataUtils
  `g(InputStream)` — the roster-side counterpart of `e` (game system) / `f` (catalogue) — followed by
  the desktop app's own load sequence (`setRoster` with default-root-entry selection suppressed, as
  the app does for a saved roster). `battlescribe-ui`, `newrecruit` and `newrecruit-ui` keep the
  defaulted throw and are opted out per spec. Covered by the new `roundtrip` roster category
  (`roundtrip-reload-roster`, `roundtrip-load-roster`) and by `protocol-kitchen-sink`.
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

- **Lock files that nothing verified, and therefore lied** — `RestorePackagesWithLockFile` has been
  on since the start and no gate ever checked the result, so `main` shipped `packages.lock.json`
  files that disagreed with `Directory.Packages.props`: #287 bumped seven central versions and
  updated **zero** lock files, #323 bumped nine and updated the `src` locks while leaving both test
  projects' `CentralTransitive` entries on OpenTelemetry **1.16.0** / System.CommandLine **2.0.9**.
  Both merged green; four separate branches then hit a plain `dotnet restore` silently rewriting
  those files and each reverted the churn as unrelated noise. Regenerated against the central
  versions (`--force-evaluate`) — every lock is now OpenTelemetry 1.17.0 / System.CommandLine
  2.0.10 — and CI's `checks` job gained a **`dotnet restore --locked-mode`** step, so a lock file
  that disagrees with its project fails the PR that introduced it, naming the project and the
  package (NU1004). Locked mode is passed on the command line, **not** set as `RestoreLockedMode`
  in `Directory.Build.props`: a contributor adding a `PackageReference` must still be able to
  restore. Dependabot's lock updates for this repo are partial — the entries it misses are the
  `CentralTransitive` ones for packages a project reaches only through a `ProjectReference`, which
  is why the two test projects lagged while `src` did not — so an occasional bump PR will now go
  red; the fix is `dotnet restore --force-evaluate` on the branch, and that is a great deal cheaper
  than a lock file nobody can trust.
- **`Microsoft.NET.ILLink.Tasks` floated with the installed SDK** — the three `IsAotCompatible`
  projects (Cli, TestKit, Telemetry) get an *implicit* `PackageReference` to it, versioned from the
  SDK's `KnownILLinkPack` rather than from `Directory.Packages.props` (central package management
  does not manage implicit references). It is a `Direct` entry in the lock, so the lock encoded
  whichever SDK last wrote it: `main` carried **10.0.3** in the Cli lock and **10.0.10** in the
  other two, and a plain restore rewrote whichever disagreed with the local SDK — with `global.json`
  on `rollForward: latestFeature` and CI on `10.0.x`, that is every contributor on a different
  patch. Now pinned to **10.0.10** via `KnownILLinkPack` in a new `Directory.Build.targets` (it has
  to be `.targets`: the SDK defines the item *after* importing `Directory.Build.props`, so an
  `Update` there is silently ignored), making the restore a property of the repository instead of
  the machine — and making locked mode survivable on a floating CI SDK. Bumping it is a deliberate
  one-line edit; dependabot does not see the item.
- **Repo-root detection resolved to the wrong tree inside a git worktree** — four production sites
  walked up for a `.git` **directory**. In a worktree `.git` is a **file** holding a `gitdir:`
  pointer, so `Directory.Exists` was false, the walk sailed past the worktree root, and — because
  this repo's worktrees live at `.claude/worktrees/<name>` *inside* the main checkout — it landed on
  the main checkout. Every artifact path then resolved against the wrong tree: `bs-engine-host` was
  probed in the main checkout's stale `artifacts/`, so the CLI suite failed with "Could not locate
  bs-engine-host" (`EngineSpecTests.ResolveLaunch_*`, `CompareCommandTests`, `RunBatchSurfaceTests`)
  for anyone working in a worktree unless they set `BSSPEC_ENGINE_HOST` by hand. Detection now walks
  up for `BattleScribeSpec.slnx` — the marker the repo's test helpers already used. It is not merely
  worktree-safe: `.git` marks "some git checkout", so a walk starting inside a submodule
  (`.deps/wham`) or a cloned data directory (`.testdata/wh40k-9e`, `lib/nr-editor`) used to stop
  there too, at a directory with no `artifacts/` or `specs/` at all. Neither marker exists next to
  an installed `bs-spec`, so the published-layout fallbacks (env override, sibling assembly, PATH,
  current directory) are unaffected. The six hand-copied walks — `EngineHostLocator`,
  `HostSpecLoading`, `HostEngineFactory`, `Cli/SpecLoading` (dead code, deleted), `SchemaValidator`
  and `BsGameDataUiEngine` — collapse into one `BattleScribeSpec.RepoRoot` in the TestKit, which
  every other project already references; copy-paste divergence is what let this bug reach four
  sites in the first place.
- **The "frozen" NR Editor snapshot was not pinned — it tracked upstream's branch tip** —
  `testdata.json` names a commit for the `nr-editor` archive, and `setup.ps1` ignored it:
  it ran `git clone --depth 1 --branch gh-pages`, which by construction can only ever produce
  that branch's newest commit, then on mismatch printed `Write-Warning "The pinned commit may
  be outdated"` and carried on with whatever it had just downloaded. The pin was advisory.
  Measured on 2026-07-30: the pin was `74e2207` (2026-04-27) while `gh-pages` was at `6165dbb`
  (2026-07-26), so every machine and every CI job ran the `nr-editor-frozen` and
  `nr-editor-ui-frozen` suites against a third-party deployment three months newer than the
  one recorded in the repo — with nothing in the test output to say so. The `.tag` marker made
  it worse rather than better: it recorded the commit actually obtained instead of the one
  required, so it could never disagree with reality, the "[OK] Already downloaded" fast path
  never engaged while a pin lagged, and CI silently re-pulled the current tip on every run.
  `setup.ps1` now resolves the pinned **object** — `git fetch --depth 1 origin <sha>`, which
  GitHub serves (`uploadpack.allowAnySHA1InWant`) and which reaches a commit that is no longer
  a branch tip — falling back to a full-history fetch of `ref` for hosts that refuse
  fetch-by-SHA, and **failing** with re-pin instructions (including the branch's current tip)
  when the commit is genuinely gone. `ref` is now only a recovery hint, never a source of
  content. The pin is verified against `HEAD` after checkout, must be a full 40-character SHA,
  and `.tag` is written afterwards holding the **pinned** commit — so a marker hit is proof
  the pinned bytes are on disk. `core.autocrlf`/`core.eol` are forced off so the snapshot is
  the same bytes on every OS. `nr-editor` is re-pinned to `6165dbb`, the current `gh-pages`.
- **Snapshot-bump PRs did not run the suites they break** — `thorough-conformance` is gated on
  schedule / `workflow_dispatch` / `[nr-test]` / the `thorough-ci` label, so a PR editing
  `testdata.json` — which swaps out the very HAR, NR Editor snapshot and BattleScribe build the
  frozen suites replay — got only the every-push lanes, and those trim exactly those suites to
  kitchen-sink. #301 (HAR `v34.93-20260708` → `v35.12`) was green through three weeks of daily
  bot re-runs, merged, and broke the NR-UI roster driver and the store-direct NR roster export;
  bisecting the HAR tag with nothing else changed confirmed the NR client had dropped a navbar
  route and replaced every raster icon with an SVG component. `ci.yml`'s gate now turns the
  thorough lanes on for **any** PR whose diff touches `testdata.json`, bot or human: the
  exposure comes from the content change, not the author, and a maintainer hand-editing a pin
  has precisely the bot's exposure. `update-nr-snapshot.yml` additionally labels its PR
  `thorough-ci`, so the reason the deep suites ran is legible on the PR itself. The `gate` job
  was also missing from `ci-gate`'s result table — a failed gate skipped the thorough lanes and
  still aggregated green — and is now checked.
- **Both frozen-NR roster smoke steps executed ZERO tests and reported green** — the `smoke` job runs
  on every PR, so this was a hole in the checks gating all normal contributions, and it failed in two
  different ways that a single "did the filter match anything?" check does **not** cover:
  - `--filter "Engine=FrozenNrUiRoster&DisplayName~kitchen-sink"` matched **0 tests**.
    `FrozenNrUiRosterConformanceTests` is a single `[Fact] AllSpecs()`, so no test's display name
    carries a spec id and a `DisplayName` clause can never match it. VSTest printed "No test matches
    the given testcase filter", exited 0, green — right through the suite breaking on the v35.12 NR
    HAR client change, which is what the step existed to catch. The class already hardcodes
    `TargetSpecs = ["protocol/protocol-kitchen-sink"]`, so the class *is* the narrowing: the filter
    is now `Engine=FrozenNrUiRoster` (measured 1 test, and it runs).
  - `--filter "Engine=FrozenNrRoster&DisplayName~kitchen-sink"` matched **exactly 1** — and it was
    `SequentialFrozenNrRosterConformanceTests`, the `Mode=Sequential` debugging variant gated behind
    `NR_SEQUENTIAL`, which self-skips in CI. Measured: `Skipped! - Failed: 0, Passed: 0, Skipped: 1,
    Total: 1`, exit 0, green. The real suite was never selected at all. The step now filters
    `Engine=FrozenNrRoster&Mode!=Sequential` (the `nr-frozen` profile's own filter) and narrows to
    kitchen-sink from the engine side with a new `NR_FROZEN_SMOKE=1` knob on
    `FrozenNrRosterConformanceTests` — the same shape as the existing `NR_UI_SMOKE`, keeping the
    smoke lane on the pooled/parallel class the thorough lane also runs.
- **A CI test step that runs nothing can no longer pass** — every `dotnet test` step in `ci.yml` now
  goes through `scripts/dotnet-test-step.ps1`, which reads the run's TRX `<Counters>` and fails the
  step when **passed + failed == 0**. The invariant is "this step *executed* a test", not "the filter
  matched something": a non-empty selection that entirely skips is the more insidious failure and
  looks exactly like a real run. (`RunConfiguration.TreatNoTestsAsError=true` is passed too, purely
  so the empty-selection case fails with VSTest's own precise message.) It is a per-invocation
  wrapper rather than a runsettings setting because a runsettings would also bind
  `dotnet test -p:TestProfile=<x>` run against the *solution* — the form AGENTS.md documents — where
  a profile's engine filter legitimately matches nothing in `BattleScribeSpec.Cli.Tests`.
  `ConcurrencyConfigurationDriftTests.EveryCiTestStep_ExecutesAtLeastOneTest` forbids a bare
  `dotnet test` in any workflow, so a new step cannot silently opt out.
- **Per-engine declarations were inert for `-ui` engines under `bs-spec run`** — the CLI collapsed a
  UI engine to its base name before handing it to `RosterRunner` (`battlescribe-ui` → `battlescribe`),
  so step-level `skipEngines: [battlescribe-ui]` and step/state `engines: {battlescribe-ui: …}` never
  matched anything and were silently ignored. Collapsing is right for *expectations* — a UI driver
  produces what its base engine produces, and every spec relies on inheriting `engines: battlescribe:`
  — but wrong for *capabilities*, which genuinely differ between a driver and its base engine. The
  runner now carries both identities and resolves most-specific-first: a `skipEngines` entry matches
  either name, and a per-engine override prefers the concrete engine's entry and falls back to the
  base engine's. That is the same rule the batch runner already applied to spec-level `engines:`
  (which filters by the concrete name). Purely additive — every existing declaration keeps its
  current meaning — and it preserves honest failure: an action an engine cannot perform still fails
  loudly unless a spec explicitly names that engine. The xUnit conformance suites, which pass one
  full name for both roles, are unaffected.
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
- **NR UI roster driver vs. NR client v35** (#301) — the frozen NR-UI suite broke the moment
  `testdata.json`'s HAR tag moved from `v34.93-20260708` to `v35.12`, because the driver reached
  for UI chrome NR had changed. Two independent changes in that range: the **"Home" navbar link**
  to `/app/MySystems` was removed (setup clicked it, and timed out for 30s), and **every raster
  icon became an `<nr-icon>` SVG component**, taking `alt="list menu"`, `alt="Save unit"` and
  `alt="edit cost limits"` with it. The driver now navigates by **route** (`NewRecruitBrowser
  .PushRouteAsync`) instead of clicking a nav control, and identifies buttons by the element
  attributes that survived the icon swap — `[title=…]` (kept on `<nr-icon>`), the wrapper classes
  `.menu` / `.back`, and the menu item's own label — rather than by `<img>` alt text. `HarRecorder`
  made the same move: its `a[href*='MySystems']` hop was guarded by `IsVisibleAsync`, so it had
  silently stopped visiting the page it believed it was visiting.
- **Store-direct NR roster export raced the editor mount** — `NewRecruitRosterEngine`'s
  `.ros` capture pushed the editor route and then immediately walked the component tree for one
  exposing `exportRos()`. `router.push()` resolves when a route is *confirmed*, which is strictly
  earlier than when its component has *mounted*, so the walk could search the outgoing page and
  report — truthfully — `no mounted component exposes exportRos()`. The only thing covering the gap
  was an accident: `DismissDialogsAsync` spends ~1s waiting for a consent root the frozen HAR does
  not contain. Measured over 8 runs per snapshot with nothing else changed, the step failed 1/8 on
  `v34.93-20260708` and 3/8 on `v35.12` — a latent race the snapshot bump made frequent, not a new
  one. The walk now polls for the mounted component against a 15s deadline, and its failure message
  names `location.pathname` so a genuinely wrong page is distinguishable from a slow mount.
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

- **BREAKING: roster step field `path`** — a decoy that was registered nearly everywhere but usable
  nowhere. It existed on `StepDef`, in `SpecValidator`'s `ActionOnlyFields` / `GetSetFields` /
  `GetFieldValue`, in `StepDef.ForEngine`'s per-engine copy list, in the CLI timeline's step
  parameters, and in `docs/spec-schema.json` — but it had **no entry in any action's
  `ActionParams`**, so the "action 'x' does not accept 'path'" check rejected it on every action.
  No spec ever set it. It looked like the natural field for a load-from-file action, but roster
  `loadRoster` deliberately takes an inline `content` payload instead (mirroring how gamedata's
  `openFile` resolves a side-file by convention from the step `id`). With
  `additionalProperties: false` on the schema's step object, a spec using `path:` is now a hard
  schema error — which is the intent.
- **Dead code cleanup** (~800 lines of C# + 3,170 lines of debug JSON):
  - 4 `Debug_Probe*` exploratory methods from `NrIntegrationTests.cs` (617 lines).
  - 7 unused diagnostic methods from `BattleScribeOracle.cs` (185 lines).
  - `docs/nr-store-dump.json` debug artifact (insights already in `nr-store-mapping.md`).
- **BREAKING:** `IRosterEngine.HasValidationErrors()` — use `GetValidationErrors().Count > 0` instead.
- **BREAKING:** Legacy spec YAML assertion fields removed from the loader model:
  `hasValidationErrors`, `noValidationErrors`, `validationErrors`, `validationErrorCount`,
  `assert`, `expected`. Specs must use the new `errors` array format with `on`/`from` syntax.
  SpecStructureTests now rejects any YAML file still using these fields.
