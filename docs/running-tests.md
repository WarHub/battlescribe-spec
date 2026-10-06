# Running the tests

The commands are in [AGENTS.md, "Build & test"](../AGENTS.md#build--test). That block is the one place
they are written down; the skills and the other docs link to it rather than copy it, because every copy
of the one-spec command this repository had went stale on its own schedule. This page is the model
behind those commands: what starts when you run one, how a test profile becomes a lane, what makes a run
fail although no test did, and what CI adds.

## The test app

Both test projects — `tests/BattleScribeSpec.Tests.csproj` and
`tests/BattleScribeSpec.Cli.Tests/BattleScribeSpec.Cli.Tests.csproj` — are **Microsoft.Testing.Platform**
applications built on xunit.v3. Each builds to an executable
(`artifacts/bin/BattleScribeSpec.Tests/debug/BattleScribeSpec.Tests.exe`, without the `.exe` off Windows)
and the tests run inside that process; there is no separate test host. `global.json`'s
`"test": {"runner": "Microsoft.Testing.Platform"}` is what puts `dotnet test` on the platform — without it
the .NET 10 SDK runs `dotnet test` the old way, which these projects cannot run at all
(`ToolchainPinDriftTests.GlobalJson_SelectsTheTestingPlatformRunner`).

| You run | What starts | What you see |
|---|---|---|
| `dotnet test --project <csproj> …` | the SDK builds, then starts the executable with `--server dotnettestcli` and the arguments MSBuild carries | one line per test project and the totals; a test's output only when it fails |
| `dotnet run --project <csproj> -- …` | the SDK builds, then starts the executable with the arguments MSBuild carries and those after `--` | what the app prints: the profile it resolved, every failure, the totals, the composition verdict — and every result as it finishes with `--output Detailed` |
| the executable itself | the executable | as `dotnet run` |
| an IDE's test explorer | the executable, with `--server` | the IDE's own view (see [IDEs](#ides)) |

Every way in goes through one entry point. Each project's `Program.cs` is one call to
`tests/TestProfiles/TestHost.cs`, so no way of starting the suites can half-apply a profile.
`tests/Directory.Build.props` makes the projects platform apps, turns off xunit's generated entry point,
and carries the arguments described below; every test project must live under `tests/` to inherit it
(`TestHostWiringTests.EveryTestProject_InheritsTheTestHostProps`).

The working directory does not matter. xunit sets it to the test assembly's output folder however the
app starts, and tests find their checkout from their own binaries (`RepoRoot.FromBinaries`); a
working-directory call in test code is a compile error (`tests/BannedSymbols.txt`). A run started from
anywhere tests the checkout its binaries came from.

To see what is running on a busy machine — another agent's suite from a sibling worktree, say — look for
the executables. The path in `CommandLine` names the checkout:

```powershell
Get-CimInstance Win32_Process -Filter "Name like 'BattleScribeSpec.%Tests.exe'" | Select-Object ProcessId, CreationDate, CommandLine
```

## A new worktree

A worktree builds and tests nothing until `setup.ps1` has provisioned it — the `.deps/wham` submodule,
`lib/` and `.testdata/` — and the fixture lookups stop at its own root, so it never borrows the main
checkout's. To take what `setup.ps1` downloads from a checkout that already has it:

```powershell
./setup.ps1 -LinkFrom <provisioned checkout>
```

The test data, the BattleScribe app, ASM and the Liberica JDK come from the same place in that checkout,
as hard links: seconds, and no GitHub token. Each must first pass, there, the check that lets a download
already on disk stand, against this worktree's pins — `testdata.json`'s tag and `sha256`, which
`TestDataPinDriftTests` holds a working copy to — and one the other checkout lacks or holds at another
pin is refused, by name: linked unverified, it would replay a snapshot this worktree does not pin. The Java
agent and the engine-patch tool are built from this worktree's sources, as always.

Hard links rather than junctions, because `git worktree remove` follows a junction and empties the
directory it points to; deleting a hard link deletes only its name. They share their bytes with the other
checkout, so write nothing into `lib/` or `.testdata/` — what the build makes goes under `src/` and
`artifacts/` — and run `./setup.ps1` without `-LinkFrom` to replace them with downloads of your own.

## Test profiles

A **test profile** is a whole lane: a test filter, the environment the lane depends on, and the test
assemblies it covers. Every profile, every engine lane and every environment switch is recorded once, in
`tests/TestProfiles/`:

| File | What it records |
|---|---|
| `TestProfiles.cs` | every profile: its name, its purpose, its selection, the environment it sets, the assemblies it covers, and the lanes it lets skip whole |
| `EngineLanes.cs` | every engine lane (each `Engine` trait value): what it needs, how much of it `pre-push` runs and the measured cost behind that decision, its own test classes, and the hint printed when it comes up empty |
| `Knobs.cs` | every `NR_*`, `BS_*`, `BSSPEC_*` and `BSUI_*` environment variable the code reads, classified by what it does to a run |
| `Selection.cs` | how a selection becomes a filter, and which lanes it claims |

**Naming a profile.** Under `dotnet test`, pass `-p:TestProfile=<name>`: MSBuild turns the property into
`--test-profile <name>` on the app's command line. To the app itself — after the `--` of a `dotnet run`,
or on the executable's command line — pass `--test-profile <name>`. The property also reaches a
`dotnet run`, by the same MSBuild route, but nothing here uses it that way: on a run the app is started
for directly, nothing can tell a profile that was dropped on the way from one that was never given.

**Listing them.** `dotnet run --project tests/BattleScribeSpec.Tests.csproj -- --list-test-profiles`
prints every profile with its filter, the environment it sets and its purpose; add `--json` for a
machine-readable form. `dotnet test` refuses the option, because it counts a run that reports no test as a
failure.

**Which project.** `pre-push` covers both test projects, `cli` the CLI's tests, and every other profile
the main test project. `dotnet test -p:TestProfile=<name>` with no `--project` starts both projects, and
the one outside the profile refuses the run (exit 5), naming the `--project` to use.

**What the app does with a profile**, printing a `[test-profile]` line for each decision:

1. it refuses a command line the profile cannot be combined with ([below](#refused-command-lines-exit-5));
2. it applies each environment switch that concerns the lane ([next](#environment-switches)), and prints
   it with `(profile)` or `(caller)`;
3. it builds the filter: the profile's, ANDed with yours when you pass `--filter` — `(P)&(U)` — so a
   `--filter` **narrows** a profile and never replaces it;
4. it runs under the strict zero-tests policy, and afterwards holds the run to the profile's claims
   ([below](#when-a-run-fails-although-no-test-did)).

**Without a profile** a run is your own `--filter`, under the strict policy, with no claims to meet.

## Environment switches

`Knobs.cs` classifies every switch the code reads, and
`TestProfileRegistryTests.EveryKnobLiteral_IsClassified` fails on a literal it does not list. **No switch
changes which tests a lane runs** — that is the profile's filter, over test names and traits — so a value
exported in your shell tunes a run and never shrinks it:

| Kind | What it does | In a profiled run |
|---|---|---|
| Default | tunes how a lane runs — endpoint URLs (`NR_ENGINE_URL`, `NR_EDITOR_URL`), `NR_HEADLESS`, `NR_VISUAL`, `NR_SLOW_MO`, timeouts, paths, diagnostics | your value wherever you set one, printed `(caller)`; otherwise the profile's, if it gives one |
| Internal | set by the harness, on itself or on a child process it starts | not yours to set |
| Retired | read by nothing; `ConcurrencyPolicy` answers what each one used to | — |

`Knobs.cs` says what each switch does. Some uses, and what replaced the switches that used to select tests:

- **Watch a live lane:** export `NR_HEADLESS=false` (and `NR_VISUAL=true` to open the roster editor after
  setup; `NR_SLOW_MO=<ms>` slows each browser action down). No profile sets them, so your values reach the
  run, and the profile supplies the URL:

  ```powershell
  $env:NR_HEADLESS = "false"; $env:NR_VISUAL = "true"
  dotnet test --project tests/BattleScribeSpec.Tests.csproj -p:TestProfile=nr-live
  ```

- **Sit out a lane:** narrow the profile with `--filter "Engine!=<lane>"` — `--filter "Engine!=BsRosterUi"`
  keeps `core` off the desktop app — or run a narrower profile. A run you narrow with `--filter` is not held
  to the lanes its profile claims ([below](#the-engine-composition-check)). A lane whose snapshot or app is
  missing skips by itself, as before.
- **Put a few specs through the NR UI roster driver:** `bs-spec run --engine newrecruit --ui <spec>` replays
  the frozen HAR when `NR_ENGINE_URL` is unset, one spec per run. The one-browser shape over every spec is
  the `nr-ui-frozen` lane.
- **Rewrite snapshot side-files:** `bs-spec run --update-snapshots <spec>` on each engine — the base engine
  first, then the family-canonical engine, then variants; `--on-diverge base|override` answers the
  base-or-override question without a prompt. No test run rewrites a snapshot: a gate that rewrites what it
  checks passes by construction.
- **Debug one spec step by step:** `bs-spec run` ([AGENTS.md, "Debugging specs"](../AGENTS.md#debugging-specs)),
  which replaced the per-spec sequential NR classes.

## When a run fails although no test did

### The strict zero-tests policy

Every `dotnet test` and `dotnet run`, and every profiled run however it starts, runs under
`--zero-tests-policy strict`: a run that executed no test fails with exit 8, and a run whose every
selected test skipped counts as one that executed none. Both silent greens CI has had were exactly that —
a kitchen-sink filter that matched no test, and one that matched a single row that skipped — and each
reported success. The only ways around it are deliberate: an unprofiled run of the executable itself that
names another policy, an IDE session ([below](#ides)), and `-automated` or `@@`, which hand the run to
xunit's own console runner.

`dotnet test` and `dotnet run` get the policy from `tests/Directory.Build.props`, through
`TestingPlatformCommandLineArguments` (its one setter, `TestHostWiringTests.StrictZeroTestPolicy_HasOneSetter`).
The app adds it to a run of the executable that names no policy, and refuses a `dotnet test` run that
arrives without it: the profile travels by the same route, so a run without the policy is one whose
MSBuild-carried arguments were dropped. A spec an engine opts out of (`engines: <engine>: skip`) is a skipped row, never
a passed one, so it cannot make a lane look executed.

### The engine-composition check

Strict judges a run as a whole, and `pre-push` claims six lanes and runs three thousand tests. On a
machine where `setup.ps1` never fetched the HAR, both frozen NR roster lanes would skip whole and the run
would still pass. So a **profiled run also passes only if every engine lane its profile claims executed at
least one of its own tests**; otherwise it exits 8 and names each empty lane with its fix
(`tests/TestProfiles/LaneComposition.cs`). A result counts towards a lane by the class that declares it,
not by its `Engine` trait: six `Engine=FrozenNrUiRoster` regression facts pass on a blank page with no
HAR at all, and must not stand in for the lane they share the trait with. Without the Playwright browsers
the run fails harder — the tests that launch Chromium themselves fail (exit 2) — and still names every
lane that went empty.

The claims come from the profile's selection: `lint` and `non-conformance` claim no lane, and a lane the
profile lets skip (`core`'s `BsRosterUi`, the only one) is exempt. The check does not run for an
unprofiled run, a run you narrowed with `--filter` (one spec through one lane need not reach the others),
`--list-tests`, `--help` or `--info`, or an IDE session; a run that already failed keeps its own exit code.
After the check the app prints a `[composition]` line, appends the per-lane table to
`$GITHUB_STEP_SUMMARY`, and writes `artifacts/telemetry/xunit-<profile>-<timestamp>.composition.json`
beside the run's telemetry.

### Refused command lines (exit 5)

The app refuses, with exit 5 and a `[test-profile]` message that names the fix, a command line that would
run less than it says:

| Refused | Instead |
|---|---|
| a VSTest option: `--settings` (which `-p:RunSettingsFilePath` becomes), `--logger`, `--collect`, `--blame*` | a lane is a profile; results go to the console, and in GitHub Actions to the GitHub reporter; `--report-xunit-trx` writes a TRX file; `--timeout <duration>` stops a hung run |
| `TESTINGPLATFORM_EXITCODE_IGNORE` in the environment | unset it — it turns the exit codes it names into a pass |
| `--test-profile`, `--filter` or `--zero-tests-policy` given twice | give one; join filters into one expression, `(A)&(B)` |
| a `dotnet test` run without the MSBuild-carried arguments — `--test-modules`, or a `.dll` named in place of a project | name the project: `--project <csproj>` |
| an unknown profile, or one that does not cover the project | the message lists the profiles, or names the project |
| with a profile: `--ignore-exit-code`, `--config-file`, `--xunit-config-filename`, a response file (`@file`), the `--filter-*` options, a zero-tests policy other than strict | each overrides the lane's verdict or its selection; narrow with `--filter "<expression>"` |
| `--list-test-profiles` under `dotnet test` | ask the app: `dotnet run --project <csproj> -- --list-test-profiles` |
| `--test-profile` with `-automated` or `@@` | those start xunit's own console runner, which knows no profiles |

`TestHostTests` holds every rule, each with the input that trips it.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | every executed test passed, and in a profiled run every lane it claims executed |
| 2 | a test failed |
| 3 | the session was aborted |
| 5 | the command line was refused; the message says why |
| 7 | the test process died |
| 8 | nothing executed: the selection matched nothing, every selected test skipped, or a lane the profile claims executed none of its own tests |
| other | another of the platform's codes (linked below), or the process failing before the platform could report one (an unhandled exception, a signal) |

The platform's own codes are listed at <https://aka.ms/testingplatform/exitcodes>; this app adds no new
one, and uses 5 and 8 for its own refusals and its composition check.

## Selecting tests by name

A per-spec theory row is named `<Namespace>.<Class>.<Method> [<category>/<id>]` and carries the spec's tags
as `Tag` traits, so `--filter "DisplayName~<id>"` and `--filter "Tag=cost"` select a spec's rows in every
per-spec lane. Five lanes are not per-spec: `FrozenNrRoster`, `FrozenNrUiRoster`, `FrozenNrGameDataUi`,
`LiveNrRoster` and `LiveNrUiRoster` run their specs inside single `[Fact]`s — in parallel over a pool of
browser contexts, or one after another in one warm browser for the two roster UI drivers. The live two are
one `AllSpecs` each. The frozen three are split in two tests that partition the specs: `KitchenSink` and
`OtherSpecs`, so selecting the engine runs the lane whole, each spec once, and a smoke profile selects its
lane without `OtherSpecs` (`Selection.KitchenSink`, a `FullyQualifiedName!=` clause). `pre-push` takes
`FrozenNrUiRoster` the same way, because the whole of it is ~27 minutes. These names carry no spec id and
no `Tag`, so neither spec filter can narrow them. Each test reports what it ran on a
`[lane] <Engine> mode=<full|kitchen-sink|other-specs> selected=N applicable=M` line, then on an
`[i/N] <spec> <verdict> <secs>s` line per spec, which a run you start directly shows with
`--show-live-output on`. One that selects no spec fails, and a cancelled one says how far it got.

A bare `--filter "DisplayName~<id>"` reaches more than the spec's offline lanes: the desktop-app lane
(`BsRosterUi`, which launches the BattleScribe app wherever `setup.ps1` provisioned it) and the live rows,
which skip without their URL. Narrowing `pre-push` instead — AGENTS.md's one-spec line — runs the spec
through every offline per-spec engine, plus its lint and schema rows.

The **real-world tests** (`RealWorld*` classes, 21 tests) load the Warhammer 40k 9th edition data that
`./setup.ps1` clones into `.testdata/wh40k-9e` unless told `-SkipWh40k` (`WH40K_DATA_DIR` points them at
another copy). `pre-push` includes them, and they skip where the data is absent — most of its skips on
such a machine. To run only them, unprofiled:
`dotnet test --project tests/BattleScribeSpec.Tests.csproj --filter "FullyQualifiedName~RealWorld"`;
without the data every row skips, and the run exits 8.

## Proving a guard can fail

A guard — a test or lint meant to go red when something breaks — passing the repository says nothing about
whether it can fail. Prove it once, and keep the proof where it is cheap:

- **A text guard keeps broken samples.** A lint over YAML, JSON, MSBuild or Dockerfiles applies the same
  function to a clean sample, which must pass, and to one break of it per row, each of which it must reject
  (`tests/Infrastructure/LintSamples.cs`; `SpecLintTests`, `GameDataSpecLintTests`, `TestDataPinDriftTests`
  and `ToolchainPinDriftTests` keep samples). The proof is a test in `lint`, re-run free on every change.
- **C# guard logic gets `tools/mutate.mjs`** (the AGENTS.md line; its usage heads the script). Each
  mutation is a text replacement and the `--filter` of the tests that must fail. It runs a green baseline
  first, builds only for a mutation the build reads, restores every file from a byte copy — never from git,
  so uncommitted work survives — and names every mutation that was not killed.

Stryker.NET does not reach these guards (#529): it mutates only projects that are not test projects, and
the guards are compiled into the test apps, `tests/TestProfiles/` included. Against a `src/` project it
drives `BattleScribeSpec.Cli.Tests` as it is.

## Parallelism

Each test assembly declares its xunit parallelism once, in its `xunit.runner.json`
(`"parallelMode": "collections"`, `"maxParallelThreads": "0.5x"`). `ConcurrencyConfigurationDriftTests` pins
both files and refuses a second record — xunit's `--max-threads`, `--parallel` or `--parallel-algorithm` in CI,
scripts or the registry, or an assembly-level parallelism attribute — and
`TestHostWiringTests.NoImplicitTestInputs` keeps out a `testconfig.json`, which could carry one. Inside a
lane, the engine pools are sized by `ConcurrencyPolicy` — see [concurrency-policy-measurements.md](concurrency-policy-measurements.md).

## In CI

Every test step runs one profile through the app:

```bash
dotnet run --project tests/BattleScribeSpec.Tests.csproj --no-build -- --test-profile <name>
```

under `xvfb-run -a` for the desktop-app profiles. `dotnet run` rather than `dotnet test`, because
`dotnet test` withholds a passing test's output: a 27-minute aggregate lane would show nothing until it
ended, and a hang would show nothing at all. The CLI's tests are the one `dotnet test` step
(`dotnet test --project tests/BattleScribeSpec.Cli.Tests/BattleScribeSpec.Cli.Tests.csproj --no-build -p:TestProfile=cli --output Detailed`),
so CI still exercises that path: server mode, the arguments MSBuild carries, and the app's refusal when
they do not arrive.

In GitHub Actions (`GITHUB_ACTIONS=true`) the app adds, unless the command line already has them:

- `--report-github --report-github-summary-include-passed false` — an annotation on each failure and a
  failures-only step summary (GitHubActionsTestLogger);
- on a run it was started for directly, `--output Detailed` and `--show-slowest-tests 10`;
- for a profile that claims an aggregate lane, `--show-live-output on`, so the lane streams its `[i/N]`
  progress.

Which lane runs in which job, and when: the CI steps name their profiles (`--list-test-profiles` says what each
runs), and [`scripts/ci-gate.json`](../scripts/ci-gate.json) says when a run owes the thorough and live jobs.
Every job runs on Linux except `windows-pre-push`, which runs `pre-push` whole on every PR, a step per test
project — so a local `pre-push` is optional.
`CiProfileLaneTests` holds every test step to one profile that covers its project, with no filter, VSTest
option or zero-tests policy of its own; `CiWorkflowDriftTests` holds each to a timeout and one invocation with nothing that could swallow its exit
code. The same command rules apply to every test command in AGENTS.md, README.md, `docs/` and the skills
(`CiProfileLaneTests.DocumentedTestCommands_RunAsWritten`), and every profile they name must exist
(`CiProfileLaneTests.NoDanglingProfileReferences`).

## Reporting and telemetry

- **Results:** the console, as above. In GitHub Actions, annotations and a failures-only step summary. A TRX
  file on demand with `--report-xunit-trx`; CI keeps none.
- **The harness's telemetry:** every run of the app writes an OpenTelemetry artifact set,
  `artifacts/telemetry/xunit-<profile>-<timestamp>.*` (`unprofiled` for a run without a profile), and prints a
  trace summary headed with the profile; in CI the summary goes to the step summary too. It stays on your
  machine. See [telemetry.md](telemetry.md).
- **Usage telemetry** is a different thing: Microsoft.Testing.Platform sends usage data to Microsoft by
  default, and so does the .NET SDK for a run started through `dotnet test` or `dotnet run`.
  `DOTNET_CLI_TELEMETRY_OPTOUT=1` turns off both; `TESTINGPLATFORM_TELEMETRY_OPTOUT=1` only the
  platform's, and CI's workflow sets it for every job. That telemetry is the tools' own: no test in
  `pre-push` reaches any site.

## IDEs

An IDE's test explorer starts the app with `--server` (no value, or `jsonrpc`), and the app passes such a
session through untouched: no profile, no strict policy, no composition check. Run a lane from the command
line.

## Coming from VSTest

The suites ran on VSTest until the move to Microsoft.Testing.Platform. What replaced what:

| On VSTest | Now |
|---|---|
| one runsettings file per profile, which `-p:TestProfile` named | the registry in `tests/TestProfiles/`; `-p:TestProfile=<name>` still works, as `--test-profile <name>` |
| a profile's environment variables overrode your shell | your values win, and none of them changes which tests run ([above](#environment-switches)) |
| CI ran `dotnet test` through a wrapper script, with inline filters | each step runs a profile through the app with `dotnet run` |
| `--logger trx`, `--logger "console;verbosity=detailed"` | `--report-xunit-trx`; `--output Detailed` |
| tests ran in `testhost.exe` | tests run in `BattleScribeSpec.Tests.exe` and `BattleScribeSpec.Cli.Tests.exe` |
| `--blame-hang` | step timeouts in CI; `--timeout <duration>` locally |
| a test project that executed nothing passed | exit 8 |
