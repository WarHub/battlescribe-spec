# AGENTS.md

BattleScribe Spec — declarative conformance test suite for BattleScribe roster engines.
YAML specs in `specs/` define setup, actions, and expected state. SpecRunner executes them.

## What is normative: the app, not our adapter

**When a UI driver and a store-direct driver for the SAME app disagree, the UI is right.** The app
is the specification; our adapters are attempts at it. A store-direct adapter exists because it is
fast and because it came first — neither is a claim to correctness.

So the resolution order for a divergence inside one engine family is:

1. What the real application does, driven through its own UI, is the normative behaviour.
2. The spec records that as the family's expectation.
3. If the store-direct adapter differs, THAT is the finding — either a bug in it, or a
   documented per-engine override naming it as a deviation. It is never the reason to bend the UI
   driver into agreement.

This cuts against the reflex to make the newer driver match the established one, which is why it is
written down. Concretely, on `battlescribe-ui` versus `battlescribe`:

- `constraint/constraint-entry-link-merged` — **worked through, and the outcome is the point.**
  BattleScribe's own message says `(maximum 2)`, the LINK's constraint. The IKVM adapter reported
  the target's `con-shared-max`, value 4, for 3 selections — a limit the message rules out — because
  its message-matching kept the target's kind-match as a fallback and returned it without ever
  asking the link. The UI was right, so the finding was a bug in the adapter and the adapter was
  fixed (2026-08-09). All three engines now agree, `newrecruit` lost the override it had needed to
  disagree with the base, and the spec carries no per-engine block at all.
  **This is the resolution to prefer**: step 3 offers a documented override OR a bug fix, and a
  divergence that turns out to be one implementation's artefact should end with one fewer override
  in the suite, not one more.
- Cost values — the desktop UI reports the raw double BattleScribe computed
  (`0.30000000000000004`); the IKVM adapter converts to decimal on the way out (`0.3`). The UI is
  the less processed answer, and the specs pin it under `battlescribe-ui`.

A spec whose expectation was written against a store-direct adapter alone has never been checked
against the app. Finding that out is what these lanes are for.

## Project status: Experimental

This project is in an **experimental stage**. All interfaces, formats, conventions, and
architecture are subject to change without notice. There is **no backward compatibility
guarantee** — breaking changes are not just allowed but actively encouraged when they
improve architecture, code quality, or reduce tech debt. Prefer bold restructuring over
incremental workarounds. When in doubt, choose the cleaner design.

## Issues and the backlog

The backlog lives on the [Conformance Spec board](https://github.com/orgs/WarHub/projects/2). All
open issues are on it; the board adds no work of its own.

**Five things live in metadata, not labels.** Read and write them through the API — a label that
looks like one of these is legacy and does not feed the board. The first three are fields; the last
two are links.

| | Where it lives | Values |
|---|---|---|
| **Type** | repo issue type | `Epic`, `Feature`, `Task`, `Bug` |
| **Priority** | org issue field | `Urgent`, `High`, `Medium`, `Low` |
| **Size** | org issue field | `XS`, `S`, `M`, `L`, `XL` |
| **Parent** | sub-issue link | see below |
| **Blocked by** | dependency link | see below |

`Priority` and `Size` are **organization-level issue fields**, shared across every WarHub repo. They
are not project fields — a project-field query returns them with an empty option list, which reads
as "unconfigured" and is not. Read them from the issue:

```bash
gh api graphql -f query='query{repository(owner:"WarHub",name:"battlescribe-spec"){issue(number:419){ issueType{name} parent{number} issueFieldValues(first:10){nodes{... on IssueFieldSingleSelectValue{name field{... on IssueFieldCommon{name}}}}} }}}'
```

Write with the `updateIssueIssueType` and `setIssueFieldValue` GraphQL mutations — `gh issue edit`
cannot set any of them. `setIssueFieldValue` takes a **list** of field writes; passing `fieldId` and
`singleSelectOptionId` as flat arguments on `input` is rejected:

```bash
gh api graphql -f query='mutation{ setIssueFieldValue(input:{issueId:"I_…", issueFields:[{fieldId:"IFSS_…", singleSelectOptionId:"IFSSO_…"},{fieldId:"IFSS_…", singleSelectOptionId:"IFSSO_…"}]}){issue{number}} }'
```

The read query above returns field *names* but neither of the ids you need to write. Field ids are
reachable by widening that query with `... on Node{id}` — but **option** ids are not, because only
the concrete `IssueFieldSingleSelect` type exposes `options`. (Widening with `... on
IssueFieldCommon{id}` instead is a query error, not an empty result: that interface has no `id`.)
Get both from the concrete type, against any issue that already carries the values you want:

```bash
gh api graphql -f query='query{repository(owner:"WarHub",name:"battlescribe-spec"){issue(number:279){ issueFieldValues(first:10){nodes{... on IssueFieldSingleSelectValue{ name optionId field{... on IssueFieldSingleSelect{ id name options{id name} }} }}} }}}'
```

Issue-type ids come from `repository{issueTypes(first:10){nodes{id name}}}`, and the type is set with
`updateIssueIssueType(input:{issueId:"I_…", issueTypeId:"IT_…"})`.

**Parentage is a real link.** Writing `Part of #N` in an issue body creates no link — the issue
stays unparented in the API and on the board. Use the sub-issue API, and note that `sub_issue_id`
is the child's **database id**, not its number:

```bash
gh api --method POST repos/WarHub/battlescribe-spec/issues/419/sub_issues -F sub_issue_id=$(gh api repos/WarHub/battlescribe-spec/issues/421 --jq .id)
```

Children keep insertion order, so add them in the order they should read. Link a new child **at
creation time**, and **delete the body-text equivalents** — the `Part of #N` line on the child and
any `## Children` checklist on the parent. Prose and metadata drift apart, and only the metadata
drives the hierarchy and the progress rollup.

**Dependencies are a real link too**, and a separate one. A `## Depends on` list in a body is prose
GitHub does not parse, for exactly the reason `Part of #N` is not parentage. GitHub has native
**blocked-by / blocking** relations; use them for "this cannot start until that lands", and keep
sub-issues for "this is part of that". A blocker does not have to be a sibling, or in the same epic.
Like `sub_issues`, the endpoint takes the other issue's **database id**, not its number:

```bash
gh api --method POST repos/WarHub/battlescribe-spec/issues/281/dependencies/blocked_by -F issue_id=$(gh api repos/WarHub/battlescribe-spec/issues/450 --jq .id)
```

```bash
gh api repos/WarHub/battlescribe-spec/issues/281/dependencies/blocked_by --jq '.[] | "\(.number) \(.title)"'
gh api repos/WarHub/battlescribe-spec/issues/281/dependencies/blocking   --jq '.[] | "\(.number) \(.title)"'
```

Unlink with `DELETE …/dependencies/blocked_by/{database id}`. The POST and DELETE responses are the
whole issue object — pipe through `--jq .issue_dependencies_summary` unless you want a screenful.

Link only **real** constraints: a blocker that merely *relates* overstates it — if the work can
proceed with an opt-out or against one engine, it is not blocked. But once a link is real it
**stays**. Closing the blocker satisfies the row, it does not make it noise — the link is the record
of what unblocked the work, and it outlives the blocker exactly as parentage outlives a closed
epic. Unlink only a constraint that was never real. As with parentage, **delete the body-text
equivalent** once the link exists.

**Labels are for what fields cannot express**: `area: *`, `needs-design`, `go:*`, `release:*`,
`thorough-ci`, `scheduled-ci-failure`. The `type:*` and `priority:*` label sets were deleted on
2026-08-13 after their values were migrated into the fields above — do not recreate them. A label
that restates a field is a second record that drifts from the first.

## Build & test

```bash
dotnet restore && dotnet build                                                     # first time
dotnet test -p:TestProfile=pre-push                                                # offline gate (no app); CI runs it, locally optional
dotnet test --project tests/BattleScribeSpec.Tests.csproj -p:TestProfile=pre-push --filter "DisplayName~my-spec-id"  # one spec: its lint + every per-spec offline engine (not the aggregate NR lanes)
dotnet run --project tests/BattleScribeSpec.Tests.csproj --no-build -- --test-profile bs --output Detailed   # a lane, every result as it finishes
dotnet run --project tests/BattleScribeSpec.Tests.csproj --no-build -- --list-test-profiles # what each profile runs
```

**This block is the one home for these commands** — the skills and the other docs link here rather than
copy them. **[docs/running-tests.md](docs/running-tests.md) is the model behind them**: the test app and
its one entry point, test profiles and the registry, environment switches, the zero-tests policy and the
engine-composition check, every refusal and exit code, and why CI runs its lanes with `dotnet run`. The
suites run on Microsoft.Testing.Platform, and each test project is an executable whose entry point
(`tests/TestProfiles/TestHost.cs`) resolves the profile itself, so
`dotnet test --project <csproj> -p:TestProfile=<name>`, `dotnet run --project <csproj> -- --test-profile <name>`
and the executable run the same lane. What to know before reading a result:

- **A run that executes no test fails with exit 8** — every selected test skipping counts — **and so does
  a profiled run in which a lane the profile claims executed none of its own tests**: on a machine
  without the HAR or the NR Editor snapshot `setup.ps1` fetches, `pre-push` exits 8 naming each empty lane
  and the fix, though thousands of other tests passed. A run you narrow with `--filter` is not held to the
  lanes.
- **A command line that would run less than it says is refused with exit 5, saying why**: an unknown
  profile; a profile that does not cover the project (a solution-wide `dotnet test` starts both test
  projects, so name the project for a profile that covers one); a VSTest option; or a lane-defining
  variable exported in your shell that the profile does not allow.
- **A `--filter` you add narrows a profile** (the two are ANDed); it never replaces it.
- **`dotnet test` shows a test's output only when it fails.** `dotnet run` shows what the app prints — the
  profile it resolved, every failure, the composition verdict — and every result with `--output Detailed`.

**The SDK band is pinned, and CI installs from `global.json`.** `rollForward: latestPatch` holds the
feature band; every `setup-dotnet` step uses `global-json-file: global.json`, so your machine and CI
run the same analyzers. (In `ci.yml` that step lives in `.github/actions/setup`, the one composite
action every building job sets up through — SDK, bot token, caches, JDK, `setup.ps1` — each job stating
what it needs in its `with:`.) This matters because `AnalysisLevel=latest-recommended` +
`TreatWarningsAsErrors=true` make the set of rules that can fail the build a property of the
installed SDK — unpinned, a runner-image bump turns untouched code red. Bumping the band is
Dependabot's job (`dotnet-sdk` ecosystem), and reviewing that PR is where a widened rule set gets
dealt with. `ToolchainPinDriftTests` fails if a workflow or composite-action step starts picking its
own SDK again, or if `docker/`'s SDK image tag leaves the pinned band. One SDK-derived pin is
invisible to Dependabot and must move by hand **in the same PR** as an SDK bump: the
`mcr.microsoft.com/dotnet/sdk` tag in `docker/`.

**What makes CI run the thorough suites is one file: [`scripts/ci-gate.json`](scripts/ci-gate.json).**
A PR that edits one of its `thoroughInputs` — `testdata.json`, `Directory.Packages.props`,
`global.json`, the CI definition, the test profiles and projects, among others, each with the reason
beside it — runs them; the gate reads the changed files, not the author or a label. Package and SDK
bumps swap out what the engines are built from (IKVM compiles the BattleScribe engine; Playwright
ships the browser every NR UI driver drives), so kitchen-sink is not enough for them. **Every PR whose
base is not `main` — every layer of a stack — runs them too**, with no label. The live NR lane
(`nr-conformance`) is a separate decision: it runs on a schedule, a manual dispatch, the `thorough-ci`
label, or an input edit on a PR to `main` — never just because a PR is stacked, so a restack cannot put
ten conformance runs on newrecruit.eu at once. `ci-gate` is red on a draft ("draft: nothing ran") and
accepts a skipped thorough or live job only when the gate said it was not owed.
`scripts/thorough-inputs.mjs` and `scripts/ci-gate.mjs` hold the rules (unit-tested with
`node --test scripts/*.test.mjs`); `CiWorkflowDriftTests` holds the workflow to them — every job and
every test step has a timeout (the job's holding all its steps'), every `steps.<id>` resolves, a test
step names its project literally and is one invocation with nothing that could swallow its exit code.

**`checks` is the analyzer gate; every other CI build is `dotnet build -p:FunctionalBuild=true`.** The
switch (`Directory.Build.props`) turns off the analyzers, code-style enforcement and XML doc generation
in this repository's projects — the same binaries, ~25s sooner per job. The vendored `.deps/wham`
keeps its own settings: its `Directory.Build.props` does not import ours. `CiWorkflowDriftTests`
keeps `checks` building with the gates on (no switch, and no property that turns analyzers, code
style, docs or `TreatWarningsAsErrors` off), every other `ci.yml` build using the switch, and the
switch's properties out of any CI command line. Locally the switch is a faster inner loop, but run a
plain `dotnet build` before pushing — `checks` will.

**The `docker` CI job builds `docker/bs-spec.Dockerfile` on every push, and runs the image.** It
exists because nothing built these files and both rotted unnoticed — one referenced a project renamed
away four months earlier, the other missed two `ProjectReference`s and shipped on a base image lacking
a shared framework it needs. A stale `COPY` list is only wrong relative to a project graph that
moves, so no lint rule finds it; building the image does. The image ships **no engine** (the built-in
one needs third-party jars from a token-gated archive) — bring your own adapter as a connectable.

**There are no NuGet lock files, on purpose.** `Directory.Packages.props` pins every version exactly,
with transitive pinning on, and is the single record of what a restore resolves. The lock files it
used to be shadowed by went stale on every Dependabot bump that reached a project only through a
`ProjectReference`, and failed CI for it; the comment at the top of that file has the history. A
package change is a one-line edit there and nothing else.

**Local `pre-push` is optional.** Run the tests you touched (`--filter`), the `lint` profile, or a
`smoke-*` profile, and push when they pass: CI runs `pre-push` whole on Windows on every PR
(`windows-pre-push`, which `ci-gate` requires) beside the Linux jobs, and that is the verdict. It is the
**offline** gate: lint, the in-process BattleScribe engines (roster + gamedata), and every frozen NR
lane — HAR replay, the local NR Editor snapshot, and the two frozen Playwright UI drivers. No desktop
app, and no test reaches any site. The tools send usage telemetry of their own by default — the test
platform, and the .NET SDK under `dotnet test` or `dotnet run`. `DOTNET_CLI_TELEMETRY_OPTOUT=1` turns
off both, `TESTINGPLATFORM_TELEMETRY_OPTOUT=1` (which CI sets) only the platform's. `pre-push` needs
what `setup.ps1` provisions, and says so rather than leaving a green run that never ran a lane: a
frozen lane that skips whole for want of its snapshot fails the run with exit 8, naming the lane and
the fix; with no Playwright browsers the browser tests fail outright (exit 2), and the lanes that went
empty are named with the fix all the same. Its skips are specs that opt an engine out and, without the
wh40k data, the real-world tests. The critical path is the in-process `BsRoster` lane, not any UI lane.

**Not in `pre-push`:** the BattleScribe desktop-app lanes (`bs-ui-roster`, `bs-ui-gamedata` — run them
when you touch the BS UI drivers or `src/bs-ui-java-agent/`), the live lanes that open sessions on
third-party sites (`nr-live*`, `nr-ui-live`, `nr-editor-live`, `nr-editor-ui-live`), and `Mode=Sequential`
classes (manual-only, behind `NR_SEQUENTIAL`).
`dotnet run --project tests/BattleScribeSpec.Tests.csproj --no-build -- --list-test-profiles` lists every
profile with its purpose.

Adding a lane is a decision, not a default (`BsRosterUi` once joined the gate by silence, #405). Every
engine lane is a row in `tests/TestProfiles/EngineLanes.cs` saying what it needs and whether `pre-push`
runs it, and `pre-push`'s filter is derived from that column. `TestProfileRegistryTests.EveryEngineTraitInTheAssembly_IsDeclared`
fails if an `Engine` trait appears with no row, `PrePushHonoursItsPromise` fails if a lane that needs
the desktop app or a third party's site is put in, and `CiProfileLaneTests.EveryEngineLane_IsRunByCi_OrSaysWhyNot`
fails on a lane no CI step runs that gives no `CiExempt` reason.

**Test profiles are defined in `tests/TestProfiles/TestProfiles.cs`**, the one record of every lane: its
selection, the environment it sets, the assemblies it covers. **A profile is a whole lane**: every CI
step that runs `BattleScribeSpec.Tests` runs one, with no filter of its own and no `env:` entry that
changes what runs, so `-p:TestProfile=<name>` on your machine selects what CI selects under that name,
with the same switches — `nr-ui-frozen` included, which is the full ~27-minute NR UI roster lane (it
sets `NR_UI_ROSTER_FULL`; `smoke-nr-ui` is the one-spec smoke). `CiProfileLaneTests` holds CI to that,
and fails if a lane-defining or profile-owned switch appears anywhere under `.github/`. Two things can
still differ. A lane the profile lets a CI runner skip whole (its `MaySkip`) runs on your machine and
skips in CI: `core` claims `BsRosterUi`, which drives the desktop app here and skips on CI's offline
runners, which do not provision it. A switch exported in your own shell is held to its kind
([docs/running-tests.md](docs/running-tests.md#environment-switches)). Every
`NR_*`/`BS_*`/`BSSPEC_*`/`BSUI_*` variable the code names is classified in `tests/TestProfiles/Knobs.cs`
(`TestProfileRegistryTests.EveryKnobLiteral_IsClassified`): a **lane-defining** one — `NR_UI_ROSTER_FILTER`,
`NR_FROZEN_SKIP`, `BSSPEC_UPDATE_SNAPSHOTS`, … — takes the profile's value or must be unset, and a profiled
run with a different value exported is refused (exit 5) rather than silently narrowed, skipped or turned
into a snapshot rewrite; to use one, run without a profile and narrow with `--filter`. The one exception
is `BS_UI_SKIP=true` with `core`, which lets its desktop-app lane skip. A **default** one — a URL,
headless mode, slow-mo — is yours where you set it: to watch a live lane, set `NR_HEADLESS=false` (and
`NR_VISUAL=true`), and the run prints `(caller)` beside each value you supplied. `--list-test-profiles`
(the last command in the block above) lists every profile with its purpose, filter and environment.

## NR frozen tests and HAR

The frozen NR tests replay a **single HAR snapshot of the entire NR web application** (JS
bundles, CSS, assets). This is NOT per-spec — all specs run against the same HAR. Adding or
editing specs requires no HAR changes; new specs work immediately. The HAR is versioned by
NR client version (pinned in `testdata.json`), updated separately via
[WarHub/newrecruit-har](https://github.com/WarHub/newrecruit-har) releases.

The pin has two halves: a `tag` naming the release and a `sha256` map naming the bytes it must
contain. `setup.ps1` refuses a download that misses the hash, and `TestDataPinDriftTests` fails a
working copy whose fixture is not what the pin declares — so a HAR swapped in by hand is a failed
lint run, not a green suite replaying something nobody chose.


**A snapshot is a freeze over a moving target.** NR is actively developed; each bump replaces the
whole application, and the app owes us nothing — routes, controls, stores and messages all change
between snapshots. Two consequences for anything written against it:

- **Do not hard-code a client version into driver code, comments or docs.** Describe the behaviour
  and why it matters, not the release that introduced it. A workaround pinned to "v35.76" reads as
  obsolete the moment the pin moves, when the code is usually still needed.
- **Prefer contracts NR is least likely to move**, and fail loudly when one does. Routes over navbar
  controls; store actions over rendered text. A driver that silently no-ops when NR renames something
  hands back a green run that proves nothing — see `NrUiSetup.SuppressServerSaveNoticeAsync` for the
  shape (install, verify, throw with the fix in the message).

Anything that must reach into the app rather than drive it belongs behind a named helper that says
what it assumes, so the next bump breaks it in one place with a message that names the assumption.

## NR Editor frozen tests

The frozen NR Editor GameData tests serve the **gh-pages static deployment** of the
[NR Editor](https://github.com/giloushaker/nr-editor) locally via Playwright route
interception. No network access needed. The static files are downloaded by `setup.ps1`, which
checks out the **exact commit** pinned in `testdata.json` (fetch-by-SHA, so the pin holds after
it stops being the `gh-pages` tip) and **fails** if that commit cannot be obtained — it never
substitutes the branch tip. Re-pinning is a deliberate edit to `testdata.json`, and because a
`testdata.json` change swaps out what the frozen suites replay, any PR touching that file runs
the thorough jobs — the NR Editor lanes in `thorough-conformance` among them.

## BS desktop UI tests (local)

Two profiles drive the **real BattleScribe desktop app** through the Java agent — the Data Editor
and the Roster Editor. Mutations go through the real UI; state is read via the Java model. After
`setup.ps1` (which downloads the BattleScribe app + Liberica full JDK and builds the agent), run:

```bash
dotnet test --project tests/BattleScribeSpec.Tests.csproj -p:TestProfile=bs-ui-gamedata   # Data Editor  (Engine=BsGameDataUi)
dotnet test --project tests/BattleScribeSpec.Tests.csproj -p:TestProfile=bs-ui-roster     # Roster Editor (Engine=BsRosterUi) — every roster spec, ~13 min
```

**Neither is in `pre-push`**, and that is deliberate: they need the app, a display, and minutes.
CI's `thorough-ui-bs` job runs both halves whole, but nothing runs them on your machine unless you
do — so run them when you touch `BsUiRosterEngine`, `BsGameDataUiEngine`, or
`src/bs-ui-java-agent/`.

The JavaFX-capable JDK is auto-discovered (`BS_UI_JAVA_PATH` → `lib/liberica-jdk` → `JAVA_HOME`),
so neither local runs nor CI need to set anything. Without the app, the JDK or the agent jar the tests
skip — and a profile that runs those lanes (`bs-ui-roster`, `bs-ui-gamedata`, `smoke-bs-gamedata-ui`) then
exits 8, naming the lane and `setup.ps1`, rather than passing on nothing. `core` is the one profile that
lets `BsRosterUi` skip: CI's offline runners do not provision the app.

## Telemetry

`bs-spec run --all`/`compare` and every run of the test app (`dotnet test`, `dotnet run`) emit
OpenTelemetry traces + metrics — a `.traces.pb`/`.metrics.pb` artifact under
`artifacts/telemetry/run-<id>.*` (or `compare-a/b-<id>.*`, `xunit-<profile>-<timestamp>.*`), plus a
trace-summary table (wall time, cold-starts vs warm-reuses, peak live resources) printed after the
run and appended to `$GITHUB_STEP_SUMMARY` in CI. Use
`bs-spec compare --config-a "" --config-b "SOME_ENV=1"` to prove a config change is
**verdict-neutral** before shipping it as an optimization — it asserts identical per-spec pass/fail
before reporting any timing delta, exits non-zero on divergence, and exits 8 when neither arm
executed anything (as `run --all` does when its selection runs nothing). See
[docs/telemetry.md](docs/telemetry.md) for the full model (spans/metrics emitted, the
parent-as-collector design, reading the artifact, known limitations).

## Debugging specs

Use `bs-spec` to run a spec step-by-step and inspect full roster state:

```bash
dotnet run --project src/BattleScribeSpec.Cli -- run selection-publication             # by spec ID
dotnet run --project src/BattleScribeSpec.Cli -- run --all-steps protocol/kitchen-sink # dump after every step
dotnet run --project src/BattleScribeSpec.Cli -- run --engine newrecruit --json spec.yaml # NR engine, JSON output
dotnet run --project src/BattleScribeSpec.Cli -- export-xml cost/cost-hidden-limit-validation ./out/
```

Verbs: `run` (execute + assert), `probe` (open a UI engine for inspection), `export-xml`,
`format`. Engine selection is orthogonal: `--engine {battlescribe,newrecruit}`, `--ui` to
drive the real app, and the domain (roster/gamedata) is inferred from the spec path
(override with `--gamedata`/`--roster`). `run` options include `--all-steps`,
`--output {tree,json}` (or `--json`), `--headed`, `--screenshots <dir>`, `--timeline <file>`,
`--record <file>`, `--save-roster <dir>`, and `--break <n>`. Concurrency and engine reuse are not
flags: `ConcurrencyPolicy` derives them from the machine, the engine, and where the engine's traffic
lands. `--policy reuse=on|off,reuse-roster=…,reuse-gamedata=…` overrides the reuse decisions for
diagnosis; `--policy workers=N` applies to `run --all` (a batch has workers) and is **rejected** on a
single-spec `run`, which has exactly one — a flag is honoured or refused, never silently dropped.
`--policy` cannot raise the load on a third party's live site, and it cannot be delivered to an
`exec:`/`dotnet:` adapter at all. **Nor can `run` force reuse ON for a domain the engine has not
earned** — `ReuseSafe*` is a claim `bs-spec compare` has demonstrated, and forcing it in a one-arm
`run` cannot test that claim, only produce a faster answer that may be wrong (it changed six verdicts
on `newrecruit-ui` once, which is why `compare` exists). That ablation belongs in `compare`, where it
stays allowed and the other arm catches the divergence; `reuse=off` is legal everywhere.
(`--workers` and `--keep-alive` are deleted.)
Specs can include `action: dump` steps for explicit dump points.

## After editing specs

```bash
pwsh -File tools/format-specs.ps1                                                  # auto-fix formatting
```

## Key files

| Path | What |
|------|------|
| `specs/roster/{category}/{id}.yaml` | Roster spec files (403 total, 24 categories) |
| `specs/gamedata/{category}/{id}.yaml` | GameData spec files (120 total, 23 categories) |
| `docs/error-assertions.md` | The two ways a spec is about something going wrong, and they are not the same: `expectedState.errors` asserts the validation list of a roster the engine **accepted**; `expectFailure` asserts an action the engine **refused** |
| `docs/running-tests.md` | How the tests run: the test app and its entry point, test profiles, environment switches, the zero-tests policy and the engine-composition check, refusals and exit codes, what CI adds. The commands themselves are in "Build & test" above |
| `src/BattleScribeSpec.TestKit/RepoRoot.cs` | Repo-root resolution (`BattleScribeSpec.slnx` marker) — the ONE implementation; never inline another walk. Tests use `RepoRoot.FromBinaries` (`TestPaths.Root` when they need a checkout), never the working directory |
| `tests/BannedSymbols.txt` | What test code may not call — the working directory and the CLI lookups that read it — enforced at compile time (RS0030) by BannedApiAnalyzers in both test projects |
| `src/BattleScribeSpec.TestKit/Protocol/ProtocolMessages.cs` | All Protocol setup types |
| `src/BattleScribeSpec.TestKit/Roster/RosterTypes.cs` | Roster state records |
| `src/BattleScribeSpec.TestKit/Roster/RosterSpecModels.cs` | Roster YAML spec model classes |
| `src/BattleScribeSpec.TestKit/Roster/RosterRunner.cs` | Roster assertion engine + dump callback |
| `src/BattleScribeSpec.TestKit/GameData/IGameDataEngine.cs` | GameData engine interface |
| `src/BattleScribeSpec.TestKit/GameData/GameDataTypes.cs` | GameData state records |
| `src/BattleScribeSpec.TestKit/GameData/GameDataSpecModels.cs` | GameData YAML spec model classes |
| `src/BattleScribeSpec.TestKit/GameData/GameDataRunner.cs` | GameData assertion engine |
| `src/BattleScribeSpec.NewRecruit/NewRecruitGameDataEngine.cs` | NR Editor GameData adapter (live + frozen) |
| `src/BattleScribeSpec.NrGameDataUiDriver/NrGameDataUiEngine.cs` | NR Editor GameData UI driver (Playwright UI) |
| `src/BattleScribeSpec.NrGameDataUiDriver/NrGameDataUiActions.cs` | NR GameData UI mutations + state reads |
| `src/BattleScribeSpec.NrGameDataUiDriver/NrGameDataUiSetup.cs` | NR GameData UI file loading + static routing |
| `src/BattleScribeSpec.BsGameDataUiDriver/BsGameDataUiEngine.cs` | BS Data Editor UI driver (Java agent RPC) |
| `src/BattleScribeSpec.BsGameDataUiDriver/BsGameDataUiDiagnostics.cs` | BS GameData UI diagnostics |
| `src/bs-ui-java-agent/src/bsspec/uiagent/DataEditorActions.java` | BS Data Editor Java agent stubs (need probing) |
| `src/BattleScribeSpec.Cli/Program.cs` | bs-spec console app (run/probe/export-xml/format) |
| `src/BattleScribeSpec.TestKit/Protocol/AdapterHandler.cs` | Action dispatch |
| `tests/Infrastructure/SpecLintTests.cs` | Roster lint rules, known tags |
| `tests/Infrastructure/GameDataSpecLintTests.cs` | GameData lint rules |
| `tests/Infrastructure/FrozenNrGameDataFixture.cs` | Frozen NR Editor GameData fixture |
| `tests/TestProfiles/` | The test-profile registry — every profile (`TestProfiles.cs`), engine lane (`EngineLanes.cs`) and environment switch (`Knobs.cs`) — the test app's entry point that resolves it (`TestHost.cs`), and the engine-composition check every profiled run is held to (`LaneComposition.cs`) |
| `tests/Infrastructure/CiProfileLaneTests.cs` | CI held to the registry: every test step runs one profile and adds nothing, no lane switch under `.github/`, xvfb and diagnostics uploads derived from the lanes, every engine lane run by CI or exempt, no dangling profile reference, every documented test command runs as written |
| `tests/Infrastructure/TestHostTests.cs`, `TestHostWiringTests.cs`, `LaneCompositionTests.cs` | The entry point's rules, each with the input that trips it; every test project wired through it, one setter of the strict zero-tests policy, no launch profile, testconfig or runsettings feeding the app; the composition check's verdicts |
| `tests/Infrastructure/AggregateLaneRun.cs` | The `[lane]` selection line, `[i/N]` progress and stop check every single-test aggregate lane reports through |
| `tools/format-specs.ps1` | Spec formatter |

