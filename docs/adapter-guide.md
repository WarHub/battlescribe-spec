# Writing a BattleScribe Spec Adapter

This guide explains how to write an adapter that allows the BattleScribe Spec Test Kit
to verify your roster engine implementation.

## Overview

An adapter is a thin wrapper around your roster engine that speaks the
[JSON-line protocol](adapter-protocol.md) over stdin/stdout. The spec runner
sends commands (setup, setupFromFiles, action, getState, getErrors, teardown) and
your adapter responds with the corresponding results.

## Architecture

```
bs-spec  ──stdin──▸  your-adapter  ──▸  your engine
         ◂─stdout──                 ◂──
```

The CLI manages the adapter as a child process. Each line on stdin is a
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

The setup command provides the full game system, catalogues, and roster definition.
Your adapter must initialize the engine with this data and respond with a
`setupResult`. The optional `specId` field contains the spec test identifier;
adapters may use it to name the roster for debugging/observability.

```json
// Input
{"type":"setup","version":"1.0","specId":"cost-default-limit-positive","gameSystem":{...},"catalogues":[...]}

// Output
{"type":"setupResult","errors":[]}
```

### 3. Handle `action` Commands

Action commands trigger mutations on the roster. All addressing is **ID-based**:
definition IDs (e.g., `forceEntryId`, `entryId`) come from the setup data;
instance IDs (e.g., `forceId`, `selectionId`) are returned in action outputs.

| Action | Description |
|--------|-------------|
| `addForce` | Add a force by `forceEntryId`. Optional: `catalogueId`. Returns `forceId` and auto-selected `selections`. |
| `addChildForce` | Add a child force under `forceId` by `forceEntryId`. Returns `forceId`. |
| `removeForce` | Remove the force identified by `forceId` |
| `selectEntry` | Select an entry by `entryId` in the force `forceId`. Returns `selectionId`. |
| `selectChildEntry` | Select a child entry by `entryId` under `selectionId` in `forceId`. Returns `selectionId`. |
| `deselectSelection` | Deselect a selection by `forceId` and `selectionId` |
| `setSelectionCount` | Set quantity on `selectionId` in `forceId` with `count` |
| `duplicateSelection` | Duplicate a selection. Returns new `selectionId`. |
| `duplicateForce` | Duplicate a force (deep copy). Returns new `forceId`. Not supported by BattleScribe Java engine. |
| `setCostLimit` | Set a cost limit by `costTypeId` and `value` |

See [adapter-protocol.md](adapter-protocol.md#action--execute-roster-action) for the
full parameter reference and output fields.

```json
// Input — add a force, then select an entry using the returned forceId
{"type":"action","action":"addForce","forceEntryId":"fe-battalion"}
// Output — includes the force instance ID for use in subsequent actions
{"type":"actionResult","ok":true,"outputs":{"forceId":"abc-123"}}

// Input — use the returned forceId to select an entry
{"type":"action","action":"selectEntry","forceId":"abc-123","entryId":"se-infantry"}
// Output — includes the selection instance ID
{"type":"actionResult","ok":true,"outputs":{"selectionId":"sel-456"}}
```

### 4. Handle `getState` Command

Return the current roster state as a `state` response.

```json
// Input
{"type":"getState"}

// Output
{
  "type": "state",
  "name": "My Roster",
  "gameSystemId": "gs-1",
  "costs": [{"typeId":"pts","name":"Points","value":100}],
  "forces": [
    {
      "catalogueId": "cat-1",
      "name": "Battalion",
      "selections": [
        {
          "entryId": "entry-1",
          "name": "Commander",
          "type": "model",
          "number": 1,
          "hidden": false,
          "costs": [{"typeId":"pts","name":"Points","value":50}],
          "children": []
        }
      ]
    }
  ],
  "validationErrors": []
}
```

### 5. Handle `getErrors` Command

Return current validation errors.

```json
// Input
{"type":"getErrors"}

// Output
{"type":"errors","errors":[{"message":"Min 1 HQ required","ownerType":"category","ownerId":"cat-hq-id","ownerEntryId":"cat-hq","entryId":"se-unit","constraintId":"con-min-1"}]}
```

### 6. Handle `teardown` Command

Clean up any engine state.

```json
// Input
{"type":"teardown"}

// Output
{"type":"teardownResult"}
```

### 7. Error Handling

If your adapter encounters an error, return a `ProtocolError`:

```json
{"type":"error","message":"Unknown action: foobar"}
```

## Running the Spec Suite

### With the `bs-spec` CLI

```bash
# Build your adapter
# Then run:
dotnet bs-spec.dll run --all --engine "exec:/path/to/your-adapter" --specs specs --output summary
```

Give the engine a name if you want it to participate in `--expected-failures`
matrices or multi-engine comparisons:

```bash
dotnet bs-spec.dll run --all --engine "myengine=exec:/path/to/your-adapter" \
  --specs specs --workers 4 --output github-actions --report artifacts/myengine-conformance.json
```

### With Docker

```bash
docker run --rm -v /path/to/your-adapter:/adapter \
  bs-spec:local \
  run --all --engine "exec:/adapter/your-adapter" --specs /specs --output summary
```

### .NET Adapter Shortcut

If your adapter is a .NET assembly, use the `dotnet:` prefix:

```bash
dotnet bs-spec.dll run --all --engine "dotnet:your-adapter.dll" --specs specs
```

## Reference Implementation

See `src/BattleScribeSpec.ReferenceAdapter/` for a complete .NET adapter implementation
that wraps the BattleScribe engine. It's a small amount of code thanks to the
`AdapterHandler` helper class from the TestKit.

## Tips

- **Flush stdout** after every response line — the runner waits for a complete line
- **One JSON object per line** — no pretty-printing in the protocol
- **Stderr is yours** — use it for debug logging without interfering with the protocol
- **State is per-session** — a new adapter process is started for each spec
- **Exact matching** — state values (names, IDs, cost values) must match exactly. When `costs` or `costLimits` are asserted in `expectedState`, the assertion is an **exact set**: extra cost types beyond those listed will fail the assertion. The same is true for all list assertions (`forces`, `selections`, `profiles`, `rules`, `categories`, `publications`). Exception: `errorsContain` is explicitly a subset match, and individual `characteristics` within profiles are matched by name without requiring an exact set.

## DataSource Specs

Some specs use `dataSource` in their setup (e.g., `github:BSData/wh40k-10e@v10.6.0`) instead
of inline game system/catalogue XML. These specs load real-world BattleScribe data files.
Actions in dataSource specs use the same **ID-based** parameters (`forceEntryId`, `entryId`,
`catalogueId`, etc.) as inline specs — the IDs come from the BattleScribe XML data files.

DataSource specs are resolved by the test runner using `DataSourceResolver` and require the
engine to implement `SetupFromFiles(files)` from `IRosterEngine` — this loads raw
`.gst`/`.cat` XML files and is sent as a `setupFromFiles` protocol command.

The protocol adapter does not need special handling for DataSource specs beyond implementing
the `setupFromFiles` command — all data resolution is done by the runner before sending.
