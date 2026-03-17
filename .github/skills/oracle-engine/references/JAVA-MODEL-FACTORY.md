# JavaModelFactory Patterns

## Factory method convention

All factory methods in `JavaModelFactory.cs` follow this pattern:

```csharp
public static JavaType CreateThing(
    string id = "default-id",
    string name = "Default Name",
    IReadOnlyList<ChildType>? children = null)
{
    var obj = new JavaType();
    obj.setId(id);
    obj.setName(name);
    if (children is not null)
        foreach (var child in children)
            obj.getChildren().add(child);
    return obj;
}
```

**Key patterns:**
- All parameters are optional with sensible defaults
- Collections use `getXxx().add()` (Java mutable list pattern)
- Null collections are skipped (no empty list created)
- String properties use `setXxx()` Java setter convention

## Core factory methods

### CreateGameSystem

```csharp
CreateGameSystem(
    string id = "test-gs",
    string name = "Test",
    int revision = 1,
    IReadOnlyList<CostType>? costTypes = null,
    IReadOnlyList<ForceEntry>? forceEntries = null,
    IReadOnlyList<CategoryEntry>? categoryEntries = null,
    IReadOnlyList<SelectionEntry>? selectionEntries = null,
    IReadOnlyList<EntryLink>? entryLinks = null,
    IReadOnlyList<SelectionEntry>? sharedSelectionEntries = null,
    IReadOnlyList<SelectionEntryGroup>? sharedSelectionEntryGroups = null,
    IReadOnlyList<Rule>? sharedRules = null,
    IReadOnlyList<Profile>? sharedProfiles = null,
    IReadOnlyList<InfoGroup>? sharedInfoGroups = null,
    IReadOnlyList<ProfileType>? profileTypes = null,
    IReadOnlyList<Publication>? publications = null,
    IReadOnlyList<Rule>? rules = null,
    IReadOnlyList<InfoLink>? infoLinks = null)
```

**Note:** GameSystem does NOT have root-level `selectionEntryGroups`.
Only `sharedSelectionEntryGroups`.

### CreateConstraint

```csharp
CreateConstraint(
    string id,
    string type,          // "min" or "max"
    double value,
    string field,         // "selections" or "forces"
    string scope,         // "parent", "roster", "self", etc.
    bool shared = false,
    bool includeChildSelections = false,
    bool includeChildForces = false,
    bool percentValue = false)
```

**Important:** When any factory method adds new parameters (e.g., `percentValue`
was added later), ALL callers must be checked. `CreateConstraint` is called from
`CreateConstraintFromProtocol` which maps Protocol fields → factory params.

### CreateCostType

```csharp
CreateCostType(
    string id,
    string name,
    double? defaultCostLimit = null,  // null → -1.0 (no limit)
    bool hidden = false,
    bool limit = false)
```

`defaultCostLimit: null` maps to `-1.0` internally (BattleScribe convention for "no limit").

## Adding a new field to a factory method

1. Add optional parameter at end of parameter list (with default)
2. Add `setXxx()` call in method body
3. Update `CreateXxxFromProtocol()` mapping method to pass the new Protocol field
4. Build and test: `dotnet build && dotnet test`

## Common pitfalls

- **GameSystem has no root selectionEntryGroups** — only shared. Don't add them.
- **CategoryEntry factory takes (name, id) only** — no `hidden` parameter currently.
- **Collection parameters must be IReadOnlyList** — not List or array.
- **Null vs empty list** — null = don't touch the Java collection; empty list = no items
  but Java still has its default empty list.
