# Label Taxonomy

## Priority labels

Mutually exclusive — every open issue should have exactly one.

### `priority: high` 🔴
**Criteria** (any of):
- Quick win with high readability/usability impact
- Prevents a problem that gets worse as spec count grows
- Fills a zero-coverage gap in an important area
- Small scope, clear requirements, immediately actionable

### `priority: medium` 🟠
**Criteria** (any of):
- Good value but requires moderate effort or investigation
- Clear scope but not urgent
- Depends on another issue being done first
- Improves quality of life but doesn't prevent problems

### `priority: low` 🟡
**Criteria** (any of):
- Large investment with uncertain payoff
- Requires significant design research
- Nice-to-have optimization
- Not blocking any other work

### `priority: backlog` ⚪
**Criteria** (any of):
- Scope not yet decided (needs design decision first)
- Depends on foundational work that hasn't started
- Far-future feature area
- All sub-issues are also backlog

## Area labels

Not mutually exclusive — an issue can have multiple areas.

### `area: spec-coverage` 🟢
Writing new or expanding existing YAML conformance specs.

### `area: framework` 🔵
Spec runner, loader, protocol, tooling, or infrastructure improvements.

### `area: devex` 🟣
Developer experience, refactoring, and code quality improvements.

### `area: newrecruit` 🩷
NewRecruit Playwright-based engine adapter — automation, HAR, NR-specific perf.

## Status labels

### `needs-design` 💜
The issue requires a design decision or scope definition before implementation.
Often paired with `priority: backlog` (can't implement until design is done) or
`priority: medium` (design is tractable, just needs a decision).

## Decision guide

```
New issue comes in:
  ├─ Is it a spec coverage gap?
  │   ├─ Zero coverage → priority: high + area: spec-coverage
  │   └─ Partial coverage → priority: medium + area: spec-coverage
  ├─ Is it NR-specific?
  │   └─ Add area: newrecruit (+ other areas if applicable)
  ├─ Does it need a design decision first?
  │   └─ Add needs-design
  ├─ Is it blocked on unstarted foundational work?
  │   └─ priority: backlog
  └─ Is the scope clear and effort reasonable?
      ├─ Small/quick → priority: high
      ├─ Moderate → priority: medium
      └─ Large/uncertain → priority: low
```
