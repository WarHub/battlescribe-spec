# Error Assertions

Spec YAML files can assert validation errors produced by the engine after each step.
An error assertion names **the roster node the engine raised the error on**, the constraint it came
from, and optionally part of its message.

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
