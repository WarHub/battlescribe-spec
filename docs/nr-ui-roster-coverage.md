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
| **Passed** | **49 (88%)** |
| Failed | 7 |

Every one of those 56 created its own roster in the same browser session, which is the fact that
retires the one-spec limit.

## The failures are four groups, not noise

First measured at 43/56. Grouping them turned "the UI driver is unreliable" into a work list: the
driver defects are now all fixed (43 → 49), and **every remaining failure is a limitation of NR's
own UI**, not of this driver.

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
calls that "the root cause of the single-spec-per-HAR limit". The 56-spec run above is the
confirmation: 56 roster creations, one session, no HAR exhaustion.

This matters because `docs/warm-reuse.md` records the cost of that lane: *"CI never caught the
original bug because the NR-UI roster lane runs a single spec."* The same blind spot hid #339.

Widening it is worth doing on the **thorough** lane rather than every push — 49 specs at ~15s each
is ~11 minutes, which is right for an opt-in suite and wrong for the fast gate the smoke lane exists
to be. No driver gap blocks it now; the 7 remaining failures need declaring (per-spec skipEngines or
expected-failure entries for newrecruit-ui) so the lane starts green.
