# Project Context

- **Owner:** Amadeusz Sadowski
- **Project:** battlescribe-spec — declarative conformance test suite for BattleScribe roster engines
- **Stack:** C# / .NET 9, Protocol types (ProtocolMessages.cs), YAML spec format
- **Created:** 2026-05-08

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- Protocol types in ProtocolMessages.cs must remain engine-agnostic — shared between all adapters
- GameData specs reuse ProtocolGameSystem/ProtocolCatalogue as setup data; engines init via IGameDataEngine.Setup()
- The spec format describes WHAT behavior to test, not HOW any engine implements it

### Issue #19 Technical Analysis (2026-05-08)

- **"Roster loading" has two meanings:** (1) file-based loading (.ros files), (2) empty roster creation (already tested)
- **Current protocol gap:** No LoadRosterCommand or SaveRosterCommand; specs can only test inline game data + actions
- **Editor round-trip limitation:** GameData mutation specs (#168-#170) can't verify persistence without save/reload cycle
- **Scope recommendation:** Option 2 (Medium) — add load/save commands to protocol; covers both roster file loading and editor round-trip
- **Engine-agnosticism check:** Load/save interface is neutral; implementation varies by adapter (DB, files, memory)
- **Downstream impact:** Unblocks #18 (data editor epic) and related #168-#172 if round-trip is supported
- **File format decision:** Start with .ros XML only; defer .rosz compression to later backlog
- **Protocol additions needed:** LoadRosterCommand, SaveRosterCommand, RosterPersistenceResult types (draft in decision doc)

### Team Decision: Option 2 Approved (2026-05-08)

**DECISION:** Amadeusz approved Option 2 (Medium scope) for Issue #19.
- Holden's technical analysis was used to evaluate 3 scope options.
- Bobbie's domain analysis clarified roster vs. editor separation.
- **Option 2 chosen:** Roster loading + editor round-trip (load/save protocol support).
- **Rationale:** Unblocks Epic #18; engine-agnostic protocol design; achievable in 2–3 sprints.
- **Next steps:** Begin Epic #18 MVP implementation (Phase 1: 13 priority specs, 4–6 week effort).
