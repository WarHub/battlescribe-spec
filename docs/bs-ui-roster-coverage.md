# BS UI roster driver — measured coverage

`battlescribe-ui` (roster domain) drives BattleScribe's desktop Roster Editor through a Java agent
injected into the JVM: real dialogs, real tree clicks, state read back from the Java model. The lane
was added in #353 and its 83 failures were unclassified, which is why it was deliberately not wired
into `ci.yml` — a permanently-red job is worse than no job. This document is the classification, and
what it turned into.

## Where it went

| | first measurement | now |
|---|---:|---:|
| Specs selected | 367 | 367 |
| **Passed** | **284 (77%)** | **362 (99%)** |
| Failed | 83 | 5 |
| Wall-clock | 29m02s | 13m32s |

**Zero regressions** against the first measurement, spec-for-spec, at every step. The first
measurement was reproduced exactly on a second run before anything was changed — which matters more
than usual here, because a third of those failures were timeouts, and a timeout that moves between
runs is a different problem from one that does not.

The 15 minutes are almost entirely 10-second state polls that no longer run out.

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
| E | 25 | 23 | 2 | validation error produced, `from` unresolved |
| B | 6 | 6 | 0 | BattleScribe deletes the staged `.cat` as corrupt |
| J | 8 | 8 | 0 | cost mismatches — float drift and lane inheritance |
| K | 9 | 7 + 2 declared | 1 | value mismatches, mostly lane inheritance |
| D | 4 | 2 + 2 declared | 0 | no validation error produced at all |
| G | 4 | 1 + 1 declared | 2 | edit-panel control not found by label |
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

## The 5 that remain

Each has a named cause. **Seven specs are declared `engines: {battlescribe-ui: fail}`** — three cost limits, two `real-world`
specs whose DATA BattleScribe refuses to parse, and two limitations that only became legible once
grouped controls were driven: a `max=1` group is a RadioButton, so its violation is unreachable
rather than unreported, and an entry whose primary category is not one of its force's is absent from
the catalogue tree entirely. The remaining 5 are NOT declared, deliberately: a cause is not a verdict, and declaring specs to get a green job
would be inventing declarations rather than earning them — the exact defect
`docs/nr-ui-roster-coverage.md` records for that lane's own history.

Every declaration records what was NOT checked. The cost-limit three say the Edit Roster dialog has
not been examined for per-cost-type limit fields. The `real-world` two name the four modifiers
BattleScribe rejects and note that every catalogue loads — which is what makes them a statement
about the data rather than cover for an unfinished `SetupFromFiles`.

| | count | what is known |
|---|---:|---|
| G | 2 | Grouped controls are DRIVEN; these are what is left behind them, and they are two different shapes. `condition-shared-flag-nested` puts two links onto one shared entry, so both render as `'Trigger'` and label lookup cannot separate them — the catalogue-tree ambiguity again, with no escape, because the panel exposes no id. `collective-instance-amount` offers `'Sergeant • 12pts' -> (no control)`: the label is there and carries nothing to click, which is neither "no row" nor "row with spinner". What BattleScribe renders for a collective child that has acquired its OWN children is not yet known. |
| E | 2 | **Not a driver gap: the two BattleScribe engines disagree.** `constraint-entry-link-merged`'s message says `(maximum 2)` — the LINK's constraint — and this driver reports it, while the in-process adapter reports the target's `con-shared-max` (value 4) because its resolution reached that one first and kept it as a fallback. The spec encodes the in-process answer. Which is right is a question about BattleScribe. `constraint-shared-flag` is the same family and still unexplained. |
| K | 1 | `selection/collective-per-model-operations` reports `cost type 'pts' not found in roster` after a deselect — the roster loses its cost types entirely, which is not a limitation of anything and has no explanation yet. |

The rule that replaces an allow-list, unchanged: **a failing spec carries its reason, or it is not
failing on purpose.** Until all 5 are fixed or declared, the lane stays out of `ci.yml` (#355).
