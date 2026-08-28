# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- **Roster load in all four engines (#450)** — `LoadRoster`/`ReloadRoster` shipped implemented in
  one engine of four, with the other three opted out of every spec that loads a roster. All four
  implement them now, no spec opts an engine out of the persistence actions, and
  `EveryRosterEngineTheHostCanServe_LoadsAndReloads` fails if a future engine quietly inherits the
  throwing default.
  - `newrecruit` loads through NewRecruit's own `listsStore.importBs(File)` — the action behind My
    Lists' "Import BattleScribe file". It answers a **string** for every file it declines and an
    object when it succeeds, so the adapter converts the string into a throw: read as a truthy
    result it would have made every refusal a silent pass. It also *adds* a list rather than
    replacing one, so the adapter re-points at the imported list and deletes the row it replaced.
  - `newrecruit-ui` does the same import through the real file input, and reads the outcome off
    NR's own message bar — the sentence a spec's `messageContains` matches is the one the user saw.
  - `battlescribe-ui` gains a `rosterLoadRosterAction`: the payload is staged into the app's roster
    folder and opened through `actLoadRoster`'s own sequence minus the native file chooser, so the
    force-to-catalogue mapping, the re-linking and the recalculation are all BattleScribe's. Where
    the app shows a dialog and returns, the action raises the app's own reasons — a dialog is not a
    result, and only a raised refusal can be asserted.
  - **Three engine differences the roundtrip specs now record**, each with a spec of its own rather
    than a skip: NewRecruit refuses a roster with no forces where BattleScribe loads it
    (`roundtrip-load-forceless-roster`); NewRecruit drops a selection carrying no primary
    `<category>` **silently** where BattleScribe restores it
    (`roundtrip-load-selection-no-primary-category`); and the desktop app closes the open roster
    *before* discovering it cannot load the replacement, so a refused load costs a
    `battlescribe-ui` user the roster they had.
  - **A reference-adapter gap the UI lane found.** `roundtrip-load-unknown-game-system` recorded
    that BattleScribe accepts a roster naming a game system it does not hold. It was the in-process
    adapter that accepted: the app answers "you do not have the right data files to be able to edit
    this roster", and `battlescribe-ui` refuses. `BattleScribeEngine.LoadRosterXml` now makes the
    same check, all four engines agree, and the spec lost an override instead of gaining one.
  - `protocol-kitchen-sink`'s load payload was a roster with no forces, which only the reference
    engine could take; it is now one every engine can load.
  - Also fixed on the way: the Java agent turned "the app has no roster open" — a state the app can
    genuinely be in — into `getValidationErrors failed: …`, because BattleScribe reports it by
    throwing from its roster getter.
  - **And a cleanup leak that only a load could see.** Both NewRecruit engines released a spec's
    synthetic game system from `localLibrary` and nowhere else; NR also registers it in the shared
    library, which was never cleared. Nothing resolved a system by id — until roster load, because
    NewRecruit's own importer reads `gameSystemId` out of the file and calls `selectSystem(id)`.
    `roundtrip-load-roster` then built its roster against a system left over from an earlier spec:
    right forces, right Squad, and the file's Trooper simply absent. It passed alone and failed in
    the lane, which is what the lane is for. Cleanup now releases the system from every registry it
    was registered in, and `NR_UI_ROSTER_FILTER` narrows that lane's spec set without changing its
    one-browser execution shape, so a warm-session failure costs 90 seconds to reproduce rather
    than half an hour.

- **Action-level failure primitive: `expectFailure` (#23, #25, #268)** — a step can now assert that
  the engine **refused** its action, which nothing in the spec model could say. The only failure
  expectation was spec-level `engines: {<name>: fail}` (whole-spec, per-engine), and
  `expectedState.errors` asserts the *validation list* of a roster the engine **accepted** — so a
  malformed `.ros`, which never becomes a roster, had nothing to assert against. Three issues in
  three epics each rediscovered this independently.

  Available on roster and gamedata action steps in three shapes — `true`, `false`, and a mapping
  with `messageContains` plus per-engine `engines:` overrides taking the same three. A refused step
  **does not end the run**, so a following `expectedState` asserts what the refusal left behind
  (whether a rejected load keeps the previous roster is the conformance question, and it is
  unanswerable if the harness stops at the refusal). An action that *succeeds* under a declared
  refusal fails the step: the assertion is two-sided.

  **Only an engine refusal satisfies it.** Four layers can make an action fail and they used to
  arrive at the runner as one thing — an exception message string. Now they are told apart, and the
  other three stay fatal: an id the spec named that the adapter could not resolve (a spec bug —
  every engine fails those identically, through its own adapter, so asserting one would make a typo
  pass), a `NotSupportedException` from an engine that does not implement the action (a capability
  gap — without this, the three engines that cannot load a roster (#450) would pass every
  malformed-input spec without parsing a byte, which is #309 at action level), and a harness fault.
  The classification is made adapter-side, the last place that still has the exception, and rides
  the wire as an optional `kind` on `actionResult`/`gamedataActionResult`. An adapter that omits it
  stays conformant and simply cannot have its refusals asserted — the spec fails naming the field
  rather than passing on an unexamined failure.

  Adapter id lookups now raise `SpecAddressingException` rather than `InvalidOperationException`,
  and IKVM binding failures raise `HarnessFaultException`; those two declarations are what let the
  classifier treat its remainder as engine behaviour. `SpecValidator` rejects `expectFailure` on a
  non-action step and an `id` on a step no engine accepts (it could never be referenced).
  Documented in `docs/error-assertions.md` and `docs/adapter-protocol.md`.
- **Roster load-failure specs (#23)** — five specs covering the malformed-input range, and one of
  them is a negative result recorded rather than assumed. BattleScribe refuses truncated XML and an
  empty payload (`ParseError`), refuses a roster naming a catalogue it does not hold, and refuses
  one naming an unknown `gameSystemId`; it **accepts** a foreign schema namespace with an
  out-of-range `battleScribeVersion`, its reader matching element names rather than namespaces.
  Each refusal spec also asserts what the refusal left behind. (The unknown-`gameSystemId` case was
  first recorded as an acceptance; #450 found that to be the in-process adapter missing a check the
  app makes, and corrected both.)
- **GameData `load` spec category (#268)** — the load-failure path, kept separate from
  `validation/` on purpose: a file the editor refuses to parse produces no validation list, which is
  why #268 was carved out of #173. Opens with `load-malformed-catalogue`. The two Data Editor UI
  lanes are skipped as the open question #268 names, not as a capability gap.
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
  the app does for a saved roster). `battlescribe-ui`, `newrecruit` and `newrecruit-ui` kept the
  defaulted throw and were opted out per spec — #450, above, implemented all three. Covered by the
  new `roundtrip` roster category (`roundtrip-reload-roster`, `roundtrip-load-roster`) and by
  `protocol-kitchen-sink`.
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

### Changed

- **SDK feature band moves to 10.0.400** — `global.json` goes from `10.0.300` to `10.0.400`, keeping
  `rollForward: latestPatch`. This is the bump path #312 pinned the band *for*: a pin with no bump
  path is a slower version of the same problem, so the band moves as a PR, tested, with a diff.

  The three SDK-derived pins that must agree, and do:
  - `global.json` — `sdk.version`.
  - `docker/bs-spec.Dockerfile` — `mcr.microsoft.com/dotnet/sdk:10.0.302` → `:10.0.400`. Invisible to
    the Dependabot updater (there is deliberately no `docker` ecosystem entry — two bots racing over
    two numbers that must agree), and asserted by
    `ToolchainPinDriftTests.DockerImagesUseTheSdkBandPinnedInGlobalJson`.
  - `Directory.Build.targets` — `KnownILLinkPack` `10.0.10` → `10.0.11`, the net10.0
    `ILLinkPackVersion` bundled with 10.0.400. Also invisible to Dependabot: the reference is implicit,
    so central package management cannot hold it down. Re-restored with `--force-evaluate`, which
    rewrites it in the three `IsAotCompatible` projects' lock files (Cli, TestKit, Telemetry) and
    nothing else.

  **The analyzer band did not widen anything.** That is the risk this pin exists to make visible —
  `AnalysisLevel=latest-recommended` + `TreatWarningsAsErrors` mean the enabled CA rule set is a
  function of the installed SDK — so it is worth recording that the build is clean rather than
  leaving a green run to imply it: solution build 0 warnings / 0 errors, `--locked-mode` restore
  clean, and the offline `pre-push` suite green on 10.0.400.

  Also corrects the `Directory.Build.targets` comment, which still described the pre-#312 world
  (`pins 10.0.100 with rollForward=latestFeature`, CI on a floating `10.0.x`) as the present tense.
  The churn it warns about is narrower now — any *patch* within the pinned band, rather than any SDK
  at all — and the pin still earns its place, because the bundled `ILLinkPackVersion` moves between
  patches.
- **Both Dockerfiles had been unbuildable for months, and the stale `10.0-preview` tag was the least
  of it** — nothing in CI, scripts, or tooling ever built them, so nobody found out.
  `reference-adapter.Dockerfile` copied `src/BattleScribeSpec.Oracle/`, a project renamed away on
  2026-04-23, and had been broken for four months; it also copied `lib/*.jar`, which is gitignored and
  where no jars have lived since they moved to `lib/battlescribe/lib/`. `bs-spec.Dockerfile` never
  copied the two Telemetry `.csproj` files the CLI has ProjectReferenced since 2026-07-13 (restore
  failed), and its `dotnet/runtime` base lacks the **ASP.NET Core** shared framework that
  `Telemetry.Collector`'s `FrameworkReference` pulls into `bs-spec.runtimeconfig.json` — so even a
  fixed restore would have produced an image that could not start. Its `CMD` defaulted to the built-in
  `battlescribe` engine, which this image cannot carry.
  **`reference-adapter.Dockerfile` is deleted** rather than repaired: its engine is IKVM-compiled from
  BattleScribe jars that `setup.ps1` fetches with an authenticated `gh release download` from a
  token-gated archive, so building it means carrying a GitHub App token into `docker build` and
  redistributing a third party's binaries. `docker-compose.yaml` loses that service and now builds the
  one image that can exist. **`bs-spec.Dockerfile` is repaired**: SDK `10.0.302` (inside the band
  `global.json` pins, which it now COPYs along with `Directory.Build.targets`), an `aspnet` runtime
  base, the two missing csproj copies, and `CMD ["--help"]` instead of a default engine it does not
  ship. A new **`docker` CI job** builds the image and runs it on every push — a stale `COPY` list is
  only wrong relative to a project graph that moves, so no lint rule can find it and only a build
  will. `ToolchainPinDriftTests.DockerImagesUseTheSdkBandPinnedInGlobalJson` catches the cheaper half
  in seconds: the image tag must stay inside `global.json`'s feature band, since the Dockerfile COPYs
  that file and a mismatch is a hard "no compatible SDK" failure.
- **`run --policy reuse=on` is refused on an engine that has not earned reuse-safety, instead of
  warned about (#313)** — `EngineProfile.ReuseSafeRoster`/`ReuseSafeGameData` are claims `bs-spec
  compare` has demonstrated against a cold arm, and `newrecruit-ui` declares its roster domain unsafe
  because enabling reuse there once silently changed **six** spec verdicts while a stopwatch reported
  success. `run --all --engine newrecruit-ui --roster --policy reuse=on` reproduced that exact
  configuration and emitted a warning, which is close enough to silence to count. Ask what forcing an
  unearned reuse is *for*: the only legitimate purpose is ablation — finding out whether verdicts
  change — and that requires two arms, which `run` does not have. So `run` now errors (exit 1),
  naming the engine, the domains, and the `compare` invocation that answers the question. **`compare`
  keeps allowing it**, deliberately: there the forced arm is the experiment, the other is the control,
  and per-spec verdict-equality is asserted before any timing is reported — closing that channel would
  leave no way for an engine to ever earn a `ReuseSafe*` flag. `--policy reuse=off` stays legal in
  every verb, since turning reuse off cannot invent a verdict. The allow/refuse choice is now an
  explicit `UnsafeReuse` argument at each of the four call sites rather than an implicit property of
  the shared helper, so a new verb has to state which it is.
- **The .NET SDK feature band is now a decision this repo makes, not one the runner image makes for
  it (#312)** — `global.json` moves from `rollForward: latestFeature` to `latestPatch` pinned at
  `10.0.300`, all six `actions/setup-dotnet` steps swap `dotnet-version: '10.0.x'` for
  `global-json-file: global.json`, and a new `dotnet-sdk` Dependabot ecosystem owns moving the band.
  The combination that made this urgent is `AnalysisLevel=latest-recommended` +
  `TreatWarningsAsErrors=true`: the set of CA rules able to fail the build was resolved from
  whatever SDK the runner fetched, so a stricter band arrives as a red build on a commit that
  changed nothing. **It had already happened, unnoticed** — SDK 10.0.400 shipped 2026-08-11 and CI
  built on it from 2026-08-13 onward (run 31746128450), on a feature band that appears in no commit
  and no review. That run was green, which is why nobody found out. Note what this deliberately does
  *not* do: `AnalysisLevel` stays `latest-recommended` rather than gaining a second pinned number
  free to drift from the first — with the SDK pinned, `latest` already resolves to a version we
  chose. The band still moves, through a Dependabot PR that CI builds; a widening rule set now lands
  as a reviewable diff. `ToolchainPinDriftTests` fails if a workflow step goes back to picking its
  own SDK, if `rollForward` loosens, or if the Dependabot entry is dropped, since a pin without a
  bump path is a slow leak rather than a fix.

### Fixed

- **`on:` schema pattern rejects the retired entry-addressed dialect (#419, #424)** — the
  `errorAssertion.on` pattern was `^(roster|group)$|^(force|category|selection)( \S.*)?$`, which
  accepted any second token, so `on: selection se-unit-a` stayed schema-valid long after #419 made it
  meaningless. Its description still promised the form was "still accepted while the corpus migrates
  (#424)" — #424 closed 2026-08-13. A spec in that dialect passed schema validation and was then
  refused by the linter: two records of one rule, with the schema being the wrong one. The pattern now
  permits only a `${{ … }}` expression as the second token, and tolerates surrounding whitespace
  because `ErrorAddress.Parse` and `ExpressionResolver.Resolve` both trim before inspecting. It also
  now rejects a value whose expression is not the whole token (`selection ${{ a }} junk`,
  `selection sel-${{ x }}`) — `Resolve` returns those unchanged, so they resolved to a literal that
  matched nothing, silently. Bare `force`/`category`/`selection` stays valid: the matcher treats a
  null node id as match-on-kind. No corpus spec changes. `SpecSchemaTests` gains a theory that reads
  the pattern out of the shipped schema, since the corpus cannot catch a loosening of it.

- **A directory the spec merely lived under could decide which engine ran it (#311)** —
  `SpecLoading.InferEngineType` classified a spec by substring-scanning
  `Path.GetFullPath(input).ToLowerInvariant()`, i.e. the absolute path, which carries every directory
  above the checkout: a home directory, a CI workspace, an agent worktree. A repo cloned into
  `gamedata-tools/` routed every roster spec in it to the gamedata engine — on every platform, not
  just Linux. A spec inside `specs/` is now classified by **containment** in `specs/gamedata` or
  `specs/roster` (via `Path.GetRelativePath`, which applies the running platform's own casing rule
  instead of a hard-coded one), and only a spec outside that tree falls back to a hint — now an exact
  path **segment** rather than a substring, so `gamedata-tools/` is no longer `gamedata/`. The tail
  `return normalized.Contains("roster") ? "roster" : "roster"` went with it: both arms were identical,
  so the probe was evaluated and discarded, and roster has always been a fallback rather than a
  detection. Same treatment for the **directory-escape guard** in `NrEditorStore`'s static-file route
  (`fullPath.StartsWith(dir, OrdinalIgnoreCase)`), which on a case-sensitive filesystem let
  `/tmp/STATIC/x` — a genuinely different directory — past a guard rooted at `/tmp/static/`; and for
  the two `OrdinalIgnoreCase` full-path compares in `SpecLoader`'s spec discovery, where the fix is a
  no-op today (both operands derive from the same root) but removes the last hand-rolled path-casing
  decision from the code that decides which specs exist. The route guard is now
  `NrEditorStore.IsInsideRoot`, extracted so it can be tested rather than argued about — and its test
  derives the case-variant expectation *from the filesystem*, so it is falsifiable in opposite
  directions on the two platforms instead of being skipped on one. **Not changed, deliberately:**
  `DataSourceResolver.FindGameSystem`/`FindCatalogue` match a spec-authored game-system name against
  real-world data-repo filenames, `TagFilter` matches `--tag` tokens, and the `.gst`/`.cat`/`.yaml`
  extension checks tolerate mixed case in third-party data. Those compare *user input*, not paths, and
  their case-insensitivity is the feature — `DataSourceResolverTests` pins it.
- **An NR UI action timeout was anonymous, and its diagnostics never left the runner** — the two
  facts compound, and the NR snapshot bump to `v35.27` is what showed it. `thorough-conformance`
  failed one spec of 363 (`constraint/constraint-forces-field-on-forceentry`, run 31568343878) and
  the complete record of it was:
  `Step 4: TimeoutException: Timeout 20000ms exceeded.` — not which of that step's half-dozen waits,
  not what page it was on, not whether the roster held the two forces it should have by then. On a
  HAR bump the question is precisely *did NR change under us*, and that message cannot separate a
  changed UI from a lost page: a driver fix from a re-run. Three parts:
  - **The dumps were written to the wrong directory.** `NrUiDiagnostics`/`NrGameDataUiDiagnostics`
    default to `artifacts/<name>` *relative to the working directory*, which is right for `bs-spec`
    and wrong under `dotnet test`: VSTest sets the test host's working directory to the test
    assembly's output folder, so every screenshot, DOM snapshot, Pinia dump and console log landed in
    `artifacts/bin/BattleScribeSpec.Tests/debug/artifacts/`. The same trap, with the same cause and
    the same fix, is already written down in `BsRosterUiFixture` and `TelemetryAssemblyFixture`;
    `UiArtifactPathsAssemblyFixture` now anchors both NR directories at the repo root, leaving an
    explicit `NR_*_DIAGNOSTICS_DIR` alone. An assembly fixture and not the four NR UI fixtures that
    need it, because the override is an environment variable: writing one at collection init is a
    process-wide write landing mid-run, and `DiagnosticsIsolationTests` is a test that clears those
    exact variables and then reads them — an intermittent introduced by the fix for an intermittent.
  - **And then not uploaded.** `thorough-conformance` is the only job that runs either NR UI driver
    over its full spec set and it uploaded telemetry alone, while `thorough-ui-bs` next door
    uploads its BS equivalents — two jobs writing dumps, one collecting them. It now uploads both NR
    directories on failure, and `ConcurrencyConfigurationDriftTests.EveryUiLane_UploadsTheDiagnosticsItWrites`
    holds every UI lane to it.
  - **The message now carries the observation.** `WithDiagnosticsAsync` describes a bare Playwright
    timeout the way `NrUiSetup.WaitForSetupConditionAsync` already describes setup's — the action,
    the page URL, what the editor held instead (`forces=`, `forcesPanel=`, `forceRows=`,
    `unitRows=`, `popups=`) and the path of the saved report — asserting no cause, only what it read.
    A timeout that is already described passes through untouched, so a setup wait failing inside an
    action keeps its own, more specific observation rather than being buried under a second one.
- **A flake in one `thorough-conformance` lane skipped the two lanes after it** — the job's steps ran
  on the default `success()`, so the intermittent NR-UI roster failure above also skipped *Full
  frozen NR Editor GameData* and its UI counterpart. Those replay the NR Editor pin, not the HAR, so
  a red NR-UI roster lane says nothing about them — yet the v35.27 bump was reported red having
  never run two of the four lanes the job exists for, and the only way to learn their verdict was
  another hour of runner time. Both now run on `!cancelled()`; a failing lane still fails the job.
- **The frozen NR-UI lane's setup guard stopped one step short of the work it guards** — the
  sequence-level retry in `NrUiSetup.LoadGameDataAsync` wrapped three steps (be on MySystems, open
  the install popup, click Add From Folder), but those only establish that the *buttons were
  pressed*; the wait that says the system actually **installed** sat outside the loop, so a drift
  into that window — the same "previous spec's navigation lands and takes the page with it" race the
  loop exists for — was past the guard and unrecoverable. `thorough-conformance` was failing one
  spec out of 363 in Setup, a different spec each time, roughly one run in two (runs 31409213032 /
  31415790894, on PRs touching neither this driver nor NR), and re-running green.
  Measured over a clean 363-spec lane: `load-gamedata/wait-local-library` runs 363 times at avg 67ms
  / max 5066ms against a 30s ceiling — **six times of headroom**, which rules out the obvious reading
  that the ceiling is too tight under CI load, and with it both a targeted increase and a
  retry-on-setup. (It was already raised once, 10s → 30s, for this exact pair of specs.) The same run
  fires the race four times and the phase counts locate every one: `wait-mysystems-rendered` 367
  (= 363 + 4 retries), `click-add-more-games` 365, `click-add-from-folder` 363 — so every *local*
  drift lands during the guarded clicks, which is why the guard has always looked sufficient. Fixed
  by moving the install-landed wait inside the loop; its discriminator is unchanged, so an NR that
  genuinely fails to install still fails on the first attempt rather than three times as slowly.
  Stated as the inference it is: what is measured is the headroom, the race's frequency, and that
  this was the one step of the sequence outside the retry — the local lane reproduces the *race*
  several times a run but has never reproduced the *failure*, because locally the drift never lands
  late enough. Verified to `docs/nr-ui-roster-coverage.md` §5's standard: two consecutive fully-green
  363-spec lanes after the change.
- **A frozen NR-UI setup timeout named neither which wait failed nor what the page was doing** — its
  complete text was `Setup failed: TimeoutException: Timeout 30000ms exceeded.` Playwright names the
  target of a *locator* wait (which is why the v35 nav-link breakage arrived as
  `waiting for Locator("a[href*='MySystems']")` and was actionable) but has nothing to name for a
  `WaitForFunctionAsync`. Setup contains exactly two of those — they are the only two waits in the
  lane that can produce this message — and they mean opposite things: *the route never arrived*
  versus *NR never installed the game data*. One is a re-run, the other a regression, and the output
  distinguished them not at all; `NR_UI_TIMINGS` would have, but it is off in CI by design, and
  `WithDiagnosticsAsync` wraps the roster-creation and force paths rather than setup, so the one
  failure the lane kept producing was the one it captured nothing for. Both now report through
  `NrUiSetup.WaitForSetupConditionAsync`, which reads back the state the condition was testing and
  prints it with the page URL — `Observed: pathname=/app/MyLists, localLibrary=[],
  systemsStore=present` is the lost-page race, the same empty library on `/app/MySystems` is NR
  genuinely failing to install. Kept as a `TimeoutException` deliberately: the retry loop
  discriminates on that type, so a friendlier exception would have opted these waits out of the guard
  that makes them survivable. Guarded by `NrUiSetupFailureMessageRegressionTests`.
- **The roster lane's diagnostics artifact collected nothing** — `BsUiDiagnostics` defaults to
  `Directory.GetCurrentDirectory()/artifacts/bs-ui-diagnostics`, which is right for the CLI and wrong
  for the conformance lane: VSTest runs the test host with its working directory set to the test
  assembly's output folder, so a failing spec wrote its dump to
  `artifacts/bin/BattleScribeSpec.Tests/debug/artifacts/bs-ui-diagnostics/` while CI's "Upload
  diagnostics" step looked at the repo root. Measured locally: 19 dumps in the nested path, 1 at the
  root. The artifact would have been empty for exactly the failures it exists to explain, and
  `if-no-files-found: ignore` would have kept that quiet. `BsRosterUiFixture` now anchors the
  directory at the repo root, which is the fix `TelemetryAssemblyFixture` already documents for the
  telemetry artifact after the same trap; an explicit `BS_UI_DIAGNOSTICS_DIR` still wins.
- **Label ranking: a longer NAME is not decoration, and the rank is taken over what the caller can
  drive** — two defects in the ranking added earlier in this stack, both found by an independent
  review of it rather than by CI. `DECORATED` rejected only a letter-or-digit continuation, and a
  space is neither, so `Armor Type` ranked as decoration of `Armor` and tied with the real
  `Armor • 3pts` row — handing the choice back to `lookupAll` order, which is the tie the ranking
  exists to break. Measured: `Armor`/`Armor Type`, `Trooper`/`Trooper Support` and
  `Bolter`/`Bolter Modifications` all mis-ranked, and an append-name modifier manufactures the shape
  from a single entry. `DECORATED` now allows one space and then requires something that is not more
  name. Separately, the rank was chosen over checkboxes and radios even for callers that scan only
  labelled rows: because the rank is a hard filter, a self-labelled control could set a rank no
  reachable candidate matched and turn a spinner that was present into "Spinner not found" — the
  tree-row bug one population over. `bestLabelMatch` now ranks over the caller's own candidates.

### Removed

- **The unscoped tree-lookup overloads, which nothing called and anything could have** —
  `waitForTreeItem(selector, id)` and `clickTreeItemById(selector, id, doubleClick)` were left behind
  when catalogue lookups gained a force scope, as delegators passing `null` for the container. Both
  had no callers; both were a way to ask the question without the scoping that is the only reason the
  answer can be trusted, and a click is where losing it is least visible — it lands on a real row, in
  a real force, and the caller finds out a poll timeout later while looking somewhere else.
  `clickControlByLabel(text, window, action)` goes with them for the same reason.

### Added

- **`protocol-kitchen-sink` takes a boolean option, and the smoke lane says which controls it drove**
  — kitchen-sink is the one spec the every-push `smoke` job runs against the real BattleScribe app,
  and it drove a spinner, an add button and a radio. Never a checkbox. So the driver's checkbox
  branch — the one control that answers both directions by toggling, and which was wrong in both of
  them until this stack — had no per-push coverage and, as far as the record goes, had never been
  observed running at all: it was written from JavaFX's class list rather than from a panel.
  `se-inf-banner` is max 1, min 0 and costless, which makes it the only child of Infantry Squad that
  can only be present or absent; the spec now takes it and gives it back, after the byte-compared
  export rather than before it — a costless option moves no cost VALUE, but BattleScribe rebuilds the
  roster's cost collection when a selection comes and goes, and it came back `power, pts` where the
  snapshot records `pts, power`. New `BS_UI_PANEL_TRACE=1` prints the control each labelled request
  resolved to, and the smoke step sets it — a passing spec proves an entry was reached, not what was
  clicked to reach it, so without it the coverage claim would be an assumption. It reports
  `'Squad Banner' -> CheckBox (DRIVEN)` on both directions, which is the first record in this repo of
  BattleScribe rendering that control at all. `newrecruit-ui` was opted out of the two steps here on
  the observation that NR's options panel rendered no row for the entry, with why left open; that
  turned out to be the NR-UI export step unmounting the editor, not anything about the entry, and
  both steps now run on every engine — see the Fixed entry below.
- **CI's `thorough-ui-bs` runs both halves of the BattleScribe desktop UI (#355)** — it filtered on
  `Engine=BsGameDataUi`, so the Data Editor had a lane and the Roster Editor had none: every change to
  `BsUiRosterEngine` and `RosterActions.java` reached `main` exercised by unit tests and one teardown
  test. `BsRosterUiConformanceTests` was written to shard on the same trait for exactly this and then
  held back (#355) until its failures were each fixed or declared, which #354 completed. The job gains
  a `suite` matrix axis — one job definition, not two, because the halves need identical artifacts,
  JDK, agent build and `xvfb`, and a copied fifty-line setup block is one that drifts — plus a
  90-minute ceiling where both halves previously inherited GitHub's six-hour default. Still opt-in on
  the same four triggers as every thorough lane. The diagnostics upload also stops pointing the
  gamedata leg at the roster driver's directory, which is what it had been collecting (nothing).

### Fixed

- **The NR-UI roster export navigated out of the editor, and everything after it in the spec had
  nowhere to click** — `ExportRosterXmlAsync` ended by going to `/app`, to leave the browser somewhere
  sane for the next spec. That unmounts NR's roster editor — no `.unitRow`, no `.inputOption` — while
  the Pinia model stays intact, so state READS kept working and only UI-driven mutations broke.
  `protocol-kitchen-sink` is the one spec with actions after its `expectedFile` export, and both of
  them (`selectChildEntry se-inf-banner`, and the `deselectSelection` giving it back) were opted out
  of `newrecruit-ui` as an NR rendering limitation of a costless max-1 upgrade. The entry's shape was
  never involved: NR renders it as a checkbox, and a variant catalogue varying one factor at a time
  (max-1/none/max-2, costed/costless, `upgrade`/`model`, min declared/absent) got a row for all eight
  variants. Replaying the spec's own steps to the failing step with and without the export step
  named it in one comparison — `route=/app unitRows=0` against
  `route=/app/Lists/<id>?view=main` with five rows, `Squad Banner` among them. The export now returns
  to the route it was invoked from (a Vue Router push, so nothing re-fetches and page globals
  survive), keeping `/app` only as the fallback for an export invoked from elsewhere; the next spec's
  clean start never depended on it, since the frozen lane gets that from `Cleanup` and the live lane
  from `Setup`. It also dismisses NR's export menu, which clicking `.ros` leaves mounted in `#popups`
  where it swallows every click aimed at the editor beneath — harmless only while the method
  navigated away afterwards. Both steps now run on every engine.
- **"Hidden entries cannot be selected via UI interaction" was an accusation, not an observation** —
  the message `SelectChildEntryByNameAsync` threw when no row carried the entry named a cause it had
  never checked, against an entry that is not hidden, and that is what kept the export bug above
  filed as an NR limitation. It now reports what the panel offers, the way `DescribeUnitListAsync`
  already does: `route=/app unitRows=0 editing=0 rows=[]` — which is the whole diagnosis, on the
  first run. Fourth occurrence of this defect recorded in `docs/nr-ui-roster-coverage.md`.
- **An edit-panel control is addressed by the closest spelling of a name, not the first one
  containing it** — label lookup matched with `contains`, and the spec corpus is full of names inside
  other names in the same panel: `Armor` inside `Light Armor`, `Heavy Armor` and `Armor Type` (and
  `Armor` is that group's auto-selected default, so all three are rendered at once); `Trigger` inside
  `Alpha Trigger` and `Beta Trigger`; `Unit 1` inside `Unit 10`; `HQ` inside `HQ Unit`; `Weapon`
  inside `Weapon Options`. The control driven was whichever node `lookupAll` yielded first. Candidates
  are now ranked — the label *is* the name, the name followed by decoration (`Sergeant • 12pts`), or
  the name somewhere inside — and only the best rank the window offers is considered. The rank is
  chosen before anything is driven and without consulting the action, so a control that declines to
  act cannot hand the request down to a worse rank — and only over things that carry a control, since
  the scene spells an entry's name in places that are not panel rows (the roster tree renders
  `Trooper` where the panel renders `Trooper • 10pts` beside its spinner). A name with no exact or
  decorated match and only containing ones now fails with the panel's contents listed, where it used
  to drive a neighbour.
- **A call that needs its own timeout passes one instead of re-tuning the shared client** —
  `AgentClient.CallTimeout` was a property seven call sites assigned, used and restored: the FX-thread
  probe (2s), both diagnostic captures (5s), and both drivers' action helpers (90s). Every one of
  them re-tuned a client the rest of the driver shares, for as long as its `finally` took to run —
  and a diagnostic capture that faulted before restoring would leave every later call on a 5s clock.
  `CallAsync` now takes an optional `timeout`, `CallTimeout` is documented as the default for calls
  that name none, and nothing assigns it. The 90s action timeout and the 5s diagnostic timeout become
  named constants next to the reasoning for their size.
- **A checkbox-rendered entry is driven towards the state that was asked for, not away from it** —
  the checkbox branch of `tryClickControlByLabel` never read `action`, so it fired blind and got both
  directions wrong from the wrong starting state. `deselectSelection` on such an entry **ticked an
  unticked box** — adding the selection the caller asked to remove — reported success, and thereby
  skipped the DELETE fallback that would have worked; `selectChildEntry` on an already-ticked one
  **unticked it**, removing the selection it was asked to make. Each then waited out its 10s poll for
  the opposite of what it had just caused. A checkbox is now driven only when it is on the wrong side
  of the request: already-ticked answers `ALREADY_SET` for a select, and already-unticked declines a
  decrement so the DELETE path runs. This is the rule the `"+"` button branch already had; the
  checkbox and radio branches sat directly beneath it without it. Also moves a misplaced
  `@SuppressWarnings("unchecked")` onto the overload that actually casts, which silences a real
  warning rather than an empty one.
- **`setSelectionCount` with a count of zero removes the selection instead of decrementing it** — it
  delegated to `deselectSelection`, and those are not the same operation. `deselectSelection` on a
  collective child steps the PER-MODEL count: one press takes `number` 6 to 3 and the selection
  stays, which is what `collective-per-model-operations` asserts and why its wait accepts
  fewer-than-there-were. Zero instances is not fewer instances, so the delegation reported
  `count: 0` about a selection sitting in the roster at 3. (Before the wait was relaxed it reached
  zero by accident — the wait timed out, the action layer retried, and the second press finished the
  job.) A count of zero now takes the row away through the one control that can, and the DELETE
  fallback both paths share is one method.
- **A control that was already in the asked-for state stopped being reported as a click** —
  grouped-control support gave `tryClickControlByLabel` a radio-button path that returns success
  without firing when the member is already chosen, on the correct grounds that the postcondition
  holds. Its caller then waited for a roster change, which could not come: a full 10s
  `STATE_POLL_TIMEOUT_MS` ending in "clicking control X left parent Y with the same child count" —
  true, and the opposite of what happened. The helper now answers `NOT_FOUND` / `DRIVEN` /
  `ALREADY_SET`, and `selectChildEntry` reads the roster once instead of polling for a delta. If the
  panel says the entry is chosen and the model holds no child for it, that disagreement is thrown
  rather than returned as a step with no `selectionId`. A decrement can no longer produce
  `ALREADY_SET` at all: a radio is a choice and not a count, so like the `"+"` button it declines a
  decrement and lets the DELETE path run.
- **A catalogue-tree lookup scoped to a force stops at that force's child forces** — confining the
  search to the target force's subtree fixed the sibling-force case that had 20 specs adding
  selections to the wrong force. It did not fix the nested case, because a force's subtree *contains*
  its child forces' subtrees, and each of those offers the same catalogue entries again: with a child
  force present, `selectEntry` on the parent could still click the child's copy, add the selection
  there, and time out looking in the parent. Which one it reached was whatever order BattleScribe
  built the tree in — so `force-nested-parent-child-selections` and the other 13 `addChildForce`
  specs were passing on tree order rather than on addressing. The search now refuses to descend into
  a nested force's subtree, with the ids taken from roster state because the tree cannot say which of
  its nodes is a force: every node renders the same `Name:id:…` shape.
- **The agent's object-graph walks answer the same way twice, and say when they gave up** — both
  walks enumerated fields with `Class.getDeclaredFields()`, whose order the JDK documents as
  unspecified, and both then answered order-sensitive questions off it: `findObjectById` returns the
  first match it reaches, and `matchConstraintOwner` keeps the first kind-match as its fallback. A
  run that attributed an error correctly was therefore no evidence about the next run. Fields are now
  taken in name order. Both walks also stopped at a 10 000-object ceiling and reported that as a
  plain negative — no instances, or no such id — so "not reached" and "not present" were the same
  answer; hitting the ceiling now prints which one it was. The two copies of the traversal are one
  method, which is what let their ceiling checks sit in different places to begin with.
- **Validation attribution stopped re-deriving the same answers per error** — resolving one error's
  `from` can reach `resolveRefFromMessage`, which asks for four classes by name and walks the object
  graph once per class; each ask was a linear scan of every class the JVM has loaded, and each walk
  was uncached. `constraintValuesOf` was a roster search per candidate constraint, per segment, per
  message check. A roster with N errors paid all of it N times over a model that cannot change while
  the call runs. `findClass` now remembers what it finds for the session (hits only — a class not
  loaded yet may be loaded later); `collectInstances` and `constraintValuesOf` remember for the
  duration of ONE `getValidationErrors` call and forget when it returns. Per call and not per
  session, because the roster changes between calls: an entry absent now exists after the next
  selection, and a session-scoped "not found" would outlive the fact that produced it. Widening the
  message-resolution fallback from "the id list is ambiguous" to "the message contradicts the id
  list" is what made this reachable often enough to matter.
- **A fractional cost limit is refused on both routes into BattleScribe, not one** — the New Roster
  dialog's spinner already declined a `defaultCostLimit` it could not spell, on the stated grounds
  that 0.25 entered as 0 puts every selection over a limit the game system never declared. The Edit
  Roster route reached by `setCostLimit` cast the same kind of value to `int` instead, so the answer
  the dialog refused to invent was invented the moment a roster already existed. Both now ask one
  rule, `BsUiCostLimits`, which also refuses a negative limit (the format's "no limit", which an
  untouched spinner already means, and which the spinner would clamp to a real 0) and a value past
  `Spinner<Integer>`'s range. A refusal is printed, because its only other symptom is a limit that is
  simply absent — indistinguishable from BattleScribe ignoring one. No spec sets a fractional limit
  through `setCostLimit` today; the rule is one file with its own tests precisely so the two routes
  cannot drift apart again, and so a route later found to carry a fractional or per-type limit is one
  edit rather than a hunt for call sites.
- **The BS Roster UI lane's last five failures (#354)** — all five turned out to be defects, not
  expectations to write down, and the lane reaches 367/367 with **no spec declared
  `battlescribe-ui: fail` for any of them**.
  - **Validation attribution stopped trusting an id list the app's own message refutes.**
    `getValidationErrorIds()` is not per-error — it lists ids an ELEMENT knows about, and one
    element carries every error raised under it. On `constraint-shared-flag` the force reports a
    single id naming `con-max-shared` while carrying three errors, two raised by `con-max-per-link`,
    which appears in no list anywhere. The value tiebreak was gated on `size() > 1`, so a
    one-element list skipped it and answered `con-max-shared` for a message reading `(maximum 2)` —
    a constraint whose limit is 3. The rendered value now decides at any list size, and
    message-resolution only wins when it can point at a constraint whose declared limit the text
    quotes, so hidden-entry errors (no value on either side) keep their path.
  - **The in-process adapter had the same defect in the other direction**, and it is fixed rather
    than documented: `ResolveEntryFromMessage` consulted entry links only when the target carried no
    kind-matching constraint of its own, so on `constraint-entry-link-merged` it returned the
    target's `con-shared-max` (value 4) for 3 selections and a `(maximum 2)` message. Links are now
    asked for a value match first. All three engines agree, so the spec's base expectation becomes
    the app's answer and the `newrecruit` override that existed to disagree with it is gone.
  - **`deselectSelection` destroyed what it was asked to decrement.** A collective child's control
    steps the per-model count, so one press takes `number` 6 to 3 and the selection stays — but the
    wait demanded it disappear, the action layer retried, and the second press took it to 0. Now the
    wait ends on gone *or* fewer-than-there-were, and `removed` reports which. The same helper would
    have fired an instanced entry's "+" for a decrement request, adding one while reporting a
    removal; it now declines and lets the DELETE fallback run.
  - **Two entry links onto one shared entry are two rows**, not one. They render spelled identically
    (BattleScribe labels a control with what a link RESOLVES to) and the panel exposes no id, so
    label lookup always drove the first. Recorded as possibly unfixable; it is not — the panel
    offers one row per child in declaration order, so the driver indexes how many earlier siblings
    share each entry's label and the agent skips that many controls. The `dataSource` path indexes
    names without tracking parents and keeps first-match behaviour.
  - **`collective-instance-amount` gains the one genuine expectation**, measured: asked for three of
    an instanced entry, the app's "+" makes three sibling selections costing 32, and only the
    COLLECTIVE child rides along into the copies — the gap to the 36 that store-direct semantics
    predict is exactly the two non-collective Badges it declines to duplicate.
- **An `expectedFile` step no longer passes when the engine cannot export (#309)** —
  `RosterRunner.ExecuteFileAssertion` opened with `catch (NotSupportedException) { return; }`, so an
  engine reporting no roster export made every byte-compare pass while comparing nothing. #326 fixed
  the *trigger* (the host wired `RosterXmlExporter` for `battlescribe-ui` only, so three of four
  engines reported "unsupported" over the protocol and every `expectedFile` assertion silently
  no-op'd) but left the swallow, which would restore the silence for the next engine, external
  adapter, or regression. An undeclared capability gap is now a **failure** naming the engine and
  both opt-outs, and opting out is the spec's job — step-level `skipEngines`, or spec-level
  `engines: {…: skip}` — exactly as `loadRoster`/`reload` already worked. No spec needed a new
  opt-out: all four engines export. Related repairs along the same path:
  - **`skipEngines` now applies to assertion steps.** The check lived inside `ExecuteAction`, so
    `skipEngines` on an `expectedState`/`expectedFile` step was silently inert — harmless while
    assertions could not trip over a capability gap, wrong now that they can, since it is the very
    declaration the new failure message asks for.
  - **`BsUiRosterEngine` implements `IRosterEngine.ExportRosterXml`** instead of exposing an
    async-only export that `ServeCommand` type-tested for. Driven in-process (any
    `new RosterRunner(engine)`, not just through `bs-engine-host`) it would otherwise have hit the
    interface default and failed every byte-compare. Its `ExportRosterXmlAsync` now throws instead of
    returning null when the agent answers without an `xml` field: null is the adapter layer's
    "unsupported" signal, so a malformed reply used to reach the runner as a capability gap — and
    silently pass. `ServeCommand`'s fork is gone, and the capability gate no longer accepts an
    async-only export as proof the host can export.
  - **The harness reports skipped steps.** `SpecResult.SkippedSteps` carries what a spec opted an
    engine out of, surfaced by `bs-spec run`, `run --all` (console, `--json`, GitHub step summary)
    and the xUnit conformance suites — so a spec that skipped half its assertions no longer reads
    exactly like one that ran them all.

  `GameDataRunner.ExecuteFileAssertion` never had the swallow (its `ExportActiveFile` call was
  unguarded, so the default throw was already recorded as a failure); it keeps that behaviour, now
  pinned by tests and with the same actionable message. It has no step-level `skipEngines` and needs
  none: all three gamedata export specs exist to byte-compare an export, so skipping the assertion
  would leave an empty spec behind.
- **NR roster export intermittently exported from the lists index instead of the editor** —
  `FrozenNrRosterConformanceTests` / `protocol/protocol-kitchen-sink` step 41 failed roughly a
  quarter of the time with `no mounted component exposes exportRos() after 15s at /app/MyLists`.
  The trailing pathname was the whole story: `exportRos()` is a method on NR's **editor page
  component** and exists nowhere else, so a 15-second hunt for it on the lists index could only
  ever end one way. The driver was not losing a mount race — it was on the wrong page, and the
  polled mount wait added in #328 could not help (measured 4/8 failures with it, 6/8 → 2/8 here).
  Cause, read out of the recorded app bundle and then confirmed at runtime: NR's editor page does
  not resolve the `:list` route param against the whole store. It calls
  `findListByKey(key, [selectedSystem.id, selectedSystem.bsid])`, which **first filters `listData`
  down to the rows owned by the currently selected game system**; a row owned by any other system
  is invisible to it, and the page then falls through `findMostRecentList(selectedSystem.id)` to
  `router.push({name:'app-MyLists'})`. So `router.push('/app/Lists/<key>')` was landing and being
  bounced straight back out. Instrumenting the store at the moment of failure showed exactly that:
  the roster's own system was the only entry in `localLibrary` and its row *was* present in
  `listData`, yet `selectedSystem` was a **previous spec's** game system (`gs-1` while the roster
  belonged to `ks-gs`), NR's own lookup returned null, and `router.afterEach` recorded the push to
  `/app/Lists/<key>` immediately followed by a redirect to `/app/MyLists`. The intermittency is
  engine reuse, not timing: a pooled browser context runs dozens of specs, `library.array` retains
  every system it ever loaded, and which stale system is selected when a given spec reaches its
  export step depends on the order `Parallel.ForEachAsync` happened to hand specs to that engine.
  The driver now re-asserts the selection to the system that owns the roster **before** pushing —
  and proves NR's own `findListByKey` resolves the key first, so the navigation is only attempted
  once it cannot bounce. After the push it confirms the app is actually on `app-Lists` with the
  expected `:list` param (re-read after a tick, since `router.push()` resolves before a page-level
  guard can redirect), and the mount wait is now bounded by the route as well as the clock: leaving
  the editor route fails immediately with the route it landed on instead of burning 15 seconds to
  report a component missing from a page that was never going to have it.
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
  (The polled wait is correct and stays, but it turned out **not** to be what made step 41 flaky —
  the `location.pathname` it started reporting is what identified the real cause; see *NR roster
  export intermittently exported from the lists index instead of the editor* above.)
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
