# Cost Number Formatting & Precision

How each engine represents, computes, and serializes **non-integer costs**, and
how this spec suite treats those values. This is the research foundation (T5a,
issue #285) for the non-integer cost conformance specs (#277) and the
`double`/`decimal` divergence it surfaces (#286). Companion to
[cost-field-repeat-algorithm.md](cost-field-repeat-algorithm.md) (how repeats are
evaluated) and the costs feature (#171).

## The reference contract: pure decimal arithmetic

The spec suite is a **contract**, not a transcript of any one engine. For
non-integer costs the contract is **exact decimal arithmetic**: `0.1 × 3 = 0.3`,
`0.5 + 0.5 = 1`, `1.33 × 2 = 2.66` — the values a hypothetical
arbitrary-precision reference engine would produce, formatted without spurious
trailing zeros (`1`, not `1.0`).

There is **no such reference engine implemented yet**. We nonetheless author the
**base `expectedState` of every cost spec to the exact decimal result**. Real
engines that compute in floating-point and drift from that result are documented
with narrow per-engine overrides (see [Authoring rule](#authoring-rule-base-vs-override)),
so the divergence is *visible and attributed* rather than baked into the contract.

## In-memory representation per engine

Everything on the C#/spec side of this repo holds costs as **`decimal`**:

| Type | File | Value type |
|---|---|---|
| `CostState` (spec model) | `src/BattleScribeSpec.TestKit/Roster/RosterTypes.cs` | `decimal` |
| `ProtocolCostValue` / `ProtocolCostType.DefaultCostLimit` | `src/BattleScribeSpec.TestKit/Protocol/ProtocolMessages.cs` | `decimal` / `decimal?` |
| `NrCostSnapshot.Value` | `src/BattleScribeSpec.NewRecruit/NewRecruitStateReader.cs` | `decimal` |
| `CostBaseCore.Value`, `CostTypeCore.DefaultCostLimit` (wham) | `.deps/wham/src/WarHub.ArmouryModel.Source/CostBaseCore.cs`, `CostTypeCore.cs` | `decimal`, `[XmlAttribute]` |

The **only** floating-point representation is *inside the engines themselves*:

- **BattleScribe** — the Java engine (`net.battlescribe.model.data.Cost`) stores
  cost values as Java **`double`** (`getValue()` / `setValue(double)`). All BS cost
  arithmetic (multiplication by count, repeat multipliers, modifier application)
  happens in `double`.
- **NewRecruit** — the NR data model uses `decimal`/JS numbers, but the roster
  **runtime multiplies `cost × count` in JavaScript floating-point** (see below),
  so NR is *also* subject to `double` drift at roster-build time.

## Conversion sites (the lossy hops)

**BattleScribe round-trip: `decimal → double → (engine math) → decimal`**

1. Build: `decimal → double` when constructing Java model objects —
   `JavaModelFactory.CreateCost(..., decimal value)` does `c.setValue((double)value)`
   (`JavaModelFactory.cs`), likewise `CreateCostType(... defaultCostLimit ...)` and
   `BattleScribeEngine.SetCostLimit(CostType, decimal)` → `_engine.a(costType, (double)value)`.
2. Compute: the Java engine performs all cost math in `double`.
3. Read back: `double → decimal` — `new CostState(..., (decimal)c.getValue(), ...)`
   in `BattleScribeRosterEngine.cs` and `ModelConverter.cs`.

The precision loss lives in the `double` hop and the engine's `double` math; the
`(decimal)` cast on read-back faithfully carries whatever `double` the engine
produced (including artifacts like `0.30000000000000004`).

**NewRecruit: JS floating-point multiplication**

The injected page script multiplies cost by selection count in JS numbers
(IEEE-754 double):

- Per-selection cost: `value: (c.value || 0) * count` (`JsHelpers.cs`).
- Total-cost summation: `result[tid].value += (c.value || 0) * count` (`JsHelpers.cs`).

The resulting JSON number is deserialized straight into a C# `decimal`
(`NrCostSnapshot.Value`, `NewRecruitStateReader.cs`) with no rounding. So NR's
*stored* fractional cost is exact `decimal`, but its *computed* roster totals can
drift the same way BS's do.

## XML serialization is exact

Cost XML is produced by wham's code-generated serializer, not hand-written
`ToString`. A cost `value` attribute is written with
`System.Xml.XmlConvert.ToString(decimal)`
(`.deps/wham/src/WarHub.ArmouryModel.Source.CodeGeneration/WhamSerializerGenerator.Writes.cs`)
and read back with `XmlConvert.ToDecimal(...)`. This is **culture-invariant**
(always `.` as the decimal separator) and **scale-preserving** — it emits exactly
the digits the `decimal` carries. Consequences:

- `0.5` → `value="0.5"`, `1.33` → `value="1.33"`.
- Trailing zeros reflect the `decimal`'s scale: `1.0m` → `value="1.0"`, `1m` → `value="1"`.
- There is **no** custom cost format string, `"0.##"`, or explicit `CultureInfo`
  around cost values on the app side.

So any serialized divergence originates in the engines' arithmetic (which `double`
lands in the `decimal`), **not** in the XML formatting layer.

## NewRecruit's `.ros` serializer applies **no** rounding

The section above is the wham/C# path (the spec side, and BattleScribe's export via
DataUtils). NewRecruit exports its `.ros` from its **own JavaScript** serializer, which
does **not** round at all — it writes the raw `double`. Traced from the roster app
bundle (`exportRos → U4e → Sb → fX`):

- **`fX(roster)`** emits roster costs as `R_(Object.values(roster.getTotalCosts()))` and,
  per force, `R_(force.getCosts())` (where `getCosts()` ≡ `Object.values(getTotalCosts())`).
- **`R_(costs)`** writes each cost's `value: n.value` **verbatim** (only dropping
  zero-value costs).
- **`ex(selections)`** — the per-selection writer — computes the count-multiplied value
  *at serialization time*: `s.forEach(a => a.value = a.value * i.number)` where
  `i.number = getSelectionCount("root")`. This is a **raw IEEE double multiply**.
- The number reaches the attribute through `wA(n) = n.toString()` + XML-escaping — no
  `toFixed`, no `Math.round`, no `Pn`.

So NewRecruit's exported selection cost is `perModelValue × getSelectionCount("root")` in
raw `double`. **Verified empirically:** a `0.1`-per-model model at count 3 exports as
`<cost … value="0.30000000000000004"/>` with `number="3"`. NewRecruit's 2-decimal display
rounding (`Pn(r) = Math.round(r*100)/100`) is a **UI-only** concern and is *not* applied to
the `.ros`. (Our injected state reader in `JsHelpers.cs` does the same raw
`cost × count` — so `expectedState` cost reads and NewRecruit's `.ros` export agree.)

## Comparison semantics (no tolerance)

- **Roster** cost values compare with **exact `decimal` inequality** —
  `RosterRunner.AssertEqual` (`if (em != am)`), no epsilon/rounding. So a
  representational difference of `0.30000000000000004` vs `0.3` is a *failure*,
  not absorbed.
- **Gamedata** cost values compare as **strings**. BS formats via
  `FormatNum(double)` (whole → `long.ToString`, else `double.ToString(Invariant)`;
  `BattleScribeGameDataEngine.cs`); NR via a JS `formatNum` (integer → `String(n)`,
  else `String(v)`; `NrEditorStore.cs`). Single fractional values round-trip
  identically; multiplication/repeat artifacts diverge in string form.

Because the comparison is exact, an **exact-decimal base + narrow per-engine
overrides** is the right modeling: engines that match the contract need no
override; engines that drift get a documented one.

## Where divergence does / doesn't appear (observed)

Confirmed by running the specs below against BattleScribe (Java, in-process) and
NewRecruit (frozen HAR replay):

- **No multiplication (a plain fractional literal):** setting a cost to `0.5`,
  `0.1`, or `1.33` and reading it back round-trips **cleanly on both engines**,
  in both gamedata (`cost-fractional-value`, `cost-fractional-modifier`) and roster.
  No override needed.
- **XML serialization is byte-identical:** the gamedata export snapshots
  (`cost-fractional-export`) show both engines write `value="0.5"`, `value="0.1"`,
  `value="1.33"` verbatim. The base `.cat` is the BattleScribe form; NewRecruit's
  only override reason is the structural `type="catalogue"` attribute — **not** cost
  formatting. The roster export snapshot (`roster-fractional-cost-export`) likewise
  locks each engine's `.ros` cost formatting: both write `value="0.5"` identically,
  and NewRecruit's structural differences (`generatedBy`, `from="entry"`, attribute
  ordering) land in its `.newrecruit.ros` override.
- **Multiplication / accumulation with binary-inexact fractions is where
  NewRecruit drifts:**
  - `0.1 × 3` per-model → NR `0.30000000000000004`, BS `0.3` (`cost-fractional-per-model`).
  - `0.1 + 0.1 + 0.1 + 0.5` roster total → NR `0.7999999999999999`, BS `0.8` (`cost-fractional-aggregation`).
  - `0.1 + 0.2` modifier increment → NR `0.30000000000000004`, BS `0.3` (`modifier-fractional-cost`).
  - repeat `0.1 × 3` → NR `0.30000000000000004`, BS `0.3` (`modifier-repeat-fractional-cost`).
  - Over-limit enforcement is **identical** on both — the fractional limit fires
    the same structured error; only NR's running total drifts (`cost-fractional-over-limit`).
- **Why BS stays clean but NR drifts:** BS's `(decimal)c.getValue()` read-back
  rounds the `double` artifact to ~15 significant digits (`0.30000000000000004` →
  `0.3`). NR's roster total/per-model value is a JS `double` deserialized by
  System.Text.Json into `decimal` at **full precision**, so the artifact survives.
- **Binary-exact fractions** (`0.5`, `0.25`, `0.125`) survive multiplication
  cleanly even in `double`, so they match on both — the "clean" half of each spec.

> Net: on the **roster** side, **NewRecruit** is the floating-point-diverging
> engine (not BattleScribe, contrary to #286's initial framing); on the
> **gamedata** side, both engines round-trip and serialize non-integer costs
> identically.

## Locale note (fixed)

`BattleScribeGameDataEngine` originally parsed spec cost strings with the current
culture (`double.TryParse(value, ...)`), so on a machine whose locale uses `,` as
the decimal separator, `"0.5"` silently parsed to `0`. Since the spec/protocol is
always invariant-format, the parse sites now use
`NumberStyles.Float` + `CultureInfo.InvariantCulture`.

## Authoring rule (base vs override)

1. Write the base `expectedState` to the **exact decimal result** (the contract).
2. Run the spec against each engine.
3. If an engine's observed output **equals** the base, add **no** override.
4. If it **drifts** (a floating-point artifact), add a minimal engine override —
   `engines: { battlescribe: … }` / `{ newrecruit: … }` for roster expectations,
   or a per-engine `.cat` byte snapshot for gamedata export — capturing that
   engine's real value, with a comment marking it a floating-point deviation.

**Undefined-default variant.** For a case where there is genuinely **no
agreed-correct value** — e.g. a binary-inexact `cost × count` product, where neither
engine's raw `double` is authoritative and the suite has not decided which is "correct"
— author the divergent field with **no base default at all** and pin *every* engine's
raw output as an override. An engine with no override then asserts nothing for that
field (the value is left undefined) rather than being held to a contract we cannot yet
justify. See `specs/roster/cost/cost-fractional-double-divergence.yaml`, which records
BattleScribe's `0.3` and NewRecruit's raw `0.30000000000000004` as overrides with no
base value, alongside the binary-**exact** `0.125 × 3 = 0.375` that both agree on.

See [engine-filtered-expected-state.md](engine-filtered-expected-state.md) for the
override merge semantics, and [error-assertions.md](error-assertions.md) for
`on`/`from`/`messageContains` when a fractional over-limit error's message differs.

## Summary

- The contract is exact decimal arithmetic; base assertions encode it.
- **BattleScribe** computes costs in Java `double`; the C# wrapper faithfully
  carries the resulting `double` into `decimal`.
- **NewRecruit** stores costs as `decimal` but multiplies `cost × count` in JS
  `double` at roster runtime, so it can drift too.
- **XML serialization** (`XmlConvert`, spec/BS side) is exact and scale-preserving —
  divergence is arithmetic, not formatting. **NewRecruit's own `.ros` JS serializer
  applies no rounding either**, writing the raw `cost × count` double (so its export and
  our state read agree).
- **Comparison** is exact (decimal / string) with no tolerance, so drift is
  surfaced and attributed via per-engine overrides rather than hidden.
- For a product with **no agreed-correct value**, a spec may omit the base default
  entirely and record each engine's raw output as an override (undefined default).
