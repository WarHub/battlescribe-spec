# New Recruit vs BattleScribe: Behavioral Differences Report

> Based on conformance testing against [newrecruit.eu](https://newrecruit.eu)
> using the battlescribe-spec test suite.
>
> See also: [NR Ordering Analysis](nr-ordering-analysis.md) for a deep-dive into
> NR's native selection and force ordering algorithm.
>
> See also: [Cost-Field Repeat Algorithm](cost-field-repeat-algorithm.md) for the
> BS vs NR cost evaluation model comparison.
>
> **Note:** References to BattleScribe Java engine internals (`c.java`, `engine.a.f`)
> are from decompiled `lib/BattleScribeEngine.jar` and are not present as files in this repository.


| Category | Count | Severity | Description |
|----------|-------|----------|-------------|
| [Import ordering](#1-import-ordering) | 3 | Low | NR puts imported entries before faction entries |
| [Missing features](#2-missing-features) | 5 | Low | InfoLink pub/page override, page modifier, unset-primary, append repeat |
| [Scope/condition evaluation](#3-scopecondition-evaluation) | 4 | Medium | NR evaluates child-force scope, ancestor scope, and null-childId conditions differently |
| [instanceOf scope limits](#instanceof-scope-limitations-both-engines) | 12 | Info | instanceOf only works with self/parent/ancestor scope — both engines agree |
| [Entry group behavior](#4-entry-group-behavior) | 2 | Low | Child ordering, category link propagation |
| [Other behavioral differences](#5-other-behavioral-differences) | 4 | Medium | Auto-select root entries, hidden selection filtering, forces-field, real-world data |
| [Naming/spelling](#6-namingspelling) | 3 | Info | Default category name spelling, force category entryId path, selection entryGroupId unavailable |
| [Roster load](#7-roster-load-ros-import) | 4 | Medium | NR refuses forceless rosters and unknown game systems, and drops a selection with no primary category without saying so |

---

## 1. Import Ordering

**3 specs** — NR orders imported entries from CatalogueLinks BEFORE
faction-specific entries. BattleScribe puts faction entries first.

| Spec | Expected first selection | NR returns first |
|------|-------------------------|-----------------|
| `selection/catalogue-link-import` | Faction Unit | Common Unit |
| `selection/import-false-entry-hidden-via-link` | Faction Unit | Common Unit |
| `selection/import-true-entry-visible-via-link` | Squad | Veteran Squad |

**Impact**: Low — cosmetic ordering difference. Data is correct.

---

## 2. Missing Features

**5 specs** — NR doesn't implement or expose certain BattleScribe features.

### InfoLink publication/page override behavior (2 specs)

| Spec | Feature | Detail |
|------|---------|--------|
| `selection/infolink-publication-override` | InfoLink publication non-override | BattleScribe preserves target's `publicationId`; NR uses the infoLink's own publication instead |
| `selection/infolink-page-override` | InfoLink page non-override | BattleScribe preserves target's `page`; NR uses the infoLink's own page instead |

**Root cause**: NR resolves InfoLink publication/page from the link itself, not
the linked target. BattleScribe preserves the target entry's values. This is a
genuine behavioral difference in link resolution semantics.

### Page modifier not applied (1 spec)

| Spec | Feature | Detail |
|------|---------|--------|
| `modifier/modifier-entry-page` | Page modifier | NR doesn't apply `type: set, field: page` modifiers to selections |

### Unset-primary modifier (1 spec)

| Spec | Feature | Detail |
|------|---------|--------|
| `modifier/modifier-category-unset-primary` | Unset-primary modifier | NR ignores the `unset-primary` category modifier |

### Append modifier not repeated (1 spec)

| Spec | Feature | Detail |
|------|---------|--------|
| `modifier/modifier-group-with-repeat` | Append with repeats | NR applies `type: append` modifiers only once regardless of repeat count; BattleScribe applies them N times (e.g. 6 repeats → name appended 6×) |

### Previously missing, now resolved

The following features were previously listed as missing but are now working
after discovering NR's publication object model (April 2026):

- **Selection publication/page** — NR stores these on `sel.source` (not `sel`
  directly). Reading `sel.source?.publication?.id` and `String(sel.source?.page)`
  now returns correct values.
- **Rule/profile publication** — NR resolves `publicationId` into a
  `.publication` object. Using `rule.publication?.id` works correctly.
- **GameSystem-level publication** — Resolved when publication is defined in the
  same scope as the entry. See [Publication Scope Resolution](#publication-scope-resolution).
- **Force publication/page** — Accessed via `f.source?.publication?.id` and
  `String(f.source?.page)`.

---

## 3. Scope/Condition Evaluation

**3 specs** — NR evaluates certain condition types differently, causing
modifiers to trigger when they shouldn't (or vice versa).

| Spec | Issue |
|------|-------|
| `scope/scope-include-child-forces` | Condition with `scope=force, childForces=true` triggers when it shouldn't |
| `scope/scope-include-child-forces-nested` | Same issue in nested force scenario |
| `scope/scope-ancestor` | Ancestor scope modifier fires in NR but not in BattleScribe |
| `condition/condition-null-childid` | Missing childId: NR counts all selections (condition fires), BS returns NaN (condition false) |

These specs test complex condition evaluation where NR's implementation
diverges from BattleScribe's. For scope specs, the modifier fires (changing the
selection name), proving the condition evaluates to true in NR but false in BS.
For the null-childId spec, BattleScribe's resolver returns null when childId is
absent, causing the query to return NaN and the condition to evaluate as false.
NR defaults missing childId based on node type: forces/groups use `"any"` (count
everything), other nodes use `"self"` (count self). See [NR Condition Engine](#nr-condition-engine-internals)
discovery section for the decompiled code analysis.

Two companion specs (`condition-null-childid-parent-scope`, `condition-null-childid-force-threshold`)
test NR's alternative defaults for missing childId on different scopes (parent → "self",
force with threshold → "any"). All three null-childid specs use per-engine `expectedState`
overrides to describe both engines' behavior.

### instanceOf Scope Limitations (both engines)

**12 specs** — `instanceOf`/`notInstanceOf` condition evaluation is limited to
specific scope values. This is a BattleScribe engine design limitation that
both engines share (NOT an NR-specific or synthetic data issue).

| Scope | Works? | Reason |
|-------|:------:|--------|
| `self` | ✅ | Resolves to current Selection |
| `parent` | ✅ | Resolves to parent Selection |
| `ancestor` | ✅ | Walks parent chain (all Selections) |
| `force` | ❌ | Resolves to Force (not a Selection) — c.java:1206-1210 |
| `roster` | ❌ | Hardcoded `return false` — c.java:1196-1197 |

Working childId types for instanceOf (with self/parent/ancestor scope):

| childId type | Works? | Example spec |
|--------------|:------:|--------------|
| SelectionEntry ID | ✅ | condition-instance-of-self |
| Type name (unit/model) | ✅ | condition-instance-of-self-type |
| CategoryEntry ID | ✅ | condition-instance-of-self-category |
| ForceEntry ID | ❌ | condition-instance-of-force-entry |
| Catalogue ID | ❌ | condition-instance-of-catalogue |

Specs tagged `undefined-behavior` document scope+childId combinations that
don't work on either engine. Each references its working counterpart.

---

## 4. Entry Group Behavior

**2 specs** — NR handles entry groups differently from BattleScribe.

### Child Ordering in Collective Groups
| Spec | Issue |
|------|-------|
| `entry-group/entry-group-collective` | NR sorts children alphabetically within collective groups |

When a `SelectionEntryGroup` has `collective=true`, its child selections should
appear in **catalogue definition order**. NR instead sorts them alphabetically
by name (e.g., "Axe" before "Sword" regardless of XML order).

### Category Link Propagation
| Spec | Issue |
|------|-------|
| `entry-group/entry-group-with-category-links` | NR doesn't propagate category links from entry groups to child selections |

When a `SelectionEntryGroup` has `categoryLinks`, the child selections within
that group should inherit those category assignments. NR ignores category links
on entry groups, so child selections don't appear under the expected categories.

---

## 5. Other Behavioral Differences

**4 specs** with distinct NR behavioral differences:

### Auto-Select with `field=forces` Constraint
| Spec | Issue |
|------|-------|
| `constraint/constraint-forces-field` | NR auto-selects entry whose only min constraint has `field=forces`; BS doesn't |

After `addForce`, spec expects 0 selections but NR has 1 (auto-selected entry
with `type=model, min=1, field=forces`). BattleScribe's auto-select mechanism
(`getDefaultAmount`) only considers `field=selections` constraints. A `field=forces`
constraint counts forces, not selections, so it doesn't trigger auto-selection.
NR doesn't distinguish `field` types and auto-selects based on any `min>=1`.

Note: BattleScribe _does_ auto-select root entries that have `min>=1` with
`field=selections` — see `constraint-hidden-enforcement` and real-world specs.

### Hidden Selection Filtering
| Spec | Issue |
|------|-------|
| `constraint/constraint-hidden-enforcement` | NR filters hidden selections entirely from the tree |

BattleScribe keeps hidden selections in the tree (visible to assertions) but
marks them hidden. NR removes them completely — `selectionCount` is 0 instead
of 1 for a hidden auto-selected entry.

### Real-World Data Source
| Spec | Issue |
|------|-------|
| `real-world/wh40k-10e-space-marines-army` | NR produces different auto-selections and cost calculations for complex multi-catalogue armies |

This real-world spec builds a Space Marines army and verifies auto-selections,
unit types, and points costs. NR's results differ from BattleScribe when
dealing with multi-catalogue data interactions and complex entry resolution
chains in production game systems.

Note: `real-world/wh40k-10e-create-army` previously failed but now passes on NR.

### Auto-Select with `field=forces` Skipped
| Spec | Issue |
|------|-------|
| `auto-select/auto-select-field-forces-skipped` | NR auto-selects entries with `field=forces` constraints; BS skips them |

BattleScribe's auto-select only triggers for `field=selections` constraints.
An entry with `min=1, field=forces` is NOT auto-selected. NR auto-selects
based on any `min>=1` regardless of field type.

### Which node an error is raised on

Both engines now report a raising node (`raisedOnType`/`raisedOnId`), and they do not always
choose the same one. This is engine behaviour, not adapter normalization: each answer is read off
the element the engine itself attached the error to.

| Case | BattleScribe | NewRecruit |
|------|--------------|------------|
| A collective over-limit or hidden violation (every over-limit spec in `constraint/`, and the whole hidden family in `modifier/`) | the CONTAINER that counted — the category, the force or the roster, matching the constraint's scope | one violating SELECTION |
| A per-model constraint on a nested collective child (`selection/collective-constraint-per-model`) | the PARENT selection that owns the per-model count (`se-trooper`) — here the container is a selection, not a category | the collective CHILD selection that broke the limit (`se-weapon`) |
| A child's over-limit inside a link-reached parent (`constraint/constraint-error-owner-link-reached`) | the counting PARENT selection (`sse-unit`) | the violating CHILD selection (`se-gear`) |
| A constraint on a `selectionEntryGroup` (`selection/selection-entry-group-constraint`, `selection/collective-group-constraint-per-model`, `selection/selection-entry-group-default-with-max`, both `real-world/wh40k-10e-*`) | the owning selection — BattleScribe materialises no group node | the GROUP node, which no engine's state model represents |

The first row is the corpus's largest divergence and the reason most `constraint/` specs carry an
`engines: newrecruit:` block: BattleScribe's answer follows the constraint's scope (`parent` → the
category node, `force` → the force node, `roster` → the roster), NewRecruit's is always a selection.

`parent` resolves to a category only while the counted selections sit directly under a force — the
force's category is what BattleScribe iterates there. One level down, the parent is an ordinary
selection and BattleScribe names it, which is the second row: `collective-constraint-per-model`
counts Weapons inside a Trooper and gets `3x Trooper has 1 too many selections of Weapon`. The rule
is "the node that did the counting", not "always a category".

BattleScribe's hidden errors are a special case of the same rule for a structural reason: the
hidden-error generator runs **only inside category validation** (`f.java` L444, decompiled in
`docs/hidden-validation-analysis.md`), so BattleScribe's raising node for a hidden violation is
always a category and never the hidden selection. NewRecruit checks hidden per selection. The
consequence is visible in `modifier/modifier-set-hidden-no-category` and
`selection/selection-hidden-entry`: with no categoryLink there is no category to validate, so
BattleScribe reports nothing at all and NewRecruit's node is the only one the spec can record.

**Which selection NewRecruit picks is the FIRST sibling, and the siblings are the violating entry's
— not the counted set's.** Measured across `constraint/` on 2026-08-13:

- with three selections of one entry over a max, it names the first
  (`constraint-two-max-one-modified`, `constraint-two-max-equal-limits`, `constraint-min-and-max` —
  there the first is the one auto-select created with the force);
- with a `shared: true` constraint counting across two entry links, it still names the first
  selection of the link that violated, not of the set that was counted. In
  `constraint-entry-link-shared-counting` the fourth selection — made from `link-beta` — is what
  pushes the shared count over 3, and NewRecruit raises the error on the first `link-alpha`
  selection. `constraint-shared-flag` shows the same node absorbing both the per-link and the
  shared violation across five assertions.

Re-measured outside `constraint/` on 2026-08-13 and unchanged: `scope/scope-parent` puts four
identical Unit A selections over a max of 3 and NewRecruit names the first. That spec is also where
the old entry-addressed form was weakest — four nodes shared the entry id it named, so the
assertion held whichever of them had raised.

Neither engine's answer is reconstructed into the other's; the specs record both (issue #419
decision 2, as amended after measurement).

The link-reached row is the same divergence the spec's own `engines: newrecruit:` block already pins
for `on:`, seen one layer down: BattleScribe owns group/child constraints on the element that counts
the children, NewRecruit on the element that broke the limit. Curiously NewRecruit *does* carry
BattleScribe's answer, in the error's `hash` prefix — the node it counts over — so the two engines
disagree about which of two nodes they both know to name.

The `selectionEntryGroup` row has no BattleScribe counterpart at all. NR's group node is a real
roster node (`isGroup() === true`, its own `uid`, its own `errors` array), it is what NR raises the
group's constraint on, and it is the one raising node the state model cannot resolve —
`getSelections()` flattens groups away, and `SelectionState` records only the group's catalogue
`entryGroupId`. Such errors report `raisedOnType: "group"` with a uid no
`ForceState`/`SelectionState`/`CategoryState` carries. They are also the only errors that reach the
adapter through the flat `army.getErrors()` merge rather than the node walk.

The adapter used to report the enclosing selection beside the group as the error's *owner*, and that
reconstruction is exactly why the divergence went unrecorded for so long: while `on:` matched
`ownerType`/`ownerEntryId`, `on: selection se-parent` passed on both lanes and the group node was
invisible. Node-addressed `on:` splits them, so all three group specs now carry a bare `on: group`
under `engines: newrecruit:`, and the walk that manufactured the parent is gone (#426).

#### How the table above used to be hidden — and what removing the normalization measured

Until #426 a shared pass, `BattleScribeErrorPlacement`, ran over both BattleScribe lanes' captured
errors and moved the first three rows of that table off the node the engine named. Its own
description of what it did is worth keeping, because it is the clearest statement of the divergence
anyone wrote:

> BattleScribe's Java engine hangs an over-limit violation on the CATEGORY, FORCE or ROSTER node
> that noticed it, and can hang a violation raised inside a link-reached selection on the PARENT
> selection. NewRecruit — and the canonical spec form — attribute it to the selection that violated
> the constraint. **Min violations are the exception: both engines place those on the category, so
> they are left alone.**

It decided structurally, not from message prose: a `max` violation was over-limit and got moved; a
`forces`-field violation was a count whose subject is the roster, so it stayed. Its output was a
second attribution on the error record (`ownerType`/`ownerEntryId`), and `on:` matched that —
which is what made the two engines look like they agreed.

The census that retired it (#426, measured 2026-08-13 across 73 assertion literals) is the reason
the table above exists at all. Of the **38 assertions both lanes evaluate, the engines disagree
about the raising node on 24 — 63%**:

| BattleScribe raises on | NewRecruit raises on | count | what these are |
|---|---|---|---|
| `category` | `selection` | 11 | `max` over-limit and `hidden` violations |
| `force` | `selection` | 7 | link / shared-scope `max` violations |
| `roster` | `selection` | 3 | roster-scope `max` violations |
| `selection` | `group` | 3 | entry-group constraints |
| agree | | 14 | `min` violations (9, both on the category) and cost limits (5, both on the roster) |

That disagreement set is, item for item, what the pass was written to move — including the
min-violation exception, which shows up here as agreement. So it was never a normalization of a
spelling difference: it was one engine's answer reconstructed into the other's, and 24 assertions is
the size of what that hid. The corpus records both engines now, and the pass is gone.

### Selection Number with Min
| Spec | Issue |
|------|-------|
| `selection/selection-number-with-min` | NR returns different number/amount for min-constrained selections |

### setSelectionCount on Child Entries — Fixed

> **Previously**: The adapter used `addInstance()` in a loop, creating N separate
> instances instead of one node with number=N. This was an adapter bug, not an NR
> engine limitation.
>
> **Now fixed**: The adapter uses NR's native `sel.setAmount({}, count)` which
> correctly sets `amount=N` on a single node with proper cost propagation.
> Both engines now produce identical results for child count changes.

| Spec | Status |
|------|--------|
| `selection/selection-set-child-count-instance-model` | ✅ Both engines agree |
| `selection/selection-set-child-count-collective` | ✅ Both engines agree |

**Root selections take their count a different way**: `selectEntry` to add one and
`deselectSelection` to remove one, because a root entry taken twice is two nodes rather than
one node at number 2 (`selection/selection-same-entry-twice`,
`selection/collective-root-ignored`).

That is a convention, not an enforced rule, and this paragraph used to say otherwise — it
claimed protocol validation rejecting root targets and a lint rule
`SetSelectionCountTargetsChildOnly` enforcing it in specs. **Neither exists.** Nothing in
`AdapterHandler`, `BattleScribeRosterEngine.SetSelectionCount` or
`NewRecruitActions.SetSelectionCountAsync` refuses a root target, and the only lint rule on
the action is `SpecLintTests.CheckSetSelectionCountHasSelectionId`, which requires a
`selectionId` and says nothing about what it points at. The one real refusal is per-engine and
narrower than the claim: `NrUiActions.SetSelectionCountAsync` throws `NotSupportedException`
for a root selection, because NR's UI renders no number input for one.

Corrected 2026-09-03, after the sentence was taken at face value and repeated into a spec
description that had to be corrected too.

### Cost-Field Repeat Evaluation Model

NR uses a **reactive fixed-point** algorithm for cost evaluation, fundamentally
different from BattleScribe's single-pass approach. This produces different results
for self-referencing and mutually-referencing cost-field repeat modifiers.

| Behavior | BattleScribe | NewRecruit |
|----------|-------------|------------|
| Algorithm | Single-pass, insertion-order, live values | Reactive fixed-point iteration |
| Self-reference | Asymmetric (order-dependent) | Symmetric (all converge to same value) |
| Mutual reference | Escalates across mutations | Diverges to infinity in single op |
| Non-self-reference | Correct | Correct (identical to BS) |

For non-circular modifiers (the vast majority of real-world data), both engines
agree. Differences only manifest with self- or mutually-referencing cost-field
repeats. See [Cost-Field Repeat Algorithm](cost-field-repeat-algorithm.md) for
the full technical breakdown.

Affected specs use per-engine `expectedState` overrides or `newrecruit: skip`:
- `modifier-repeat-cost-self-reference` — per-engine overrides (NR converges higher)
- `modifier-repeat-cost-mutual-reference` — NR skipped (diverges)

---

## Discoveries

Technical findings from reverse-engineering NR's internal API and comparing
with BattleScribe's decompiled Java engine.

### Publication and Page Field Resolution

BattleScribe stores `publicationId` and `page` on virtually every catalogue node
(except CostType and ProfileType). These fields are resolved during roster creation:

**Selection-level**: The selection inherits `publicationId` from its source entry
(`BaseSelectable.setPublicationId(baseEntry.getPublicationId())`). Both `page` and
`publicationId` are raw IDs/strings, not resolved names.

**Profile/Rule-level**: Profiles and rules inherit `publicationId` from their
definition via `BaseBookData.getPublicationId()`. The page field is also preserved.

**InfoLink behavior**: InfoLink `publicationId` and `page` do **NOT** override the
linked target's values. When an InfoLink references a shared rule with
`publicationId: pub-core` and the InfoLink itself has `publicationId: pub-faq`,
the resulting rule on the selection has `publicationId: pub-core` (the target's
value, not the link's).

**NR behavior** (updated April 2026 — major discovery):
- NR resolves `publicationId` XML attributes into `.publication` object references
  at catalogue parse time. The raw `.publicationId` property is always `undefined`.
- **Selections**: Page and publication live on `sel.source` (not `sel` directly).
  Access via `sel.source?.publication?.id` and `String(sel.source?.page)`.
- **Profiles**: `profile.publication?.id` and `String(profile.page)` work directly.
- **Rules**: `rule.publication?.id` and `String(rule.page)` work directly.
- **Forces**: `f.source?.publication?.id` and `String(f.source?.page)`.
- **Categories**: `cat.publication?.id` works directly.
- **InfoLinks**: NR uses the infoLink's own publication, NOT the target's (differs
  from BattleScribe). This is the remaining behavioral difference.
- **Page type**: NR stores page as a number (BattleScribe XML stores it as a
  string). Must stringify: `obj.page != null ? String(obj.page) : null`.

### Publication Scope Resolution

NR resolves `publicationId` references **within the same scope** at parse time.
A forceEntry in the gameSystem referencing a publication defined only in a
catalogue will NOT resolve — the `.publication` object will be `undefined`.
BattleScribe resolves cross-scope publication references.

**Rule**: Define publications in the same file (gameSystem or catalogue) as the
entries that reference them. A forceEntry in a gameSystem must reference a
publication also defined in that gameSystem.

### NR `setAmount()` — Signature Gotcha

> **Previously documented as corruption bugs**: The issues below were discovered
> using `setAmount(n)` with one arg, which silently corrupts state (`ctx=n,
> n=undefined`). With the correct two-arg form `setAmount({}, n)`, NR's UI
> uses this on all entry types without issues. The "corruption" was caused by
> the wrong calling convention, not by setAmount itself.

**Two args required**: `setAmount(ctx, n)` where `ctx` = checker context (pass `{}`).
`setAmount(5)` with one arg sets `ctx=5, n=undefined` → silent corruption.

**Protocol validation**: `setSelectionCount` now rejects root selections
(target must be a child selection). Root selection lifecycle is managed via
`selectEntry`/`deselectSelection` only.

### NR Hidden Cost Types

NR's `army.calcTotalCosts()` method omits hidden cost types from its results.
The adapter uses **uniform manual summation** for all cost types (hidden and
visible alike), walking the selection tree and multiplying `getCosts()` by
`getAmount()` per selection. This is simpler and produces correct totals for
all types regardless of visibility.

NR's `createRoster(costs)` sets cost limits to 0 (from `costs[].value`, which
is the starting total, not the limit). The adapter explicitly applies
`defaultCostLimit` via `setMaxCosts()` after roster creation so that NR's
native `checkConstraints()` correctly validates limits for both visible and
hidden cost types.

### NR Selection Ordering

NR sorts selections **alphabetically by name** within each category. BattleScribe
uses **insertion order** — selections appear in the order the user added them.

The adapter tracks insertion order by tagging new selections with a monotonically
increasing `__bsspec_seq` sequence number on the raw Vue object. The state reader
sorts by this tag, with auto-selected entries (untagged) sorting first in
catalogue definition order.

Child selections always sort by **catalogue definition order** (entryOrder) since
they're part of the entry definition, not user-ordered.

### NR Selection Model: bumping the amount vs. `addInstance()`

NR pre-creates **selector nodes** with `amount=0` for all child entries when a
parent is selected. These are placeholder objects representing available entries.

- **`addInstance()`** on a selector template creates a NEW node with `amount=0`
  (broken — produces duplicates, costs not aggregated)
- **bumping the amount** on an existing child node takes it from 0 to 1
  (correct — costs properly included in `calcTotalCosts()`)

This discovery resolved the **child cost aggregation** issue (8 specs fixed).

> **v35.72:** the bump used to be `incrementAmount()`. NR deleted that method
> (and `decrementAmount()`) in v35.72 and moved the "+1" into its Vue widgets,
> which express it over the surviving primitive:
> `node.setAmount({}, node.getAmount() + (node.getStep() ?? 1))`. The adapter
> now does the same. One behavioural consequence: `incrementAmount` clamped
> **up** to satisfy unmet `min` constraints and `setAmount` does not, so an
> entry with `min >= 2` on the bump target lands on `amount + step` plus a
> validation error rather than jumping to the minimum.

### Deselect reduces the amount rather than deleting

Deselecting reduces the selection's amount by one step — the inverse of the
bump above — which matches BattleScribe's deselect semantics (decrement number
by 1, or remove the node entirely when it reaches 0).

Previously the adapter used `sel.delete()` which always removes the selection
completely, regardless of its current amount. For collective entries with
scaled counts (e.g., Weapon×6 from `setSelectionCount(2)` on a parent with
number=3), `delete()` would remove the entry entirely instead of reducing to
Weapon×3.

Since v35.72 this is `sel.setAmount({}, Math.max(0, sel.getAmount() - step))`,
followed by `delete()` when the amount reaches 0 so NR clears the associated
validation errors. Note the `typeof` guard around this call matters: when the
method it probes for disappears, the fallback silently deletes the whole
instance instead of decrementing — wrong results rather than an error.

### BattleScribe Auto-Select Mechanism

Decompiled from `engine.a.f` (BattleScribe Java engine):

- Private method `x()` ("Select default root entries") at line 978
- Called during `setRoster(bl=true)` when creating a new roster
- Iterates all forces, auto-selects entries where `getDefaultAmount >= 1`
- `getDefaultAmount` returns the entry's `min` constraint value

the BattleScribe engine adapter replicates this via reflection: `_autoSelectMethod.Invoke()`.

### NR Error Extraction

NR validation errors are extracted by calling `checkConstraints()` on each
roster node, then reading the node's error arrays. Key findings:

- `checkConstraints()` must be called explicitly per node
- Can crash with undefined reference errors — wrapped in try-catch
- Errors on army node are cost limit violations
- Error structure: `{message, entryId, constraintId, raisedOnType, raisedOnId, raisedOnEntryId}`;
  `entryId` is reconstructed by a candidate-constraint back-search (see
  [adapter-reconstruction-audit.md](adapter-reconstruction-audit.md))
- **The raising node is reported, and it is `uid`.** `raisedOnType`/`raisedOnId` name the runtime
  node the error was raised on, read off the node the walk was visiting — the same value the state
  model reports as `ForceState.Id`/`SelectionState.Id`/`CategoryState.Id`. The roster included:
  `army.uid` is real, while `army.getId()` returns the literal `"(roster)"` (#422)
- **`error.hash` is not a second name for the raising node.** It is `<uid>::<constraintId>`, but
  that uid is the node the constraint COUNTS OVER — the one the message names — equal to the raising
  node only when the constraint's scope is `self`. Measured over the roster corpus 2026-08-13: the
  prefix named a different node on 72 of 142 errors. The reference that IS the raising node is
  `error.parent`, a handle carrying a uid and nothing else; it agreed with the walked node on all
  142 and disagreed on none
- ConstraintId format: NR now maps cost limit errors to the `costLimits/`
  pseudo-entry convention (matching BattleScribe's format)
- Max constraint errors go on the selection (both BS BattleScribe adapter and NR now agree)

### Catalogue Expansion and Entry Links

BattleScribe resolves entry links during force creation:

1. Entry link references a shared selection entry
2. Engine copies the shared entry and merges the link's properties
3. Expanded copy gets composite ID: `linkId::sharedEntryId`
4. Registered as a regular (non-shared) selection entry
5. Both the shared entry's constraints and the link's constraints are evaluated

Key findings:
- `scope=parent` on entry link constraints refers to the catalogue root, not
  the force — use `scope=force` or `scope=roster` instead
- `shared=true` counting works across multiple entry links to the same shared
  target — the engine counts by `sharedEntryId`

### NR Pinia Store Access

Access NR's internal stores via:
```javascript
document.querySelector('#__nuxt')?.__vue_app__
  ?.config?.globalProperties?.$pinia._s.get('storeName')
```

Key stores: `lists`, `listsPage`, `systemsStore`, `gameStore`.

Roster access: `lists.getCurrentList()` returns `{row, army, book}`.

### NR Condition Engine Internals

Analysis of NR's minified JS bundles (`rfaH3HIo.js`) reveals the condition
evaluation chain for missing `childId`:

**Evaluation chain**: `Ty()` → `pR()` → `sj()` → `state.eval()`

```javascript
// state.eval — key method on roster node state
eval(e, t) {
    // ...
    // When node isGroup() and childId missing → defaults to "any"
    this.isGroup() && !e.childId
        ? n = this.hash({field: e.field, childId: "any"})
        : n = this.hash(e);
    return this.do_get(n) || 0;
}

// hash — builds lookup key, defaults childId
hash(e) {
    return `${prefix}::${field}::${e.childId || (this.isForce() ? "any" : "self")}`;
}
```

**Default childId by node type**:
| Node type | Missing childId defaults to | Effect |
|-----------|----------------------------|--------|
| Group (`isGroup()`) | `"any"` (in eval) | Counts all children |
| Force (`isForce()`) | `"any"` (in hash) | Counts all selections |
| Other (selection) | `"self"` (in hash) | Counts self |

**Comparison operator** (`Zl` function):
```javascript
case "atLeast": return scope === "self" && count === 0 ? false : count >= value;
```

NR has a special case: `atLeast` with `scope=self` and `count=0` returns `false`
regardless of value.

**BattleScribe comparison**: BattleScribe's `BaseFilteredQuery` (decompiled Java)
resolves `childId` via `h.d(string)` → returns `null` for empty/missing →
query returns `Double.NaN` → any comparison with NaN returns `false`. This means
BattleScribe silently ignores conditions with missing childId (always false).

### NR Data Loading

Three methods for loading game data:
1. **`sysStore.loadSystemFromFs(files)`** — accepts `[{name, path, data}]`
   array with XML strings (used for spec tests)
2. **`addGithubSystem()`** — downloads from BSData GitHub repos
3. **Mock `showDirectoryPicker()`** — intercepts folder upload UI

### `setAmount()` vs `addInstance()` Deep Dive

**Discovered April 2026 via live Playwright UI replay and method tracing.**

These are two fundamentally different operations in NR's selection tree:

| | `node.setAmount(ctx, n)` | `selector.addInstance()` |
|---|---|---|
| **What it does** | Changes `amount` property on an **existing** node | Creates a **new sibling node** (amount=0) |
| **Tree effect** | No structural change (property mutation) | Structural change (new node) |
| **Cost recalculation** | Full scope propagation via queue (correct) | New node doesn't trigger parent cost update (stale) |
| **Used by NR UI** | ✅ Spinbutton count changes | ✅ "Duplicate Unit", "Create Unit (+)" |

#### `setAmount(ctx, n)` — Signature Gotcha

**Two args required**: `ctx` = checker context object, `n` = new amount value.

```javascript
// ✅ Correct — NR UI passes {} as context
node.setAmount({}, 5);

// ❌ WRONG — sets ctx=5, n=undefined → amount becomes undefined (silent corruption)
node.setAmount(5);
```

---

## 6. Naming/Spelling

### Default category name: "Uncategorised" vs "Uncategorized"

BattleScribe uses the British spelling **"Uncategorised"** for the default/fallback
category. NewRecruit uses the American spelling **"Uncategorized"**.

This affects any assertion that references the default category by name. Use NR engine
overrides in specs to provide the American spelling when asserting on force categories.

### Force category `entryId`: Available via `source.targetId`

NR force categories (from `force.getCategories()`) DO expose the target category entry
ID, but via a different property than expected:

| Property | Value | What it is |
|----------|-------|-----------|
| `c.source?.id` | `"cl-hq"` | The **categoryLink** ID (link, not target) |
| `c.source?.targetId` | `"cat-hq"` | The **category entry** ID ✅ |
| `c.source?.entryId` | `undefined` | Not available |
| `c.entryId` | `undefined` | Not available on instance |

The adapter reads `entryId` from `source.targetId` (falling back to `source.id`).

### Selection `entryGroupId`: Not available

NR does **not** expose `entryGroupId` on selections. Neither the instance nor its
`source` object has this property. Selections that are children of a
`selectionEntryGroup` in BattleScribe will have `entryGroupId: null` in NR.

Confirmed via live Playwright probing: `source.entryGroupId` is `undefined` on child
entries (Bold, Cunning) within a selection entry group (Traits), both before and after
selection via `addInstance()`.

---

## 7. Roster Load (`.ros` Import)

**6 specs** — NewRecruit reaches roster load through `listsStore.importBs(File)`, the action
behind My Lists' "Import BattleScribe file" button. It is a different shape of loader from
BattleScribe's, and the differences are visible from the first payload.

### It refuses files BattleScribe accepts — two of the three it first appeared to

| Payload | BattleScribe | NewRecruit | Spec |
|---|---|---|---|
| No `<forces>` at all | loads the empty roster | **refuses** — "This file is not a roster" | `roundtrip-load-forceless-roster` |
| `gameSystemId` that is not loaded | refuses (see note) | **refuses** — it resolves the id before anything else | `roundtrip-load-unknown-game-system` |
| Selection with no `<categories>` | restores the selection | **drops it, silently** — see below | `roundtrip-load-selection-no-primary-category` |

The forceless case is worth reading twice: NR's guard for it (`"Roster contains no forces!"`)
is unreachable, because its XML-to-object step drops empty containers. `<forces/>` arrives as
*no* `forces` key, which trips the earlier "is this a roster?" check instead.

### It reads `number` differently — one difference confirmed, one still open

| Payload | BattleScribe | NewRecruit | Spec |
|---|---|---|---|
| `number > 1` on a FORCE-LEVEL selection | restores it (130 pts) | **clamps every root to 1** (70 pts), silently | `roundtrip-load-root-selection-number` |
| `number > 1` on a selection that has CHILDREN | one node at number 3 | store-direct: **three nodes at number 1**; through the UI: one node at number 3 | `roundtrip-load-number-on-parent-selection` |

The first is NewRecruit's, and both NR lanes agree on it, which places it inside `importBs`
rather than in either observation point. The roster still validates and still looks like a
roster; it just costs 60 points less than the file said.

The second is **not yet attributed**, and the table above is deliberately worded as two lanes
rather than as one engine. `newrecruit` reads `importBs`'s in-memory return value; `newrecruit-ui`
reads the roster NR re-hydrated from the saved list — different objects, so the disagreement could
be our reconstruction, NR's importer and rehydrate disagreeing with each other, or the two state
readers. Note the identical signature (N nodes at amount 1 versus one node at amount N) in
"setSelectionCount on Child Entries — Fixed" above, which turned out to be an `addInstance()` loop
in our adapter — a lead, not a verdict. Both quantities survive a load intact when the quantified
selection is childless, which is what `roundtrip-load-selection-numbers` pins.

**The unknown-game-system row is a correction, not a divergence.** NewRecruit refusing that file was
first recorded against a BattleScribe that accepted it — which turned out to be the in-process
adapter, not the app. The desktop app's own loader answers "you do not have the right data files to
be able to edit this roster", the `battlescribe-ui` lane refuses accordingly, and the adapter now
makes the same check. All four engines agree; the row stays because the measurement is what found
the adapter gap.

### A selection with no primary category is dropped without a word

`BH()`, NR's per-force restore, requires each top-level selection to carry a
`<category primary="true">` and `console.warn`s past any selection that does not:

```js
const u = l.categories?.find(b => b.primary);
if (!u) { console.warn("found Unit without Primary Category", l.name, l.entryId); continue; }
```

The load reports success, the force arrives, and the unit that was in the file is not in the
roster. Nothing in the schema requires the element — BattleScribe re-derives categorisation
from the catalogue and writes no `<categories>` on a selection that has none — so a
BattleScribe-exported roster of uncategorised units imports into NewRecruit as empty forces.

Every real exporter writes the primary category, NR's and BattleScribe's both, which is why
no user has hit this; it is also why spec payloads carry the element rather than omitting it.

### Refusals are return values, not throws

`importBs` answers a **string** for every case it declines, and `{row, army, book}` when it
succeeds. An adapter that treats the string as success reports every refusal as a silent pass —
so both NR adapters convert it into a throw, and the message a spec matches with
`expectFailure.messageContains` is NR's own sentence.

### Loading resolves against the library, not against `Setup`

`IRosterEngine.LoadRoster` is specified as re-linking against the game system and catalogues
from `Setup`. NR does something subtly different: it reads `gameSystemId` out of the payload,
resolves it through `systemStore.getSystem`, and **selects** that system before building the
roster. With one system loaded the two are indistinguishable; with a dangling id they are not —
and measuring that is what surfaced the adapter gap above, because the two BattleScribe lanes
disagreed with each other about the same file.

### Import adds a list, it does not replace one

`importBs` ends in `addList(list, false)` — a new row, and no selection. Both NR adapters
therefore re-point `window.__bsspec` at the imported list and delete the row it replaced: a
roster is a singleton that is replaced, and a load that only ever appends leaves one dead row
per load for NR's own `findListByKey` to trip over later.

---

## Architecture Notes

### How NR Is Tested

The NR adapter uses **Playwright** to drive a headless Chromium browser loading
`newrecruit.eu`. Instead of UI interaction, it directly calls NR's internal
**Pinia store API** via JavaScript evaluation:

- **Data loading**: `loadSystemFromFs(files)` — injects BattleScribe XML
  (either synthetic from specs or real from DataSource repos like wh40k-10e)
- **Actions**: Direct Pinia store method calls (`insertForce`, `addInstance`,
  `delete`, `setAmount`, `getAmount`, `getStep`)
- **State reading**: `getCurrentList().army` tree traversal using NR's reactive
  object API (`getForces`, `getSelections`, `getName`, `getCosts`, etc.)
- **Validation**: Error extraction via `checkConstraints()` per node

### Test Infrastructure

- **Browser lifecycle**: `NewRecruitFixture` (xUnit collection fixture) shares
  one Playwright browser across all NR tests, which run serially
- **Live testing**: NR tests only run when `NR_ENGINE_URL` environment variable
  is set (on-demand via `workflow_dispatch` or `[nr-test]` commit message)
- **Frozen testing**: `FrozenNewRecruitFixture` loads HAR recordings from
  [WarHub/newrecruit-har](https://github.com/WarHub/newrecruit-har) for fully
  offline, deterministic replay via Playwright's `RouteFromHARAsync`
- **Expected failures**: Encoded directly in spec YAML files via the `engines`
  field (map of engine name → expectation: `pass`, `fail`, or `skip`).
  If a spec is expected to fail and does fail, the test passes. If an expected
  failure suddenly passes, the test FAILS (detecting behavior changes).
  Most specs now use **per-engine `expectedState` overrides** instead of
  `engines: {engineName: fail}` — the override describes the actual engine
  behavior, keeping both engines passing. Only 1 real-world spec still uses
  `newrecruit: fail` due to fundamental data incompatibilities.
- **BattleScribe**: All specs expected to pass except 2 NR-specific
  null-childId condition behavior specs. DataSource specs (real-world wh40k-10e)
  are fully supported via IKVM engine with DataUtils XML loading.

### Resolved Issues

| Issue | Fix | Specs Fixed |
|-------|-----|-------------|
| Error placement mismatch | ~~BS adapter remaps category-level max/cost/hidden errors to selection-level (matching NR)~~ — **reverted by #426**: that remap was BattleScribe reconstructed into NewRecruit's answer, and the divergence is recorded per engine instead (see "Which node an error is raised on" above) | 4 |
| Selection ordering | Insertion-order tracking via `__bsspec_seq` tags | ~15 |
| Action/state index mismatch | `getSortedSelections()` helper for all action methods | 3 |
| Child cost aggregation | `incrementAmount()` instead of `addInstance()` | 8 |
| Error extraction | Tree-walking `checkConstraints()` with structured error parsing | ~10 |
| Entry link resolution | BattleScribe queries `_engine.e(force).R()` for expanded entries | 4 |
| Auto-select replication | BattleScribe adapter calls `x()` via reflection | ~15 |
| GameSystem entry resolution | `SelectEntry` now includes GS-level SelectionEntries and EntryLinks | 2 |
| SelectChildEntry flattening | `FlattenChildEntries` resolves EntryLinks and nested SelectionEntryGroups | 6 |
| FindEntryById scope | `FindEntryById` now searches GameSystem entries in addition to catalogue | 2 |
| Force-catalogue map state leak | `_forceCatalogueMap.Clear()` in Setup prevents cross-test contamination | 1 |
| NR cost limit false positives | Structural read: `e.constraint.type === 'max'` + `.field` names the cost type — no message parsing | ~65 |
| NR generic hidden errors | Suppress "cannot be selected while hidden" without `constraint.id` | 4 |
| Publication field extraction | Use `.publication?.id` object pattern instead of `.publicationId` string | 7 |
| Selection page/pub on source | Read from `sel.source?.page` / `sel.source?.publication` instead of `sel` directly | 4 |
| Force page/pub on source | Read from `f.source?.page` / `f.source?.publication` | 1 |
| Hidden cost types omitted | Always use manual summation instead of `calcTotalCosts()` | 1 |
| setAmount corrupts NR state | Remove `setSelectionCount` on entries with children/min constraints | 1 |
| Publication scope in forceEntry | Move publication to gameSystem (same scope as forceEntry) | 1 |
