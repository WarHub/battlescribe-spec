# NR UI roster driver — measured coverage

`newrecruit-ui` (roster domain) drives NewRecruit's web UI through Playwright: real clicks, real
form input, state read back from Pinia. Its coverage had never been measured, and the CI lane ran
**one** spec on a reason that had gone stale — see "The one-spec lane" below.

## What it actually covers

Measured 2026-08-05, frozen HAR (offline), over **every applicable roster spec**:

| | |
|---|---:|
| Specs selected | 363 |
| **Passed** | **340 (94%)** |
| Failed | 23 — every one declared, with a reason, in the spec itself |
| Skipped | 4 — never run on this engine |

Re-measured 2026-08-28 after #450 gave this engine roster load: **378 specs selected, 354 passed, 23
declared failures**. The seven `roundtrip` specs this engine used to opt out of — it could not load a
roster at all — run now, and two more were added alongside them.

Every one of those 363 created its own roster in the same browser session, which is the fact that
retires the one-spec limit. Wall-clock: **18m17s**, sequential, one shared browser — down from 47
minutes, see "Where the time went" below.

There is no allow-list. A spec added tomorrow is covered the day it lands, and a driver regression
that breaks a category nobody was watching is a failed lane rather than silence.

## The failures are four groups, not noise

First measured at 43/56. Grouping them turned "the UI driver is unreliable" into a work list, and
working through the groups took the six measured categories to 178/188 — then the same treatment
applied to the rest took the whole suite to 340/363.

**Almost every remaining failure is a limitation of NR's own UI**, not of this driver; the two
exceptions are named as driver gaps in §6. Each is declared in the spec itself, and almost all are
declared `fail` rather than `skip`, so the spec still RUNS and an NR release that lifts a limitation
is reported as an unexpected pass rather than going quiet.

The four groups below are the six-category batch. The rest are summarised in §6.

### 1. Empty catalogue — 8 specs — NR-UI limitation

`force-add-single`, `force-remove`, `force-add-multiple`, `force-add-and-remove-all`,
`force-with-categories`, `ordering-nested-forces`, and — reached the same way but via a step-0 read
rather than an `addForce` — `roster-create-empty`, `roster-no-cost-types`,
`roster-name-and-metadata`.

NR's Create List dialog reports "could not be loaded" for a catalogue with nothing in it. All five
declare `catalogues: [- id: cat-1]` and no entries anywhere.

The criterion first written here — "empty catalogue plus empty game system" — was **too loose**.
`ordering-nested-forces` has game-system content (nested `forceEntries`) and still fails, while
`gamesystem/gamesystem-root-selectionentry` has `selectionEntries` and passes. The real threshold is
**at least one SELECTABLE entry** (`selectionEntries`/`entryLinks`); force and category entries do
not count. `force-with-categories` confirms it from the other side: `categoryEntries` +
`forceEntries`, and it fails.

The store-direct `newrecruit` engine builds all five rosters fine. The dialog refuses; the API does
not. Not a data error, and not fixable from this side.

> The driver used to report this as *"NR does not support creating rosters from library catalogues"*
> — a cause it never checked, and false for all five (none declare `library`). Corrected to state
> the observation and both known causes.

### 2. Multi-catalogue — was 3 specs, **2 fixed**

`force-remove-first-multi-catalogue` and `force-multi-catalogue-two-forces` **now pass**.

`AddForceByNameAsync` ignored its `catalogueId`, on the strength of a comment claiming *"NR shows
force entries without distinguishing which catalogue they belong to. The UI picks the correct book
internally when clicked."* It does not. NR renders `select.faction-select` whenever the system has
more than one playable catalogue; the force list below is derived from it, and the chosen book is
what `roster.insertForce(book, entryId)` receives — which is also what calls `addCatalogue()` to
bring the second catalogue into the roster. Leaving it alone silently created the force against the
list's own catalogue, so the entries it should have offered were absent.

Measured on `force-multi-catalogue-two-forces`: force 2 was created against `cat-a`, and
`selectEntry se-b1` returned Alpha Unit instead of Beta Unit.

The child-force path had already been doing this correctly via `.childForces select` — only the
top-level path had not.

### 3. Nested child forces — was 3 specs, **2 fixed**

`force-nested-deep-selections` and `force-nested-multi-level` **now pass**.

`AddChildForceByNameAsync` located the parent with `.bookForce` filtered on `HasText = <parent's
name>`, taking `.First`. **A parent's section lists its available child force types by name**, so
Army's `.bookForce` contains the text "Division"; asking for "Division" matched Army's section first,
and the driver then hunted for "Platoon" in Army's child-force picker, which only ever offers
"Division". It surfaced as the misleading `child force 'Platoon' section is not
visible/interactable` — the section was visible, the driver was looking in the wrong one.

Name matching is also ambiguous whenever two forces share a name, which
`force-multi-catalogue-two-forces` does by design (two "Patrol" forces).

Forces are now addressed by uid. That path already existed — `TagBookForceElementsAsync` plus a
`[data-nrui-force-uid]` selector — but only as a *fallback* for when the name lookup returned
nothing, so the broken path was the one that ran. The tagger also stopped silently truncating: it
maps forces to `.bookForce` sections positionally, so it now verifies the counts match and reports
a diagnostic instead of leaving elements untagged (an untagged element is indistinguishable from a
force that does not exist).

### 3b. The last two — **both fixed, and neither was what its error said**

Both were filed as "entry panel" gaps because that is where they threw. Neither was.

`force-nested-multi-catalogue` was **still the multi-catalogue bug**, one level down. The child-force
picker resolved its catalogue name through `army.system || army.gameSystem` — not where the books
live — so the lookup returned null and the code fell back to `SelectOptionAsync(Index = 1)`, i.e.
"Faction A". A child force requested from `cat-b` was built against `cat-a`, and `selectEntry se-b1`
then correctly reported that Beta Unit was not there. Both pickers now share one helper that resolves
through `systemsStore` and **throws rather than falling back to an index**.

`gamesystem-shared-entry` was **name registration**. `RegisterCatalogue` registers a catalogue's
`SharedSelectionEntries` and `SharedSelectionEntryGroups`; `BuildEntryLookups` registered neither for
the *game system*. Every UI action addresses entries by visible name, so an unregistered entry falls
back to its raw id — the driver hunted for a label "se-shared-weapon" in a panel that says "Shared
Weapon" and called it hidden. Two loops, mirroring the catalogue path.

Both had been diagnosed from where the exception surfaced rather than from where the state went
wrong, which is the same mistake the "library catalogues" and "child force not visible" messages
encode. Probing the state at the point of failure is what separated them.

### 4. Hidden entries and validation — 2 specs — NR-UI limitations

`force-entry-hidden` (`force 'Hidden Detachment' is not visible in the forces panel`) — hidden means
hidden in a UI; the store-direct engine can add it, a user cannot.
`cost-hidden-limit-validation` (expected `costLimits/cp` error, got none) — the UI does not surface
that violation.

## The one-spec lane

`FrozenNrUiRosterConformanceTests` runs `TargetSpecs = ["protocol/protocol-kitchen-sink"]`, justified
in a comment as *"the frozen HAR supports a single roster-creation flow per run"*.

**That limit is gone.** `NewRecruitBrowser`'s HAR fallback benign-fulfills `/api/` calls with an
empty JSON body specifically so the SPA stops hanging across repeated roster flows — its own comment
calls that "the root cause of the single-spec-per-HAR limit". The run above is the confirmation:
188 roster creations, one session, no HAR exhaustion.

This matters because `docs/warm-reuse.md` records the cost of that lane: *"CI never caught the
original bug because the NR-UI roster lane runs a single spec."* The same blind spot hid #339.

**Widened, in two steps.** First to six measured categories (188 specs), then — once the last
unclassified failure had a name — to the whole applicable suite: **363 specs, 47 minutes**, behind
`NR_UI_ROSTER_FULL`, which the `Full frozen NR UI roster` CI step sets. Every-push and `pre-push`
still run kitchen-sink alone, unchanged.

**The intermediate step was not caution for its own sake.** Running everything selected 28 failures
in categories nobody had classified. Declaring those to get a green lane would have been inventing
declarations rather than earning them — a spec marked `fail` with no reason behind it is
indistinguishable from a bug someone decided to stop looking at. So the lane grew as the
classifications did, and the allow-list was deleted the moment it had nothing left to exclude.

**The lane is opt-in only** — label, schedule or manual dispatch — so per-PR CI is untouched. It
was 47 minutes when first widened to 363 specs; it is now 18m17s. See below; §8 covers a
snapshot-introduced cost that none of these sleeps could explain.

## Where the time went — 47m to 18m17s

Measured, not estimated: `NR_UI_TIMINGS=1` prints a per-phase breakdown, added precisely because the
case for this work had been arithmetic ("seven sleeps totalling 6.3s times 363 specs"), which says
what the driver WAITS and not what it SPENDS.

| per spec | before | after |
|---|---:|---:|
| `create-roster` | 2384ms | 278ms |
| `load-gamedata` | 1039ms | 288ms |

Five fixed sleeps became the condition each stood in for. Every replacement is strictly stronger
than the constant — a slow render satisfies the condition and did not satisfy the sleep:

| | was | now |
|---|---|---:|
| after "Create List" | 2000ms | wait `currentList.army != null` — 26ms |
| after "New" | 1000ms | `SelectOptionAsync`'s own auto-wait — 11ms |
| between install clicks | 500ms | nothing; `ClickAsync` already waits |
| after `/app/MySystems` | 500ms | wait for the control, and for the route to hold — 17ms |
| after MyLists click | 500ms | wait `location.pathname` — 27ms |
| after popup close | 300ms | wait `.xCross` hidden |
| before "Create List" | 1500ms | wait `loadedCatalogues` drained — 268ms |

`NrUiSetup` now holds exactly one fixed wait: 1000ms in a branch only the two gamesystem-only specs
reach, which is 2 seconds across the whole lane.

Two were correctness fixes rather than trades. The 2000ms preceded a one-shot snapshot that returns
null when the army is not ready, and on that path `window.__bsspec` — the state reader's only
source — is never CREATED; nothing downstream repairs it, because `WaitForEditorLoadedAsync` guards
its sync with `if (window.__bsspec && …)`. The MyLists sleep preceded `a[href='#']` matched on the
substring "New", loose enough to hit a control on the page being navigated away from, which
auto-waiting cannot protect against: it guards an element being absent, never the wrong one being
present.

**The last fixed wait in roster creation is gone too**, and with it the driver's habit of hoping NR
guesses right. NR renders a FORCE dropdown whenever a roster could start from more than one force
entry, and this driver never set it — so every roster began life with NR's default, which a flat
1500ms made correct by letting `manager.loadedCatalogues` drain and re-render the control.

The `<option>` carries the force entry id as its Vue-bound `_value`, which is the whole fix:

    select#0  _value = {id: 'cat-1', name: …}   ← faction, binds a catalogue OBJECT
    select#1  _value = 'fe-gs' / 'fe-cat'        ← force,   binds the entry id STRING

The control identifies itself by what it carries — a string equal to the requested entry id can only
be a force option. A first attempt matched option **text** and required the select to be **visible**,
and both were wrong: names are ambiguous by design (`force-multi-catalogue-two-forces` has two
"Patrol" forces), and the force select always exists — NR merely hides it when there is one option,
so the filter skipped exactly the specs with nothing to choose.

**Removing that sleep exposed what else it covered**, which is the finding worth keeping: NR renders
and wires the unit list *before* it finishes parsing the catalogue, and a `+` clicked in that window
is **discarded, not delayed** — a 10s wait proves it. Eight specs failed on their first action rather
than during creation. `WaitForCatalogueWorkSettledAsync` now waits for `loadedCatalogues` to drain,
bounded and tolerated (draining is not universal; a hard wait would hang the specs that never
populate it).

**Position mattered more than duration, at the cost of two lane runs.** Placed *after* roster
creation the same wait left 63 specs failing — a list built from a half-parsed catalogue is wired to
incomplete data, and the discarded clicks were a symptom of the roster, not the click. It also
measured 1467ms there, close enough to the old 1500ms to support a confident and wrong conclusion,
written down at the time, that this was NR's real work and unrecoverable. Moved to where the sleep
actually sat, the identical condition costs **268ms**: by then the parse has been running since the
faction was chosen. Same code, 5.5× cheaper, one statement's difference in placement.

Hybrid parallelism — N workers over contiguous chunks — remains available and untaken; it buys
wall-clock by shortening exactly the cross-spec chains that found all four races below.

**The widening paid for itself on its first run**, which is the point of it. Sequencing 56 specs
through one shared browser surfaced a navigation race in `LoadGameDataAsync` — it injected the
directory-picker mock as its first action, between the previous spec's cleanup navigation and its
own, and Playwright reported "Execution context was destroyed". Structurally invisible to
`bs-spec run` (a fresh engine per spec) and to the old one-spec lane (no previous spec to race).
Fixed by injecting the mock after the navigation, where it is actually read; two clean 56-spec runs
since, against one failure in two before.

## 5. What widening it actually found — four navigation races

Sequencing specs through one shared browser surfaced four separate races, each fixed at its source.
All four share a shape — **an operation racing a navigation** — and all four were structurally
invisible to `bs-spec run`, whose per-spec engines never navigate twice in quick succession, and to
the one-spec lane, which has no previous spec to race with at all.

| Where | Mechanism |
|---|---|
| `LoadGameDataAsync` mock injection | Injected as the method's FIRST action, between the previous spec's cleanup navigation and its own. Moved to after the navigation, where the mock is actually read. |
| `LoadGameDataAsync` popup close | `IsVisibleAsync` is a snapshot, not a wait, so `if (visible) click()` is check-then-act. When NR closed its own popup in between, the click burned Playwright's full 30s default and failed Setup. Bounded and tolerated — "already closed" is success for that step. |
| `PushRouteAsync` | The evaluate **awaits the `router.push` it triggered**, so a navigation that replaces the execution context kills the very call that caused it. The push had succeeded; only the reporting channel died. Now tolerated, then the new context's router is awaited. Deliberately without re-asserting the route: `/app` legitimately redirects to `/app/MySystems`. |
| `LoadGameDataAsync` whole install sequence | The route push to MySystems **succeeds and is then overridden**: the previous spec's navigation (its `CreateRosterAsync` navigates; §8 made that hop an awaited route push, which settles before it returns, but the retry still fires, so this window has another source) is still in flight, and when it lands it takes the page with it. The install controls exist only on MySystems, so they were visible, then gone. Retried at SEQUENCE level, re-pushing only when the page really has drifted. Fires 5 times across 363 specs. |

The fourth is worth reading for how it was found rather than what it was. It was misdiagnosed twice
— as an animating element, then as a re-render — and "fixed" twice by guarding a single step, which
cost a ~20-minute lane run each time. Guarding the *wait* could never work, because the drift
happens after it: the wait passes and the click a moment later is on the wrong page. What ended it
was not a better hypothesis but a better failure message. The one in place asserted a cause it had
never checked — *"This is NR reflowing the page, not a missing element"* — and was false for
precisely the cases that reached it; replacing it with the observation plus `page.Url` named the
real cause on the next run:

    'Add more games' … is now gone for 10s (page: https://www.newrecruit.eu/app/MyLists)

That is the same defect this document records for the `library catalogues` and `child force not
visible` messages, written fresh by someone who had just finished removing it elsewhere.

Each surfaced as `Setup failed: …` on a **different spec each run**, which is what an intermittent
looks like when the cause is shared and the victim is whoever happens to be next.

This is the lane paying for itself. `docs/warm-reuse.md` records the cost of the old one-spec
version — *"CI never caught the original bug because the NR-UI roster lane runs a single spec"* — and
with one spec there is no previous spec to race with, so none of these three could ever have appeared.

Two consecutive fully-green runs before shipping, every time, because a race that fires once in two
runs hides from a single pass about a quarter of the time. That rule is why the fourth race was
caught at all: the 20-spec sample used while cutting the sleeps went green twice and was simply too
small to contain it, and each premature "this is fixed" cost a full lane run to disprove.

### 5b. The fourth race again, one step further along — and why nobody could tell

**Fixing a race by guarding a sequence only works if the guard ends where the work does.** The
retry added for the fourth race wrapped three steps — be on MySystems, open the install popup,
choose Add From Folder — and stopped there. But those three only establish that the *buttons were
pressed*. The thing the caller needs is that the *system is installed*, and that landed one
statement later, outside the loop. A drift into that window was past the guard and unrecoverable.

It showed up on 2026-08-10 as a `thorough-conformance` failure on two unrelated PRs, one spec out
of 363 each time, both in Setup, both re-running green:

    run 31409213032 (#392)  ordering/ordering-categories
    run 31415790894 (#395)  modifier/modifier-conditional-set-name

Different specs, same shape as the four above: **the cause is shared and the victim is whoever
happens to be next.** Neither PR touched this driver or NR; #395's whole diff is `CHANGELOG.md` plus
a BS fixture.

**The observed rate is roughly one run in two — two occurrences on 2026-08-10, both re-running
green — and it does not fall with a bigger ceiling.** (Observed, not measured: the rate comes from
CI's own history; the numbers below are what was measured locally.) That matters because raising
the ceiling is the obvious response and had already been tried: this exact
pair of specs is the pair named in `NrUiTimeouts`' remarks, which failed together at `Timeout
10000ms exceeded` and prompted 10s → 30s. Tripling it took two failures per run down to about half
of one — which reads as progress and is really the signature of a bound clipping *two different
populations*. The slow-but-correct installs stopped being clipped; the ones on a page that had
already been navigated away never had anything to wait for, and 30s is as useless to them as 10s
was.

**Measured, over a clean 363-spec lane** (`NR_UI_TIMINGS=1`, 340 passed / 23 expected failures / 0
failures):

| phase | count | avg | min | max | ceiling |
|---|---:|---:|---:|---:|---:|
| `load-gamedata/wait-local-library` | 363 | 67ms | 3ms | 5066ms | 30000ms |
| `load-gamedata/wait-mysystems-rendered` | 367 | 166ms | 11ms | 20045ms | 30000ms |

**Six times of headroom under the ceiling, against a 67ms average.** Nothing here is running out of
time, which rules out "30s is too tight for a cold setup under load" — and with it the targeted
increase and the retry-on-setup. (It also rules out CI contention as the mechanism: each
`ubuntu-latest` job gets its own runner VM, so `thorough-conformance` never shares CPU with the four
`thorough-ui-bs` legs or `nr-conformance` in the first place.)

The same run shows the race alive and frequent, and the phase counts say exactly where each
occurrence landed. Four `navigated to '/app/MyLists' mid-install` retries; `wait-mysystems-rendered`
ran 367 times (= 363 + those 4), `click-add-more-games` 365, `click-add-from-folder` **363**. So
every drift observed locally arrived *during* the guarded clicks — which is precisely why the guard
has always looked sufficient. What walks past it is the same drift arriving in the window *after*
them: 67ms wide on average, 5s at its worst, and until now unguarded.

That window is the hypothesis, and it is worth being exact about its status: the local lane
reproduces the *race* four times a run but has not reproduced the *failure*, because locally the
drift never lands late enough. What is measured is that the ceiling has six times of headroom, that
the race fires several times per run, and that this is the one step of the sequence outside the
retry. What is inferred is that the CI failures are that race landing in that window.

The fix is a scope correction, not a bigger number and not a retry-on-setup: the install-landed wait
moves *inside* the loop that already exists for its cause. The loop's discriminator does the rest
and is why this stays honest — it re-pushes the route only when the page really has drifted, so an
NR that genuinely fails to install still fails on the first attempt instead of three times as
slowly.

**The second half of the fix is that the failure could not be read at all.** Its complete text was:

    Setup failed: TimeoutException: Timeout 30000ms exceeded.

Playwright names the target of a *locator* wait — that is why the v35 nav-link breakage arrived as
`waiting for Locator("a[href*='MySystems']")` and was actionable — but it has nothing to name for a
`WaitForFunctionAsync`. Setup contains exactly two of those, they are the only two waits in the lane
that can produce this message, and they mean opposite things: *the route never arrived* versus *NR
never installed the game data*. One is a re-run, the other is a regression, and the output
distinguished them not at all. `NR_UI_TIMINGS` would have, but it is off in CI by design, and
`WithDiagnosticsAsync` wraps the roster-creation and force paths rather than setup — so the one
failure the lane kept producing was the one it captured nothing for.

Both now report through `NrUiSetup.WaitForSetupConditionAsync`, which on timeout reads back the
state the condition was testing and prints it with the page URL:

    Setup failed: TimeoutException: NR UI setup: waited 30000ms for NR installed the game data for
    system 'ordering-categories' and it did not happen (page: https://www.newrecruit.eu/app/MyLists).
    Observed: pathname=/app/MyLists, localLibrary=[], systemsStore=present.

`pathname=/app/MyLists` is the lost-page race and a re-run clears it; the same empty library on
`/app/MySystems` is NR genuinely failing to install and wants a person. That is the whole difference
between the two runs above and a real regression, and it now fits in the failure message —
the same lesson §5 already records: *what ended it was not a better hypothesis but a better failure
message.*

**Verified to this section's own standard** — two consecutive fully-green 363-spec lanes after the
change, `340 passed / 0 skipped / 23 expected failures / 0 failures` both times, with the race still
firing three times per run and the retry absorbing every one:

| run | result | `mid-install` retries | `wait-mysystems-rendered` | `wait-local-library` max |
|---|---|---:|---:|---:|
| baseline (before) | green | 4 | 367 | 5066ms |
| verify 1 (after) | green | 3 | 366 | 6152ms |
| verify 2 (after) | green | 3 | 366 | 5085ms |

The retry count not dropping is the expected result and worth saying plainly: this change does not
make the race rarer, it makes the last step of the sequence survive it. What would show the fix
working is a `wait-local-library` count above 363 — a drift caught in the newly-guarded window — and
neither local run produced one, for the same reason the failure has never reproduced locally.

### What to do when `thorough-conformance` goes red here

Read the `Observed:` clause; it is there to make this a decision rather than a judgement call.

- `pathname=/app/MyLists` (or any non-MySystems route) — the lost-page race. Expected at a low rate,
  now retried; if it reaches you anyway it means three consecutive drifts on one spec. Re-run.
- `pathname=/app/MySystems` with `localLibrary=[]` — NR was where it should be and did not install.
  That is a real regression (driver, HAR, or NR snapshot) and wants a person, not a re-run.
- `systemsStore=MISSING` — Pinia is not up; suspect the HAR or an NR client bump, not this code.

Those three are `Setup failed:`. A failure reported as `Step N:` is an **action**, and until the
v35.27 HAR bump it arrived with nothing in it at all — `Step 4: TimeoutException: Timeout 20000ms
exceeded.` was the complete record of `constraint/constraint-forces-field-on-forceentry` failing in
run 31568343878, on a PR whose entire question was whether NR had changed under the driver. Actions
now describe themselves the same way (`NrRosterUiEngine.WithDiagnosticsAsync`, `docs/nr-ui-driver.md`
§Diagnostics), and the same decision applies:

- The `page:` is not the roster editor — the drift race again, one step further along than §5b's.
  Re-run; a driver that is on `/app/MyLists` is not being told anything about NR's UI.
- The page is the editor and the counts are zero (`forceRows=0`, `unitRows=0`) — the panel the action
  reached for did not render. That is a driver-or-NR question and wants a person: pair it with the
  screenshot and DOM in the run's `thorough-conformance-nr-ui-diagnostics` artifact.
- The counts look right — the timeout is downstream of rendering; the store trace in the same
  artifact is the next thing to read.

Both halves of that used to be missing, and they were missing together: the artifact was never
uploaded, so the message *was* the diagnosis. One flaky spec per run is this lane's known shape (§5,
§5b) — the point is that telling it apart from a regression should not cost an hour of runner time.

## 6. Nothing is outside the lane

The remaining categories — `catalogue/`, `modifier/`, `entry-id/`, `ordering/`, `roster/`, `scope/`,
`deep-nesting/`, `customization/`, `entry-link/`, `category/`, `real-world/`, `roundtrip/`,
`protocol/` — are all in, and every failure they held has been named. The first sweep across all 363
found 28; the driver fixes earlier in this stack cleared 12 of them outright, leaving 16 to account
for here: **1 fixed, 15 declared.**

**`roster/roster-full-lifecycle` was the one real fix** of the batch: it asserts before its first
mutation, and roster creation was deferred to the first `addForce`. The reasoning first recorded
here for that fix was wrong; the correction lives in the commit and in
`EnsureRosterMaterialisedForReadAsync`'s remarks.

Everything else was a declaration, and which KIND it got was a decision each time:

| Kind | Count | Why that kind |
|---|---:|---|
| NR-UI limitation | 11 | `fail`, because a future NR release could plausibly lift it — and `fail` still RUNS the spec, so that arrives as an unexpected pass rather than as nothing. Seven hidden-entry/hidden-modifier cases, plus four that hit the empty-catalogue limitation of §1. |
| NR behaviour, by design | 2 | `skip`. Both engines fail byte-identically and both NR drivers `return` early on the relevant input on purpose. No NR release can flip a call the drivers no-op deliberately, so `fail` there would be an alarm that cannot ring. |
| Driver gap | 2 | `fail`. `real-world/wh40k-10e-*` need `SetupFromFilesAsync` to record the spec's model, which it does not — an unimplemented feature, not a patch. Declared so implementing it is reported. |

**One of them was nearly mis-declared, which is the argument for settling open questions rather
than declaring around them.** `catalogue/catalogue-category-entries` looked like the same
hidden-entry limitation as the rest, but its `se-1` carries neither `hidden` nor any modifier — NR
files it under the `(Illegal Units)` group it builds with `hidden: true`, because its primary
category is not one of the force's own. That left one question open: NR's own search box exists
page-wide, so could it reach the entry anyway? If yes, this was a driver gap and the declaration
would have been false. It returns zero rows for it. Genuine NR-UI limitation, confirmed rather than
assumed.

The rule that replaces the allow-list: **a failing spec carries its reason, or it is not failing on
purpose.** There is no longer a place to park a spec nobody has looked at.

## 7. The export that unmounted the editor — a fifth navigation defect

Measured 2026-08-11. `protocol-kitchen-sink` had two steps opted out of this engine — a
`selectChildEntry` taking a costless max-1 upgrade (`se-inf-banner`, "Squad Banner") and the
`deselectSelection` giving it back — on the observation that *"NR's options panel renders no row at
all for this entry"*. The entry's shape was the suspect: a `max: 1` constraint outside a group, no
costs, `type: upgrade`. **None of it was relevant.**

`ExportRosterXmlAsync` ended by navigating to `/app`, to leave the browser somewhere sane for the
next spec. That **unmounts the roster editor** — zero `.unitRow`, zero `.inputOption` — while the
Pinia model stays fully intact. So state reads keep working and only UI-driven mutations break, and
kitchen-sink is the one spec in the suite with actions after its `expectedFile` export. Every child
of Infantry Squad was equally unreachable; Squad Banner was simply the first one asked for.

Measured by replaying the spec's own step sequence to the failing step twice, dropping only the
export step in the second run: with it, `route=/app`, `unitRows=0`, the select throws; without it,
`route=/app/Lists/<id>?view=main`, five `.inputOption` rows, `Squad Banner` among them carrying an
`input[type=checkbox]`, and the select succeeds. NR renders that control exactly as BattleScribe
does. A variant catalogue varying one factor at a time (max-1 vs none vs max-2, costed vs costless,
`upgrade` vs `model`, min declared vs absent) rendered a row for **all eight** variants — the shape
never mattered.

Two fixes, both in the export path:

| | |
|---|---|
| Route | It now returns to the route it was invoked from (Vue Router push, so nothing re-fetches and page globals survive), falling back to `/app` only when it was not called from an editor. The next spec's clean start never depended on this: frozen gets it from `Cleanup` → `ResetBrowserStateAsync`, live from `Setup`, which navigates whenever `FrozenReady` is false — which, live, it always is. |
| Popup | Clicking `.ros` does not close NR's export menu. It stays mounted in `#popups` and swallows every click at the editor beneath — invisible while the method navigated away afterwards. Now dismissed with Escape, and waited for. |

**The failure message is what made this look like NR's fault.** It read *"has no row in the options
panel. Hidden entries cannot be selected via UI interaction"* — naming a cause it had never tested,
against an entry that is not hidden — and it has said that since the driver's first commit
(2026-05-23). That is the fourth occurrence of this defect recorded in this document (§5, and the
two messages named there). It now reports the observation instead:

    NR UI: no visible options-panel row for child entry 'Squad Banner' (entryId=se-inf-banner)
    under selection 'jzhjg8v'. route=/app unitRows=0 editing=0 rows=[]

`route=/app unitRows=0` is the whole diagnosis, in the message, on the first run.

## 8. The snapshot's message bar covers the navbar

Frozen replay fulfils every `/api/` call with `{}` because there is no server, so **every list save
comes back "rejected"** and NR reports it in the shared message bar. The notice is manufactured by
replay and is never news.

It is also expensive. The bar renders as a full-width strip across the top of the page, over the
navbar, and Playwright will not click an element that another one covers — so any control underneath
waits out the notice's life. When a snapshot introduced this reporter it cost ~4.5s per spec and
roughly doubled the lane, all of it inside one click.

Two changes, and the first alone is not enough:

- **`CreateRosterAsync` reaches MyLists by route.** A route push never asks whether a pixel is
  covered. On its own it only moves the stall onto the next control the bar happens to cover.
- **`SuppressServerSaveNoticeAsync` stubs NR's reporter**, so the notice is never posted. This is
  what removes the cost. It is silenced at the reporter rather than by clearing the message bar,
  because the bar cannot distinguish a manufactured notice from a message a spec asserts on: the
  text is a translation string and its `type` is shared with real refusals. Frozen only — against a
  live NR a refused save is real news.

The suppression throws rather than skipping when it cannot install, because one that quietly does
nothing gives back a lane at twice the runtime with nothing to point at.

**Why the save is attempted at all.** NR only syncs a list when `!systemsStore.local && userStore.user
!= null`, and `systemsStore.init()` sets `local = false` in any browser (it stays true only under
Electron, or with `localStorage.local === "true"`). The `user` is ours: `BypassSupporterPaywallAsync`
sets a fake supporter to unlock Custom Names/Notes. That is why this is a UI-lane problem only — the
store-direct engine sets no user, so the sync is unreachable there, and the pooled engines replay
with `HarNotFound.Abort`, which makes `/api/` calls throw instead of returning a rejection.

Setting `localStorage.local = "true"` would switch the sync off at the app level. It is not used:
local mode also skips the online library load and makes `installedSystems()` read `localLibrary`,
changing how game data is discovered for the whole lane.

**Diagnosing a repeat.** If a future snapshot makes setup slow again, `NR_UI_TIMINGS=1` names the
phase, and `document.elementFromPoint` at the stalled control's centre says what is covering it — a
covered click and a slow one are indistinguishable in the timings alone.
