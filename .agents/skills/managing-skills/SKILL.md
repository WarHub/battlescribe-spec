---
name: managing-skills
description: >
  Create new or update existing agent skills in this repository. Use when adding a skill
  for a new domain area, updating an existing skill with new information, or restructuring
  skill content. Covers the agentskills.io format, this repo's conventions, knowledge
  gathering workflow, and validation.
---

# Managing Agent Skills

## Skill location

All skills live in `.agents/skills/{skill-name}/`. Each is a self-contained directory:

```
.agents/skills/{skill-name}/
├── SKILL.md              # Required: frontmatter + instructions
└── references/           # Optional: detailed reference docs
    ├── TOPIC-A.md
    └── TOPIC-B.md
```

## Creating a new skill

### 1. Choose a name

- Lowercase letters, numbers, and hyphens only
- Must not start/end with a hyphen or contain consecutive hyphens (`--`)
- Max 64 characters
- Directory name **must** match the `name` field in frontmatter

Good: `debugging-spec-failures`, `battlescribe-engine`, `changing-protocol-types`
Bad: `DebugSpecs`, `--BattleScribe`, `my_skill`

### 2. Gather domain knowledge

Before writing, explore the codebase to collect the knowledge the skill encodes.
Use parallel explore agents for efficiency. Key questions to answer:

- **What files are involved?** List paths, line numbers, key types/methods
- **What non-obvious behaviors exist?** Quirks, conventions, implicit defaults
- **What mistakes do people make?** Common pitfalls and how to avoid them
- **What is the workflow?** Step-by-step procedures with commands

### 3. Write SKILL.md

Follow the frontmatter format exactly:

```yaml
---
name: my-skill-name
description: >
  {Verb phrase}. Use when {triggers}. Covers {scope}.
---
```

**Description conventions:**
- Start with an action verb: "Write", "Debug", "Work with", "Add/modify/remove"
- Include "Use when..." to specify activation triggers
- Include "Covers..." to define scope
- Max 1024 characters

**Body structure** (adapt sections to your domain):

```markdown
# {Skill Title}

## Quick start / Workflow
{Numbered steps with commands}

## {Core concept 1}
{Explanation with code examples}

## {Core concept 2}
{Tables, patterns, gotchas}

## Common {failures/mistakes/pitfalls}
{Numbered list with symptom → cause → fix}

## Reference files
- [FILE.md](references/FILE.md) — brief description
```

### 4. Write reference files

Move detailed content out of SKILL.md into `references/` files when:
- A single topic exceeds ~50 lines of detail
- The content is a lookup table or exhaustive reference
- The content is useful but not essential for every activation

Reference files have no frontmatter — just markdown with a `# Title` heading.

### 5. Validate

```bash
# Check structure
ls .agents/skills/{skill-name}/SKILL.md          # must exist
ls .agents/skills/{skill-name}/references/        # optional

# Check frontmatter
head -10 .agents/skills/{skill-name}/SKILL.md     # starts with ---
grep "^name:" .agents/skills/{skill-name}/SKILL.md # matches dir name

# Check line counts (all files under 500 lines)
wc -l .agents/skills/{skill-name}/**/*.md
```

No CI validation exists for skills — manual checks only.

## Updating an existing skill

1. **Read the current SKILL.md** and all its reference files
2. **Identify what changed** — new behavior, renamed files, fixed bugs, new patterns
3. **Update surgically** — edit only the affected sections
4. **Keep reference links consistent** — if you rename a reference file, update the link
5. **Verify line counts** — SKILL.md under 500 lines, references under 500 lines each

### When to split content

If SKILL.md grows past ~300 lines, extract detailed sections into new reference files.
The main SKILL.md should be a concise guide; references hold the deep details.

### When to merge skills

If two skills overlap significantly, consider merging. A skill should cover one
coherent domain area, not fragment into many tiny skills.

## Format rules

### Frontmatter (required)

| Field | Required | Constraint |
|-------|----------|-----------|
| `name` | Yes | 1–64 chars, lowercase + hyphens, matches directory |
| `description` | Yes | 1–1024 chars, describes what + when to use |

### Progressive disclosure

Skills are loaded in layers:

1. **Metadata** (~100 tokens): `name` + `description` — loaded at startup for all skills
2. **Instructions** (< 5000 tokens): SKILL.md body — loaded when skill is activated
3. **References** (on demand): `references/*.md` — loaded only when agent needs detail

This means: **SKILL.md must be self-sufficient for common tasks.** Reference files
are for deep dives, not prerequisites.

### Markdown conventions in this repo

- H1 (`#`) for the skill title (one per file)
- H2 (`##`) for major sections
- H3 (`###`) for subsections and numbered items in lists
- Language-tagged code blocks: ` ```csharp `, ` ```yaml `, ` ```bash `, ` ```javascript `
- Tables for APIs, field references, and decision matrices
- `## Reference files` section at the end, with relative links

## Existing skills inventory

| Skill | Domain | References |
|-------|--------|-----------|
| `writing-specs` | Spec YAML authoring | PROTOCOL-TYPES.md, KNOWN-TAGS.md |
| `debugging-spec-failures` | SpecRunner error diagnosis | ERROR-ASSERTIONS.md |
| `changing-protocol-types` | Protocol type file sync | COMMON-MISTAKES.md, FILE-MAP.md |
| `battlescribe-engine` | IKVM Java interop engine | JAVA-MODEL-FACTORY.md |
| `newrecruit-adapter` | Playwright browser adapter | STATE-EXTRACTION.md |
| `nr-adhoc-probing` | NR UI probe and JS REPL debugging | NR-INTERNALS.md, NR-UI-PROBE.md |
| `bs-ui-probing` | BS Roster UI driver probe and diagnostics | BS-UI-PROBE.md |
| `nr-gamedata-ui` | NR Editor GameData UI driver (Playwright) | — |
| `bs-gamedata-ui` | BS Data Editor GameData UI driver (Java agent) | — |
| `managing-skills` | This skill — creating/updating skills | SKILL-TEMPLATE.md |
| `managing-backlog` | Issue triage, labels, hierarchy, grooming | ISSUE-HIERARCHY.md, LABEL-TAXONOMY.md |

## Reference files

- [SKILL-TEMPLATE.md](references/SKILL-TEMPLATE.md) — Copy-paste starter template
