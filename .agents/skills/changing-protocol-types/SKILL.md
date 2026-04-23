---
name: changing-protocol-types
description: >
  Add, modify, or remove fields in BattleScribe protocol types. Use when changing the
  data format shared between spec setup, engine adapters, and assertions. Guides you
  through the synchronized file set that must be updated together.
---

# Changing Protocol Types

## Overview

Protocol types define the data contract between spec YAML files, the SpecRunner assertion
engine, and the engine adapters (BattleScribe, NewRecruit). Changes to protocol types cascade
across multiple files that must stay synchronized.

## Synchronized file set

Every protocol type change must update **all applicable** files:

| # | File | What it defines | When to update |
|---|------|----------------|----------------|
| 1 | `Protocol/ProtocolMessages.cs` | Wire format (JSON) — Protocol* classes | Always |
| 2 | `EngineTypes.cs` | Runtime state records — *State types | If field appears in roster state |
| 3 | `SpecFileModels.cs` | YAML assertion models — Expected* types | If specs need to assert on it |
| 4 | `SpecRunner.cs` | Assertion logic — Assert* methods | If specs need to assert on it |
| 5 | `JavaModelFactory.cs` (BattleScribe project) | Java object creation from protocol | If BattleScribe needs to produce it |
| 6 | `JsonProtocolEngine.cs` | Protocol JSON engine adapter | If adding new commands/responses |

All paths are relative to `src/BattleScribeSpec.TestKit/` except `JavaModelFactory.cs`
which is in `src/BattleScribeSpec.BattleScribe/`.

## Step-by-step: Adding a new field

### Example: Add `author` to game system setup and force state

**Step 1: ProtocolMessages.cs — Add to Protocol type**

```csharp
public class ProtocolGameSystem
{
    // ... existing fields ...

    [JsonPropertyName("author")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Author { get; set; }
}
```

**Step 2: EngineTypes.cs — Add to State record (if runtime state)**

```csharp
public record ForceState(
    // ... existing parameters ...
    [property: JsonPropertyName("author"),
     JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Author = null);
```

**Step 3: SpecFileModels.cs — Add to Expected type (if assertable)**

```csharp
public sealed class ExpectedForceDef
{
    // ... existing properties ...

    [YamlMember(Alias = "author")]
    public string? Author { get; set; }
}
```

**Step 4: SpecRunner.cs — Add assertion logic**

Add in the relevant Assert* method (e.g., inside the force assertion loop):

```csharp
if (ef.Author is not null)
    AssertEqual(stepIndex, $"force[{fi}].author", ef.Author, af.Author ?? "");
```

**Step 5: JavaModelFactory.cs — Add to factory method**

```csharp
public static GameSystem CreateGameSystem(
    // ... existing params ...
    string? author = null)
{
    var gs = new GameSystem();
    // ... existing setters ...
    if (!string.IsNullOrEmpty(author))
        gs.setAuthor(author);
    return gs;
}
```

**Step 6: Verify**

```bash
dotnet build --no-restore
dotnet test --no-restore
```

## Checklist

Use this checklist for every protocol type change:

- [ ] **ProtocolMessages.cs**: Added/changed field with `[JsonPropertyName]`
- [ ] **ProtocolMessages.cs**: Used `[JsonIgnore(WhenWritingNull)]` for optional fields
- [ ] **EngineTypes.cs**: Updated state record (if field is in runtime state)
- [ ] **SpecFileModels.cs**: Updated Expected* class (if field is assertable)
- [ ] **SpecFileModels.cs**: Used `[YamlMember(Alias = "...")]` for YAML mapping
- [ ] **SpecRunner.cs**: Added assertion logic in relevant Assert* method
- [ ] **JavaModelFactory.cs**: Updated factory method (if BattleScribe needs it)
- [ ] **Build passes**: `dotnet build --no-restore`
- [ ] **Tests pass**: `dotnet test --no-restore`

## Common mistakes

See [COMMON-MISTAKES.md](references/COMMON-MISTAKES.md) for pitfalls and how to avoid them.

## File locations quick reference

See [FILE-MAP.md](references/FILE-MAP.md) for a map of which Protocol* types correspond
to which State records and Expected* types.
