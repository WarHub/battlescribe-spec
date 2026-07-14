# Writing a BattleScribe Spec Adapter

This guide explains how to write an adapter that allows the BattleScribe Spec Test Kit
to verify your roster engine implementation.

## Overview

An adapter is a thin wrapper around your roster engine that speaks the
[JSON-line protocol](adapter-protocol.md) over stdin/stdout. `bs-spec`
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
  --specs specs --output github-actions --report artifacts/myengine-conformance.json
```

The worker count is chosen by the harness's `ConcurrencyPolicy` (machine + what the engine declares),
clamped by the `maxParallel` your `describe` response advertises — there is no `--workers` flag to
set. Note `--policy` cannot be delivered to an `exec:`/`dotnet:` adapter at all (there is no channel
for it, and the CLI errors rather than silently dropping it); `describe`'s `maxParallel` is how a
launchable adapter states its own ceiling.

**`maxParallel` is a ceiling on adapter PROCESSES, and on nothing else.** It does not bound the
harness's in-process browser-context pool — a separate axis, sized by `contextPoolSize`, with its own
ceiling (`maxContexts`) in `engines.json`. The two were briefly the same number, which meant
`{"maxParallel": 2, "contextPoolSize": 4}` — "don't run more than 2 of my processes", exactly what
this paragraph says that field means — silently halved the pool. Declare the axis you mean.

**But read this before you tune `maxParallel`: it is probably not what is binding you.** An adapter
the harness does not recognise has an *undeclared endpoint*, and an undeclared endpoint is treated as
**a third party's live service** — so both axes are held to `ConcurrencyPolicy.ThirdPartyLiveLoadLimit`
(2), whatever your `describe` advertises. That is deliberate: guessing "local" for an executable we
have never seen spends *someone else's* production capacity, and the harness will not do that on an
assumption. It costs you wall-clock, and you take it back with one line:

```jsonc
// engines.json, at the repo root
{
  "engines": {
    "myengine": {
      "exec": "/path/to/your-adapter",
      "endpoint": "local",            // "my service runs on this machine" — the opt-in to full width
      "maxParallel": 0,               // 0 = unlimited: let the policy size the PROCESS axis
      "memPerInstanceBytes": 0        // declare a measured footprint to lift the conservative cap
    }
  }
}
```

`"endpoint"` takes `"local"`, `"third-party-live"` (you drive someone else's production site — please
say so), or `"url-var:NAME"` (live iff `NAME` holds a non-loopback URL, which is how the built-in
NewRecruit engines work via `NR_ENGINE_URL`). If your run prints *"Load target: third-party live
service — held to 2 concurrent sessions"* and it is not, this is the line you are missing.

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

## Distributed Tracing (Optional)

Every command your adapter receives may carry two optional string fields, `traceparent` and
`tracestate` — a [W3C Trace Context](https://www.w3.org/TR/trace-context/) header pair. You can
ignore them completely and remain fully conformant; a single `bs-spec`/`bs-engine-host` run just
won't show your adapter's own work nested in its trace.

If your adapter already uses (or wants to use) OpenTelemetry, you get correct span nesting with
**no bs-spec-specific code** by:

1. Reading `OTEL_EXPORTER_OTLP_ENDPOINT` from your process environment and pointing your
   language's stock OTel SDK at it (the harness sets this on the child process it spawns).
2. Using the command's `traceparent`/`tracestate` as the parent context (via your SDK's normal
   W3C propagator) before starting your own span to handle the command.

That's it — the harness's spec span, its per-command CLIENT span, and your adapter's spans all
land in the same distributed trace, in any language, because everyone is speaking the same W3C
standard. See [adapter-protocol.md](adapter-protocol.md#distributed-tracing-traceparent--tracestate)
for the full rationale, including why the built-in adapter uses CLIENT/SERVER span kinds rather
than Internal (it's what lets Jaeger/Tempo draw the `bs-spec → adapter` edge at all).

## Tips

- **Flush stdout** after every response line — the client waits for a complete line
- **One JSON object per line** — no pretty-printing in the protocol
- **Stderr is yours** — use it for debug logging without interfering with the protocol
- **State is per-session** — a new adapter process is started for each spec
- **Exact matching** — state values (names, IDs, cost values) must match exactly. When `costs` or `costLimits` are asserted in `expectedState`, the assertion is an **exact set**: extra cost types beyond those listed will fail the assertion. The same is true for all list assertions (`forces`, `selections`, `profiles`, `rules`, `categories`, `publications`). Exception: `errorsContain` is explicitly a subset match, and individual `characteristics` within profiles are matched by name without requiring an exact set.

## DataSource Specs

Some specs use `dataSource` in their setup (e.g., `github:BSData/wh40k-10e@v10.6.0`) instead
of inline game system/catalogue XML. These specs load real-world BattleScribe data files.
Actions in dataSource specs use the same **ID-based** parameters (`forceEntryId`, `entryId`,
`catalogueId`, etc.) as inline specs — the IDs come from the BattleScribe XML data files.

DataSource specs are resolved by the client using `DataSourceResolver` and require the
engine to implement `SetupFromFiles(files)` from `IRosterEngine` — this loads raw
`.gst`/`.cat` XML files and is sent as a `setupFromFiles` protocol command.

The protocol adapter does not need special handling for DataSource specs beyond implementing
the `setupFromFiles` command — all data resolution is done by the client before sending.
