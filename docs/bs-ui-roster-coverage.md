# BS UI roster driver — measured coverage

`battlescribe-ui` (roster domain) drives BattleScribe's desktop Roster Editor through a Java agent
injected into the JVM: real dialogs, real tree clicks, state read back from the Java model. The lane
was added in #353 and its 83 failures were unclassified, which is why it was deliberately not wired
into `ci.yml` — a permanently-red job is worse than no job. This document is the classification, and
what it turned into. It is wired in now; see [In CI](#in-ci).

## Where it went

| | first measurement | now (in CI) |
|---|---:|---:|
| Specs selected | 367 | 367 |
| **Passed** | **284 (77%)** | **367 (100%)** |
| Failed | 83 | 0 |
| Wall-clock | 29m02s | 3m55s / 3m36s, 2 shards in parallel (jobs 6m33s / 6m20s) |

**Zero regressions** against the first measurement, spec-for-spec, at every step. The first
measurement was reproduced exactly on a second run before anything was changed — which matters more
than usual here, because a third of those failures were timeouts, and a timeout that moves between
runs is a different problem from one that does not.

The 18 minutes — 29m02s to 10m50s, both measured unsharded, before the lane was split for CI — are
almost entirely 10-second state polls that no longer run out.

Both figures are from confirmed runs, not computed ones. The 362/5 row this table used to carry was
re-measured before anything in that session changed — 362 passed, 5 failed, 11m52s — because the
commit that preceded it had altered how two of the five failed, and the recorded number predated it.

## The classification

The shapes the issue first recorded were counted off console text, which merges causes that read
alike. Grouping by what actually failed split them differently, and that regrouping is most of the
work:

- the single "46 specs time out in `selectEntryAction`" was **28 specs and three unrelated causes**;
- the "12 specs where the UI does not surface a validation error" was **25 specs where the UI
  surfaces it correctly** and only the `from` attribution is missing, plus 4 where there really is
  no error.

| | was | fixed | left | cause |
|---|---:|---:|---:|---|
| A | 28 | 28 | 0 | `selectEntry`/`selectChildEntry` state-change timeouts — three causes |
| E | 25 | 25 | 0 | validation error produced, `from` unresolved |
| B | 6 | 6 | 0 | BattleScribe deletes the staged `.cat` as corrupt |
| J | 8 | 8 | 0 | cost mismatches — float drift and lane inheritance |
| K | 9 | 8 + 2 declared | 0 | value mismatches, mostly lane inheritance |
| D | 4 | 2 + 2 declared | 0 | no validation error produced at all |
| G | 4 | 3 + 1 declared | 0 | edit-panel control not found by label |
| C | 2 | 2 | 0 | `CategoryLink must have an ID` |
| F | 2 | implemented + 2 declared | 0 | `SetupFromFiles` unimplemented (`dataSource` specs) |

## What was actually wrong

### The lane never inherited BattleScribe's own expectations

`ConformanceTestBase` passed ONE engine name to `RosterRunner`, so a lane running
`battlescribe-ui` resolved every per-engine `expectedState` under its own name, found none, and fell
through to the **base** assertion — the one written for the engine whose behaviour differs. Twenty
roster specs carry a `battlescribe:` override precisely because BattleScribe diverges there.

`ordering/ordering-categories` is the clean case: the base expects NR's category-definition order
(`Zulu, Alpha, Mike`), the `battlescribe:` override expects the render-layer alphabetical order, and
the lane observed `Alpha Unit` and called it a failure against the base.

**A lane defect, not a driver one**, and invisible from the failure text: the spec reports a plain
value mismatch with no hint that a correct expectation for this engine is sitting in the same file.
The frozen NR-UI lane had already been fixed this way; this one had not. 16 specs.

### `#treeCatalogue` is not per-force

It holds the entire roster — one subtree per force, each offering that force's own copy of the same
catalogue entries. `findTreeItemByText` returns the first match in tree order, so in a multi-force
roster `selectEntry` always clicked **force 1's** copy: the selection was created, in the wrong
force, and the wait then timed out looking in the force it asked for.

The roster tree had the right force selected the whole time, which is why two guesses at this failed
before it was instrumented. This is the same shape as the NR driver's `.bookForce` / `HasText` /
`.First` bug in `docs/nr-ui-roster-coverage.md` §3 — a lookup that is unique in the developer's head
and ambiguous in the tree. 14 specs.

### Collective selections count differently, twice

A collective entry does not gain a second child when selected again — BattleScribe increments the
one already there. And its spinner is **per model**: set it to 2 under a parent of 3 and the stored
`number` is 6. Both predicates watched for only one of the two shapes, and waited out their full
timeout while the answer sat in the state they had just read.

### Validation errors carry no provenance

`resolveValidationRef` derived `entryId/constraintId` from `getValidationErrorIds()`, and for
constraint errors that collection is **empty on every element** — roster, force, category and
selection alike, measured with `BS_UI_VALIDATION_TRACE=1`. BattleScribe attaches no constraint to a
validation error, so the rendered message is the only carrier.

The in-process adapter has always known this. Its rule is now ported rather than reinvented — same
question, same Java model, and the in-process adapter is what every spec's `from` was written
against. Four things the port had to get right, each found by a spec that stayed red: `ForceEntry`
is a constraint owner; a hidden-entry error names its container AND its subject; two constraints of
one kind on one entry are told apart only by the rendered value; and **placement**.

Placement was three private remap methods on the in-process adapter, moving an over-limit violation
off the category/force/roster that noticed it and onto the selection responsible. This driver had
none, so it produced the right `from` on the wrong `on`. They are now `BattleScribeErrorPlacement`
in TestKit and both engines call it — verdict-neutral for the reference engine, and the two can no
longer drift.

> **A correction worth recording.** The error object `net.battlescribe.engine.b.a` is constructed as
> `(Object, String)` and exposes the object as `a()`. That reads exactly like the source constraint,
> and it is not: it is the roster element the error hangs on, carrying runtime ids regenerated on
> every recalculation. Reading ids off it yields a well-formed and entirely wrong `from` — worse
> than none, and it looks right in the output. It was written, run, and reverted on the evidence.
> The trace switch that showed it is what stays.

### We were generating XML BattleScribe refuses

`Catalogue.xsd` marks `childId` `use="required"` on `QueryFilteredBase`. The generator omitted it
whenever a spec set none, and BattleScribe answered by deleting the staged file — "File was
corrupted and has been deleted" — and failing roster creation.

Two engines never noticed: the in-process adapter builds Java model objects directly through
`JavaModelFactory` and never parses a file, and NewRecruit's parser is more forgiving than the
schema. Only the desktop app goes through the format BattleScribe defines.

**`SchemaValidator` had been in this repo, wrapping this exact XSD, with zero callers.** It now has
one: every roster spec's generated data is validated, 367 of them in under two seconds, offline.
That is the same question the 29-minute lane was being used to answer.

## Better failure messages did the work

Three of the causes above were found by one change rather than by a better hypothesis, which is the
lesson `docs/nr-ui-roster-coverage.md` records and this lane repeated.

"Timed out waiting for state change" says only that the loop ran out. It cannot distinguish an
action that did nothing, an action whose result the predicate did not recognise, and an action that
landed somewhere else — and 28 specs were failing with that one message across all three.
`waitForStateChange` now renders the last state read, and `selectEntry` reports which force the
roster tree had selected. It named two causes on its first run:

    no selection for entryId 'link-alpha' in force a5d7-…; that scope holds:
      [link-alpha::shared-unit=4e28-… (Squad x1)]; roster tree had selected: Patrol:a5d7-…

    no selection for entryId 'se-target' in force 30b8-…; that scope holds: (nothing);
      but the roster DOES hold that entryId elsewhere: [496a-… (Target)]

Two diagnostic switches are kept, both off by default and both justified by a bug they found:
`BS_UI_VALIDATION_TRACE=1` prints every validation error with each id source that could name it, and
`BS_UI_TREE_TRACE=1` dumps both roster trees around a `selectEntry`.

## The last 5 — and why none of them was declared

**Seven specs are declared `engines: {battlescribe-ui: fail}`**, and the number did not grow to
twelve. The seven are three cost limits, two `real-world` specs whose DATA BattleScribe refuses to
parse, and two limitations that only became legible once grouped controls were driven: a `max=1`
group is a RadioButton, so its violation is unreachable rather than unreported, and an entry whose
primary category is not one of its force's is absent from the catalogue tree entirely.

Every declaration records what was NOT checked. The cost-limit three say the Edit Roster dialog has
not been examined for per-cost-type limit fields. The `real-world` two name the four modifiers
BattleScribe rejects and note that every catalogue loads — which is what makes them a statement
about the data rather than cover for an unfinished `SetupFromFiles`.

The last five stayed failing for as long as they had a cause and no verdict, and that was the right
state to leave them in. **Four of the five turned out to be driver defects and one an expectation to
measure — none was the app's limitation, and none needed declaring.** Three had been characterised
in this document as something other than what they were, so what each actually was is worth keeping:

| filed as | actually |
|---|---|
| G — `condition-shared-flag-nested`: two links render as `'Trigger'`, "with no escape, because the panel exposes no id" | **Wrong, and one screenshot settled it.** The panel offers `[1] Target`, `[1] Trigger`, `[0] Trigger` — three distinct rows, one per child, in declaration order. There is no id, but there is a POSITION, and the catalogue supplies it. The driver now indexes how many earlier siblings share an entry's label and the agent skips that many controls. Both orders come from the same catalogue, so they agree by construction. |
| G — `collective-instance-amount`: `'Sergeant • 12pts' -> (no control)`, "what BattleScribe renders is still unknown" | Answered by #377's screenshot (a `+` where the code demanded a Spinner) and now by the assertion behind it. Asked for three of an instanced entry, the app makes **three sibling selections costing 32**, and only the COLLECTIVE child rides along into the copies: Sergeants 2 and 3 have Weapon and not Badge. The gap to the 36 store-direct semantics predict is exactly the two Badges the app declines to duplicate. That is the spec's one new `battlescribe-ui` expectation. |
| E — `constraint-entry-link-merged` / `constraint-shared-flag`: "not a driver gap: the two BattleScribe engines disagree" | **Both were bugs — one in each engine.** See below; the divergence ended with one fewer override in the suite rather than one more. |
| K — `collective-per-model-operations`: "the SAME cause as `collective-instance-amount` — the decrement path cannot find a control" | **Wrong. The control was found and driven correctly.** The decrement worked; the POSTCONDITION was wrong. Detail below. |

### An id list is not per-error, and a short one is not evidence

`constraint-shared-flag` was the one genuine open puzzle: the message said `(maximum 2)` and
resolution answered `con-max-shared`, whose value is 3. `BS_UI_VALIDATION_TRACE=1` printed the
reason next to itself — the force reports **one** id, `…::shared-unit::con-max-shared`, while
carrying **three** errors, two of them raised by `con-max-per-link`, which appears in no list
anywhere.

`getValidationErrorIds()` lists ids the ELEMENT knows about; one element carries every error raised
under it. The value tiebreak was gated on `size() > 1`, so a one-element list skipped it entirely
and returned the only candidate offered — naming a limit the message rules out. The gate is gone:
the rendered value decides at any list size, and message-resolution only wins when it can point at a
constraint whose declared limit the text actually quotes. Hidden-entry errors carry no value on
either side and keep their path.

> **A cause is not a verdict, and a verdict is not a declaration.** This one sat named-but-unexplained
> across two sessions. Declaring it would have bought a green job and lost the bug.

### The same defect in the in-process adapter, fixed rather than documented

`constraint-entry-link-merged` looked like a genuine engine divergence and was written up here as
one. It was not: `ResolveEntryFromMessage` consulted entry links only when the target carried no
kind-matching constraint of its own, so it kept the target's `con-shared-max` (value 4) as a
fallback and returned it for 3 selections against a `(maximum 2)` message — an answer that is
self-inconsistent, since 3 does not exceed 4. Links are now asked for a value match first.

All three engines now agree. The spec's base expectation becomes the app's answer, `newrecruit`
loses the override it had needed to disagree with it, and the spec carries no per-engine block at
all. AGENTS.md's step 3 offers "a bug in it, OR a documented override" — this records that the bug
fix is the one to prefer, because it removes a divergence instead of enshrining one.

### A decrement is not a removal

`deselectSelection` on a collective child read as "the decrement path cannot find a control". It
found it and drove it correctly. The wait then demanded the selection DISAPPEAR — but a collective
control steps the PER-MODEL count, so one press takes `number` 6 to 3 under a parent of 3 and the
selection stays. That correct press timed out, the action layer **retried the whole action**, and
the second press took it to 0. The roster read back `costs: []` and the step reported success.

Two screenshots showed it in one look: `[2] Weapon • 30pts` before, `[0] Weapon • 15pts` after — a
spinner two steps down from one requested decrement, with a label still quoting the value it had at
1. The wait now ends on gone OR fewer-than-there-were.

Found alongside it: the same helper would have fired an instanced entry's `+` for a decrement
request, adding one while reporting a removal. It now declines and lets the DELETE fallback run.

### Method, again

Both of the two hardest were settled by looking rather than reasoning — a trace switch that printed
the id list beside the messages it was being asked to explain, and a screenshot pair that showed a
spinner at 0 where the theory said 1. #377 recorded that lesson about `--screenshots`; this session
underused it again for a while, and the entries above are what that cost.

**Not covered by the occurrence fix:** the `dataSource` path indexes names from XML without tracking
parents, so it computes no occurrences and keeps the previous first-match behaviour. No `dataSource`
spec currently needs one.

The rule that replaces an allow-list, unchanged: **a failing spec carries its reason, or it is not
failing on purpose.** With all 5 resolved, the lane's blocker on `ci.yml` (#355) is gone.

## Two specs built their rosters on another spec's data

`specs/roster/scope/scope-roster.yaml` passed on its own and failed in an unsharded lane run. It
declares no `engines:` block and nothing about it had changed — it is expected to pass everywhere.
What it failed on was a game system belonging to a different spec.

It only became reproducible once a different mistake was cleared out of the way:

> **A measurement can be invalid, and read exactly like a regression.** The first full-lane run
> showed three failures — `condition-shared-flag-nested`, `constraint-shared-flag` and
> `collective-per-model-operations` — each reading precisely as a regression of a fix this document
> records above as landed. None of them was. `src/bs-ui-java-agent/bs-ui-java-agent.jar` is
> gitignored, and NOTHING in the .NET build rebuilds it: `setup.ps1` builds it once, or
> `src/bs-ui-java-agent/build.ps1` does. The jar under test was two days older than
> `RosterActions.java` and `EngineAccessor.java`. Rebuilt from source, all three passed. It had also
> been hiding the defect being hunted: each of those three failures poisons the engine and forces a
> cold start, a cold start builds a NEW temp home, and that wiped the accumulated data directory
> before `scope/scope-roster` was reached. One stale artifact was both the false regression and the
> reason the real one would not reproduce.

### Four mechanisms, none of them wrong by itself

1. `SpecLoader.ApplySetupDefaults` defaults a spec's game-system **id and name** to the spec id.
2. `BsUiDataStaging.StageDataFilesAsync` cleared only the CURRENT spec's directory, and
   `battlescribe-ui` is `ReuseSafeRoster: true` — one JVM and one isolated home span the lane — so
   the data directory accumulated one game system per spec run since the last cold start.
3. `RosterActions.createRosterAction` chose the game system with `selectComboBoxItemByText`:
   `item.toString().contains(text)`, first hit wins.
4. Spec ids nest. `scope-roster` is a substring of `condition-scope-roster`, which sorts earlier
   (`c` < `s`) and runs earlier — discovery is alphabetical, `condition/` before `scope/`.

So `scope/scope-roster` built its roster on `condition-scope-roster`'s game system. The three specs
in that id family declare `fe-patrol`, `cat-1`, `se-target` and `se-trigger` under the same names,
so the catalogue and force-entry combos found their ids under either one and every step reported
success. The failure surfaced four steps later, as a value:

    Step 4: force[0].selection[0].name: expected Roster Has Trigger but got Roster Triggered

`Roster Triggered` is `condition-scope-roster`'s modifier value. It appears nowhere in
`scope-roster`'s own data.

### The shards hid it, by luck

`scope/scope-roster` hashes to shard 0; `condition/condition-scope-roster` and
`scope/scope-roster-cross-force` both to shard 1. No shard held both, so no CI run ever put the
decoy in front of the victim — established by enumerating the real test list, not by recomputing the
hash. That is a property of where the current 2-way boundary happens to fall and of nothing else:
adding one spec, or changing `ShardCount`, re-rolls every assignment. The lane's green in CI was
never evidence the defect was absent.

### The second victim passed

The corpus holds 41 spec-id substring collisions, and only two of them have the CONTAINING id
sorting earlier; the rest are prefix-extensions, where the shorter id wins its own match. The other
one is `profile-publication` ⊂ `infolink-profile-publication`, and those two specs are
observationally identical: same `fe-1`, `cat-1` and `se-1` "Marine", same expected
`Marine Stats`/`pub-1`/M=6, one reaching the profile directly and one through an infoLink.

So `profile-publication` was building on the infoLink spec's data **and passing** — green on a path
it does not describe, in every green lane run in this repo's history. Every value it asserted was
correct; only the identity of the data was wrong, and **no `expectedState` can catch that.** Only an
identity check can. Restoring the pre-fix rule with the new postcondition in place failed it at
Step 0, naming `infolink-profile-publication`.

### The fix is four commits, and the first three each close it alone

- **The game system is chosen by id, exactly.** `selectComboBoxItemByText` is deleted rather than
  left unused; its one call site was this bug. The name is still sent and now reaches only the
  failure message, which lists what the combo was offering as `name (id)`.
- **The remaining matcher lost its fallback.** `selectComboBoxItemById` matched `getObjectId`
  exactly and then took the first item whose `toString()` CONTAINED the id. `javap` on the shipped
  app says why that is dangerous rather than merely loose: those combos hold `BaseData` subclasses
  and `BaseData.toString()` is `name + ":" + id + ":" + super`, so every item's text contains its
  own id and `contains("cat-1")` matches `cat-10`. It could never have been load-bearing either —
  `getObjectId` never fails on those types, so reaching the fallback always meant the wanted item
  was ABSENT, and the only thing a fallback can do from there is select a DIFFERENT real item. The
  two matchers are one function now.
- **A postcondition, not a third mechanism.** `createRosterAction` compares the created roster's own
  `gameSystemId` against the one requested, which is what keeps working if the dialog flow, the
  staging or the engine reuse is ever rewritten. Java-side, because a C# check risks comparing a
  value against itself: `GetRosterState()` falls back to `EmptyRosterState()`, which returns the
  driver's own `_gameSystemId`.
- **Staging retires the system it staged last**, so only one is ever on disk. Not a blanket sibling
  delete: `BsUiOptions.IsolatedHomePath` exists, and under a shared home "delete everything that
  isn't mine" is one engine destroying another's data mid-run. Decompilation confirms
  `#cboGameSystem` is rebuilt from a live `data/` walk on every dialog open — the only cache on that
  path is per file, revalidated against `lastModified()` — so the removal shrinks the combo inside a
  running JVM rather than only at the next cold start. Confirmed empirically as well: with staging
  fixed and the PRE-FIX substring rule restored, `profile-publication` passes, because there is no
  second candidate left for it to match.

That last one is the exception. It removes the ambiguity rather than detecting it, and it cannot
close the class on its own — a stager the engine does not own can still leave a second game system
in a shared home, which is exactly what the regression test now does. The mechanism detail is in
`docs/bs-ui-driver.md`.

**Nothing here earned a declaration.** The app selected what it was asked for every time; the driver
asked wrong. An `engines:` block would have recorded a limitation that does not exist.

### The runs, and what the wall column is not

Unsharded, complete stack — 368 tests, the 367 specs plus the one new regression test:

| run | result | wall |
|---|---|---:|
| on the combo-fallback commit | 368/368 | 8m58s |
| 1 | 367/368 — `force/force-remove-first-multi-catalogue` state-change timeout | 16m13s |
| 2 | 368/368 | 15m04s |
| 3 | 368/368 | 15m23s |
| 4 | 368/368 | 17m49s |

**Do not read the wall column as a measurement of what the fix cost.** The box was running 18
concurrent agent processes on 8 cores, and the figure moved between 9 and 18 minutes with no code
change at all between several of these runs. These timings are not comparable with each other, and
not with the ones in *Where it went*.

Run 1's single failure is a state-change timeout — `no selection for entryId 'se-beta'; that scope
holds: (nothing)` — a different shape from this defect, and it did not recur in the three runs after
it.

### A lead, not a cause: an uncaught exception on the FX thread

The JVM stderr of the GREEN runs carries about 20 uncaught
`ClassCastException: Integer cannot be cast to Double` per run, thrown from
`javafx.scene.control.SpinnerValueFactory$DoubleSpinnerValueFactory$1.toString` by way of
`Spinner.setText`: the driver sets an `int` into a `Double`-backed cost-limit spinner. The value
still applies, so every spec that hits it passes and nothing in the lane reports it.

An uncaught exception on the JavaFX Application Thread is, however, exactly what produces **an
action that did nothing** — the shape run 1 failed in. That is the whole of the evidence: a
mechanism that could produce that failure, and a failure of that shape, with nothing yet tying the
two together. Recorded as a lead, not diagnosed, and not fixed here.

## In CI

The lane now runs as the `roster` half of `thorough-ui-bs`, sharded 2 ways on the same `Shard` trait
as the gamedata half — a `suite` axis on the existing job rather than a second job, because the two
halves need identical artifacts, JDK, agent build and `xvfb`, and a copied setup block is one that
drifts.

It is **opt-in**, like every other thorough lane: `workflow_dispatch`, the weekly Monday schedule, a
`thorough-ci` label on a PR, a `[nr-test]` commit message, or a PR touching `testdata.json`. So a
BattleScribe change still merges without it unless someone asks — the difference is that asking is
now possible, and the weekly run reports drift that previously nothing looked for.

**The confirming run has happened.** The 367/367 was first measured before the nested-force scoping,
the label ranking, the checkbox direction and the count-of-zero fix landed, each of which changes
behaviour on paths the corpus exercises. Run
[31462320864](https://github.com/WarHub/battlescribe-spec/actions/runs/31462320864), against `main`
at f7e4223, re-measured it on that stack: **195 passed on shard 0 and 172 on shard 1 — zero failed,
zero skipped, on either.** It was not a dedicated run: #338 is the NR-snapshot bump, which carries
the `thorough-ci` label, so the opt-in lanes fired on it and the confirmation arrived as a by-product
of the bump.

That run predates the game-system commits above, and two things about it have moved since. Shard 0
now selects 196 tests rather than 195, because `BsUiGameSystemSelectionTests` is traited `Shard 0` —
a test carrying no `Shard` trait matches neither filter and would run nowhere while looking covered.
And one of the 367 greens was not what it looked like: `profile-publication` passed on
`infolink-profile-publication`'s data, in that run and in every one before it. The counts stand as
measured; what one of the passes established does not. No sharded run has been made on the current
stack — the four unsharded runs above are what confirms it.
