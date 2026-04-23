# Common Mistakes When Changing Protocol Types

## 1. Missing JsonPropertyName attribute

❌ **Wrong:**
```csharp
public string? Author { get; set; }
```

✅ **Correct:**
```csharp
[JsonPropertyName("author")]
public string? Author { get; set; }
```

Protocol types use explicit `[JsonPropertyName]` on every property. The serializer uses
`CamelCase` naming policy as a fallback, but explicit attributes are the convention.

## 2. Missing JsonIgnore for nullable fields

❌ **Wrong:** (serializes `"author": null` into JSON)
```csharp
[JsonPropertyName("author")]
public string? Author { get; set; }
```

✅ **Correct:** (omits `author` from JSON when null)
```csharp
[JsonPropertyName("author")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? Author { get; set; }
```

## 3. Forgetting the State record field

Protocol types define **setup data** (sent TO the adapter).
State records define **runtime state** (returned FROM the adapter).

Not every Protocol field needs a State field, but any field you want to **read back**
from the roster must be in the corresponding State record.

| Protocol type | State record |
|--------------|-------------|
| ProtocolForceEntry | ForceState |
| ProtocolSelectionEntry | SelectionState |
| ProtocolProfile | ProfileState |
| ProtocolRule | RuleState |
| ProtocolCategoryEntry | CategoryState |
| ProtocolCostValue | CostState |
| ProtocolCharacteristic | CharacteristicState |

## 4. Forgetting SpecFileModels Expected type

If you add a State field but don't add the matching Expected* field, specs can't
assert on it. Tests will pass silently even when the field is wrong.

## 5. Forgetting SpecRunner assertion logic

Adding an Expected* field without assertion logic means the field is deserialized
from YAML but never checked. The field will be silently ignored.

## 6. Not updating JavaModelFactory

The BattleScribe engine creates Java model objects from protocol types. If a new field
isn't set in the factory, BattleScribe tests will use default/null values.

**Pattern:** All factory methods use optional parameters with defaults:
```csharp
public static GameSystem CreateGameSystem(
    string id = "test-gs",
    string name = "Test",
    string? author = null)  // Optional, null = not set
```

## 7. Case sensitivity in field names

YAML spec files use `camelCase` field names.
`[YamlMember(Alias = "fieldName")]` must match exactly.
`[JsonPropertyName("fieldName")]` must match the protocol JSON format.

## 8. Record parameter ordering in EngineTypes.cs

State records use **positional constructor parameters**. New fields must be added
at the **end** with default values to avoid breaking existing code:

```csharp
public record ForceState(
    string Name,                    // Required, no default
    string? CatalogueId = null,     // Optional with default
    // ... existing params ...
    string? Author = null);         // NEW: add at end with default
```

## 9. Forgetting ForEngine() override propagation

If a new field is added to `ExpectedStateDef` and it needs engine-specific overrides,
the `ForEngine()` method must be updated to merge the new field:

```csharp
public ExpectedStateDef ForEngine(string? engineName)
{
    // ... existing merge logic ...
    return new ExpectedStateDef
    {
        // ... existing fields ...
        Author = over.Author ?? Author,  // NEW: merge override
    };
}
```

This only applies to `ExpectedStateDef` top-level fields, not nested types.
