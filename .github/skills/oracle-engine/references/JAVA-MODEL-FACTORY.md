# JavaModelFactory Patterns

## Factory method convention

All factory methods in `JavaModelFactory.cs` follow this pattern:

```csharp
public static JavaType CreateThing(
    string id,
    string name,
    IEnumerable<ChildType>? children = null)
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
- `id`/`name` are typically required (no default) on most methods; some like
  `CreateGameSystem` provide defaults for convenience
- Collections use `IEnumerable<T>?` (not `IReadOnlyList`)
- Collections use `getXxx().add()` (Java mutable list pattern)
- Null collections are skipped (no empty list created)
- String properties use `setXxx()` Java setter convention

## Core factory methods

### CreateGameSystem

```csharp
CreateGameSystem(
    string id = "test-gs",
    string name = "Test Game System",
    int revision = 1,
    string bsVersion = "2.03",
    IEnumerable<CostType>? costTypes = null,
    IEnumerable<ForceEntry>? forceEntries = null,
    IEnumerable<CategoryEntry>? categoryEntries = null,
    IEnumerable<ProfileType>? profileTypes = null,
    IEnumerable<Publication>? publications = null,
    IEnumerable<SelectionEntry>? selectionEntries = null,
    IEnumerable<EntryLink>? entryLinks = null,
    IEnumerable<Rule>? rules = null,
    IEnumerable<InfoLink>? infoLinks = null,
    IEnumerable<SelectionEntry>? sharedSelectionEntries = null,
    IEnumerable<SelectionEntryGroup>? sharedSelectionEntryGroups = null,
    IEnumerable<Rule>? sharedRules = null,
    IEnumerable<Profile>? sharedProfiles = null,
    IEnumerable<InfoGroup>? sharedInfoGroups = null)
```

**Note:** GameSystem does NOT have root-level `selectionEntryGroups`.
Only `sharedSelectionEntryGroups`.

### CreateCostType

```csharp
CreateCostType(
    string id,            // required, no default
    string name,          // required, no default
    double? defaultCostLimit = null,  // null → -1.0 (no limit)
    bool hidden = false,
    bool limit = false)
```

`defaultCostLimit: null` maps to `-1.0` internally (BattleScribe convention for "no limit").

### CreateCategoryEntry

```csharp
CreateCategoryEntry(
    string id,            // required, no default
    string name,          // required, no default
    bool hidden = false,
    IEnumerable<Constraint>? constraints = null,
    IEnumerable<Modifier>? modifiers = null,
    IEnumerable<ModifierGroup>? modifierGroups = null,
    IEnumerable<Profile>? profiles = null,
    IEnumerable<Rule>? rules = null,
    IEnumerable<InfoGroup>? infoGroups = null,
    IEnumerable<InfoLink>? infoLinks = null,
    string? publicationId = null,
    string? page = null)
```

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

## Adding a new field to a factory method

1. Add optional parameter at end of parameter list (with default)
2. Add `setXxx()` call in method body
3. Update `CreateXxxFromProtocol()` mapping method to pass the new Protocol field
4. Build and test: `dotnet build && dotnet test`

## Common pitfalls

- **GameSystem has no root selectionEntryGroups** — only shared. Don't add them.
- **Collection parameters use `IEnumerable<T>?`** — not `IReadOnlyList` or array.
- **Null vs empty list** — null = don't touch the Java collection; empty list = no items
  but Java still has its default empty list.
