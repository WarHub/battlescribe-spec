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
