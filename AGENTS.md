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

**Four things are fields, not labels.** Read and write them through the API — a label that looks
like one of these is legacy and does not feed the board.

| | Where it lives | Values |
|---|---|---|
| **Type** | repo issue type | `Epic`, `Feature`, `Task`, `Bug` |
| **Priority** | org issue field | `Urgent`, `High`, `Medium`, `Low` |
| **Size** | org issue field | `XS`, `S`, `M`, `L`, `XL` |
| **Parent** | sub-issue link | see below |

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

The read query above returns field *names* but not the ids you need to write, and `IssueFieldCommon`
has **no `id` field** — asking for one is a query error, not an empty result. Get ids from the
concrete type, against any issue that already carries the values you want:

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
drives the hierarchy and the progress rollup. See `.squad/decisions/decisions.md` — "Sub-issue
parentage is a real link, not body prose".

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

Link only **live** constraints. A closed blocker adds a satisfied row that reads as noise, and a
blocker that merely *relates* overstates the constraint — if the work can proceed with an opt-out or
against one engine, it is not blocked. As with parentage, **delete the body-text equivalent** once
the link exists.

**Labels are for what fields cannot express**: `area: *`, `needs-design`, `squad:*`, `go:*`,
`release:*`, `thorough-ci`, `scheduled-ci-failure`. The `type:*` and `priority:*` label sets were
deleted on 2026-08-13 after their values were migrated into the fields above — do not recreate them.
A label that restates a field is a second record that drifts from the first.

## Build & test

```bash
dotnet restore && dotnet build                                                     # first time
dotnet test -p:TestProfile=pre-push                                                # offline gate (~4.5 min, no app)
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "DisplayName~my-spec-id"  # one spec
```

**The SDK band is pinned, and CI installs from `global.json`.** `rollForward: latestPatch` holds the
feature band; every `setup-dotnet` step uses `global-json-file: global.json`, so your machine and CI
run the same analyzers. This matters because `AnalysisLevel=latest-recommended` +
`TreatWarningsAsErrors=true` make the set of rules that can fail the build a property of the
installed SDK — unpinned, a runner-image bump turns untouched code red. Bumping the band is
Dependabot's job (`dotnet-sdk` ecosystem), and reviewing that PR is where a widened rule set gets
dealt with. `ToolchainPinDriftTests` fails if a workflow step starts picking its own SDK again, or if
`docker/`'s SDK image tag leaves the pinned band. Two SDK-derived pins are invisible to Dependabot and
must move by hand **in the same PR** as an SDK bump: `Directory.Build.targets`' `KnownILLinkPack` (see
the comment there) and the `mcr.microsoft.com/dotnet/sdk` tag in `docker/`.

**The `docker` CI job builds `docker/bs-spec.Dockerfile` on every push, and runs the image.** It
exists because nothing built these files and both rotted unnoticed — one referenced a project renamed
away four months earlier, the other missed two `ProjectReference`s and shipped on a base image lacking
a shared framework it needs. A stale `COPY` list is only wrong relative to a project graph that
moves, so no lint rule finds it; building the image does. The image ships **no engine** (the built-in
one needs third-party jars from a token-gated archive) — bring your own adapter as a connectable.

**Lock files are real.** Every project has a `packages.lock.json` and CI verifies it
(`dotnet restore --locked-mode`, `checks` job). If a restore rewrites one, that is a **finding, not
noise** — do not revert it. Regenerate with `dotnet restore --force-evaluate` and commit the result;
`git add` normalises the CRLF that NuGet writes on Windows, so only genuinely-changed files remain.

**Always run `pre-push` before pushing.** It is the **offline** gate: lint, the in-process
BattleScribe engines (roster + gamedata), and every frozen NR lane — HAR replay, the local NR Editor
snapshot, and the two frozen Playwright UI drivers. No network, no desktop app.
**Measured 2026-08-12 on a 32-core dev box: `Failed: 0, Passed: 2571, Skipped: 0, Total: 2571,
Duration: 4 m 27 s`** for `BattleScribeSpec.Tests`, plus 126 tests / 53s for
`BattleScribeSpec.Cli.Tests` — 5m47s end to end including the build. The critical path is `BsRoster`
(367 specs, 267s of in-process engine), not any UI lane. It was **11m29s** before #405.

**What `pre-push` deliberately does NOT cover**, so you know when to run something else yourself:

| Not in `pre-push` | Run it with | Covered in CI by |
|---|---|---|
| `BsRosterUi`, `BsGameDataUi` — launch the real BattleScribe desktop app | `-p:TestProfile=bs-ui-roster` / `bs-ui-gamedata` | `thorough-ui-bs` (sharded, opt-in) |
| `LiveNr*` — traffic to a third party's production site | `-p:TestProfile=nr-live*`, `nr-editor-*-live` | `nr-conformance` (opt-in) |
| `Mode=Sequential` — manual-only, gated behind `NR_SEQUENTIAL` | `NR_SEQUENTIAL=1` + the matching profile | — |

That table is enforced, not aspirational:
`ConcurrencyConfigurationDriftTests.EveryEngineLane_IsADeliberateDecisionInThePrePushProfile` fails
if a new `Engine` trait appears that `pre-push.runsettings` neither runs nor explicitly excludes.
Adding a lane is therefore a decision, not a default — which it was not when `BsRosterUi` arrived and
quietly spent 688.8s of a 689.2s run driving the desktop app, in a profile advertised here at `~40s`
(#405). Note the `~40s` had stopped being true well before that: even with no UI lane at all,
`BsRoster` alone is over three minutes now.

Other profiles: `core` (offline suite, no NR engines), `lint`, `bs`, `nr-frozen`, `nr-ui-frozen`,
`nr-editor-frozen`, `nr-editor-live`, `nr-editor-ui-frozen`, `nr-editor-ui-live`, `bs-ui-roster`,
`bs-ui-gamedata`, `nr-live`, `nr-live-smoke`, `nr-live-conformance`, `nr-live-visible`,
`nr-ui-live`, `nr-ui-live-visible`. CI runs entirely through these profiles
(`.github/workflows/ci.yml`).

## NR frozen tests and HAR

The frozen NR tests replay a **single HAR snapshot of the entire NR web application** (JS
bundles, CSS, assets). This is NOT per-spec — all specs run against the same HAR. Adding or
editing specs requires no HAR changes; new specs work immediately. The HAR is versioned by
NR client version (pinned in `testdata.json`), updated separately via
[WarHub/newrecruit-har](https://github.com/WarHub/newrecruit-har) releases.

## NR Editor frozen tests

The frozen NR Editor GameData tests serve the **gh-pages static deployment** of the
[NR Editor](https://github.com/giloushaker/nr-editor) locally via Playwright route
interception. No network access needed. The static files are downloaded by `setup.ps1`, which
checks out the **exact commit** pinned in `testdata.json` (fetch-by-SHA, so the pin holds after
it stops being the `gh-pages` tip) and **fails** if that commit cannot be obtained — it never
substitutes the branch tip. Re-pinning is a deliberate edit to `testdata.json`, and because a
`testdata.json` change swaps out what the frozen suites replay, any PR touching that file runs
the full `thorough-conformance` lane.

## BS desktop UI tests (local)

Two profiles drive the **real BattleScribe desktop app** through the Java agent — the Data Editor
and the Roster Editor. Mutations go through the real UI; state is read via the Java model. After
`setup.ps1` (which downloads the BattleScribe app + Liberica full JDK and builds the agent), run:

```bash
dotnet test -p:TestProfile=bs-ui-gamedata   # Data Editor  (Engine=BsGameDataUi)
dotnet test -p:TestProfile=bs-ui-roster     # Roster Editor (Engine=BsRosterUi) — 367 specs, ~11.5 min
```

**Neither is in `pre-push`**, and that is deliberate: they need the app, a display, and minutes.
CI's `thorough-ui-bs` job runs both halves sharded, but nothing runs them on your machine unless you
do — so run them when you touch `BsUiRosterEngine`, `BsGameDataUiEngine`, or
`src/bs-ui-java-agent/`.

The JavaFX-capable JDK is auto-discovered (`BS_UI_JAVA_PATH` → `lib/liberica-jdk` → `JAVA_HOME`),
so neither local runs nor CI need to set anything. Tests self-skip when BS artifacts are absent.

## Telemetry

`bs-spec run --all`/`compare` and `dotnet test` all emit OpenTelemetry traces + metrics — a
`.traces.pb`/`.metrics.pb` artifact under `artifacts/telemetry/run-<id>.*` (or `compare-a/b-<id>.*`,
`xunit-<timestamp>.*`), plus a trace-summary table (wall time, cold-starts vs warm-reuses, peak
live resources) printed after the run and appended to `$GITHUB_STEP_SUMMARY` in CI. Use
`bs-spec compare --config-a "" --config-b "SOME_ENV=1"` to prove a config change is
**verdict-neutral** before shipping it as an optimization — it asserts identical per-spec
pass/fail before reporting any timing delta, and exits non-zero on divergence. See
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
| `specs/roster/{category}/{id}.yaml` | Roster spec files (373 total, 23 categories) |
| `specs/gamedata/{category}/{id}.yaml` | GameData spec files (113 total, 22 categories) |
| `src/BattleScribeSpec.TestKit/RepoRoot.cs` | Repo-root resolution (`BattleScribeSpec.slnx` marker) — the ONE implementation; never inline another walk |
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
| `tools/format-specs.ps1` | Spec formatter |

