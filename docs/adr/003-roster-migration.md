# ADR 003: Roster migration — what a saved roster becomes when its data changes

- **Status:** Accepted
- **Date:** 2026-10-31
- **Issue:** #280 (spike). Builds on ADR 002 (scenario specs). Feeds #281 (the end-to-end kitchen-sink
  spec) and #568 (BattleScribe's live reload).

## Context

A roster outlives the data it was built from. Data authors fix costs, rename entries, retire units and
tighten limits; a user who opens last month's roster opens it against this month's data. Every roster
editor has to decide, for each selection in the file, what to do when the data no longer agrees with it.
That is migration, and #280 asked three questions about it:

1. What does each engine do on reload against changed data: re-resolve, drop orphans, re-apply
   constraints and costs, keep custom names?
2. How does a spec say "edit the data, then reload the roster against it"?
3. What is assertable across engines, and what is legitimately divergent?

The second question is answered by ADR 002. A scenario spec saves a roster (`saveRoster`), edits and
saves the data (`data:`, `saveData`), and opens the roster again (`openRoster`), and the roster editor
sees exactly the data as of the last save. Nothing new is needed to express a migration.

## Decision

**Migration is asserted by scenario specs under `specs/scenario/migration/`, one data change per spec.**

Each spec has the same shape, so a reader compares like with like:

1. `newRoster`, then build a roster small enough to see whole: one Detachment, one Squad, sometimes an
   upgrade or a second Squad.
2. `saveRoster`.
3. One change in the data editor, then `saveData`.
4. `openRoster`, then one `expectedState` saying what came back.

One change per spec keeps a verdict about one thing. A spec that renamed an entry and deleted another
would fail on either, and say less about both.

**The expectation is the BattleScribe app's, checked through `battlescribe-ui` first**, as for every spec
(AGENTS.md, "What is normative"). NewRecruit's differences are per-engine overrides, each saying in the
description what NewRecruit does and why it is a difference rather than a fault.

**The `battlescribe-ui` roster driver answers the app's data-problems dialog.** When BattleScribe fits a
loaded roster to data that has lost something, it rebuilds the roster and then lists what it did in a
modal "There was a problem with one or more of your data files" dialog. A user reads it and presses DONE.
The driver used to time out there, with the roster already loaded behind the dialog. It now presses DONE
when every item the dialog lists is a data-change notice:

- "Some selections have been removed from your roster because they no longer exist in the data file(s)."
- "No force entry found", under its "The catalogue data file is invalid:" header.

Anything else fails the load with the app's own words. A roster loaded against data the harness got wrong
should be a failure, not a green run with a warning in its log. The notices go to the run log, because no
engine contract carries them yet (#578).

## What the migration scenarios found

Eight specs, each run on `battlescribe-ui` first, then `battlescribe`, `newrecruit` and `newrecruit-ui`.

| Data change | BattleScribe (app and adapter) | NewRecruit |
|---|---|---|
| Entry deleted (`migration-entry-removed`) | Selection dropped, no error. The app also says so (#578). | Same; nothing said. |
| Entry renamed (`migration-entry-renamed`) | Selection shows the new name. | Same. |
| Entry hidden (`migration-entry-hidden`) | Selection kept and hidden; hidden violation (on its **category**). | Same (violation on the **selection**). |
| Max tightened below the count (`migration-constraint-tightened`) | Both selections kept; max violation on the **force**. | Kept; violation on the first **selection**. |
| Cost changed, with a custom name and notes (`migration-custom-name-kept`) | New cost; name and notes kept. | New cost; name kept, **notes lost**. |
| Child entry deleted (`migration-child-entry-removed`) | Child dropped, parent kept and re-priced (#578). | Same. |
| Mandatory child added (`migration-mandatory-child-added`) | **Not added**; min violation reported. | **Added on load**; nothing reported. |
| Force entry deleted (`migration-force-entry-removed`) | Force dropped; the app calls the catalogue invalid, "No force entry found" (#578). | **Load refused**: "Couldn't add Force". |

**Answers to #280:**

1. **What each engine does.** Both families re-resolve a saved roster against the data on hand. Names,
   costs and validation come from the data, and what the user typed comes from the file. Neither trims a
   roster to fit a tightened rule; both report it. A selection whose entry is gone is dropped
   everywhere; only the BattleScribe app says so.
2. **How to say it.** ADR 002's hand-offs, as above. No new vocabulary.
3. **Assertable versus divergent.** Re-resolution, re-pricing, renaming, kept custom names, dropped
   orphans and reported violations are common to both families, and each spec asserts them once, for
   every engine. Four things differ, and each is an override with its reason in the spec's description:
   - where a violation is raised (the families' usual difference, not a migration one);
   - NewRecruit losing selection notes through the saved file;
   - NewRecruit selecting a newly mandatory child on load, where BattleScribe only selects one when the
     user selects the parent;
   - NewRecruit refusing a roster whose force entry is gone.

**The adapter lead from #273 is settled.** ADR 002 recorded that the in-process `battlescribe` adapter
silently drops orphaned selections on load, "unverified against the app". The app drops them too, so
the adapter is right about the roster. What it lacks is the dialog, which is #578's question for every
engine.

## Consequences

### Positive

- Migration has a home and a shape. A new case is one more spec in `migration/`, built the same way.
- Four real differences between the families are pinned and explained, where before nothing asserted
  them.
- The `battlescribe-ui` driver can load a roster the data has outgrown. Any roster spec that loads one
  against changed data now reaches its assertions instead of timing out.

### Negative

- The dialog's contents are logged, not asserted (#578).
- Two specs say nothing about migration on `newrecruit-ui`: the NR Editor UI driver cannot make their
  data edit. A setup-declared constraint is unreachable to `setFields`, and an `addEntry` under an entry
  that has children times out. Both declare `newrecruit-ui: fail` naming #579.
- A category change is not covered. Neither data-editor UI driver can retarget a category link, and an
  expectation the app never ran is not one this suite keeps.

## Not decided here

- **Live reload.** BattleScribe offers a second way into migration: change the data under an open roster,
  and it asks "A file was modified outside of BattleScribe. Would you like to reload your roster?" #568
  covers what accepting it does.
- **Read-only open.** When the data cannot edit a roster at all (a catalogue it names is missing), the app
  offers to open it read-only. The protocol has no read-only roster, and the driver refuses that load.

## Implementation

This ADR lands with:

- the eight `migration` specs;
- the `battlescribe-ui` driver's answer to the data-problems dialog (`RosterActions.applyLoadedRoster`,
  with the notices logged by `BsUiRosterEngine.LoadRosterAsync`);
- #578 (load notices) and #579 (the NR Editor UI gaps) as follow-ups.
