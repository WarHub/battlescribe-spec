# BattleScribe Spec Adapter Protocol v1.0

This document defines the JSON-line protocol used for communication between the
**bs-spec-runner** (conformance test runner) and an **engine adapter** (a thin wrapper
around a BattleScribe-compatible roster editing engine).

## Overview

The runner launches the adapter as a child process and communicates via **stdin/stdout**
using NDJSON (newline-delimited JSON) — one JSON object per line.

```mermaid
sequenceDiagram
    participant Runner as bs-spec-runner
    participant Adapter as Engine Adapter

    Runner->>Adapter: {"type":"setup", ...}
    Adapter-->>Runner: {"type":"setupResult", ...}

    loop For each spec step
        Runner->>Adapter: {"type":"action", ...}
        Adapter-->>Runner: {"type":"actionResult", ...}
        Runner->>Adapter: {"type":"getState"}
        Adapter-->>Runner: {"type":"state", ...}
    end

    Runner->>Adapter: {"type":"teardown"}
    Adapter-->>Runner: {"type":"teardownResult"}
```

## Protocol Rules

1. Each message is a single JSON object on exactly one line (no embedded newlines).
2. The adapter MUST NOT write anything to stdout except protocol response messages.
3. The adapter MAY write diagnostic information to stderr.
4. The runner sends exactly one command, then waits for exactly one response.
5. The `type` field discriminates message kinds.
6. Unknown fields should be ignored (forward compatibility).

## Runner → Adapter Commands

### `setup` — Initialize Engine

Sent once at the start of each spec test. Provides game system and catalogue data.

```json
{
  "type": "setup",
  "version": "1.0",
  "gameSystem": {
    "id": "test-gs",
    "name": "Test Game System",
    "costTypes": [
      { "id": "ct-pts", "name": "pts", "defaultCostLimit": -1.0, "hidden": false, "limit": false }
    ],
    "forceEntries": [
      {
        "id": "fe-1", "name": "Patrol",
        "categoryLinks": [{ "id": "cl-1", "targetId": "cat-1", "name": "HQ", "primary": false }],
        "forceEntries": []
      }
    ],
    "categoryEntries": [
      { "id": "cat-1", "name": "HQ" }
    ]
  },
  "catalogue": {
    "id": "cat-1",
    "name": "Test Catalogue",
    "gameSystemId": "test-gs",
    "selectionEntries": [
      {
        "id": "se-1", "name": "Unit", "type": "unit", "hidden": false, "collective": false,
        "costs": [{ "name": "pts", "typeId": "ct-pts", "value": 50 }],
        "constraints": [], "modifiers": [], "modifierGroups": [],
        "selectionEntries": [], "selectionEntryGroups": [],
        "entryLinks": [], "categoryLinks": [], "rules": [], "profiles": [], "infoGroups": []
      }
    ],
    "selectionEntryGroups": [],
    "entryLinks": []
  }
}
```

### `action` — Execute Roster Action

| Action | Required Fields | Description |
|--------|----------------|-------------|
| `addForce` | `forceEntryIndex` | Add a force using a force entry by index |
| `removeForce` | `forceIndex` | Remove a force by index |
| `selectEntry` | `forceIndex`, `entryIndex` | Add a selection to a force |
| `selectChildEntry` | `forceIndex`, `selectionIndex`, `childEntryIndex` | Add a child selection |
| `deselectSelection` | `forceIndex`, `selectionIndex` | Remove a selection |
| `setSelectionCount` | `forceIndex`, `entryIndex`, `count` | Set selection quantity |
| `duplicateSelection` | `forceIndex`, `selectionIndex` | Duplicate a selection |
| `setCostLimit` | `costTypeId`, `value` | Set cost limit for a cost type |

Example:
```json
{"type":"action","action":"addForce","forceEntryIndex":0}
{"type":"action","action":"selectEntry","forceIndex":0,"entryIndex":0}
{"type":"action","action":"setCostLimit","costTypeId":"ct-pts","value":500}
```

### `getState` — Query Roster State

```json
{"type":"getState"}
```

### `getErrors` — Query Validation Errors

```json
{"type":"getErrors"}
```

### `teardown` — End Test

Sent after each spec test completes. The adapter should reset its state.

```json
{"type":"teardown"}
```

## Adapter → Runner Responses

### `setupResult`

```json
{"type":"setupResult","errors":[]}
```

### `actionResult`

```json
{"type":"actionResult","ok":true}
{"type":"actionResult","ok":false,"error":"Force index out of range"}
```

### `state` — Full Roster State

```json
{
  "type": "state",
  "name": "New Roster",
  "gameSystemId": "test-gs",
  "forces": [
    {
      "name": "Patrol",
      "catalogueId": "cat-1",
      "selections": [
        {
          "name": "Unit",
          "entryId": "se-1",
          "type": "unit",
          "number": 1,
          "hidden": false,
          "costs": [{ "name": "pts", "typeId": "ct-pts", "value": 50 }],
          "children": []
        }
      ]
    }
  ],
  "costs": [{ "name": "pts", "typeId": "ct-pts", "value": 50 }],
  "validationErrors": []
}
```

### `errors`

```json
{"type":"errors","errors":["Min 1 selection required for HQ"]}
```

### `teardownResult`

```json
{"type":"teardownResult"}
```

### `error` — Protocol Error

Returned when the adapter encounters an unrecoverable error:

```json
{"type":"error","message":"Failed to initialize engine"}
```

## Data Type Reference

### Selection Entry

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `id` | string | yes | Unique identifier |
| `name` | string | yes | Display name |
| `type` | string | yes | One of: `unit`, `model`, `upgrade` |
| `hidden` | bool | yes | Whether hidden from selection |
| `collective` | bool | yes | Whether costs are collective |
| `costs` | CostValue[] | no | Associated costs |
| `constraints` | Constraint[] | no | Min/max constraints |
| `modifiers` | Modifier[] | no | Conditional modifiers |
| `modifierGroups` | ModifierGroup[] | no | Grouped conditional modifiers |
| `selectionEntries` | SelectionEntry[] | no | Child entries |
| `selectionEntryGroups` | SelectionEntryGroup[] | no | Child entry groups |
| `entryLinks` | EntryLink[] | no | Links to shared entries |
| `categoryLinks` | CategoryLink[] | no | Category associations |
| `rules` | Rule[] | no | Associated rules |
| `profiles` | Profile[] | no | Associated profiles |
| `infoGroups` | InfoGroup[] | no | Grouped info (profiles + rules) |
| `page` | string | no | Page reference |

### Modifier

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `type` | string | yes | `set`, `increment`, `decrement`, `append`, `add`, `remove` |
| `field` | string | yes | Target field (e.g., `hidden`, `name`, cost type ID) |
| `value` | string | yes | New/delta value |
| `conditions` | Condition[] | no | Conditions to evaluate |
| `conditionGroups` | ConditionGroup[] | no | Grouped conditions |
| `repeats` | Repeat[] | no | Repeat modifiers |

### Condition

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `type` | string | yes | `equalTo`, `notEqualTo`, `greaterThan`, `lessThan`, `atLeast`, `atMost`, `instanceOf` |
| `value` | number | yes | Comparison value |
| `field` | string | yes | `selections`, `forces`, `cost type ID` |
| `scope` | string | yes | `self`, `parent`, `roster`, `force`, `primary-category`, `primary-catalogue`, `ancestor` |
| `childId` | string | no | Filter to specific child type |
| `shared` | bool | no | Whether shared across catalogue |
| `includeChildSelections` | bool | no | Include nested selections |
| `includeChildForces` | bool | no | Include nested forces |
| `percentValue` | bool | no | Value is a percentage |

## Implementing an Adapter

An adapter is a program that:

1. Reads JSON lines from **stdin**
2. Parses the `type` field to determine the command
3. Dispatches to the native engine API
4. Writes a JSON response line to **stdout**
5. Repeats until stdin is closed or a `teardown` command is received

### Pseudocode

```
while line = readline(stdin):
    command = json_parse(line)
    match command.type:
        "setup":
            errors = engine.setup(command.gameSystem, command.catalogue)
            write(stdout, {"type":"setupResult","errors":errors})
        "action":
            try:
                dispatch_action(engine, command)
                write(stdout, {"type":"actionResult","ok":true})
            catch error:
                write(stdout, {"type":"actionResult","ok":false,"error":str(error)})
        "getState":
            state = engine.get_roster_state()
            write(stdout, state_to_json(state))
        "getErrors":
            errors = engine.get_validation_errors()
            write(stdout, {"type":"errors","errors":errors})
        "teardown":
            engine.dispose()
            write(stdout, {"type":"teardownResult"})
            engine = new_engine()  // ready for next spec
```

### Notes

- The adapter should handle multiple setup/teardown cycles (one per spec test).
- stderr is free for diagnostics — the runner ignores it.
- The runner may terminate the adapter process if it doesn't respond within a timeout.
