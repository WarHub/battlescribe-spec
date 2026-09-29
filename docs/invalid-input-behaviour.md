# Invalid and Edge-of-Range Input: What Each Engine Does

What the four roster lanes do when an action is handed input at or past the edge of what it takes —
the spike [#25](https://github.com/WarHub/battlescribe-spec/issues/25) was rescoped into. Engine
behaviour here was undocumented; this is the measurement, and the specs it produced are the
executable half of it.

**The short version.** The engines refuse almost nothing. Handed a count past a max, a negative
cost limit, a duplicate past a max, they accept the action and change the roster — sometimes less
than asked — and a constraint then reports on what results. One genuine refusal was found among the
mutations, and it is reached by order of operations rather than by a bad argument. Most of what
*looked* like refusals on the first pass were the harness: the adapters' own lookups, the runner's
expression resolver, and UI drivers' waits, all reported to `expectFailure` as the engine saying
no. Those are fixed, and the second half of this page is what they were.

## How it was measured

Fifty-four probe rosters, one questionable step each, run through `bs-spec run --all-steps` on
`battlescribe`, `newrecruit`, `battlescribe-ui` and `newrecruit-ui` (the two UI lanes against the
real desktop app and NewRecruit's frozen snapshot). The step under test carried
`expectFailure: {messageContains: "@@probe@@"}` — a message no engine produces — so every outcome
explained itself: an acceptance as "succeeded", a refusal with the engine's own words, and every
non-engine failure with its classification. The dump after the step recorded what the action left.

Each probe was run again after the fixes below, and the tables show the final state. Where a lane's
first answer was the harness's rather than the engine's, the row says what it was.

## Outcomes

- **Accepted** — the action succeeded and did what it was asked.
- **Changed otherwise** — the action succeeded and did something else: clamped the value, removed
  the selection, or left the roster as it was. Evidence lives in `expectedState`.
- **Refused** — the engine declined: `kind:"engine"`, the only outcome `expectFailure` accepts.
- **Address / harness / unsupported** — not the engine's judgement. Fatal; never assertable.

## Boundary conditions

Specs: [`specs/roster/boundary/`](../specs/roster/boundary).

| Input | `battlescribe` | `battlescribe-ui` | `newrecruit` | `newrecruit-ui` | Spec |
|---|---|---|---|---|---|
| count 5, max 3 | clamped to 3, legal | clamped to 3, legal | 5, max error | 5, max error | `boundary-count-above-max` |
| count 1, min 2 | held at 2, legal | held at 2, legal | 1, min error | 1, min error | `boundary-count-below-min` |
| count 0, no min | removed | removed | removed | removed | `boundary-count-zero` |
| count 0, min 2 | removed, min error | removed, min error | removed, min error | removed, min error | `boundary-count-zero-below-min` |
| count −3, no min | removed | removed | removed | removed | `boundary-count-negative` |
| count −3, min 2 | held at 2, legal | held at 2, legal | gone, min error counting −3 ⁰ | gone, min error counting −3 ⁰ | `boundary-count-negative` |
| count 1000, no max | 1000 | 1000 | 1000 | 1000 | `boundary-count-large` |
| count 3 on a root selection | unchanged | unsupported (no control) | number 3 | unsupported (no control) | `boundary-count-root-selection` |
| cost limit 0 | limit 0, over-limit error | same | same | same | `boundary-cost-limit-zero` |
| cost limit −100 | no limit | no limit ¹ | no limit | no limit | `boundary-cost-limit-negative` |
| cost limit 12.5 | 12.5 | not driven ² | 12.5 | 12 | `boundary-cost-limit-fractional` |
| cost limit 10⁹ | 10⁹ | 10⁹ | 10⁹ | 10⁹ | `boundary-cost-limit-large` |
| select a root entry past max 1 | 2, max error | 2, max error | 2, max error | 2, max error | (`constraint-max-violation-linked`) |
| select a max-1 child again | 2, max error | unchanged (ticked checkbox) | 2, max error | unchanged (ticked checkbox) | `boundary-select-child-past-max` |
| duplicate a root selection past max 1 | 2, max error | 2, max error | 2, max error | 2, max error | `boundary-duplicate-past-max` |

⁰ NewRecruit keeps the negative amount. The Boys drop out of the roster, but the min constraint
counts −3 of them: two short of a min of two reads "requires 5 selections more". Its options panel
takes −3 typed into the count the same way its store does.
¹ The driver's translation: the Edit Roster dialog spells "no limit" as −1 and the driver leaves the
spinner there rather than entering the number. The NewRecruit UI driver used to translate too,
typing an empty field — which NewRecruit stores as the string `""` and does not count as none, so
the lane read back a limit nobody set. It types the number now, and NewRecruit counts any negative
as none.
² `BsUiCostLimits` refuses to enter a non-integer limit, on the premise that the app's cost-limit
spinners are integer ones. `Spinners.java` records the New Roster dialog's as a double spinner, so
the premise needs checking against the Edit Roster dialog before this row can be measured.

Three rows moved during the work, because the first answer was an adapter's:

- **Count 0 with a min, and count −3 with no min, on `battlescribe`.** The in-process adapter asked
  both through the engine's `getNumChanges`, which answered "no change": it bounds zero by the
  entry's min, and treats a negative target as nothing to do. Driven through the app, zero is the
  roster's remove control (the selection goes, whatever its min, and the min then reports), and a
  negative count is as far down as the spinner goes — removal for an entry with no min, the min for
  one that has it. The adapter now asks for those, and the in-process lane joined the others. This
  overturned `docs/error-assertions.md`, which had cited "a negative count is a no-op on
  BattleScribe" as its canonical example of a no-op.
- **Count past a max on `battlescribe-ui`.** The spinner clamped exactly as the engine does, and the
  driver — waiting for the number it had asked for — timed out and reported BattleScribe refusing
  the count. It now waits for the value the control settled on.
- **Count −3 on `battlescribe-ui`.** The agent refused a negative count itself (`count must be >=
  0`), and that reached the spec as the app's refusal. It now drives the spinner to its floor.

## Empty rosters and order of operations

| Sequence | `battlescribe` | `battlescribe-ui` | `newrecruit` | `newrecruit-ui` | Spec |
|---|---|---|---|---|---|
| remove the last force, set a cost limit | accepted | accepted | accepted | accepted | `roundtrip-reload-forceless-roster` |
| … then `reload` | **refused** ³ | **refused** ³ | **refused** ⁴ | **refused** ⁴ | `roundtrip-reload-forceless-roster` |
| `reload` a roster whose force holds nothing | accepted | accepted | accepted | accepted | — |
| remove a parent force holding a populated child force | all removed | all removed | all removed | all removed | `force-remove-parent-with-child-force` |
| remove a child force | accepted | accepted | accepted | accepted | (`force-nested-remove-child`) |
| `reload`, then act with ids captured before it | ids still resolve | ids still resolve | force id gone (address) | force id gone (address) | `roundtrip-reload-keeps-node-ids` |
| count 0, then act on the same child id | gone (address) | gone (address) | still resolves ⁵ | still resolves ⁵ | — |
| duplicate a child selection | nothing minted, no id | not observable ⁶ | nothing minted, no id | no control ⁶ | — |

³ `Unable to satisfy @org.simpleframework.xml.ElementList(…) on field 'forces'`. BattleScribe's
writer omits `<forces>` for an empty list and its reader requires the element, so it cannot reopen
the file it just wrote — though it loads `<forces/>` written out explicitly
(`roundtrip-load-forceless-roster`).
⁴ `This file is not a roster` — the same refusal NewRecruit makes of any roster without forces.
⁵ A NewRecruit child is a node pre-created at amount 0; zero hides it rather than deleting it.
⁶ The desktop driver waits for a copy that never appears, which is now reported as its own
timeout rather than as a refusal. NewRecruit's UI has no duplicate control on a child row.

The refusal is the only one the spike found among roster mutations. The others in the suite are the
`.ros` payloads `loadRoster` will not parse (`roundtrip-load-*`, #23).

## Ids the roster does not have

An id that names nothing — a selection already removed, a force already gone, a selection filed
under the wrong force, a cost type the game system never declared — fails in the adapter's own
lookup, before any engine is asked. That is an **addressing** failure: every engine fails it the
same way, so it measures the harness, and #25 moved it out of `specs/` for that reason. What can be
asserted is that every lane reports it as one, and
[`tests/Infrastructure/AddressingScenarios.cs`](../tests/Infrastructure/AddressingScenarios.cs)
does, on each lane.

When the spike started, it was true on one lane of four:

| Lookup miss | `battlescribe` | `battlescribe-ui` | `newrecruit` | `newrecruit-ui` |
|---|---|---|---|---|
| before #25 | address | **engine refusal** — `AgentException` | **engine refusal** — `InvalidOperationException` | unsupported / timeout, or **acted on the wrong force** |
| after | address | address | address | address |

On the two lanes in bold, `expectFailure: true` on a stale id **passed**. NewRecruit UI never passed
one, but it deselected a selection from whichever force held it, whatever force the spec named.

Entries are coarser, because an entry that is not offered where the spec asked is a lookup miss only
from the spec's side:

| Entry the spec named | `battlescribe` | `battlescribe-ui` | `newrecruit` | `newrecruit-ui` |
|---|---|---|---|---|
| a child entry, selected at the root | address | address (catalogue tree never offers it) | address | unsupported ⁷ |
| a root entry, as a child | address | address (panel never offers it) | address | unsupported ⁷ |
| an entry group's id, as a child | address | address | address ⁸ | unsupported ⁷ |
| a top-level force entry, as a child force | **accepted** ⁹ | address (dialog never offers it) | **accepted** ⁹ | unsupported ⁷ |
| a child force entry, at the top level | **accepted** ⁹ | address (dialog never offers it) | address | unsupported ⁷ |

⁷ The NewRecruit UI driver cannot tell "not offered here" from "hidden" — NR renders no row for
either — so it reports both as a capability gap. Safe (it never reads as a refusal) but imprecise.
⁸ It used to be accepted: NewRecruit's store minted "group" nodes under the selection and the step
reported success.
⁹ The store-direct adapters are more permissive than their apps here, which make these impossible.
Recorded as a follow-up rather than specced: a spec would be asserting the adapter's lookup. One
NewRecruit detail belongs with it — `addChildForce` with an id NR does not know silently adds the
book's FIRST force entry instead (see [`nr-behavioral-differences.md`](nr-behavioral-differences.md) §8).

## What the spec itself got wrong

Three failures were never an adapter's or an engine's at all, and all three satisfied
`expectFailure: true` on every engine at once:

| Step | before | after |
|---|---|---|
| `${{ steps.X.selections.se-a[7] }}` past the end of the list | engine refusal | fatal: *index 7 is out of range (valid: 0..0)* |
| `${{ steps.add-froce.forceId }}` — a step id that does not exist | engine refusal | fatal: *step 'add-froce' not found* |
| `catalogueId: cat-typo` | engine refusal | fatal: *catalogueId 'cat-typo' not found in setup catalogues* |

The runner resolved a step's inputs inside the same `try` as the engine call, and the resolver
throws `InvalidOperationException`, which the classifier reads as a refusal. Both runners now
resolve inputs first, outside the declaration, and hand it nothing but the engine call.

## Open questions

- Whether the desktop app takes a fractional cost limit (²). The driver's premise and
  `Spinners.java` disagree.
- The two store-direct adapters accepting force entries at levels their apps do not offer them (⁹),
  and NewRecruit's substitution of the first force entry for an unknown child-force id.
- Duplicating a child selection mints nothing on either engine and cannot be driven on either app;
  whether that is a refusal the engines should report is unanswered.
- An intermittent `NullPointerException` inside BattleScribe's own
  `EditRosterWindowController.removeForce`, thrown after the force is already gone. Seen twice,
  both times while a second UI lane ran on the same machine: once during the probes, and once in a
  whole `bs-ui-roster` run on `force-remove-parent-with-child-force`, which then passed five
  isolated reruns. The driver retried the removal, found the force already gone, and failed the step
  as a lookup miss — the retry is the part worth a look, since `removeForce` is not idempotent.
