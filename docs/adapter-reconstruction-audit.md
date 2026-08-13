# Adapter reconstruction audit

> The deliverable of #282, for #270 to burn down. "Reconstruction" here means an adapter
> recomputing something the engine already knows — an identity, a placement, a number, an order —
> instead of reading it out. Every such site is a place the adapter can disagree with the engine
> it is supposed to be measuring.
>
> Provenance: engine claims are read out of BattleScribe 2.03.21 bytecode
> (`net.battlescribe.engine`) and measured at runtime, in the sessions behind the error-identity
> stack (#402 → #404 → #415 → #416). `file:line` references are to this repository at the
> stack's head. NR claims are read out of the pinned snapshot bundle — see
> [nr-behavioral-differences.md](nr-behavioral-differences.md) for that method.

The audit had one dominant finding, and it changed the codebase before this document could list
it as debt: **the largest reconstruction surface — recovering error identity from rendered
message text — is gone.** The engine computes the exact identity of every constraint error,
discarded it at construction, and a two-lane bytecode patch (#416) now stores it instead. The
sections below record the mechanism, what the deletion retired, and what remains — with the
reason each remainder exists.

## The error funnel, and the id it discarded

Every constraint, hidden and collective validation error in the engine routes through one private
method — `net.battlescribe.engine.a.f.a(BaseRosterElement, String, String)`; the patch tool pins
it by owner and descriptor
(`src/bs-engine-patch/src/bsspec/enginepatch/ErrorIdPatcher.java:50`, `:158`). Its third argument
is an identity string the **caller** builds; the body reads the element and the message and never
reads the id. Computed, passed, discarded. Three call sites build it:

| call site | third argument |
|---|---|
| constraint errors | `ownerId::entryId::constraintId` |
| hidden-entry errors | literal pseudo-id, third segment `collective` |
| collective errors | the same literal — the id distinguishes neither from the other |

Measured, not inferred from the dedup set: the argument carries the id for **every** error,
shared entry or not. The `getValidationErrorIds()` collection that #283 measured empty is a
different thing — a dedup set that registers **shared entries only** and exists for an
early-return. Its anatomy, and the parse rule for reading it, are written down in
[`BattleScribeErrorIds.cs`](../src/BattleScribeSpec.TestKit/Roster/BattleScribeErrorIds.cs)
(#402). The funnel argument has the same shape and none of the same restriction.

Id anatomy, measured:

- **first segment** — the owner ELEMENT's runtime instance id, regenerated per recalculation.
  Not an entry id; this is the same id #283 measured, read off the error object, and correctly
  reverted as "well-formed and entirely wrong". As a *segment alongside the other two* it is
  harmless; alone it was worse than nothing.
- **second segment** — the constraint-declaring entry id. Composite for link-reached entries
  (`link1::…::actualEntryId`, per [entry-id-construction.md](entry-id-construction.md)), rejoined
  by the #402 parse rule.
- **third segment** — the constraint id, or the literal `collective`.

**The patch (#416).** The funnel is patched to store its otherwise-discarded id argument onto a
new field — `public String bsspecErrorId` on the error class `net.battlescribe.engine.b.a`
(`ErrorIdPatcher.java:48`, `:52`) — applied to identical bytes in both lanes by one shared
transform, `ErrorIdPatcher.transform(byte[])`:

- **in-process lane** — the `PatchBattleScribeEngineJar` MSBuild target patches the engine jar
  before IKVM compiles it (`src/BattleScribeSpec.BattleScribe/BattleScribeSpec.BattleScribe.csproj`,
  running `PatchJarMain`);
- **desktop lane** — `ErrorIdTransformer`, registered from the existing `BsUiAgent.premain`
  (`src/bs-ui-java-agent/src/bsspec/uiagent/BsUiAgent.java:22`), rewrites the same classes as the
  app JVM loads them.

`b.a` is `Serializable` with no explicit `serialVersionUID`, so adding a field would shift the
default-computed value; the patch pins the pre-transform value (`-8003478985922043719L`,
`ErrorIdPatcher.java:64`) to keep the addition serialization-neutral. Neither lane can run
silently unpatched: `tests/BattleScribe/EngineErrorFieldTests.cs` asserts the field and the UID
on the compiled assembly, the transformer throws rather than swallow a failed transform, and both
adapters fail fast on an error that carries no `bsspecErrorId`.

**Why messages could never answer identity**, so nothing text-shaped was worth keeping: the
rendered message quotes the POST-modifier effective limit via the engine's formatter, contains no
constraint id, and two same-kind, same-scope constraints with equal effective limits render
byte-identical strings — measured on `constraint-two-max-equal-limits`: identical messages,
distinct ids `con-max-alpha` / `con-max-beta`. The message is a *rendering* of the violation;
identity was never in it.

## Retired: the reconstruction the patch deleted

#416 deletes all message-matching identity code from `BattleScribeEngine.cs` and
`EngineAccessor.java`. At the moment of deletion the surface was 16 message `.Contains` in
`BattleScribeEngine.cs`, 14 in `EngineAccessor.java`, 2 in `BattleScribeErrorPlacement.cs` — the
same counts #283 recorded, which is what "bounded" looked like while it was load-bearing. After
it: two per BattleScribe lane, zero in placement (each survivor is one of the two no-id paths in
the next section). What died, defect by defect:

| retired site (as of #404's tree) | what it reconstructed | the defect it carried |
|---|---|---|
| `ResolveEntryFromMessage` (was `BattleScribeEngine.cs:558`) | constraint identity from the rendered limit | compared the AUTHORED value where the message quotes the post-modifier one, so a modified constraint never matched and fell through to the first kind-match in declaration order (#403, pinned by `constraint-two-max-one-modified`) |
| the percent branch of the same | percent-constraint identity | compared `(int)getValue()` — `50` — against a rendered `2.10`; could never have matched (pinned by `constraint-two-max-one-percent`, #415) |
| `_linkConstraintLookup` (was `BattleScribeEngine.cs:715`) | constraint values for link-reached entries | stored `cSpec.Value` raw off the spec YAML, which never sees a modifier at all — the same defect in a third shape |
| `ResolveForceEntryFromMessage` (was `BattleScribeEngine.cs:430`) | force-entry constraint identity | matched on constraint KIND alone — any same-kind sibling answered |
| `EngineAccessor.matchConstraintOwner` (was `EngineAccessor.java:1428`) | the same tiebreak, desktop lane | deliberate port of `ResolveEntryFromMessage`, defects included — byte-identical failure text was the measured evidence for treating the lanes as one |
| `BattleScribeErrorPlacement`'s `" forces from "` probe (was `:82`) | which errors are force-count errors | the engine also renders `" forces of "`, which the probe missed; placement now reads `ConstraintType`/`ConstraintField`, captured from the live constraint into `ValidationErrorState` (`src/BattleScribeSpec.TestKit/Roster/RosterTypes.cs`) |
| `linkTargetOf` + four orphaned helpers (`containsIgnoreCase`, `getEntryName`, `getCostTypeName`, `extractCostTypeIdFromMessage`), agent | the agent's own copy of owner reduction and its message-probing support | two implementations of one reduction is how the lanes drifted (#400); the survivor is below |

The declarations the defects forced are gone with them: `constraint-two-max-one-modified` lost
its `battlescribe: fail` outright, and `constraint-two-max-equal-limits` now asserts both errors
structurally on BattleScribe — what remains on it is NewRecruit's own behavior, one error for the
pair, kept as a per-engine override.

## Remaining: every site, with its reason

This is the list #270 tracks. Each row is a decision, not an oversight; a row with no reason
would be a bug in this document.

### BattleScribe — identity and placement

| site | what it still reconstructs | why it stays |
|---|---|---|
| roster cost-limit errors, resolved by cost-type name (`BattleScribeEngine.cs:450` `ResolveRosterCostLimit`; agent mirror `EngineAccessor.java:1510`) | which cost type a roster-level error is about | the engine's `a.f#v()` adds these errors directly to `Roster.getValidationErrors()` — they never pass the funnel, and #416 deliberately patches nothing else, so **no id exists anywhere** for them |
| `(hidden)` message probe (`BattleScribeEngine.cs:440`; agent mirror `EngineAccessor.java:1418`) | hidden vs collective | the funnel id's third segment is the literal `collective` for BOTH kinds — the pseudo-id distinguishes neither, the prose `(hidden)` suffix still does. Specs assert the two as reserved pseudo-constraints `hidden` / `collective`. See [hidden-validation-analysis.md](hidden-validation-analysis.md), [collective-flag.md](collective-flag.md) |
| `BattleScribeErrorPlacement.ApplyTo` (`BattleScribeErrorPlacement.cs:42`) | where an error BELONGS (roster/force/category → selection re-homing) | placement is semantics, not identity — the spec model wants errors on the selection the user acts on; the engine hangs them per element. Two callers (`BattleScribeEngine.cs:332`, `BsUiRosterEngine.cs:646`), which is what keeps the lanes from drifting on placement |
| owner reduction for link-composite ids — `BattleScribeErrorIds.ReduceToTargetEntry` (`BattleScribeErrorIds.cs:179`), `ApplyTo`'s first step (`BattleScribeErrorPlacement.cs:58`) | the owner ENTRY of an error whose owner id is a link-composite | this is #400's fix, and the point is that it exists ONCE: both lanes ship the raw composite `ownerEntryId` and the shared step reduces it, so the lanes agree by construction rather than by keeping two implementations in step |
| the agent's `declaringEntryOf` first-segment fallback (`EngineAccessor.java:1254`) | which segment of a composite id DECLARES the constraint, when the source-declarer map does not know the constraint | a live `EntryLink` does not surface its own constraints in the source walk, so a link-declared constraint is exactly the one the map cannot know — and its declaring container is the outermost link, the first segment. Correct for every corpus shape today (CI-green on both roster shards). **Caveat, recorded deliberately:** a constraint declared on a MIDDLE link of a multi-link chain would want a middle segment; no such spec exists today, and this row is where that spec's author starts |

The engine-side text-reading that remains is safe for the reason #283 recorded: the pinned
engine is EOL at `v2.03.21`, its strings cannot move, and the pin is what CI downloads.

### Engine divergence the identity work surfaced (not reconstruction — recorded so nobody "fixes" it)

Reading real ids exposed a genuine BattleScribe-vs-NewRecruit disagreement that message matching
had blurred: for a child-entry constraint violation inside a group, **BattleScribe owns the error
on the counting parent** (measured: `sse-unit`), **NewRecruit on the violating child**
(`se-gear`). The `from` — constraint and declaring entry — is not in dispute; only the owner is,
and it is pinned per engine by `constraint-error-owner-link-reached` (#415) rather than
normalised, because the corpus's owner convention is BattleScribe's and moving it would
contradict every group/child spec. Likewise `constraint-collective-same-number` (#415):
BattleScribe raises ONE collective bookkeeping error with no authored constraint behind it
(asserted as the reserved pseudo-constraint `collective`); NewRecruit raises nothing, declared as
an explicitly empty error set. This is
[nr-behavioral-differences.md](nr-behavioral-differences.md) material, cross-linked from its
validation-errors notes.

### NewRecruit — structural, but still reconstruction

| site | what it reconstructs | status |
|---|---|---|
| `entryId` back-search (`JsHelpers.cs`, `extractErrors`, `:424`–`:470`) | NR errors carry no `entryId`; the extractor scans four tiers of candidate `source.constraints[]` for the matching constraint id | structural, correct today, and the sole remaining *derivation* of an identity field on the NR side — #283 finding 1, landed here |
| roster-level non-`max` drop (`JsHelpers.cs:472`–`:483`) | roster-scope errors that are not a `max` cost limit end in an unconditional `return` — an `exactly` cost limit would vanish rather than fail | known hole; wants its own issue the day a spec needs one |
| cost-limit constraint id (`JsHelpers.cs:479`) | `costLimits`/`e.constraint.field` pseudo-entry convention, mirroring the BattleScribe shape | convention, kept deliberately so both engines report cost-limit errors in one shape |

### Cost value conversions — the `double ↔ decimal` boundary

The protocol and spec model carry `decimal`; the engine is `double` end to end. Every crossing is
a cast, and each is a rounding decision made by the adapter rather than the engine:

- **write side** (spec → engine): `JavaModelFactory.cs:169` (`setDefaultCostLimit`), `:982`,
  `:1003`, `:1063` (constraint/cost `setValue`), `:1120` (repeat value);
  `BattleScribeEngine.cs:302` (`SetCostLimit`).
- **read side** (engine → state): `BattleScribeRosterEngine.cs:262`, `:267`, `:666`;
  `ModelConverter.cs:33`, `:54`, `:71`, `:87`; `BattleScribeGameDataEngine.cs:205`;
  `BattleScribeTestFixture.cs:116`.

Formatting rules for what those values look like when rendered are in
[cost-number-formatting.md](cost-number-formatting.md); float-drift episodes are recorded in
[bs-ui-roster-coverage.md](bs-ui-roster-coverage.md) (class J).

### Ordering — adapters impose order instead of reading it

Cross-links #138, #227. Where an adapter sorts, the engine's own order is not what specs see:

- `BattleScribeRosterEngine.cs:257` (forces), `:602`–`:603` (selections, child forces), `:647`
  (children) — name sorts, `OrdinalIgnoreCase`.
- `BattleScribeEngine.cs:1248` — cost types re-ordered by a declared-order map.
- `BattleScribeGameDataEngine.cs:870` — cost types by id, ordinal.
- NR's own ordering is documented in [nr-ordering-analysis.md](nr-ordering-analysis.md)
  (`sortIndex` → group flag → `localeCompare`); the NR adapters read it rather than reconstruct
  it, with one normalization at `NrEditorStore.cs:525` (`.gst`-first file ordering).

## The #270 checklist

Read the groups, not the boxes. An earlier version of this list was one flat run of checkboxes,
and it read as half-finished work — an unchecked box for "the engine provides nothing to read
here" looks identical to an unchecked box for "nobody has done this yet". Only the last group is
work. #270 closed against this list.

### Retired — the reconstruction this stack deleted

- [x] Constraint/hidden/collective error identity, both BattleScribe lanes — **deleted** in
  #416; read from the funnel-patched `bsspecErrorId`.
- [x] Placement's force-count prose probe — **deleted** in #416; structural
  `ConstraintType`/`ConstraintField`.
- [x] Owner reduction duplicated per lane (#400) — **deleted** in #416; one implementation,
  `ReduceToTargetEntry`, applied in shared placement.

### Stays — permanent, because there is nothing to read

Not debt. Each of these reconstructs something the engine never exposes; "completing" them is
not a thing that can happen, and the reason is recorded in the table above.

- Roster cost-limit errors resolved by cost-type name — no id exists (`a.f#v()` bypass); stays
  until someone patches a second funnel, which the audit does not recommend.
- `(hidden)` vs collective prose probe — pseudo-id ambiguity is the engine's; stays.
- `ApplyTo` re-homing — semantics by design; stays, single implementation, two callers.

### Waiting on a trigger — no work owed until it fires

Correct as they stand. Each names the event that would make it work.

- Agent `declaringEntryOf` first-segment fallback — write the middle-link spec if a multi-link
  chain ever declares a constraint mid-chain; until then, stays with its caveat.
- NR roster-level non-`max` drop — file an issue when a spec first needs `exactly` at roster
  scope.
- NR `entryId` four-tier back-search — structural, and derives a field NR does not emit; the fix
  is upstream, the day NR ships one.

### Enumerated here, owned elsewhere

Listed because #282 asked for *every* reconstruction site, not because #270 owned fixing them.
Both areas are closed.

- `double ↔ decimal` casts (15 sites above) — out of #270's scope by its own terms; cost
  conformance is #277 / #286. Still a candidate for one conversion helper with a written
  rounding rule, if anyone wants it.
- Ordering normalization (6 sites above) — #138 and #227, both closed as completed. Still a
  candidate for per-engine ordering declared in one place.
