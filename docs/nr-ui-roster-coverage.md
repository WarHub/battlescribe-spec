# NR UI roster driver — measured coverage

`newrecruit-ui` (roster domain) drives NewRecruit's web UI through Playwright: real clicks, real
form input, state read back from Pinia. Its coverage had never been measured. The CI lane runs
**one** spec, and the reason recorded for that was stale — see "The one-spec lane" below.

## What it actually covers

Measured 2026-07-31, frozen HAR (offline), `bs-spec run --all --engine newrecruit-ui --roster`
over `force/`, `cost/`, `entry-group/`, `gamesystem/`:

| | |
|---|---:|
| Specs run | 56 |
| **Passed** | **45 (80%)** |
| Failed | 11 |

Every one of those 56 created its own roster in the same browser session, which is the fact that
retires the one-spec limit.

## The 13 failures are four groups, not noise

`force/` looks alarming on its own — 11 of 21 fail — until they are grouped. Three groups are
limitations of NR's UI; two are gaps in this driver.

### 1. Empty catalogue — 5 specs — NR-UI limitation

`force-add-single`, `force-remove`, `force-add-multiple`, `force-add-and-remove-all`,
`force-with-categories`.

NR's Create List dialog reports "could not be loaded" for a catalogue with nothing in it. All five
declare `catalogues: [- id: cat-1]` and no entries anywhere.

The distinguishing case is what makes this a diagnosis rather than a correlation:
`gamesystem/gamesystem-root-selectionentry` **also** has an empty catalogue and passes — because its
content lives in the `gameSystem`, so NR has something to load. Empty catalogue plus empty game
system is the failing shape; empty catalogue alone is not.

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

**Still failing: `force-nested-multi-catalogue`**, which straddles this group and the next. Its child
force from `cat-b` is created correctly now; `selectEntry se-b1` then fails with "not visible in the
catalogue panel". That is the *entry* panel, a different surface from add-force, and it belongs with
group 3.

### 3. Nested child forces — 3 specs — **driver gap**

`force-nested-deep-selections`, `force-nested-multi-level`, `gamesystem-shared-entry`, all failing
`addChildForce` with `child force 'Platoon' section is not visible/interactable`.

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
calls that "the root cause of the single-spec-per-HAR limit". The 56-spec run above is the
confirmation: 56 roster creations, one session, no HAR exhaustion.

This matters because `docs/warm-reuse.md` records the cost of that lane: *"CI never caught the
original bug because the NR-UI roster lane runs a single spec."* The same blind spot hid #339.

Widening it is worth doing on the **thorough** lane rather than every push — 45 specs at ~15s each
is ~11 minutes, which is right for an opt-in suite and wrong for the fast gate the smoke lane exists
to be. The remaining groups should be fixed or declared before widening, so the lane starts green.
