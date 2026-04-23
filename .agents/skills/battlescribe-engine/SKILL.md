---
name: battlescribe-engine
description: >
  Work with the BattleScribe (BS) engine adapter — the IKVM-based Java interop engine.
  Use when debugging BS engine test failures, understanding Java reflection quirks,
  modifying JavaModelFactory, or troubleshooting XML loading and composite entry IDs.
---

# BattleScribe engine

The BattleScribe engine wraps the original BattleScribe desktop Java engine (v2.3.21) via IKVM.NET, compiling
Java bytecode to .NET assemblies. It is one of the two conformance engines under test
(alongside NewRecruit).

## Architecture

```
ProtocolMessages (spec YAML)
    ↓ JavaModelFactory
Java model objects (GameSystem, Catalogue, ...)
    ↓ BattleScribeEngine.Initialize()
JavaEngine (_engine) — obfuscated net.battlescribe.engine.a.f
    ↓ BattleScribeRosterEngine
IRosterEngine interface (shared with NR adapter)
```

**Key files:**

| File | Purpose |
|------|---------|
| `src/BattleScribeSpec.BattleScribe/BattleScribeEngine.cs` | Core engine wrapper |
| `src/BattleScribeSpec.BattleScribe/BattleScribeRosterEngine.cs` | IRosterEngine adapter |
| `src/BattleScribeSpec.BattleScribe/JavaModelFactory.cs` | Protocol → Java object factory |
| `src/BattleScribeSpec.BattleScribe/BattleScribeSpec.BattleScribe.csproj` | JAR dependencies |

## IKVM Java interop

The Java engine classes are obfuscated. C# type aliases map readable names:

```csharp
using JavaEngine = net.battlescribe.engine.a.f;
using JavaCatalogueManager = net.battlescribe.engine.a.d;
```

**Engine methods are also obfuscated:**

| C# wrapper | Java method | Purpose |
|------------|-------------|---------|
| Initialize() | `_engine.a()` | Core engine init |
| AddForce() | `_engine.b()` | Create force |
| SelectEntry() | `_engine.b()` | Select entry |
| DeselectEntry() | `_engine.m()` | Remove selection |
| SetNumSelections() | `_engine.a()` | Set count |
| DuplicateSelection() | `_engine.k()` | Duplicate |
| RemoveForce() | `_engine.g()` | Remove force |
| SetCostLimit() | `_engine.a()` | Set cost limit |
| SelectDefaultRootEntries() | `_engine.x()` | Auto-select (reflection) |

## Critical quirks

### 1. Auto-select via reflection (x() method)

The private `x()` method auto-selects entries with `min≥1` constraints. the BattleScribe engine
invokes it via reflection because the desktop app calls it during `setRoster(bl=true)`,
but the BS engine creates forces separately via `b()` which doesn't trigger auto-select.

```csharp
var method = _engine.GetType().GetMethod("x",
    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
    binder: null, types: Type.EmptyTypes, modifiers: null);
method.Invoke(_engine, null);
```

**Critical:** `x()` must be called **exactly once** after the first force is added.
Calling it multiple times creates duplicate selections. Tracked via `_autoSelectDone` flag.

### 2. defaultCostLimit = -1 means "no limit"

Negative values (conventionally -1) mean "no cost limit." Only non-negative values
create actual roster cost limits:

```csharp
if (dcl >= 0)  // Skip negative = no limit
    roster.getCostLimits().add(limit);
```

### 3. DataUtils namespace collision

`net.battlescribe.a.c.e` (DataUtils serializer) can't be imported directly because
`c` collides with the `net.battlescribe.a.c` namespace. Resolution via reflection:

```csharp
var assembly = Assembly.Load("DataUtils");
var type = assembly.GetType("net.battlescribe.a.c.e");
var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, [paramType]);
method.Invoke(null, [arg]);
```

### 4. Composite entry IDs (linkId::targetId)

EntryLinks create composite IDs at runtime. Resolution tries each part:

```csharp
if (compositeId.Contains("::"))
    foreach (var part in compositeId.Split("::"))
        if (_entryLookup.TryGetValue(part, out var entry))
            return entry;
```

### 5. XML preprocessing for modifier values

BattleScribe 2.3.21 requires `value` attribute on `<modifier>` tags, but newer data
formats omit it. Preprocessing adds `value=""` via regex before loading:

```csharp
Regex.Replace(xml, @"(<modifier\b(?![^>]*\bvalue\s*=))([^>]*?)(\/?>)", "$1 value=\"\"$2$3");
```

### 6. Desktop platform enum via reflection

The platform enum has obfuscated field names. Resolution iterates enum constants to
find the 4th value (desktop), since the order is: android, android-debug, ios, desktop.

### 7. Not thread-safe

The BattleScribe engine wrapper is **not thread-safe**. All methods must be called from a single
thread, even though the `threadCount` parameter controls Java engine internals.

### 8. Error remapping

Validation errors are distributed across roster elements (roster/force/category/selection).
The BS engine remaps errors using English message string matching ("too many", "too much").
This is accepted because the Java engine is EOL (v2.3.21) with stable messages.

## JAR dependencies

All located in `lib/` and configured in the .csproj as IKVM references:

| JAR | Purpose |
|-----|---------|
| BattleScribeEngine.jar | Main roster engine |
| DataUtils.jar | XML serialization |
| simple-xml-2.7.1.jar | XML marshalling |
| kotlin-stdlib-1.3.71.jar | Kotlin runtime |
| kotlin-stdlib-common-1.3.71.jar | Kotlin common library |
| annotations-13.0.jar | JetBrains nullability annotations |
| commons-io-2.4.jar | IO utilities |
| stax-1.2.0.jar + stax-api-1.0.1.jar | StAX XML parser |
| xpp3-1.1.3.3.jar | XML pull parser |

## Reference files

- [JAVA-MODEL-FACTORY.md](references/JAVA-MODEL-FACTORY.md) — Factory method patterns
