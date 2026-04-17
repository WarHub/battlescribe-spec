# Skill Template

Copy this template to `.agents/skills/{skill-name}/SKILL.md` and fill in the sections.

---

## SKILL.md

````yaml
---
name: my-skill-name
description: >
  {Action verb} {what}. Use when {triggers — when should an agent activate this skill}.
  Covers {scope — what topics are included}.
---

# {Skill Title}

## Quick start

1. {First step with concrete command}
   ```bash
   {command}
   ```
2. {Second step}
3. {Third step}

## {Core concept}

{Explain the key concept. Use code examples:}

```csharp
// Example code with language tag
public void Example() { }
```

## {Another section}

| Column A | Column B | Notes |
|----------|---------|-------|
| value | value | explanation |

## Common mistakes

### 1. {Mistake name}

**Symptom:** {What the agent sees}

**Cause:** {Why it happens}

**Fix:** {How to resolve it}

### 2. {Another mistake}

...

## Reference files

- [REFERENCE.md](references/REFERENCE.md) — {Brief description of what this covers}
````

---

## references/REFERENCE.md

````markdown
# {Reference Title}

## Overview

{Brief summary of what this reference covers.}

## {Section}

{Detailed content — tables, code examples, exhaustive lists.}
````

---

## Checklist

Before committing a new skill:

- [ ] Directory: `.agents/skills/{name}/SKILL.md` exists
- [ ] Frontmatter: `name` matches directory name
- [ ] Frontmatter: `description` starts with verb, includes "Use when", under 1024 chars
- [ ] Body: Under 500 lines
- [ ] References: Each under 500 lines
- [ ] Links: All `references/` links resolve to actual files
- [ ] No code changes: Skills are documentation only
