# Writing a BattleScribe Spec Adapter

This guide explains how to write an adapter that allows the BattleScribe Spec Test Kit
to verify your roster engine implementation.

## Overview

An adapter is a thin wrapper around your roster engine that speaks the
[JSON-line protocol](adapter-protocol.md) over stdin/stdout. The spec runner
sends commands (setup, action, getState, getErrors, teardown) and your adapter
responds with the corresponding results.

## Architecture

```
bs-spec-runner  ──stdin──▸  your-adapter  ──▸  your engine
                ◂─stdout──                 ◂──
```

The runner manages the adapter as a child process. Each line on stdin is a
JSON command; each line on stdout is a JSON response. Stderr is ignored
(use it for logging/debugging).

## Step-by-Step

### 1. Create a Console Application

Your adapter must be a console application that reads from stdin and writes to stdout.

**Python example skeleton:**

```python
import sys
import json

for line in sys.stdin:
    command = json.loads(line.strip())
    response = handle_command(command)
    sys.stdout.write(json.dumps(response) + "\n")
    sys.stdout.flush()
```

**Go example skeleton:**

```go
scanner := bufio.NewScanner(os.Stdin)
for scanner.Scan() {
    var cmd map[string]interface{}
    json.Unmarshal(scanner.Bytes(), &cmd)
    resp := handleCommand(cmd)
    out, _ := json.Marshal(resp)
    fmt.Println(string(out))
}
```

### 2. Handle the `setup` Command

The setup command provides the full game system, catalogue, and roster definition.
Your adapter must initialize the engine with this data and respond with a
`setupResult`.

```json
// Input
{"type":"setup","gameSystem":{...},"catalogues":[...],"roster":{...}}

// Output
{"type":"setupResult","success":true}
```

### 3. Handle `action` Commands

Action commands trigger mutations on the roster. The action type determines
what to do:

| Action | Description |
|--------|-------------|
| `addForce` | Add a force with the given forceEntryId and catalogueId |
| `removeForce` | Remove the force at the given index |
| `selectEntry` | Select an entry in the given force |
| `deselectEntry` | Deselect a selection by force and selection index |
| `setSelectionCount` | Change the count of a selection |
| `duplicateSelection` | Duplicate a selection |
| `setCostLimit` | Set a cost limit value |

```json
// Input
{"type":"action","action":"selectEntry","forceIndex":0,"entryId":"entry-1"}

// Output
{"type":"actionResult","success":true}
```

### 4. Handle `getState` Command

Return the current roster state as a `state` response.

```json
// Input
{"type":"getState"}

// Output
{
  "type": "state",
  "roster": {
    "name": "My Roster",
    "gameSystemId": "gs-1",
    "costs": [{"typeId":"pts","name":"Points","value":100}],
    "costLimits": [],
    "forces": [
      {
        "catalogueId": "cat-1",
        "name": "Battalion",
        "categories": [{"id":"cat-1","name":"HQ","primary":false}],
        "selections": [
          {
            "entryId": "entry-1",
            "name": "Commander",
            "type": "model",
            "count": 1,
            "costs": [{"typeId":"pts","name":"Points","value":50}],
            "categories": [],
            "children": []
          }
        ]
      }
    ]
  }
}
```

### 5. Handle `getErrors` Command

Return current validation errors.

```json
// Input
{"type":"getErrors"}

// Output
{"type":"errors","errors":["Min 1 HQ required"]}
```

### 6. Handle `teardown` Command

Clean up any engine state.

```json
// Input
{"type":"teardown"}

// Output
{"type":"teardownResult","success":true}
```

### 7. Error Handling

If your adapter encounters an error, return a `ProtocolError`:

```json
{"type":"error","message":"Unknown action: foobar"}
```

## Running the Spec Suite

### With .NET CLI Runner

```bash
# Build your adapter
# Then run:
dotnet bs-spec-runner.dll --adapter "/path/to/your-adapter" --specs specs --output summary
```

### With Docker

```bash
docker run --rm -v /path/to/your-adapter:/adapter \
  bs-spec-runner:local \
  --adapter "/adapter/your-adapter" --specs /specs --output summary
```

### .NET Adapter Shortcut

If your adapter is a .NET assembly, use the `dotnet:` prefix:

```bash
dotnet bs-spec-runner.dll --adapter "dotnet:your-adapter.dll" --specs specs
```

## Reference Implementation

See `src/BattleScribeSpec.ReferenceAdapter/` for a complete .NET adapter implementation
that wraps the BattleScribe oracle engine. It's only ~10 lines of code thanks to the
`AdapterHandler` helper class from the TestKit.

## Tips

- **Flush stdout** after every response line — the runner waits for a complete line
- **One JSON object per line** — no pretty-printing in the protocol
- **Stderr is yours** — use it for debug logging without interfering with the protocol
- **State is per-session** — a new adapter process is started for each spec
- **Exact matching** — state values (names, IDs, costs) must match exactly
