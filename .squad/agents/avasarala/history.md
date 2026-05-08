# Project Context

- **Owner:** Amadeusz Sadowski
- **Project:** battlescribe-spec — declarative conformance test suite for BattleScribe roster engines
- **Stack:** C# / .NET 9, YAML specs, IKVM (Java interop), Playwright, NUnit
- **Created:** 2026-05-08

## Learnings

### Project Board Structure (2026-05-08)
- **WarHub/battlescribe-spec uses GitHub Project Board v2 (#2)** for tracking conformance spec work across 32 open issues
- **Label taxonomy is coherent:** Priority tiers (backlog/high/medium) + Area ownership (spec-coverage/framework/newrecruit/devex) + Special flags (needs-design, bug)
- **Backlog split:** 56% backlog (long-term), 22% high (ready next), 9% medium/untagged
- **Area breakdown:** 53% spec-coverage (17 issues), 19% framework (6), 16% newrecruit (5), 6% devex (2)
- **Issue types:** 38% features, 25% other (untyped), 16% epics, 16% tasks, 6% bugs
- **Authentication blocker:** gh token lacks `read:project` scope; blocks Copilot CLI automation of board queries. Must regenerate token with full scopes.
- **Recent triage gap:** 3 recent issues (#199, #197, #196) lack priority/area labels. Must tag within 1 day.
- **Design blocker:** Epic #18 (Data Editor) is high priority but marked `needs-design`; unblock first before implementation.
- **Recommendation:** Regenerate gh token immediately, triage untagged issues today, resolve #18 design question within 3 days.

<!-- Append new learnings below. Each entry is something lasting about the project. -->
