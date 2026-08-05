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

Every one of those 363 created its own roster in the same browser session, which is the fact that
retires the one-spec limit. Wall-clock: **47 minutes**, sequential, one shared browser.

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

**47 minutes is the honest cost of "complete".** The lane is opt-in only — label, schedule or
manual dispatch — so per-PR CI is untouched. Two measured levers remain if that becomes too steep,
neither taken here: roughly 5s/spec of fixed sleeps still sit in `CreateRosterAsync` and
`LoadGameDataAsync` (~15 minutes at this scale, though several of them gate NEGATIVE assertions and
need conditions rather than smaller constants), and hybrid parallelism — N workers over contiguous
chunks — which buys wall-clock by shortening the cross-spec chains that found the three races below.

**The widening paid for itself on its first run**, which is the point of it. Sequencing 56 specs
through one shared browser surfaced a navigation race in `LoadGameDataAsync` — it injected the
directory-picker mock as its first action, between the previous spec's cleanup navigation and its
own, and Playwright reported "Execution context was destroyed". Structurally invisible to
`bs-spec run` (a fresh engine per spec) and to the old one-spec lane (no previous spec to race).
Fixed by injecting the mock after the navigation, where it is actually read; two clean 56-spec runs
since, against one failure in two before.

## 5. What widening it actually found — three navigation races

Sequencing 188 specs through one shared browser surfaced three separate races, one per lane run,
each fixed at its source rather than retried away. All three share a shape — **an `evaluate` racing
a navigation** — and all three were structurally invisible to `bs-spec run`, whose per-spec engines
never navigate twice in quick succession.

| Where | Mechanism |
|---|---|
| `LoadGameDataAsync` mock injection | Injected as the method's FIRST action, between the previous spec's cleanup navigation and its own. Moved to after the navigation, where the mock is actually read. |
| `LoadGameDataAsync` popup close | `IsVisibleAsync` is a snapshot, not a wait, so `if (visible) click()` is check-then-act. When NR closed its own popup in between, the click burned Playwright's full 30s default and failed Setup. Bounded and tolerated — "already closed" is success for that step. |
| `PushRouteAsync` | The evaluate **awaits the `router.push` it triggered**, so a navigation that replaces the execution context kills the very call that caused it. The push had succeeded; only the reporting channel died. Now tolerated, then the new context's router is awaited. Deliberately without re-asserting the route: `/app` legitimately redirects to `/app/MySystems`. |

Each surfaced as `Setup failed: …` on a **different spec each run**, which is what an intermittent
looks like when the cause is shared and the victim is whoever happens to be next.

This is the lane paying for itself. `docs/warm-reuse.md` records the cost of the old one-spec
version — *"CI never caught the original bug because the NR-UI roster lane runs a single spec"* — and
with one spec there is no previous spec to race with, so none of these three could ever have appeared.

Two consecutive fully-green 188-spec runs before shipping, because a race that fires once in two runs
hides from a single pass about a quarter of the time.

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
