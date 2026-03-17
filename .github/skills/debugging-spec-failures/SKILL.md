---
name: debugging-spec-failures
description: >
  Debug BattleScribe spec test failures. Use when a spec fails against the Oracle or
  NewRecruit engine, when assertion errors are unclear, or when diagnosing why expected
  state doesn't match actual roster state. Covers SpecRunner error interpretation,
  assertion matching rules, and common failure patterns.
---

# Debugging Spec Failures

## Quick start

1. Run the failing spec:
   ```bash
   dotnet test tests/BattleScribeSpec.Tests.csproj --filter "DisplayName~{spec-id}"
   ```
2. Read the assertion error output — it tells you the step index, field path, and mismatch.
3. Check the matching rules below to understand *how* actual state was compared to expected.
4. Fix the spec or the engine adapter and re-run.

## Reading error messages

All assertion errors follow this format:

```
Step {stepIndex}: {fieldPath}: expected {expected} but got {actual}
```

**Field path examples:**

| Path | Meaning |
|------|---------|
| `forceCount` | Roster-level force count |
| `force[0].name` | First force's name |
| `force[0].selection[1].name` | Second selection in first force |
| `force[0].selection[0].cost[pts]` | Cost on first selection, matched by typeId or name |
| `force[0].profile[Marine].typeName` | Profile matched by name "Marine" |
| `force[0].rule[0].hidden` | Rule matched by index |

**Count mismatch errors:**

```
Step 0: force[0].selection[2] expected but only 2 selections
```

This means your expected state lists 3+ selections but the roster only has 2.

## Assertion matching rules

These rules determine how SpecRunner pairs expected items with actual roster items.

### Selections → matched by INDEX

Selections are matched **strictly by position** in the force's selection list.
Expected selection `[0]` compares to actual `[0]`, `[1]` to `[1]`, etc.

**This means order matters.** Auto-selected entries (from `min≥1` constraints) appear
first in the selection list. You must list them before manually-added selections.

### Profiles, rules, categories → matched by NAME (index fallback)

If the expected item has a `name` field set, it finds the actual item with that exact name.
If `name` is null/omitted, it falls back to index-based matching.

**Recommendation:** Always specify `name` for profiles, rules, and categories.

### Costs → matched by TYPEID (name fallback)

If `typeId` is set, matches by `typeId`. Otherwise falls back to `name`.

### Omitted fields are not checked

Only non-null fields in expected state are asserted. Omitting a field means "don't care."

## Common failure patterns

### 1. Implicit zero-errors default

**Symptom:**
```
Step 0: expected no errors (default) but got 2: ...
```

**Cause:** When `expectedState` has no `errors`, `errorsContain`, or `errorCount` field,
SpecRunner automatically asserts zero validation errors. If your roster has constraint
violations, this fails.

**Fixes:**
- Add the missing constraints/entries to make the roster valid
- Use `errorCount: N` to acknowledge expected errors
- Use `errorsContain` to assert specific expected errors
- Use `errors: [...]` for exact error matching

**Exception:** DataSource specs (`setup.dataSource`) skip this check entirely.

### 2. Auto-select ordering

**Symptom:** Selection names are in wrong order or unexpected selections appear.

**Cause:** Entries with `min≥1` constraints are auto-selected when a force is added.
These appear **before** any manually-selected entries.

**Fix:** List auto-selected entries first in your expected selections:

```yaml
- expectedState:
    forces:
      - selections:
          - name: Auto-Selected Unit   # min≥1, appears first
          - name: Manually Added Unit   # added via selectEntry action
```

### 3. Cost limit side effects

**Symptom:** Unexpected validation errors about cost limits after adding entries.

**Cause:** `defaultCostLimit: -1` means "no limit." Positive values create actual
limits. When total costs exceed the limit, validation errors appear.

**Fix:** Either set `defaultCostLimit: -1` (no limit) or assert the expected
cost limit errors.

### 4. Selection count mismatch

**Symptom:**
```
Step 0: force[0].selection[3] expected but only 2 selections
```

**Cause:** Expected state lists more selections than actually exist. Common when
forgetting that auto-select adds entries, or when an action failed silently.

**Fix:** Run the spec with only the `expectedState` step (no specific assertions)
to see what the actual roster state looks like. Then adjust expectations.

### 5. Engine-specific differences

**Symptom:** Spec passes for Oracle but fails for NewRecruit (or vice versa).

**Cause:** Engines may differ in behavior. Use engine-specific overrides:

```yaml
- expectedState:
    forces:
      - selections:
          - name: Unit A
            page: "42"
    engines:
      newrecruit:
        forces:
          - selections:
              - name: Unit A
              # page omitted — NR doesn't expose page on selections
```

Non-null fields in the engine override **replace** the base fields for that engine.
Null/omitted fields inherit from the base.

Mark known engine differences in the `engines` top-level field:

```yaml
engines:
  newrecruit: fail   # known NR failure
```

### 6. Floating-point cost comparison

**Symptom:** Cost value looks correct but assertion fails.

**Cause:** SpecRunner uses tolerance-based comparison for doubles (±1e-9).
Values like `10.0` vs `10.000000001` will pass, but `10.0` vs `10.1` won't.

### 7. Error assertion mismatch

**Symptom:**
```
Step 0: errors: expected 2 errors but got 3
Step 0: errors: unexpected error: ...
```

**Cause:** `errors` requires **exact match** — every expected error must exist and
no extra errors are allowed. `errorsContain` allows extra actual errors.

**Fix:** Use `errorsContain` for subset matching, or add all errors to `errors`.

See [ERROR-ASSERTIONS.md](references/ERROR-ASSERTIONS.md) for error matching details.

## Debugging workflow

1. **Isolate:** Run single spec with `--filter "DisplayName~{id}"`
2. **Read:** Parse the `Step N: path: expected X but got Y` messages
3. **Check matching:** Is it index vs name? Is ordering correct?
4. **Check defaults:** Is zero-errors implicit check triggering?
5. **Compare engines:** Does it fail for one engine only? Use `engines:` overrides.
6. **Iterate:** Fix and re-run. Lint after: `pwsh -File tools/format-specs.ps1`

## Reference files

- [ERROR-ASSERTIONS.md](references/ERROR-ASSERTIONS.md) — Error `on`/`from` format and matching
