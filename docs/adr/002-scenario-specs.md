# ADR 002: Scenario specs — gamedata and roster steps in one spec

- **Status:** Accepted
- **Date:** 2026-10-30
- **Issue:** #273 (spike). Unblocks #280 (roster update from new data) and #281 (the end-to-end
  kitchen-sink spec).

## Context

Every spec today belongs to exactly one domain, inferred from its path. A **roster** spec
(`specs/roster/`) builds rosters from data that never changes. A **gamedata** spec
(`specs/gamedata/`) edits data files and never builds a roster. Each domain has its own model
(`SpecFile`, `GameDataSpecFile`), its own runner (`RosterRunner`, `GameDataRunner`) and its own
engine interface (`IRosterEngine`, `IGameDataEngine`).

What neither domain can say is the thing a data author actually does:

1. Edit a catalogue in the data editor and save it.
2. Start a roster in the roster editor, which picks up the saved data.
3. Save the roster.
4. Edit the data again and save it.
5. Reopen the saved roster and see what changed.

The step that matters is the **hand-off**: what crosses from one editor to the other, and when.
Today's specs test each editor on its own, so they never exercise it.

### How the real applications are put together

The normative application has the hand-off in a specific shape (see AGENTS.md, "What is
normative"). BattleScribe 2.03 is two programs, `DataEditor.jar` and `RosterEditor.jar`. They share
one thing, the data folder `~/BattleScribe/data`:

- The Data Editor writes a file there when the author saves.
- The Roster Editor reads the folder again every time its New Roster dialog opens. Its cache is
  per file and revalidated against `lastModified()` (`docs/bs-ui-driver.md`).
- The Roster Editor reads the folder when it opens a `.ros`.
- Unsaved edits never reach the roster side, because they are not on disk.

NewRecruit has the same shape across two web apps. The NR Editor exports BattleScribe XML, and the
NR app loads it into its library.

Nothing else is shared. The engine survey for this ADR found that **no built-in family shares
in-memory state between its two engines**:

- `battlescribe-ui` runs two JVMs.
- `newrecruit` and `newrecruit-ui` run two browsers on two origins.
- `battlescribe` runs two unrelated object graphs in one process.

Every one of them already exchanges data as XML: the gamedata engines export it, and the roster
engines take it through `SetupFromFiles`.

## Decision

Add a third spec domain, **scenario**, under `specs/scenario/{category}/{id}.yaml`. A scenario spec
drives one engine family's **data editor** and **roster editor** in a single run. The two are joined
by a **data directory** that only explicit hand-off steps write to and read from.

### Spec shape

```yaml
id: handoff-reopen-roster-against-edited-data
category: handoff
description: >
  ...

setup:
  edit: cat-1 # the data editor's open file, exactly as in a gamedata spec
  gameSystem: {...} # the files the data directory starts with
  catalogues: [...]

steps:
  - action: newRoster # roster editor: new roster on the data directory as it is

  - roster: # steps in the roster editor — the roster spec vocabulary, unchanged
      - action: addForce
        id: add-detachment
        forceEntryId: fe-1

      - action: selectEntry
        forceId: ${{ steps.add-detachment.forceId }}
        entryId: se-squad

  - action: saveRoster # roster editor: File > Save
    file: my-list

  - data: # steps in the data editor — the gamedata spec vocabulary, unchanged
      - action: setFields
        entryId: se-squad
        costs:
          ct-pts: "15"

  - action: saveData # data editor: File > Save, for the open file

  - action: openRoster # roster editor: File > Open, against the data directory as it is now
    file: my-list

  - roster:
      - expectedState: {...}
```

The top-level `steps` list holds three kinds of item, and each item is exactly one of them:

| Item | What it is |
|---|---|
| `data:` | A block of gamedata steps (`GameDataStepDef`) run against the family's gamedata engine |
| `roster:` | A block of roster steps (`StepDef`) run against the family's roster engine |
| `action:` | A **hand-off**: one of the four actions below |

Blocks reuse the existing step models and vocabularies verbatim. A scenario adds no field to either.
This is why steps are grouped into blocks instead of each step carrying a `domain:` tag. The two
vocabularies share field names that mean different things: `entryId` is "the entry to select" in a
roster step and "the entry to edit, or the declared id" in a gamedata step. A flat merged step type
would consume one meaning as the other.

The hand-offs sit at the top level, outside every block, so a reader sees at a glance where data
crosses between the editors.

### The four hand-offs

| Action | Fields | Semantics |
|---|---|---|
| `saveData` | — | The data editor saves its **open** file: `Reload()` (serialize to the on-disk form and load back) and then `ExportActiveFile()`. The data directory now holds that XML under the file's root id. Saving another file means an `openFile` step in a `data:` block first, as in the app. |
| `newRoster` | — | The roster editor reads the data directory and starts an empty roster on it: `Cleanup()`, then `SetupFromFiles(data directory)`. |
| `saveRoster` | `file` | The roster editor saves its roster: `ExportRosterXml()` is kept under `file`. |
| `openRoster` | `file` | The roster editor reads the data directory and opens the roster saved as `file`: `Cleanup()`, then `SetupFromFiles(data directory)`, then `LoadRoster(xml)`. |

The rule a spec author has to learn is one sentence: **the roster editor sees data only as of the
last `saveData`, and only when it starts or opens a roster.** Unsaved edits stay in the data editor.

Any hand-off may carry `expectFailure`, with the same contract as on an action step
(`docs/error-assertions.md`). An `openRoster` that the engine refuses is exactly what #280 needs to
assert when an edit removes something the roster depends on.

### The data directory

The data directory is a map from root id to `(fileName, xml)`, held by the runner. It plays the
part of `~/BattleScribe/data`.

- **Seeded at setup from the data editor's own serialization.** After `Setup` + `OpenFile(edit)`,
  the runner opens and exports each setup file in turn (`OpenFile(id)`, `ExportActiveFile()`), then
  reopens `setup.edit`. So the files the roster editor first reads are the ones its own family's data
  editor writes. They are also the only XML the runner can obtain: the TestKit is engine-free and
  AOT-clean, and it does not reference the Protocol→XML generator in `BattleScribeSpec.XmlGen`.
- **Written only by `saveData`.** A file loaded mid-spec with `openFile` + `content` joins the data
  directory the first time it is saved, and not before.
- **Named `{id}.gst` / `{id}.cat`**, with the root element and id read from the XML.

`saveData` calls `Reload()` before exporting because, in the Data Editor, opening another file
re-reads it from disk. A save that only exported would lose the edits the next time the author
switched files. Every gamedata engine already implements `Reload` as save-and-reopen, so the rule is
the same for every family.

### Engine session: a pair, not a merged interface

A scenario runs against an `(IGameDataEngine, IRosterEngine)` pair from one family. Neither
interface gains a member, because every hand-off is a composition of operations both interfaces
already have. The roster engine's existing `Cleanup()` → `Setup*` sequence is what the protocol
host's warm reuse already relies on between specs. Here it is also used between roster sessions
within one spec, and the `IRosterEngine` contract now says so.

**Over the protocol nothing changes.** One adapter connection already holds a live roster engine and
a live gamedata engine side by side (`AdapterHandler` keeps them in separate slots). Every hand-off
maps onto commands that exist in v1.1:

| Hand-off | Wire |
|---|---|
| seed / `saveData` | `gamedataAction` `openFile` / `reload` / `exportFile` |
| `newRoster` | `setupFromFiles` (the host recreates or cleans the roster engine) |
| `saveRoster` | `exportRosterXml` |
| `openRoster` | `setupFromFiles` + `action` `loadRoster` |

An external adapter that advertises both `roster` and `gamedata` in `describe` can run scenarios with
no new code. The runner resolves `addForce`'s default catalogue from the data directory's catalogue
ids on the client side, as it does from a roster spec's setup. So the host clearing its catalogue
list on `setupFromFiles` does not reach the spec.

### Runner

`ScenarioRunner` composes the two existing runners rather than reimplementing them. Each runner gains
a step-at-a-time entry point that its own `Run` now goes through as well: begin, execute one step,
read the errors it added, end. So a step in a `roster:` block is executed by exactly the code that
executes it in a roster spec: assertions, `skipEngines`, `expectFailure`, `expectedFile`
snapshots and all. The scenario runner adds:

- the data directory and the saved rosters;
- the four hand-offs;
- one error list in step order;
- one global step index across blocks, counted from 0 like the other runners' indices, so `Step 7`
  is the eighth step or hand-off down the file, whichever block it is in.

Step ids are one namespace across the whole spec. Expressions resolve within their domain: a roster
step references roster outputs and a data step references data outputs. A data-side id a roster step
needs, such as a declared `entryId`, is written as the literal id. That id is stable by construction,
which is why declared ids exist.

### Per-engine expectations

A scenario's identity is the family's concrete engine name (`battlescribe`, `battlescribe-ui`,
`newrecruit`, `newrecruit-ui`). Overrides follow one rule in both kinds of block: the concrete name's
entry wins, otherwise the base engine's entry applies (`battlescribe-ui` → `battlescribe`). That is
the roster runner's existing rule. The gamedata runner takes the same optional base identity, which
only scenario runs pass, so gamedata specs are unaffected.

Spec-level `engines: {name: skip|fail}` is unchanged.

### Where scenarios run

| Family | Test lane | `pre-push` |
|---|---|---|
| `battlescribe` | `BsScenario` | whole |
| `newrecruit` (frozen HAR + frozen NR Editor) | `FrozenNrScenario` | whole |
| `newrecruit-ui` (frozen) | `FrozenNrUiScenario` | whole |
| `battlescribe-ui` (desktop apps) | `BsScenarioUi` | no (needs the app); CI `thorough-ui-bs` runs it |

`bs-spec run <spec>` infers the domain from `specs/scenario/`, `--scenario` selects it explicitly, and
`run --all` / `compare` include it. A scenario needs an adapter that advertises both domains and is
skipped, with that reason, when it does not.

## Consequences

### Positive

- #280 and #281 can be written. Roster-update-from-new-data becomes an ordinary assertion after
  `openRoster`.
- The hand-off is explicit in the spec and has one rule. A spec cannot leak unsaved edits into a
  roster by accident, and an engine that does leak them fails a spec that says so.
- No protocol change and no engine interface change. Every adapter that serves both domains runs
  scenarios as it stands.
- Assertion logic stays in one place per domain. The runners gain an entry point, not a fork.

### Negative

- **Roster engines are now set up from files in the main suite**, not only in `dataSource` specs.
  `SetupFromFiles` had to become a full equal of `Setup` in the engines where it was not:
  - `battlescribe`: entry lookup, cost types, constraint-declarer index, catalogue force entries,
    and a clean second setup. Before the fix, 68 of the 400 inline roster specs failed through file
    setup and passed through `Setup`; `BsFileSetupParityTests` now holds every one to the same
    verdict both ways.
  - `newrecruit-ui`: game-system and entry records, so the UI driver can act after a file setup. Both
    setup paths now build one index from the XML NR is given (`NrUiDataIndex`).
- **The protocol host resets a warm roster engine set up again mid-spec**, as teardown resets it
  between specs. The client cannot ask for that reset itself: `teardown` resets both domains, and the
  gamedata engine is mid-spec.
- **`battlescribe-ui` runs its two editors as two isolated apps** and the runner carries the files
  between them. The real apps share one folder on disk. Sharing a home between the two JVMs collides
  in staging today (see `docs/bs-ui-driver.md`), and the bytes that cross are the ones the Data
  Editor's own serializer produces, so what the Roster Editor reads is the same. The difference only
  matters for live file-watching (below).
- Every `newRoster`/`openRoster` on `battlescribe-ui` restarts the Roster Editor, because the data
  changed. Each costs a JVM cold start (~5–10 s).

### Not decided here

- **The Roster Editor's live reload prompt.** When the data under an open roster changes,
  BattleScribe asks "A file was modified outside of BattleScribe. Would you like to reload your
  roster?". Asserting what accepting it does is a fourth way into roster migration, alongside reopen.
  It needs the shared folder above. Tracked in #568, beside #280.
- **Which roster-node ids survive `openRoster`.** Expressions captured before a save still resolve
  after a reopen; whether they still name live nodes is an engine fact, and #280 records it.
- A JSON Schema for scenario specs (#569). Gamedata specs have none either; the roster schema covers
  roster specs only.

## What the first scenarios found

All five `handoff` scenarios pass on every family, `battlescribe-ui` (the desktop apps) included.

- **Reopening a roster re-prices it from the new data.** BattleScribe shows the saved roster at the
  edited cost, not the cost the `.ros` recorded. So do the other three families.
- **Selection ids survive a reopen.** They do on BattleScribe and on both NR engines. NewRecruit
  mints a new force id. Expressions captured before a save therefore still name the same selection
  afterwards, but not necessarily the same force.
- **NR loads NR Editor output.** Both NR roster engines load the NR Editor's own exports, edited and
  reloaded ones included.
- **For #280, not yet checked against the app:** the in-process adapter's `LoadRoster` keeps the
  force and silently drops a selection whose entry the edited data no longer has.

Found along the way and filed:

- #566: game-system entries are missing from the in-process adapter's entry lookup.
- #567: newrecruit-ui's by-name pick matches a substring of the entry label.

## Options considered

### Spec schema

- **A per-step `domain:` tag on a merged step type.** Rejected: the two vocabularies reuse field names
  with different meanings (`entryId`), and every step would carry the tag.
- **Per-step wrappers** (`- roster: {action: …}`). Unambiguous, but one wrapper per step for no gain
  over blocks.
- **Blocks with hand-offs inside them** (`- data: [..., {action: save}]`). Rejected: it hides the
  hand-off inside a block, and the hand-off is what the spec is about.
- **One model for every spec** — roster and gamedata specs as scenarios with one kind of block.
  Rejected for now: it would migrate 523 files and two lint suites for no new expressive power. A
  scenario with a single block already is a roster or gamedata spec, so the merge stays available.
- **Blocks plus top-level hand-offs** (chosen).

### Hand-off

- **Implicit at block boundaries** (entering a `roster:` block re-reads data). Rejected: it erases
  exactly the distinction the spec is about, saved versus unsaved, and it makes "when does the roster
  editor read the data" a property of the YAML's layout.
- **A merged engine object over shared in-memory data.** Rejected: no engine family has one, the real
  applications do not work that way, and it would need a new interface on every adapter.
- **Explicit hand-offs over a data directory** (chosen).

## Implementation

This ADR lands with its implementation:

- the scenario model, loader, runner and lint, plus the formatter's knowledge of blocks;
- the runner entry points;
- the `SetupFromFiles` fixes and the host reset above;
- the four lanes;
- the CLI support (`run`, `run --all`, `compare`, with results keyed by domain);
- a first set of `handoff` scenarios checked against the BattleScribe desktop apps.

Follow-ups:

- #568: the live reload prompt.
- #569: a scenario schema.
- #570: `bs-spec run --break` on scenarios — done. `ScenarioRunner.OnBeforeStep` fires once before
  every block step and every hand-off, with the global index, and can stop the run. The REPL reaches
  both editors over the one connection: `data state` and `roster state`, with bare `state` meaning the
  paused step's editor. It refuses the roster editor before the first `newRoster`/`openRoster`, and an
  unnamed editor at a hand-off.
- #280 and #281, which this unblocks.
