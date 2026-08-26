# Error Assertions

Spec YAML files can assert validation errors produced by the engine after each step.
An error assertion names **the roster node the engine raised the error on**, the constraint it came
from, and optionally part of its message.

## Two different questions

There are two ways a spec can be about something going wrong, and they are not the same question:

| | Asserts | Field |
|---|---|---|
| The engine **accepted** the operation, and the roster it produced is not legal to field | the roster's validation list | `expectedState.errors` |
| The engine **refused** the operation — it never happened | the refusal itself | `expectFailure` |

A malformed `.ros` never becomes a roster, so it has no validation list to match against; an
over-limit roster exists perfectly well and merely reports errors. Everything from `## Quick
Reference` down is the first question. The second is [`expectFailure`](#expectfailure--asserting-that-an-action-was-refused),
at the end.

## Quick Reference

| Field | Where | Purpose |
|-------|-------|---------|
| `errors:` | `expectedState` | Exact-set match: every assertion must match, no extras allowed |
| `errorsContain:` | `expectedState` | Subset match: listed errors must match, extras are allowed |
| `errorCount:` | `expectedState` | Count-only: asserts the total number of errors |
| `on:` | error assertion item | The roster **node** the engine raised the error on |
| `from:` | error assertion item | The source entry and constraint (required) |
| `messageContains:` | error assertion item | Optional substring match on the error message text |

## Implicit Zero-Errors Default

If a step's `expectedState` does not include any error assertion (`errors:`,
`errorsContain:`, or `errorCount:`), the spec runner automatically asserts that
there are **zero** validation errors. This default is skipped for `dataSource` specs.

To explicitly expect zero errors, use `errors: []`.

## `on:` — the raising node

`on:` names a **roster node**: the element the engine was looking at when it raised the error. It is
matched against `raisedOnType` + `raisedOnId`, which every engine reports and which nothing in the
pipeline rewrites.

Node ids are minted at run time and are per-session on every lane — NewRecruit mints short uids,
BattleScribe mints GUIDs, and both regenerate on every run. **A node id is therefore always written
as a `${{ steps.… }}` reference**, resolved against the outputs of the step that created the node.
A literal id can never be correct and is not read as one.

| Kind | Form | Example |
|------|------|---------|
| `force` | `force ${{ steps.<id>.forceId }}` | `force ${{ steps.add-army.forceId }}` |
| `category` | `category ${{ steps.<id>.categories.<categoryEntryId> }}` | `category ${{ steps.add-patrol.categories.cat-troops }}` |
| `selection` | `selection ${{ steps.<id>.selectionId }}` | `selection ${{ steps.select-parent.selectionId }}` |
| `selection` (auto-selected) | `selection ${{ steps.<id>.selections.<entryId> }}` | `selection ${{ steps.add-patrol.selections.se-unit-a }}` |
| `roster` | `roster` — **bare** | `roster` |
| `group` | `group` — **bare** | `group` |

### Naming one of several nodes of the same entry

`selections` and `categories` are keyed by **catalogue entry id**, because that is the only name a
spec can write down. One step routinely mints more than one node from one entry — `min: 2` auto-adds
two selections, a force entry can link one category entry twice — so each key holds an ordered
**list**, and a trailing `[n]` picks one:

```yaml
selectionId: ${{ steps.add-patrol.selections.se-unit-a }}      # the first  Unit A
selectionId: ${{ steps.add-patrol.selections.se-unit-a[1] }}   # the second
```

The bare form is index `0` — the first node — so `[0]` and no index are the same address. An index
past the end fails loudly, naming how many nodes there actually are; it never resolves to nothing.

This is the same defect as the entry-addressed `on:`, one level down: until it was fixed the map held
one node per entry id, so a step that created two selections of one entry left one of them in the
roster with nothing able to name it (#428).

`roster` and `group` are written bare because **neither node has an id a spec can name**:
`RosterState` exposes none on any of the four lanes, and a `selectionEntryGroup` node — which
NewRecruit materialises with its own errors — appears in no engine's state model at all. Both are
matched on kind alone, which measurement says is never ambiguous: across the whole corpus, on both
lanes, no step has more than one roster-raised or more than one group-raised error.

The record carries one more field about the same node: `raisedOnEntryId`, the raising node's
*catalogue entry* id. It is not a weaker `raisedOnId` — it names the entry, which every node built
from that entry shares, so it can never address one node and `on:` never matches it. It exists
because a per-run node id is unreadable on its own: it is what makes a failure line say `category
cat-node-9f3 [cat-troops]` instead of just the id.

### Where the engines disagree, the spec says so

BattleScribe and NewRecruit raise the same violation on different nodes, and they do it often — on
**24 of the 38 assertions both lanes evaluate**. A collective over-limit violation is raised by
BattleScribe on the container that counted it (the category, the force, or the roster) and by
NewRecruit on one violating selection. Entry-group constraints are raised by NewRecruit on the group
node and by BattleScribe on the enclosing selection.

Neither answer is reconstructed into the other. The spec records both, as a base assertion plus an
`engines:` block:

```yaml
- expectedState:
    errors:
      - on: category ${{ steps.add-patrol.categories.cat-troops }}
        from: se-unit-a/con-max-boosted
    engines:
      newrecruit:
        errors:
          - on: selection ${{ steps.select-first.selectionId }}
            from: se-unit-a/con-max-boosted
```

An `engines:` key replaces the base list wholesale for that engine; other `expectedState` fields are
inherited. A UI lane inherits its base engine's block unless it declares its own
(`newrecruit-ui` falls back to `newrecruit`).

### The entry-addressed form is gone

A second token that is **not** a `${{ … }}` expression names a catalogue entry, which is the form
#419 removed. It matches nothing, and the linter rejects the spec before it runs so the failure names
the mistake rather than reading as a missing error:

```yaml
- on: selection se-unit-a                              # rejected: an entry names a SET of nodes
- on: selection ${{ steps.select-first.selectionId }}  # the node
```

The discriminator is the presence of `${{`, and it is exact rather than a guess: a node id can only
ever be written as a step reference, and a catalogue entry id never is.

The whole second token must be the expression. `${{ … }}` with a stray brace, a prefix
(`selection sel-${{ … }}`), or trailing text is not a partial reference — the resolver substitutes
nothing and hands the value back, so the address resolves to a literal that matches no node and the
spec fails as though the engine had stopped raising the error. The linter and the schema both reject
it, so that silent shape cannot reach a run.

## `from:` (required)

Identifies the source entry and constraint that caused the error.
Format: `{entryId}/{constraintId}`.

| Format | Example | Meaning |
|--------|---------|---------|
| `{entryId}/{constraintId}` | `se-unit-a/con-min-1` | Error from constraint `con-min-1` on entry `se-unit-a` |
| `costLimits/{costTypeId}` | `costLimits/pts` | Cost limit violation for cost type `pts` |
| `{entryId}/hidden` | `se-unit-a/hidden` | Hidden entry error for entry `se-unit-a` |

## `messageContains:` (optional)

When set, the actual error's message text must contain this substring
(case-insensitive). Useful for distinguishing between errors that share the same
`on`/`from` but have different messages.

```yaml
errors:
  - on: category ${{ steps.add-patrol.categories.cat-troops }}
    from: se-unit-a/con-min-1
    messageContains: "at least 1"
```

## Exact-Set Matching (`errors:`)

The `errors:` field requires an **exact-set match**:
1. Every listed assertion must match exactly one actual error
2. Every actual error must be matched by an assertion (no extras allowed)
3. Matching is **order-independent** — the order of assertions doesn't matter

```yaml
- expectedState:
    errors:
      - on: category ${{ steps.add-patrol.categories.cat-troops }}
        from: se-unit-a/con-min-troops
      - on: category ${{ steps.add-patrol.categories.cat-hq }}
        from: se-hq/con-min-hq
```

An empty list `errors: []` explicitly asserts zero errors.

### One assertion consumes one error

Matching is **one-to-one and consume-once**: each assertion claims one unmatched error, and no error
can satisfy two assertions. Node addressing narrows what an assertion names; it does not make one
individually identifying, and some errors are genuinely indistinguishable. BattleScribe evaluates a
`field: forces` constraint once per force instance and hangs every result on the roster, so
`constraint-forces-field-on-forceentry` step 5 produces **three byte-identical errors** sharing
raising node, `from:` and message. NewRecruit reports nothing at that step, so the fact that would
tell them apart exists on neither lane. Three assertions are written, and three errors are consumed.

## Subset Matching (`errorsContain:`)

The `errorsContain:` field requires a **subset match**:
1. Every listed assertion must match at least one actual error
2. Additional actual errors are allowed (not flagged)

This is useful for specs that focus on specific errors without caring about
the full error set.

```yaml
- expectedState:
    errorsContain:
      - on: selection ${{ steps.select-parent.selectionId }}
        from: se-unit-a/con-min-1
```

**Mutually exclusive** with `errors:` — using both in the same `expectedState`
is a runtime error.

## Count-Only Matching (`errorCount:`)

The `errorCount:` field asserts only the total number of validation errors
without matching specifics. Useful for smoke tests.

```yaml
- expectedState:
    errorCount: 3
```

**Cannot** be combined with `errorsContain:` or `errors:` — all three are mutually exclusive.

## Examples

### A collective violation, recorded on both engines

Three selections of one entry exceed a max. BattleScribe raises it on the force's Troops category;
NewRecruit raises it on the first of the three selections.

```yaml
- action: addForce
  id: add-patrol
  forceEntryId: fe-patrol

- action: selectEntry
  id: select-first
  forceId: ${{ steps.add-patrol.forceId }}
  entryId: se-unit-a

# … two more selections …

- expectedState:
    errors:
      - on: category ${{ steps.add-patrol.categories.cat-troops }}
        from: se-unit-a/con-max-boosted
    engines:
      newrecruit:
        errors:
          - on: selection ${{ steps.select-first.selectionId }}
            from: se-unit-a/con-max-boosted
```

### Cost limit exceeded

```yaml
- expectedState:
    costs:
      - typeId: pts
        value: 10
    errors:
      - on: roster
        from: costLimits/pts
```

### A constraint scoped across child forces

The error belongs to the force whose scope did the counting, not to the force holding the extra
selection.

```yaml
- expectedState:
    errors:
      - on: force ${{ steps.add-army.forceId }}
        from: se-squad/con-1
```

### An entry-group constraint

```yaml
- expectedState:
    errorsContain:
      - on: selection ${{ steps.select-parent.selectionId }}
        from: seg-weapons/con-max-1
    engines:
      newrecruit:
        errorsContain:
          - on: group
            from: seg-weapons/con-max-1
```

### Hidden entry error

```yaml
- expectedState:
    errors:
      - on: selection ${{ steps.select-unit.selectionId }}
        from: se-1/hidden
```

## `expectFailure` — asserting that an action was refused

`expectFailure` goes on an **action step** and says the engine will not do what the step asks. It is
the other question from the one the rest of this document answers: `errors:` describes a roster the
engine built, `expectFailure` describes an operation the engine declined.

```yaml
- action: loadRoster
  content: |
    <?xml version="1.0"?>
    <roster id="ros-broken" name="Broken"
  expectFailure: true
```

Three shapes, and per-engine overrides take the same three:

```yaml
expectFailure: true                        # must be refused; message unconstrained
expectFailure: false                       # must succeed
expectFailure:
  messageContains: "ParseError"            # must be refused, saying this
  engines:
    newrecruit: { messageContains: "Unexpected close tag" }
    newrecruit-ui: false                   # this engine ACCEPTS the payload
```

`messageContains` is a case-insensitive substring of **the engine's own message**, not of the
harness framing around it — so an expectation survives the harness rewording its logs.

### The run continues past a refusal

A refused step does not end the spec. That is the point: what a refusal *left behind* is usually the
conformance question worth asking.

```yaml
- action: loadRoster
  content: "<roster truncated"
  expectFailure:
    messageContains: "ParseError"

# The refusal changed nothing — the roster the editor built is still the one in hand.
- expectedState:
    forceCount: 1
```

An action that **succeeds** where the spec declared a refusal fails the step, naming the outputs it
got. The assertion is two-sided.

### Only an engine refusal satisfies it

Four different things can make an action fail, and only one of them is engine behaviour. The other
three stay fatal:

| What happened | Satisfies `expectFailure`? |
|---|---|
| The engine looked at the input and declined | **yes** |
| The adapter could not resolve an id the spec named | no — a spec bug |
| The engine does not implement the action at all | no — a capability gap |
| The harness or transport broke; or the adapter did not classify | no |

Without that line, `expectFailure: true` would be satisfied by a typo in the spec's own payload, by
an engine that cannot even attempt the action, and by a dead adapter — three ways for a conformance
test to pass while verifying nothing. It is the same rule as #309 (an engine that cannot load must
fail, never silently skip) and the same rule as the export gap in `ExecuteFileAssertion`.

Each of the three failing branches says what to do next, so the distinction shows up as a fix and
not as a puzzle:

```
Step 4: 'deselectSelection' failed because the adapter could not resolve an id this spec named —
not because engine 'battlescribe' refused it: "Selection with ID '5515-887b' not found in force
'edbd-4f03'.". expectFailure asserts engine refusals only: every engine resolves ids through its
own adapter, so an unresolvable id fails identically everywhere and asserting one would make a spec
typo pass. Fix the id, or drop expectFailure from this step.
```

The classification is made by the adapter, which is the last place that still has the exception, and
travels as [`kind`](adapter-protocol.md#kind--why-the-action-failed-optional) on the action result.
An adapter that does not send it is fully conformant and simply cannot have its refusals asserted:
the spec fails, naming the field, rather than passing on a failure nothing examined.

### `expectFailure: false` says more than nothing does

Omitting the field means the load was never in question. `false` means the refusal was the
hypothesis, it was tested, and the engine accepted — a recorded negative result rather than an
untested assumption. `specs/roster/roundtrip/roundtrip-load-unknown-game-system.yaml` uses it that
way: BattleScribe loads a roster naming a game system it does not hold, keeping the dangling
`gameSystemId` verbatim while resolving everything in the file against the system that *is* loaded.

Use it as a per-engine override for the same reason — an engine that accepts input the others reject
is a finding, and `skipEngines` would hide it behind "we did not look".

### What it is not for

An operation the engine silently ignores is not a refusal, and `expectFailure` will fail the spec
for saying it is. `setSelectionCount` with a negative count is a no-op on BattleScribe: the
selection keeps its previous count and no error is raised. The conformance question there is
answered by `expectedState`, which is where a no-op's evidence lives.

Nor does it apply to ids that do not exist. A step naming a selection that was already removed fails
in the adapter's lookup, identically on every engine, before any engine is consulted — that is the
`address` row in the table above, and it measures the harness rather than the engines.
