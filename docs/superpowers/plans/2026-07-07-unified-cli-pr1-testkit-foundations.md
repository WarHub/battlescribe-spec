# Unified CLI PR 1 — TestKit Foundations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the TestKit foundations for the unified `bs-spec` CLI (#271): adapter protocol v1.1 (`describe` + roster parity messages), gamedata over the NDJSON wire, engine connectables + registry, and the batch pipeline extracted from `bs-spec-runner` — with the Runner staying alive and green throughout.

**Architecture:** All changes are additive to `src/BattleScribeSpec.TestKit` (AOT-compatible, source-generated JSON only) plus small wiring in `src/BattleScribeSpec.ReferenceAdapter` and a slimming of `src/BattleScribeSpec.Runner/Program.cs` into a thin shell over the extracted pipeline. PR 2 (engine host + CLI rewire) and PR 3 (consumer migration + Runner deletion) build on these shapes and get their own plans.

**Tech Stack:** .NET 10, xunit (tests in `tests/BattleScribeSpec.Tests.csproj`), System.Text.Json source generation, NDJSON adapter protocol.

**Spec:** `docs/superpowers/specs/2026-07-07-unified-cli-design.md`

## Global Constraints

- Branch: `feat/271-unified-cli`. Work directly on it; commit after every task.
- TestKit has `IsAotCompatible=true` — new TestKit code MUST NOT use reflection-based `JsonSerializer` calls; register every serialized root type in a source-generated `JsonSerializerContext`.
- Always `dotnet build` before `dotnet test --no-build` (this repo's analyzers run as errors during build; `--no-build` after an edit runs a stale dll and your change silently doesn't apply).
- Test command shape: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "<filter>" --logger "console;verbosity=minimal"`.
- `bs-spec-runner` must build and behave identically after every task (it is deleted in PR 3, not here).
- Protocol rule: each message is one JSON object per line; the `type` field discriminates; unknown fields are ignored.
- Style: file-scoped namespaces, `sealed` classes, collection expressions (`[.. x]`, `[]`), XML doc comments on public API (see any TestKit file for the idiom).

---

### Task 1: Protocol v1.1 — `describe` messages

**Files:**
- Modify: `src/BattleScribeSpec.TestKit/Protocol/ProtocolMessages.cs`
- Modify: `src/BattleScribeSpec.TestKit/Protocol/ProtocolJsonContext.cs` (only if new root types aren't reachable from `ProtocolCommand`/`ProtocolResponse` — polymorphic derived types registered via attributes are reachable, so likely no change)
- Test: `tests/Features/ProtocolV11SerializationTests.cs` (new)

**Interfaces:**
- Consumes: existing `ProtocolCommand`/`ProtocolResponse` polymorphic base classes, `ProtocolSerializer`.
- Produces: `DescribeCommand` (type `"describe"`), `DescribeResult` (type `"describeResult"`) with `Name`, `Version`, `ProtocolVersion`, `Domains`, `Capabilities`; `AdapterCapabilities` with `Screenshot`, `Record`, `RosterXml`, `MaxParallel` (0 = unlimited). Task 2–6 and PR 2 depend on these exact names.

- [ ] **Step 1: Write the failing test**

Create `tests/Features/ProtocolV11SerializationTests.cs`:

```csharp
using BattleScribeSpec.Protocol;
using Xunit;

namespace BattleScribeSpec.Tests.Features;

public sealed class ProtocolV11SerializationTests
{
    [Fact]
    public void DescribeCommand_RoundTrips()
    {
        var json = ProtocolSerializer.SerializeCommand(new DescribeCommand());
        Assert.Contains("\"type\":\"describe\"", json);

        var command = ProtocolSerializer.DeserializeCommand(json);
        Assert.IsType<DescribeCommand>(command);
    }

    [Fact]
    public void DescribeResult_RoundTrips_WithCapabilities()
    {
        var result = new DescribeResult
        {
            Name = "battlescribe",
            Version = "2.03.29",
            Domains = ["roster", "gamedata"],
            Capabilities = new AdapterCapabilities { Screenshot = true, MaxParallel = 4 },
        };

        var json = ProtocolSerializer.SerializeResponse(result);
        Assert.Contains("\"type\":\"describeResult\"", json);

        var parsed = Assert.IsType<DescribeResult>(ProtocolSerializer.DeserializeResponse(json));
        Assert.Equal("battlescribe", parsed.Name);
        Assert.Equal("1.1", parsed.ProtocolVersion);
        Assert.Equal(["roster", "gamedata"], parsed.Domains);
        Assert.True(parsed.Capabilities.Screenshot);
        Assert.False(parsed.Capabilities.Record);
        Assert.Equal(4, parsed.Capabilities.MaxParallel);
    }

    [Fact]
    public void DescribeResult_Defaults_AreRosterOnlyNoCapabilities()
    {
        var parsed = Assert.IsType<DescribeResult>(
            ProtocolSerializer.DeserializeResponse("""{"type":"describeResult","name":"x"}"""));
        Assert.Equal(["roster"], parsed.Domains);
        Assert.False(parsed.Capabilities.Screenshot);
        Assert.Equal(0, parsed.Capabilities.MaxParallel);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet build 2>&1 | tail -5`
Expected: build FAILS — `DescribeCommand` does not exist (compile error is the failure mode for serialization tests).

- [ ] **Step 3: Add the message types**

In `src/BattleScribeSpec.TestKit/Protocol/ProtocolMessages.cs`, add to the `ProtocolCommand` polymorphic attribute list:

```csharp
[JsonDerivedType(typeof(DescribeCommand), "describe")]
```

and to the `ProtocolResponse` list:

```csharp
[JsonDerivedType(typeof(DescribeResult), "describeResult")]
```

Add after `TeardownCommand` (keep the `// ===== Runner → Adapter Commands =====` grouping):

```csharp
/// <summary>
/// Protocol v1.1: capability handshake. Sent once after process start; the adapter answers
/// with its identity, supported domains, and optional capabilities. Legacy v1.0 adapters
/// answer with an error — callers treat that as roster-only with no optional capabilities.
/// </summary>
public sealed class DescribeCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "describe";
}
```

Add after `ProtocolError` (in the responses section):

```csharp
/// <summary>Protocol v1.1: response to <see cref="DescribeCommand"/>.</summary>
public sealed class DescribeResult : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "describeResult";

    /// <summary>Engine identity (e.g. "battlescribe"); keys spec applicability and report labels.</summary>
    public string Name { get; set; } = "";

    /// <summary>Engine/adapter version, free-form.</summary>
    public string? Version { get; set; }

    public string ProtocolVersion { get; set; } = "1.1";

    /// <summary>Supported spec domains: "roster" and/or "gamedata".</summary>
    public List<string> Domains { get; set; } = ["roster"];

    public AdapterCapabilities Capabilities { get; set; } = new();
}

/// <summary>Optional protocol v1.1 capabilities advertised by <see cref="DescribeResult"/>.</summary>
public sealed class AdapterCapabilities
{
    public bool Screenshot { get; set; }

    public bool Record { get; set; }

    /// <summary>Supports <c>exportRosterXml</c>.</summary>
    public bool RosterXml { get; set; }

    /// <summary>Max concurrent instances the engine tolerates; 0 = unlimited.</summary>
    public int MaxParallel { get; set; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "FullyQualifiedName~ProtocolV11" --logger "console;verbosity=minimal"`
Expected: PASS (3 tests). If the build fails with a source-gen error about unregistered types, add `[JsonSerializable(typeof(DescribeResult))]` and `[JsonSerializable(typeof(DescribeCommand))]` to `ProtocolJsonContext.cs` — but polymorphic derived types should be picked up from the base-type registration.

- [ ] **Step 5: Commit**

```bash
git add src/BattleScribeSpec.TestKit/Protocol/ProtocolMessages.cs tests/Features/ProtocolV11SerializationTests.cs
git commit -m "feat(protocol): add v1.1 describe handshake messages (#271)"
```

---

### Task 2: `IAdapterConnection` seam + in-memory adapter test harness

**Files:**
- Modify: `src/BattleScribeSpec.TestKit/Protocol/AdapterProcess.cs`
- Create: `src/BattleScribeSpec.TestKit/Protocol/IAdapterConnection.cs`
- Modify: `src/BattleScribeSpec.TestKit/Protocol/JsonProtocolEngine.cs:11-19` (field + ctor type)
- Create: `tests/Infrastructure/InMemoryAdapterConnection.cs`
- Test: `tests/Features/AdapterHandlerTests.cs` (new)

**Interfaces:**
- Consumes: `AdapterProcess.SendCommandAsync(ProtocolCommand, CancellationToken)`, `AdapterHandler.RunAsync(Func<IRosterEngine>, TextReader, TextWriter, CancellationToken)`.
- Produces: `public interface IAdapterConnection { Task<ProtocolResponse> SendCommandAsync(ProtocolCommand command, CancellationToken ct = default); }`; `AdapterProcess : IAdapterConnection`; `JsonProtocolEngine` ctor takes `IAdapterConnection`. Test-side `InMemoryAdapterConnection` (namespace `BattleScribeSpec.Tests.Infrastructure`) constructed from an `AdapterHandler`-compatible run delegate. Tasks 3–6 use all of these.

- [ ] **Step 1: Create the seam interface**

Create `src/BattleScribeSpec.TestKit/Protocol/IAdapterConnection.cs`:

```csharp
namespace BattleScribeSpec.Protocol;

/// <summary>
/// A request/response channel to an adapter speaking the NDJSON protocol.
/// Implemented by <see cref="AdapterProcess"/> (child process) and by in-memory
/// test doubles; protocol engines depend on this instead of a concrete process.
/// </summary>
public interface IAdapterConnection
{
    /// <summary>Send a protocol command and await the single response.</summary>
    Task<ProtocolResponse> SendCommandAsync(ProtocolCommand command, CancellationToken ct = default);
}
```

In `AdapterProcess.cs` change the class declaration to:

```csharp
public sealed class AdapterProcess : IAdapterConnection, IDisposable
```

In `JsonProtocolEngine.cs` change the field and constructor parameter types from `AdapterProcess` to `IAdapterConnection` (the body is unchanged — it only calls `SendCommandAsync`):

```csharp
private readonly IAdapterConnection _adapter;

public JsonProtocolEngine(IAdapterConnection adapter, TimeSpan? requestTimeout = null)
```

- [ ] **Step 2: Verify nothing broke**

Run: `dotnet build`
Expected: build succeeds (callers pass `AdapterProcess`, which satisfies the interface).

- [ ] **Step 3: Write the in-memory harness + a smoke test proving it works**

Create `tests/Infrastructure/InMemoryAdapterConnection.cs`. It runs `AdapterHandler.RunAsync` on a background task, bridged by `Channel<string>`-backed `TextReader`/`TextWriter` shims:

```csharp
using System.Threading.Channels;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests.Infrastructure;

/// <summary>
/// Runs an adapter handler loop in-process and exposes it as an
/// <see cref="IAdapterConnection"/> — no child process, fully deterministic.
/// </summary>
public sealed class InMemoryAdapterConnection : IAdapterConnection, IAsyncDisposable
{
    private readonly Channel<string> _toAdapter = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _fromAdapter = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public InMemoryAdapterConnection(
        Func<TextReader, TextWriter, CancellationToken, Task> runHandler)
    {
        _loop = Task.Run(() => runHandler(
            new ChannelTextReader(_toAdapter.Reader),
            new ChannelTextWriter(_fromAdapter.Writer),
            _cts.Token));
    }

    public async Task<ProtocolResponse> SendCommandAsync(ProtocolCommand command, CancellationToken ct = default)
    {
        await _toAdapter.Writer.WriteAsync(ProtocolSerializer.SerializeCommand(command), ct);
        var line = await _fromAdapter.Reader.ReadAsync(ct);
        return ProtocolSerializer.DeserializeResponse(line)
            ?? throw new InvalidOperationException($"Bad response: {line}");
    }

    public async ValueTask DisposeAsync()
    {
        _toAdapter.Writer.TryComplete(); // handler loop sees end-of-input and exits
        await _loop.WaitAsync(TimeSpan.FromSeconds(5));
        _cts.Dispose();
    }

    private sealed class ChannelTextReader(ChannelReader<string> reader) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken ct)
        {
            try
            {
                return await reader.ReadAsync(ct);
            }
            catch (ChannelClosedException)
            {
                return null; // simulates stdin closing
            }
        }
    }

    private sealed class ChannelTextWriter(ChannelWriter<string> writer) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override async Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken ct = default)
            => await writer.WriteAsync(value.ToString(), ct);

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task FlushAsync() => Task.CompletedTask;
    }
}
```

Note: `AdapterHandler.RunAsync` only calls `input.ReadLineAsync(ct)`, `output.WriteLineAsync(ReadOnlyMemory<char>, ct)`, and `output.FlushAsync(ct)` — exactly the members overridden above. If the handler gains other calls later, the shims throw `NotSupportedException` from the base class and the test fails loudly.

Create `tests/Features/AdapterHandlerTests.cs` with a smoke test using the reference roster engine (already a test dependency via the BattleScribe project):

```csharp
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Tests.Infrastructure;
using Xunit;

namespace BattleScribeSpec.Tests.Features;

public sealed class AdapterHandlerTests
{
    private static InMemoryAdapterConnection Connect() => new(
        (input, output, ct) => AdapterHandler.RunAsync(
            () => new BattleScribeSpec.BattleScribeRosterEngine(), input, output, ct));

    [Fact]
    public async Task Setup_GetState_Teardown_RoundTrips()
    {
        await using var connection = Connect();

        var setup = await connection.SendCommandAsync(new SetupCommand
        {
            GameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" },
        });
        Assert.IsType<SetupResult>(setup);

        var state = await connection.SendCommandAsync(new GetStateCommand());
        Assert.IsType<StateResponse>(state);

        Assert.IsType<TeardownResult>(await connection.SendCommandAsync(new TeardownCommand()));
    }

    [Fact]
    public async Task LegacyHandler_AnswersDescribe_WithError()
    {
        await using var connection = Connect();

        // Task 3 replaces this expectation with a real DescribeResult; today the
        // legacy loop reports an unknown command — which is exactly the legacy
        // adapter behavior the describe fallback must tolerate.
        var response = await connection.SendCommandAsync(new DescribeCommand());
        Assert.IsType<ProtocolError>(response);
    }
}
```

Check the exact type name/namespace of the reference roster engine first (`grep -rn "class BattleScribeRosterEngine" src/BattleScribeSpec.BattleScribe/`) and adjust the `using`/qualification to match.

Caveat: deserializing an unknown `"type"` discriminator makes STJ throw inside the handler's try/catch, which yields `ProtocolError` — same observable result; the assertion holds either way.

- [ ] **Step 4: Run the tests**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "FullyQualifiedName~AdapterHandlerTests" --logger "console;verbosity=minimal"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/BattleScribeSpec.TestKit/Protocol/ tests/Infrastructure/InMemoryAdapterConnection.cs tests/Features/AdapterHandlerTests.cs
git commit -m "feat(protocol): IAdapterConnection seam + in-memory adapter test harness (#271)"
```

---

### Task 3: `AdapterHandler` options overload, `describe` dispatch, and client-side fallback

**Files:**
- Modify: `src/BattleScribeSpec.TestKit/Protocol/AdapterHandler.cs`
- Create: `src/BattleScribeSpec.TestKit/Protocol/AdapterDescriber.cs`
- Modify: `src/BattleScribeSpec.ReferenceAdapter/Program.cs`
- Test: extend `tests/Features/AdapterHandlerTests.cs`

**Interfaces:**
- Consumes: Task 1 message types, Task 2 harness.
- Produces:
  - `public sealed class AdapterOptions { required Func<IRosterEngine> RosterEngineFactory; Func<IGameDataEngine>? GameDataEngineFactory; string Name; string? Version; AdapterCapabilities Capabilities; }`
  - `AdapterHandler.RunAsync(AdapterOptions options, TextReader input, TextWriter output, CancellationToken ct = default)` — new primary overload; the existing `Func<IRosterEngine>` overload delegates to it with `Name = "unknown"`.
  - `public static class AdapterDescriber { public static Task<DescribeResult> DescribeAsync(IAdapterConnection connection, TimeSpan? timeout = null) }` — sends `describe`, returns the result, or a **legacy default** (`Name = ""`, `ProtocolVersion = "1.0"`, `Domains = ["roster"]`, empty capabilities) when the adapter answers with `ProtocolError` or an unparseable/timeout response.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Features/AdapterHandlerTests.cs`:

```csharp
    private static InMemoryAdapterConnection ConnectV11() => new(
        (input, output, ct) => AdapterHandler.RunAsync(
            new AdapterOptions
            {
                RosterEngineFactory = () => new BattleScribeSpec.BattleScribeRosterEngine(),
                Name = "battlescribe",
                Version = "test",
            },
            input, output, ct));

    [Fact]
    public async Task Describe_ReturnsIdentityAndDomains()
    {
        await using var connection = ConnectV11();

        var described = Assert.IsType<DescribeResult>(
            await connection.SendCommandAsync(new DescribeCommand()));
        Assert.Equal("battlescribe", described.Name);
        Assert.Equal("1.1", described.ProtocolVersion);
        Assert.Equal(["roster"], described.Domains); // no gamedata factory registered
    }

    [Fact]
    public async Task AdapterDescriber_FallsBack_OnLegacyAdapter()
    {
        await using var legacy = Connect(); // old overload — answers describe with an error

        var described = await AdapterDescriber.DescribeAsync(legacy);
        Assert.Equal("1.0", described.ProtocolVersion);
        Assert.Equal(["roster"], described.Domains);
        Assert.False(described.Capabilities.Screenshot);
    }

    [Fact]
    public async Task AdapterDescriber_ReturnsRealDescription_OnV11Adapter()
    {
        await using var connection = ConnectV11();

        var described = await AdapterDescriber.DescribeAsync(connection);
        Assert.Equal("battlescribe", described.Name);
        Assert.Equal("1.1", described.ProtocolVersion);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet build 2>&1 | tail -5`
Expected: build FAILS — `AdapterOptions` / `AdapterDescriber` do not exist.

- [ ] **Step 3: Implement**

In `AdapterHandler.cs`, add the options type and overload; the existing `RunAsync(Func<IRosterEngine>, …)` body moves into the new overload (leave `GameDataEngineFactory` unused until Task 6 — declare it now so the public shape is final):

```csharp
/// <summary>Configuration for <see cref="AdapterHandler.RunAsync(AdapterOptions, TextReader, TextWriter, CancellationToken)"/>.</summary>
public sealed class AdapterOptions
{
    public required Func<IRosterEngine> RosterEngineFactory { get; init; }

    /// <summary>Optional gamedata engine factory; when null, gamedata commands answer with an error.</summary>
    public Func<GameData.IGameDataEngine>? GameDataEngineFactory { get; init; }

    /// <summary>Engine identity reported by describe (e.g. "battlescribe").</summary>
    public string Name { get; init; } = "unknown";

    public string? Version { get; init; }

    public AdapterCapabilities Capabilities { get; init; } = new();
}
```

New overload (existing loop body moves here; the `switch` gains one arm):

```csharp
public static async Task RunAsync(
    AdapterOptions options, TextReader input, TextWriter output, CancellationToken ct = default)
```

with, inside the command `switch`:

```csharp
DescribeCommand => new DescribeResult
{
    Name = options.Name,
    Version = options.Version,
    Domains = options.GameDataEngineFactory is null ? ["roster"] : ["roster", "gamedata"],
    Capabilities = options.Capabilities,
},
```

Old overload becomes:

```csharp
public static Task RunAsync(
    Func<IRosterEngine> engineFactory, TextReader input, TextWriter output, CancellationToken ct = default)
    => RunAsync(new AdapterOptions { RosterEngineFactory = engineFactory }, input, output, ct);
```

Wait — the legacy-fallback test (Task 2) relies on the old overload NOT answering describe. Since the old overload now delegates, it WILL answer describe with `Name = "unknown"`. Fix the Task 2 test `LegacyHandler_AnswersDescribe_WithError` in the same commit: rename it to `Legacy_Fallback_Simulation_UsesErrorResponse` and simulate a legacy adapter explicitly instead:

```csharp
    [Fact]
    public async Task AdapterDescriber_FallsBack_OnErrorResponse()
    {
        // Simulate a legacy v1.0 adapter: a handler loop that answers everything with an error.
        await using var legacy = new InMemoryAdapterConnection(async (input, output, ct) =>
        {
            while (await input.ReadLineAsync(ct) is { } _)
            {
                await output.WriteLineAsync(ProtocolSerializer.SerializeResponse(
                    new ProtocolError { Message = "Unknown command" }).AsMemory(), ct);
                await output.FlushAsync(ct);
            }
        });

        var described = await AdapterDescriber.DescribeAsync(legacy);
        Assert.Equal("1.0", described.ProtocolVersion);
    }
```

(Replace the `AdapterDescriber_FallsBack_OnLegacyAdapter` test from Step 1 with this version, and delete Task 2's `LegacyHandler_AnswersDescribe_WithError`.)

Create `src/BattleScribeSpec.TestKit/Protocol/AdapterDescriber.cs`:

```csharp
namespace BattleScribeSpec.Protocol;

/// <summary>Client-side describe handshake with legacy-adapter fallback.</summary>
public static class AdapterDescriber
{
    /// <summary>
    /// Send <c>describe</c> and return the adapter's self-description. Adapters predating
    /// protocol v1.1 answer with an error (or nothing useful) — those get a legacy default:
    /// protocol 1.0, roster-only, no optional capabilities.
    /// </summary>
    public static async Task<DescribeResult> DescribeAsync(IAdapterConnection connection, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            var response = await connection.SendCommandAsync(new DescribeCommand(), cts.Token);
            if (response is DescribeResult described)
            {
                return described;
            }
        }
        catch (Exception)
        {
            // Legacy adapters may fail to parse the command entirely; fall through.
        }

        return new DescribeResult { Name = "", ProtocolVersion = "1.0", Domains = ["roster"] };
    }
}
```

Update `src/BattleScribeSpec.ReferenceAdapter/Program.cs` to use the options overload:

```csharp
await AdapterHandler.RunAsync(
    new AdapterOptions
    {
        RosterEngineFactory = () => new BattleScribeRosterEngine(),
        Name = "battlescribe",
        Version = typeof(BattleScribeRosterEngine).Assembly.GetName().Version?.ToString(),
    },
    input: Console.In,
    output: Console.Out);
```

- [ ] **Step 4: Run tests**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "FullyQualifiedName~AdapterHandlerTests" --logger "console;verbosity=minimal"`
Expected: PASS (5 tests).

- [ ] **Step 5: Update the protocol doc**

In `docs/adapter-protocol.md`: change the title version to `v1.1`, and add a `### describe — Capability Handshake (v1.1)` section under "Runner → Adapter Commands" documenting the command (`{"type":"describe"}`), the response shape:

```json
{"type":"describeResult","name":"battlescribe","version":"2.03.29","protocolVersion":"1.1","domains":["roster","gamedata"],"capabilities":{"screenshot":false,"record":false,"rosterXml":false,"maxParallel":0}}
```

and the legacy rule: *"Adapters predating v1.1 answer `describe` with an `error` response; runners MUST treat that as protocol 1.0, roster-only, no optional capabilities. Adapters SHOULD answer `describe`; all v1.1 messages are optional beyond it."*

- [ ] **Step 6: Commit**

```bash
git add src/BattleScribeSpec.TestKit/Protocol/ src/BattleScribeSpec.ReferenceAdapter/Program.cs tests/Features/AdapterHandlerTests.cs docs/adapter-protocol.md
git commit -m "feat(protocol): describe dispatch, AdapterOptions, client fallback (#271)"
```

---

### Task 4: Protocol v1.1 — roster parity messages (screenshot / exportRosterXml / record)

**Files:**
- Modify: `src/BattleScribeSpec.TestKit/Protocol/ProtocolMessages.cs`
- Modify: `src/BattleScribeSpec.TestKit/Protocol/AdapterHandler.cs`
- Modify: `src/BattleScribeSpec.TestKit/Protocol/JsonProtocolEngine.cs`
- Test: extend `tests/Features/ProtocolV11SerializationTests.cs` and `tests/Features/AdapterHandlerTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces:
  - Commands: `ScreenshotCommand` (`"screenshot"`), `ExportRosterXmlCommand` (`"exportRosterXml"`), `RecordStartCommand` (`"recordStart"`), `RecordStopCommand` (`"recordStop"`).
  - Responses: `ScreenshotResult` (`"screenshotResult"`, `string PngBase64`), `RosterXmlResult` (`"rosterXmlResult"`, `string Xml`), `RecordResult` (`"recordResult"`, `string? ActionsJson`).
  - `AdapterOptions` gains optional delegates: `Func<IRosterEngine, byte[]?>? ScreenshotProvider`, `Func<IRosterEngine, string?>? RosterXmlExporter`, `Action<IRosterEngine>? RecordStarter`, `Func<IRosterEngine, string?>? RecordStopper`. When null → `ProtocolError` `"<type> is not supported by this adapter"`.
  - `JsonProtocolEngine` gains: `byte[]? CaptureScreenshot()`, `string? ExportRosterXml()`, `void StartRecording()`, `string? StopRecording()` — each throws `NotSupportedException` mapped from the adapter's error response. PR 2's CLI capability gating calls these guarded by `DescribeResult.Capabilities`.

- [ ] **Step 1: Write failing serialization tests**

Add to `ProtocolV11SerializationTests`:

```csharp
    [Theory]
    [InlineData("""{"type":"screenshot"}""", typeof(ScreenshotCommand))]
    [InlineData("""{"type":"exportRosterXml"}""", typeof(ExportRosterXmlCommand))]
    [InlineData("""{"type":"recordStart"}""", typeof(RecordStartCommand))]
    [InlineData("""{"type":"recordStop"}""", typeof(RecordStopCommand))]
    public void ParityCommands_Deserialize(string json, Type expected)
        => Assert.IsType(expected, ProtocolSerializer.DeserializeCommand(json), exactMatch: true);

    [Fact]
    public void ParityResponses_RoundTrip()
    {
        Assert.Contains("\"pngBase64\":\"QUJD\"", ProtocolSerializer.SerializeResponse(
            new ScreenshotResult { PngBase64 = "QUJD" }));
        Assert.Contains("\"xml\":\"<roster/>\"", ProtocolSerializer.SerializeResponse(
            new RosterXmlResult { Xml = "<roster/>" }));
        Assert.IsType<RecordResult>(ProtocolSerializer.DeserializeResponse(
            """{"type":"recordResult","actionsJson":"[]"}"""));
    }
```

- [ ] **Step 2: Verify failure**

Run: `dotnet build 2>&1 | tail -5`
Expected: build FAILS — types don't exist.

- [ ] **Step 3: Implement messages, handler dispatch, engine methods**

`ProtocolMessages.cs` — register the four commands and three responses in the polymorphic attribute lists (discriminators as in Interfaces above), then add:

```csharp
/// <summary>Protocol v1.1 (optional): capture the engine UI as a PNG.</summary>
public sealed class ScreenshotCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "screenshot";
}

/// <summary>Protocol v1.1 (optional): export the current roster as .ros XML.</summary>
public sealed class ExportRosterXmlCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "exportRosterXml";
}

/// <summary>Protocol v1.1 (optional): start recording UI actions.</summary>
public sealed class RecordStartCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "recordStart";
}

/// <summary>Protocol v1.1 (optional): stop recording and return the recorded actions.</summary>
public sealed class RecordStopCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "recordStop";
}

public sealed class ScreenshotResult : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "screenshotResult";

    public string PngBase64 { get; set; } = "";
}

public sealed class RosterXmlResult : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "rosterXmlResult";

    public string Xml { get; set; } = "";
}

public sealed class RecordResult : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "recordResult";

    /// <summary>Recorded actions as a JSON array string; null when nothing was recorded.</summary>
    public string? ActionsJson { get; set; }
}
```

`AdapterOptions` — add the four optional delegates (see Interfaces). `AdapterHandler` switch — add four arms; each returns `ProtocolError { Message = "<type> is not supported by this adapter" }` when the delegate is null or `engine` is null, otherwise wraps the delegate:

```csharp
ScreenshotCommand => engine is not null && options.ScreenshotProvider?.Invoke(engine) is { } png
    ? new ScreenshotResult { PngBase64 = Convert.ToBase64String(png) }
    : new ProtocolError { Message = "screenshot is not supported by this adapter" },
ExportRosterXmlCommand => engine is not null && options.RosterXmlExporter?.Invoke(engine) is { } xml
    ? new RosterXmlResult { Xml = xml }
    : new ProtocolError { Message = "exportRosterXml is not supported by this adapter" },
RecordStartCommand => HandleRecordStart(options, engine),
RecordStopCommand => engine is not null && options.RecordStopper is not null
    ? new RecordResult { ActionsJson = options.RecordStopper(engine) }
    : new ProtocolError { Message = "recordStop is not supported by this adapter" },
```

with:

```csharp
private static ProtocolResponse HandleRecordStart(AdapterOptions options, IRosterEngine? engine)
{
    if (engine is null || options.RecordStarter is null)
    {
        return new ProtocolError { Message = "recordStart is not supported by this adapter" };
    }

    options.RecordStarter(engine);
    return new ActionResult { Ok = true };
}
```

`JsonProtocolEngine` — add:

```csharp
/// <summary>Protocol v1.1: capture a UI screenshot; throws NotSupportedException if the adapter can't.</summary>
public byte[] CaptureScreenshot() => SendCommand(new ScreenshotCommand()) switch
{
    ScreenshotResult sr => Convert.FromBase64String(sr.PngBase64),
    ProtocolError pe => throw new NotSupportedException(pe.Message),
    var other => throw new InvalidOperationException($"Unexpected response type: {other.Type}"),
};

/// <summary>Protocol v1.1: export the roster as .ros XML; throws NotSupportedException if unsupported.</summary>
public string ExportRosterXml() => SendCommand(new ExportRosterXmlCommand()) switch
{
    RosterXmlResult r => r.Xml,
    ProtocolError pe => throw new NotSupportedException(pe.Message),
    var other => throw new InvalidOperationException($"Unexpected response type: {other.Type}"),
};

/// <summary>Protocol v1.1: start UI action recording; throws NotSupportedException if unsupported.</summary>
public void StartRecording()
{
    var response = SendCommand(new RecordStartCommand());
    if (response is ProtocolError pe)
    {
        throw new NotSupportedException(pe.Message);
    }
}

/// <summary>Protocol v1.1: stop recording; returns the actions JSON (null if none). Throws NotSupportedException if unsupported.</summary>
public string? StopRecording() => SendCommand(new RecordStopCommand()) switch
{
    RecordResult r => r.ActionsJson,
    ProtocolError pe => throw new NotSupportedException(pe.Message),
    var other => throw new InvalidOperationException($"Unexpected response type: {other.Type}"),
};
```

- [ ] **Step 4: Add handler-level tests**

Add to `AdapterHandlerTests`:

```csharp
    [Fact]
    public async Task ParityCommands_WithoutProviders_AnswerNotSupported()
    {
        await using var connection = ConnectV11();
        await connection.SendCommandAsync(new SetupCommand
        {
            GameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" },
        });

        var error = Assert.IsType<ProtocolError>(await connection.SendCommandAsync(new ScreenshotCommand()));
        Assert.Contains("not supported", error.Message);
    }

    [Fact]
    public async Task Screenshot_WithProvider_ReturnsPng()
    {
        await using var connection = new InMemoryAdapterConnection(
            (input, output, ct) => AdapterHandler.RunAsync(
                new AdapterOptions
                {
                    RosterEngineFactory = () => new BattleScribeSpec.BattleScribeRosterEngine(),
                    Name = "battlescribe",
                    Capabilities = new AdapterCapabilities { Screenshot = true },
                    ScreenshotProvider = _ => [1, 2, 3],
                },
                input, output, ct));

        await connection.SendCommandAsync(new SetupCommand
        {
            GameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" },
        });
        var engine = new JsonProtocolEngine(connection);
        Assert.Equal([1, 2, 3], engine.CaptureScreenshot());
    }
```

- [ ] **Step 5: Run tests**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "FullyQualifiedName~ProtocolV11|FullyQualifiedName~AdapterHandlerTests" --logger "console;verbosity=minimal"`
Expected: PASS (all).

- [ ] **Step 6: Document + commit**

Add a `### Optional v1.1 commands` section to `docs/adapter-protocol.md` listing the four commands with request/response one-liner examples and the rule that unsupported → `error` response (runner maps to NotSupported).

```bash
git add src/BattleScribeSpec.TestKit/Protocol/ tests/Features/ docs/adapter-protocol.md
git commit -m "feat(protocol): v1.1 screenshot/exportRosterXml/record messages (#271)"
```

---

### Task 5: Gamedata protocol messages

**Files:**
- Modify: `src/BattleScribeSpec.TestKit/Protocol/ProtocolMessages.cs`
- Modify: `src/BattleScribeSpec.TestKit/Protocol/ProtocolJsonContext.cs`
- Test: extend `tests/Features/ProtocolV11SerializationTests.cs`

**Interfaces:**
- Consumes: `ProtocolGameSystem`/`ProtocolCatalogue` DTOs, `GameDataState` record tree (`BattleScribeSpec.GameData`).
- Produces (modeled 1:1 on the `IGameDataEngine` operation table in `docs/adapter-protocol.md`):
  - `GameDataSetupCommand` (`"gamedataSetup"`): `SpecId?`, `GameSystem`, `Catalogues`.
  - `GameDataActionCommand` (`"gamedataAction"`): `Action` ∈ `openFile|addEntry|addLink|removeEntry|setField|setCost|setCharacteristic|reload|exportFile|loadFile`; params `Id?` (declared/target id: openFile target, addEntry/addLink declared id), `ParentId?`, `EntryType?`, `Name?`, `EntryId?`, `Field?`, `Value?`, `TargetId?`, `LinkType?`, `CostTypeId?`, `NameOrTypeId?`, `Xml?` (loadFile payload). All strings.
  - `GameDataActionResult` (`"gamedataActionResult"`): `Ok`, `Error?`, `EntryId?` (addEntry/addLink output), `Xml?` (exportFile output), `Id?` (loadFile output).
  - `GameDataGetStateCommand` (`"gamedataGetState"`) → `GameDataStateResponse` (`"gamedataState"`) with `GameDataState State`.
  - `GameDataGetErrorsCommand` (`"gamedataGetErrors"`) → reuses `ErrorsResponse`.
- Task 6 (engine + handler) and PR 2 depend on these exact names.

- [ ] **Step 1: Write failing serialization tests**

Add to `ProtocolV11SerializationTests`:

```csharp
    [Fact]
    public void GameDataSetup_RoundTrips()
    {
        var json = ProtocolSerializer.SerializeCommand(new GameDataSetupCommand
        {
            SpecId = "spec-1",
            GameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" },
            Catalogues = [new ProtocolCatalogue { Id = "cat-1", Name = "Cat", GameSystemId = "gs" }],
        });
        Assert.Contains("\"type\":\"gamedataSetup\"", json);

        var parsed = Assert.IsType<GameDataSetupCommand>(ProtocolSerializer.DeserializeCommand(json));
        Assert.Equal("cat-1", Assert.Single(parsed.Catalogues).Id);
    }

    [Fact]
    public void GameDataAction_RoundTrips()
    {
        var json = ProtocolSerializer.SerializeCommand(new GameDataActionCommand
        {
            Action = "addEntry",
            ParentId = "cat-1",
            EntryType = "selectionEntry",
            Name = "Unit",
            Id = "declared-id",
        });
        var parsed = Assert.IsType<GameDataActionCommand>(ProtocolSerializer.DeserializeCommand(json));
        Assert.Equal("addEntry", parsed.Action);
        Assert.Equal("declared-id", parsed.Id);
    }

    [Fact]
    public void GameDataState_RoundTrips()
    {
        var response = new GameDataStateResponse
        {
            State = new BattleScribeSpec.GameData.GameDataState
            {
                GameSystem = new BattleScribeSpec.GameData.GameSystemDataState { Id = "gs", Name = "GS" },
            },
        };
        var json = ProtocolSerializer.SerializeResponse(response);
        Assert.Contains("\"type\":\"gamedataState\"", json);

        var parsed = Assert.IsType<GameDataStateResponse>(ProtocolSerializer.DeserializeResponse(json));
        Assert.Equal("gs", parsed.State.GameSystem!.Id);
    }
```

- [ ] **Step 2: Verify failure**

Run: `dotnet build 2>&1 | tail -5`
Expected: build FAILS — types don't exist.

- [ ] **Step 3: Implement the messages**

Register in the polymorphic lists: commands `gamedataSetup`, `gamedataAction`, `gamedataGetState`, `gamedataGetErrors`; responses `gamedataActionResult`, `gamedataState`. Add a `// ===== GameData protocol (v1.1) =====` section:

```csharp
/// <summary>
/// Protocol v1.1: initialize a gamedata (data-file editing) engine. The payload shapes
/// match roster <see cref="SetupCommand"/>, but the data IS the editable artifact.
/// </summary>
public sealed class GameDataSetupCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "gamedataSetup";

    public string? SpecId { get; set; }

    public ProtocolGameSystem GameSystem { get; set; } = new();

    public List<ProtocolCatalogue> Catalogues { get; set; } = [];
}

/// <summary>
/// Protocol v1.1: execute a data-editing action. Modeled 1:1 on the IGameDataEngine
/// operation table in docs/adapter-protocol.md.
/// </summary>
public sealed class GameDataActionCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "gamedataAction";

    /// <summary>openFile|addEntry|addLink|removeEntry|setField|setCost|setCharacteristic|reload|exportFile|loadFile.</summary>
    public string Action { get; set; } = "";

    /// <summary>openFile target id, or the declared id for addEntry/addLink.</summary>
    public string? Id { get; set; }

    public string? ParentId { get; set; }

    public string? EntryType { get; set; }

    public string? Name { get; set; }

    public string? EntryId { get; set; }

    public string? Field { get; set; }

    public string? Value { get; set; }

    public string? TargetId { get; set; }

    public string? LinkType { get; set; }

    public string? CostTypeId { get; set; }

    public string? NameOrTypeId { get; set; }

    /// <summary>loadFile: the BattleScribe XML payload.</summary>
    public string? Xml { get; set; }
}

public sealed class GameDataGetStateCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "gamedataGetState";
}

public sealed class GameDataGetErrorsCommand : ProtocolCommand
{
    [JsonIgnore]
    public override string Type => "gamedataGetErrors";
}

public sealed class GameDataActionResult : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "gamedataActionResult";

    public bool Ok { get; set; }

    public string? Error { get; set; }

    /// <summary>Created entry/link id (addEntry, addLink).</summary>
    public string? EntryId { get; set; }

    /// <summary>Exported XML (exportFile).</summary>
    public string? Xml { get; set; }

    /// <summary>Loaded file root id (loadFile).</summary>
    public string? Id { get; set; }
}

public sealed class GameDataStateResponse : ProtocolResponse
{
    [JsonIgnore]
    public override string Type => "gamedataState";

    public GameData.GameDataState State { get; set; } = new();
}
```

If the build then fails with STJ source-gen errors on the `GameDataState` graph (records with `IReadOnlyList<>`/`IReadOnlyDictionary<>` members), add `[JsonSerializable(typeof(GameData.GameDataState))]` to `ProtocolJsonContext.cs` and, if individual member types are reported, register those too (`GameSystemDataState`, `CatalogueDataState`, `DataEntryState` — check `src/BattleScribeSpec.TestKit/GameData/GameDataTypes.cs` for the full record list).

- [ ] **Step 4: Run tests**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "FullyQualifiedName~ProtocolV11" --logger "console;verbosity=minimal"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/BattleScribeSpec.TestKit/Protocol/ tests/Features/ProtocolV11SerializationTests.cs
git commit -m "feat(protocol): gamedata commands over the NDJSON wire (#271)"
```

---

### Task 6: `JsonProtocolGameDataEngine` + handler dispatch + reference adapter gamedata

**Files:**
- Create: `src/BattleScribeSpec.TestKit/Protocol/JsonProtocolGameDataEngine.cs`
- Modify: `src/BattleScribeSpec.TestKit/Protocol/AdapterHandler.cs`
- Modify: `src/BattleScribeSpec.ReferenceAdapter/Program.cs`
- Test: `tests/Features/GameDataProtocolTests.cs` (new)

**Interfaces:**
- Consumes: Task 5 messages, Task 2 harness, `IGameDataEngine` (`src/BattleScribeSpec.TestKit/GameData/IGameDataEngine.cs`), `BattleScribeGameDataEngine` (`src/BattleScribeSpec.BattleScribe/BattleScribeGameDataEngine.cs`), `GameDataRunner`.
- Produces: `public sealed class JsonProtocolGameDataEngine(IAdapterConnection adapter, TimeSpan? requestTimeout = null) : IGameDataEngine` — full mapping of the interface onto the wire. `AdapterHandler` dispatches gamedata commands to `AdapterOptions.GameDataEngineFactory`. The reference adapter serves both domains. PR 2's `run`/`verify`/batch gamedata-over-adapter path depends on this.

- [ ] **Step 1: Write the failing end-to-end test**

Create `tests/Features/GameDataProtocolTests.cs` — drive a real gamedata edit through the wire against the reference gamedata engine, then assert via `GetState`, `ExportActiveFile`, and the unsupported-path error. Check the reference engine's constructor signature first (`grep -n "public BattleScribeGameDataEngine" src/BattleScribeSpec.BattleScribe/BattleScribeGameDataEngine.cs`) and adjust construction if it takes arguments.

```csharp
using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Tests.Infrastructure;
using Xunit;

namespace BattleScribeSpec.Tests.Features;

public sealed class GameDataProtocolTests
{
    private static InMemoryAdapterConnection Connect(bool gamedata = true) => new(
        (input, output, ct) => AdapterHandler.RunAsync(
            new AdapterOptions
            {
                RosterEngineFactory = () => new BattleScribeSpec.BattleScribeRosterEngine(),
                GameDataEngineFactory = gamedata ? () => new BattleScribeSpec.BattleScribeGameDataEngine() : null,
                Name = "battlescribe",
            },
            input, output, ct));

    private static readonly ProtocolGameSystem GameSystem = new() { Id = "gs", Name = "GS" };

    private static readonly ProtocolCatalogue Catalogue = new()
    {
        Id = "cat-1",
        Name = "Cat",
        GameSystemId = "gs",
    };

    [Fact]
    public async Task AddEntry_SetField_GetState_OverTheWire()
    {
        await using var connection = Connect();
        using IGameDataEngine engine = new JsonProtocolGameDataEngine(connection);

        Assert.Empty(engine.Setup(GameSystem, [Catalogue]));
        engine.OpenFile("cat-1");

        var outputs = engine.AddEntry("cat-1", "selectionEntry", name: "Unit", id: "se-new");
        Assert.Equal("se-new", outputs.EntryId);

        engine.SetField("se-new", "name", "Renamed Unit");

        var state = engine.GetState();
        var catalogue = Assert.Single(state.Catalogues);
        Assert.Contains(catalogue.SelectionEntries, e => e.Name == "Renamed Unit");
    }

    [Fact]
    public async Task Describe_AdvertisesGamedataDomain()
    {
        await using var connection = Connect();
        var described = await AdapterDescriber.DescribeAsync(connection);
        Assert.Equal(["roster", "gamedata"], described.Domains);
    }

    [Fact]
    public async Task GamedataCommands_WithoutFactory_AnswerNotSupported()
    {
        await using var connection = Connect(gamedata: false);
        var response = await connection.SendCommandAsync(new GameDataSetupCommand { GameSystem = GameSystem });
        var error = Assert.IsType<ProtocolError>(response);
        Assert.Contains("gamedata", error.Message);
    }
}
```

Note: `Assert.Contains(catalogue.SelectionEntries, …)` assumes `CatalogueDataState.SelectionEntries` of `DataEntryState { Id, Name, … }` — check `GameDataTypes.cs` and match the real member names (the `GameSystemDataState` shown there has `SelectionEntries`; the catalogue record is analogous).

- [ ] **Step 2: Verify failure**

Run: `dotnet build 2>&1 | tail -5`
Expected: build FAILS — `JsonProtocolGameDataEngine` doesn't exist.

- [ ] **Step 3: Implement the client engine**

Create `src/BattleScribeSpec.TestKit/Protocol/JsonProtocolGameDataEngine.cs`:

```csharp
using BattleScribeSpec.GameData;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Protocol;

/// <summary>
/// IGameDataEngine implementation over the NDJSON adapter protocol (v1.1 gamedata commands).
/// Counterpart of <see cref="JsonProtocolEngine"/> for the data-editing domain.
/// </summary>
public sealed class JsonProtocolGameDataEngine : IGameDataEngine
{
    private readonly IAdapterConnection _adapter;
    private readonly TimeSpan _requestTimeout;
    private string? _specId;

    public JsonProtocolGameDataEngine(IAdapterConnection adapter, TimeSpan? requestTimeout = null)
    {
        _adapter = adapter;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
    }

    public void SetTestContext(string specId) => _specId = specId;

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        var response = SendCommand(new GameDataSetupCommand
        {
            SpecId = _specId,
            GameSystem = gameSystem,
            Catalogues = [.. catalogues],
        });
        return response switch
        {
            SetupResult sr => sr.Errors,
            ProtocolError pe => [pe.Message],
            _ => [$"Unexpected response type: {response.Type}"],
        };
    }

    public void OpenFile(string id) => SendAction(new GameDataActionCommand { Action = "openFile", Id = id });

    public GameDataActionOutputs AddEntry(string parentId, string entryType, string? name = null, string? id = null)
    {
        var result = SendAction(new GameDataActionCommand
        {
            Action = "addEntry",
            ParentId = parentId,
            EntryType = entryType,
            Name = name,
            Id = id,
        });
        return new GameDataActionOutputs { EntryId = result.EntryId };
    }

    public GameDataActionOutputs AddLink(string parentId, string linkType, string targetId, string? id = null)
    {
        var result = SendAction(new GameDataActionCommand
        {
            Action = "addLink",
            ParentId = parentId,
            LinkType = linkType,
            TargetId = targetId,
            Id = id,
        });
        return new GameDataActionOutputs { EntryId = result.EntryId };
    }

    public void RemoveEntry(string entryId) =>
        SendAction(new GameDataActionCommand { Action = "removeEntry", EntryId = entryId });

    public void SetField(string entryId, string field, string? value) =>
        SendAction(new GameDataActionCommand { Action = "setField", EntryId = entryId, Field = field, Value = value });

    public void SetCost(string entryId, string costTypeId, string? value) =>
        SendAction(new GameDataActionCommand { Action = "setCost", EntryId = entryId, CostTypeId = costTypeId, Value = value });

    public void SetCharacteristic(string entryId, string nameOrTypeId, string? value) =>
        SendAction(new GameDataActionCommand { Action = "setCharacteristic", EntryId = entryId, NameOrTypeId = nameOrTypeId, Value = value });

    public void Reload() => SendAction(new GameDataActionCommand { Action = "reload" });

    public string ExportActiveFile() =>
        SendAction(new GameDataActionCommand { Action = "exportFile" }).Xml
            ?? throw new InvalidOperationException("exportFile returned no xml.");

    public string LoadFile(string xml) =>
        SendAction(new GameDataActionCommand { Action = "loadFile", Xml = xml }).Id
            ?? throw new InvalidOperationException("loadFile returned no id.");

    public GameDataState GetState() => SendCommand(new GameDataGetStateCommand()) switch
    {
        GameDataStateResponse sr => sr.State,
        ProtocolError pe => throw new InvalidOperationException($"Adapter error: {pe.Message}"),
        var other => throw new InvalidOperationException($"Unexpected response type: {other.Type}"),
    };

    public IReadOnlyList<ValidationErrorState> GetValidationErrors() =>
        SendCommand(new GameDataGetErrorsCommand()) switch
        {
            ErrorsResponse er => er.Errors,
            ProtocolError pe => [new ValidationErrorState(pe.Message)],
            var other => [new ValidationErrorState($"Unexpected response type: {other.Type}")],
        };

    public void Dispose()
    {
        try
        {
            SendCommand(new TeardownCommand());
        }
        catch
        {
            // Best-effort teardown
        }
    }

    private GameDataActionResult SendAction(GameDataActionCommand command) => SendCommand(command) switch
    {
        GameDataActionResult { Ok: true } result => result,
        GameDataActionResult { Ok: false, Error: var error } =>
            throw new InvalidOperationException($"Action '{command.Action}' failed: {error}"),
        ProtocolError pe => throw new InvalidOperationException($"Adapter error: {pe.Message}"),
        var other => throw new InvalidOperationException($"Unexpected response type: {other.Type}"),
    };

    private ProtocolResponse SendCommand(ProtocolCommand command)
    {
        using var cts = new CancellationTokenSource(_requestTimeout);
        try
        {
            return _adapter.SendCommandAsync(command, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Adapter timed out after {_requestTimeout.TotalSeconds:0}s while handling '{command.Type}'.", ex);
        }
    }
}
```

- [ ] **Step 4: Implement handler dispatch**

In `AdapterHandler.RunAsync(AdapterOptions, …)`: hold `IGameDataEngine? gdEngine = null;` alongside the roster engine; dispose it in `finally` and on teardown. Add switch arms:

```csharp
GameDataSetupCommand gdSetup => HandleGameDataSetup(gdSetup, options, ref gdEngine),
GameDataActionCommand gdAction => HandleGameDataAction(gdAction, gdEngine),
GameDataGetStateCommand => gdEngine is null
    ? new ProtocolError { Message = "gamedata engine not initialized (call gamedataSetup first)" }
    : new GameDataStateResponse { State = gdEngine.GetState() },
GameDataGetErrorsCommand => gdEngine is null
    ? new ProtocolError { Message = "gamedata engine not initialized (call gamedataSetup first)" }
    : new ErrorsResponse { Errors = [.. gdEngine.GetValidationErrors()] },
```

(A `ref` local can't be used in a lambda/switch-expression capture the way roster setup does it — follow the existing pattern: roster setup already uses `ref engine` via a static method call from a switch *expression*, which compiles because the switch arms call static methods with `ref` parameters. Mirror that exactly.)

```csharp
private static ProtocolResponse HandleGameDataSetup(
    GameDataSetupCommand cmd, AdapterOptions options, ref GameData.IGameDataEngine? engine)
{
    if (options.GameDataEngineFactory is null)
    {
        return new ProtocolError { Message = "gamedata domain is not supported by this adapter" };
    }

    engine?.Dispose();
    engine = options.GameDataEngineFactory();
    if (cmd.SpecId is { Length: > 0 })
    {
        engine.SetTestContext(cmd.SpecId);
    }

    var errors = engine.Setup(cmd.GameSystem, [.. cmd.Catalogues]);
    return new SetupResult { Errors = [.. errors] };
}

private static ProtocolResponse HandleGameDataAction(GameDataActionCommand cmd, GameData.IGameDataEngine? engine)
{
    if (engine is null)
    {
        return new GameDataActionResult { Ok = false, Error = "gamedata engine not initialized (call gamedataSetup first)" };
    }

    try
    {
        var result = new GameDataActionResult { Ok = true };
        switch (cmd.Action)
        {
            case "openFile":
                engine.OpenFile(cmd.Id ?? throw new InvalidOperationException("openFile requires id"));
                break;
            case "addEntry":
                result.EntryId = engine.AddEntry(
                    cmd.ParentId ?? throw new InvalidOperationException("addEntry requires parentId"),
                    cmd.EntryType ?? throw new InvalidOperationException("addEntry requires entryType"),
                    cmd.Name,
                    cmd.Id).EntryId;
                break;
            case "addLink":
                result.EntryId = engine.AddLink(
                    cmd.ParentId ?? throw new InvalidOperationException("addLink requires parentId"),
                    cmd.LinkType ?? throw new InvalidOperationException("addLink requires linkType"),
                    cmd.TargetId ?? throw new InvalidOperationException("addLink requires targetId"),
                    cmd.Id).EntryId;
                break;
            case "removeEntry":
                engine.RemoveEntry(cmd.EntryId ?? throw new InvalidOperationException("removeEntry requires entryId"));
                break;
            case "setField":
                engine.SetField(
                    cmd.EntryId ?? throw new InvalidOperationException("setField requires entryId"),
                    cmd.Field ?? throw new InvalidOperationException("setField requires field"),
                    cmd.Value);
                break;
            case "setCost":
                engine.SetCost(
                    cmd.EntryId ?? throw new InvalidOperationException("setCost requires entryId"),
                    cmd.CostTypeId ?? throw new InvalidOperationException("setCost requires costTypeId"),
                    cmd.Value);
                break;
            case "setCharacteristic":
                engine.SetCharacteristic(
                    cmd.EntryId ?? throw new InvalidOperationException("setCharacteristic requires entryId"),
                    cmd.NameOrTypeId ?? throw new InvalidOperationException("setCharacteristic requires nameOrTypeId"),
                    cmd.Value);
                break;
            case "reload":
                engine.Reload();
                break;
            case "exportFile":
                result.Xml = engine.ExportActiveFile();
                break;
            case "loadFile":
                result.Id = engine.LoadFile(cmd.Xml ?? throw new InvalidOperationException("loadFile requires xml"));
                break;
            default:
                return new GameDataActionResult { Ok = false, Error = $"Unknown gamedata action: {cmd.Action}" };
        }

        return result;
    }
    catch (Exception ex)
    {
        return new GameDataActionResult { Ok = false, Error = ex.Message };
    }
}
```

(`GameDataActionResult` needs settable properties for this — they are `{ get; set; }` per Task 5.)

Update `AdapterOptions`-based describe arm: it already derives domains from `GameDataEngineFactory` (Task 3). Update `src/BattleScribeSpec.ReferenceAdapter/Program.cs` to pass `GameDataEngineFactory = () => new BattleScribeGameDataEngine()` (verify constructor args as noted in Step 1).

- [ ] **Step 5: Run tests**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "FullyQualifiedName~GameDataProtocolTests" --logger "console;verbosity=minimal"`
Expected: PASS (3 tests).

- [ ] **Step 6: Run the full offline suite (regression gate)**

Run: `dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "Category!=Conformance" --logger "console;verbosity=minimal"`
Expected: PASS, same counts as `main` (no regressions).

- [ ] **Step 7: Document + commit**

Rewrite the `## GameData Protocol (data-file editing)` section of `docs/adapter-protocol.md`: keep the `IGameDataEngine` operation table, replace the "GameData conformance is defined by the interface, not the wire" paragraph with the v1.1 statement that the NDJSON protocol now carries gamedata via `gamedataSetup` / `gamedataAction` / `gamedataGetState` / `gamedataGetErrors`, with one request/response JSON example per command. Note the JSON-RPC `gamedata*Action` wire remains the BS-UI Java agent's internal transport.

```bash
git add src/BattleScribeSpec.TestKit/Protocol/ src/BattleScribeSpec.ReferenceAdapter/Program.cs tests/Features/GameDataProtocolTests.cs docs/adapter-protocol.md
git commit -m "feat(protocol): JsonProtocolGameDataEngine + gamedata dispatch in AdapterHandler (#271)"
```

---

### Task 7: Engine connectables

**Files:**
- Create: `src/BattleScribeSpec.TestKit/Engines/EngineConnectable.cs`
- Test: `tests/Features/EngineConnectableTests.cs` (new)

**Interfaces:**
- Consumes: nothing.
- Produces: `public sealed record EngineConnectable(string? Name, string? Executable, string? Arguments)` with `public static EngineConnectable Parse(string input)` and `public bool IsLaunchable => Executable is not null`. Grammar:
  - `battlescribe-ui` → `(Name: "battlescribe-ui", null, null)` — registry lookup.
  - `exec:node adapters/wham.js` → `(null, "node", "adapters/wham.js")` — first whitespace splits executable from arguments.
  - `dotnet:path/adapter.dll` → `(null, "dotnet", "path/adapter.dll")` (mirrors the Runner's `dotnet:` convention).
  - `wham=exec:node adapters/wham.js` → `(Name: "wham", "node", "adapters/wham.js")`; same for `name=dotnet:…`.
  - A `name` is `^[a-zA-Z0-9][a-zA-Z0-9._-]*$`; the `name=` split happens only when the value after `=` starts with a known scheme (`exec:` or `dotnet:`). Unknown scheme (`foo:bar` where `foo` isn't a scheme and contains no `=`) → treated as a registry name if it matches the name regex, else `FormatException`.
  - Empty input, `exec:` with no command, `name=` with no connectable → `FormatException` with the offending input in the message.

- [ ] **Step 1: Write the failing tests**

Create `tests/Features/EngineConnectableTests.cs`:

```csharp
using BattleScribeSpec.Engines;
using Xunit;

namespace BattleScribeSpec.Tests.Features;

public sealed class EngineConnectableTests
{
    [Theory]
    [InlineData("battlescribe", "battlescribe")]
    [InlineData("newrecruit-ui", "newrecruit-ui")]
    [InlineData("wham", "wham")]
    public void PlainName_IsRegistryLookup(string input, string expectedName)
    {
        var connectable = EngineConnectable.Parse(input);
        Assert.Equal(expectedName, connectable.Name);
        Assert.False(connectable.IsLaunchable);
    }

    [Fact]
    public void Exec_SplitsExecutableAndArguments()
    {
        var connectable = EngineConnectable.Parse("exec:node adapters/wham.js --fast");
        Assert.Null(connectable.Name);
        Assert.Equal("node", connectable.Executable);
        Assert.Equal("adapters/wham.js --fast", connectable.Arguments);
    }

    [Fact]
    public void Exec_WithoutArguments_HasNullArguments()
    {
        var connectable = EngineConnectable.Parse("exec:./adapter");
        Assert.Equal("./adapter", connectable.Executable);
        Assert.Null(connectable.Arguments);
    }

    [Fact]
    public void Dotnet_IsSugarForDotnetExec()
    {
        var connectable = EngineConnectable.Parse("dotnet:artifacts/bin/adapter.dll");
        Assert.Null(connectable.Name);
        Assert.Equal("dotnet", connectable.Executable);
        Assert.Equal("artifacts/bin/adapter.dll", connectable.Arguments);
    }

    [Fact]
    public void NameEqualsConnectable_CarriesIdentityAndLaunch()
    {
        var connectable = EngineConnectable.Parse("battlescribe=dotnet:bs-reference-adapter.dll");
        Assert.Equal("battlescribe", connectable.Name);
        Assert.Equal("dotnet", connectable.Executable);
        Assert.Equal("bs-reference-adapter.dll", connectable.Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("exec:")]
    [InlineData("wham=")]
    [InlineData("wham=notascheme")]
    [InlineData("not a name")]
    public void Invalid_Throws(string input)
        => Assert.Throws<FormatException>(() => EngineConnectable.Parse(input));

    [Fact]
    public void ExecArguments_MayContainEquals()
    {
        var connectable = EngineConnectable.Parse("exec:node app.js --mode=fast");
        Assert.Equal("node", connectable.Executable);
        Assert.Equal("app.js --mode=fast", connectable.Arguments);
    }
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet build 2>&1 | tail -5`
Expected: build FAILS.

- [ ] **Step 3: Implement**

Create `src/BattleScribeSpec.TestKit/Engines/EngineConnectable.cs`:

```csharp
using System.Text.RegularExpressions;

namespace BattleScribeSpec.Engines;

/// <summary>
/// A parsed engine selector: a registry name (<c>battlescribe-ui</c>, <c>wham</c>),
/// an ad-hoc launchable (<c>exec:node adapter.js</c>, <c>dotnet:adapter.dll</c>),
/// or both (<c>battlescribe=dotnet:adapter.dll</c> — run THIS adapter AS that identity).
/// Inspired by bowtie connectables and the eshost host registry.
/// </summary>
public sealed partial record EngineConnectable(string? Name, string? Executable, string? Arguments)
{
    /// <summary>True when this connectable carries its own launch command.</summary>
    public bool IsLaunchable => Executable is not null;

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]*$")]
    private static partial Regex NamePattern();

    public static EngineConnectable Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new FormatException("Engine connectable must not be empty.");
        }

        // name=<scheme:...> — identity + launch.
        var eq = input.IndexOf('=');
        if (eq > 0 && NamePattern().IsMatch(input[..eq]))
        {
            var launch = ParseLaunch(input[(eq + 1)..]);
            if (launch is null)
            {
                throw new FormatException(
                    $"Invalid engine connectable '{input}': expected <name>=exec:<command> or <name>=dotnet:<dll>.");
            }

            return launch with { Name = input[..eq] };
        }

        if (ParseLaunch(input) is { } anonymous)
        {
            return anonymous;
        }

        if (NamePattern().IsMatch(input))
        {
            return new EngineConnectable(input, null, null);
        }

        throw new FormatException(
            $"Invalid engine connectable '{input}': expected an engine name, exec:<command>, dotnet:<dll>, or <name>=<connectable>.");
    }

    private static EngineConnectable? ParseLaunch(string input)
    {
        if (input.StartsWith("exec:", StringComparison.Ordinal))
        {
            var command = input[5..].Trim();
            if (command.Length == 0)
            {
                throw new FormatException("exec: connectable requires a command.");
            }

            var space = command.IndexOf(' ');
            return space < 0
                ? new EngineConnectable(null, command, null)
                : new EngineConnectable(null, command[..space], command[(space + 1)..].Trim());
        }

        if (input.StartsWith("dotnet:", StringComparison.Ordinal))
        {
            var dll = input[7..].Trim();
            if (dll.Length == 0)
            {
                throw new FormatException("dotnet: connectable requires a dll path.");
            }

            return new EngineConnectable(null, "dotnet", dll);
        }

        return null;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "FullyQualifiedName~EngineConnectableTests" --logger "console;verbosity=minimal"`
Expected: PASS (all cases; note `"wham=notascheme"` throws because after a valid name + `=`, a non-scheme remainder is an error).

- [ ] **Step 5: Commit**

```bash
git add src/BattleScribeSpec.TestKit/Engines/EngineConnectable.cs tests/Features/EngineConnectableTests.cs
git commit -m "feat(engines): connectable parsing (name / exec: / dotnet: / name=connectable) (#271)"
```

---

### Task 8: Engine registry + `engines.json`

**Files:**
- Create: `src/BattleScribeSpec.TestKit/Engines/EngineRegistry.cs`
- Create: `src/BattleScribeSpec.TestKit/Engines/EnginesConfigJsonContext.cs`
- Test: `tests/Features/EngineRegistryTests.cs` (new)

**Interfaces:**
- Consumes: Task 7 `EngineConnectable`.
- Produces:
  - `public sealed record EngineEntry(string? Name, string? Executable, string? Arguments, IReadOnlyList<string> Domains, int MaxParallel, bool Builtin)` — `Name` null only for anonymous ad-hoc connectables; `Executable` null for built-ins until PR 2 wires the engine host; `MaxParallel` 0 = unlimited.
  - `public sealed class EngineRegistry` with:
    - `public static EngineRegistry LoadDefault(string? startDirectory = null)` — walks up from `startDirectory` (default: current dir) looking for `engines.json`; returns built-ins-only registry when absent.
    - `public static EngineRegistry Load(string? configPath)` — explicit path (null → built-ins only).
    - `public EngineEntry Resolve(EngineConnectable connectable)` — resolution rules below.
    - `public IReadOnlyCollection<string> KnownNames { get; }`
  - Built-in names: `battlescribe`, `battlescribe-ui`, `newrecruit`, `newrecruit-ui` — all `Builtin: true`, `Domains: ["roster", "gamedata"]`, `MaxParallel: 1` for `battlescribe-ui`, 0 otherwise.
  - Resolution: launchable connectable → entry from the connectable (config metadata merged when `Name` matches a config entry); plain name → config entry, else built-in, else `KeyNotFoundException` listing `KnownNames`.
  - `engines.json` format:

```json
{
  "engines": {
    "wham": {
      "exec": "node adapters/wham.js",
      "domains": ["roster"],
      "maxParallel": 8
    }
  }
}
```

- [ ] **Step 1: Write the failing tests**

Create `tests/Features/EngineRegistryTests.cs`:

```csharp
using BattleScribeSpec.Engines;
using Xunit;

namespace BattleScribeSpec.Tests.Features;

public sealed class EngineRegistryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bsspec-registry-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_dir, "engines.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Builtins_AreAlwaysKnown()
    {
        var registry = EngineRegistry.Load(null);
        var entry = registry.Resolve(EngineConnectable.Parse("battlescribe-ui"));
        Assert.True(entry.Builtin);
        Assert.Equal(1, entry.MaxParallel);
        Assert.Contains("gamedata", entry.Domains);
    }

    [Fact]
    public void ConfigEngine_ResolvesWithLaunchAndMetadata()
    {
        var path = WriteConfig("""
            {"engines":{"wham":{"exec":"node adapters/wham.js","domains":["roster"],"maxParallel":8}}}
            """);
        var registry = EngineRegistry.Load(path);

        var entry = registry.Resolve(EngineConnectable.Parse("wham"));
        Assert.Equal("wham", entry.Name);
        Assert.Equal("node", entry.Executable);
        Assert.Equal("adapters/wham.js", entry.Arguments);
        Assert.Equal(["roster"], entry.Domains);
        Assert.Equal(8, entry.MaxParallel);
        Assert.False(entry.Builtin);
    }

    [Fact]
    public void UnknownName_ThrowsWithKnownNames()
    {
        var registry = EngineRegistry.Load(null);
        var ex = Assert.Throws<KeyNotFoundException>(
            () => registry.Resolve(EngineConnectable.Parse("phalanx")));
        Assert.Contains("battlescribe", ex.Message);
    }

    [Fact]
    public void AdHocLaunchable_ResolvesWithoutRegistry()
    {
        var registry = EngineRegistry.Load(null);
        var entry = registry.Resolve(EngineConnectable.Parse("exec:./my-adapter"));
        Assert.Null(entry.Name);
        Assert.Equal("./my-adapter", entry.Executable);
        Assert.Equal(["roster", "gamedata"], entry.Domains); // optimistic; describe narrows at runtime
    }

    [Fact]
    public void NameEqualsConnectable_OverridesConfigLaunch_KeepsMetadata()
    {
        var path = WriteConfig("""
            {"engines":{"wham":{"exec":"node old.js","domains":["roster"],"maxParallel":2}}}
            """);
        var registry = EngineRegistry.Load(path);

        var entry = registry.Resolve(EngineConnectable.Parse("wham=exec:node new.js"));
        Assert.Equal("wham", entry.Name);
        Assert.Equal("new.js", entry.Arguments);
        Assert.Equal(2, entry.MaxParallel); // metadata merged from config
    }

    [Fact]
    public void LoadDefault_FindsConfigInAncestorDirectory()
    {
        WriteConfig("""{"engines":{"wham":{"exec":"node w.js"}}}""");
        var nested = Directory.CreateDirectory(Path.Combine(_dir, "a", "b")).FullName;

        var registry = EngineRegistry.LoadDefault(nested);
        Assert.Equal("node", registry.Resolve(EngineConnectable.Parse("wham")).Executable);
    }
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet build 2>&1 | tail -5`
Expected: build FAILS.

- [ ] **Step 3: Implement**

Create `src/BattleScribeSpec.TestKit/Engines/EnginesConfigJsonContext.cs` (source-gen, AOT-safe):

```csharp
using System.Text.Json.Serialization;

namespace BattleScribeSpec.Engines;

/// <summary>engines.json config models + source-generated JSON context.</summary>
public sealed class EnginesConfig
{
    [JsonPropertyName("engines")]
    public Dictionary<string, EngineConfigEntry> Engines { get; set; } = [];
}

public sealed class EngineConfigEntry
{
    [JsonPropertyName("exec")]
    public string? Exec { get; set; }

    [JsonPropertyName("domains")]
    public List<string>? Domains { get; set; }

    [JsonPropertyName("maxParallel")]
    public int MaxParallel { get; set; }
}

[JsonSourceGenerationOptions(ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(EnginesConfig))]
public sealed partial class EnginesConfigJsonContext : JsonSerializerContext;
```

Create `src/BattleScribeSpec.TestKit/Engines/EngineRegistry.cs`:

```csharp
using System.Text.Json;

namespace BattleScribeSpec.Engines;

/// <summary>Resolved engine selection: identity + launch info + metadata.</summary>
/// <param name="Name">Registry identity (spec applicability, report labels); null for anonymous ad-hoc adapters.</param>
/// <param name="Executable">Launch executable; null for built-ins (resolved by the engine host integration).</param>
/// <param name="Arguments">Launch arguments; null when none.</param>
/// <param name="Domains">Spec domains the engine claims; the describe handshake narrows this at runtime.</param>
/// <param name="MaxParallel">Max concurrent instances; 0 = unlimited.</param>
/// <param name="Builtin">True for the in-box engines.</param>
public sealed record EngineEntry(
    string? Name,
    string? Executable,
    string? Arguments,
    IReadOnlyList<string> Domains,
    int MaxParallel,
    bool Builtin);

/// <summary>
/// Maps engine names to launch info: built-in entries plus optional repo-level
/// <c>engines.json</c> registrations (eshost-style named host registry).
/// </summary>
public sealed class EngineRegistry
{
    private static readonly string[] BothDomains = ["roster", "gamedata"];

    private static readonly Dictionary<string, EngineEntry> Builtins = new()
    {
        ["battlescribe"] = new("battlescribe", null, null, BothDomains, 0, Builtin: true),
        ["battlescribe-ui"] = new("battlescribe-ui", null, null, BothDomains, 1, Builtin: true),
        ["newrecruit"] = new("newrecruit", null, null, BothDomains, 0, Builtin: true),
        ["newrecruit-ui"] = new("newrecruit-ui", null, null, BothDomains, 0, Builtin: true),
    };

    private readonly Dictionary<string, EngineEntry> _configured;

    private EngineRegistry(Dictionary<string, EngineEntry> configured) => _configured = configured;

    public IReadOnlyCollection<string> KnownNames =>
        [.. _configured.Keys.Union(Builtins.Keys).Order()];

    /// <summary>Load from an explicit engines.json path; null → built-ins only.</summary>
    public static EngineRegistry Load(string? configPath)
    {
        if (configPath is null)
        {
            return new EngineRegistry([]);
        }

        var config = JsonSerializer.Deserialize(
            File.ReadAllText(configPath), EnginesConfigJsonContext.Default.EnginesConfig)
            ?? throw new InvalidDataException($"Invalid engines config: {configPath}");

        var configured = new Dictionary<string, EngineEntry>();
        foreach (var (name, entry) in config.Engines)
        {
            var launch = entry.Exec is { Length: > 0 }
                ? EngineConnectable.Parse($"exec:{entry.Exec}")
                : null;
            configured[name] = new EngineEntry(
                name,
                launch?.Executable,
                launch?.Arguments,
                entry.Domains is { Count: > 0 } ? [.. entry.Domains] : BothDomains,
                entry.MaxParallel,
                Builtin: false);
        }

        return new EngineRegistry(configured);
    }

    /// <summary>Walk up from <paramref name="startDirectory"/> looking for engines.json.</summary>
    public static EngineRegistry LoadDefault(string? startDirectory = null)
    {
        var dir = new DirectoryInfo(startDirectory ?? Directory.GetCurrentDirectory());
        for (; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "engines.json");
            if (File.Exists(candidate))
            {
                return Load(candidate);
            }
        }

        return new EngineRegistry([]);
    }

    /// <summary>Resolve a parsed connectable to a full entry (see class doc for rules).</summary>
    public EngineEntry Resolve(EngineConnectable connectable)
    {
        if (connectable.IsLaunchable)
        {
            // Ad-hoc launch; merge metadata when the identity is a configured name.
            var metadata = connectable.Name is not null && _configured.TryGetValue(connectable.Name, out var known)
                ? known
                : null;
            return new EngineEntry(
                connectable.Name,
                connectable.Executable,
                connectable.Arguments,
                metadata?.Domains ?? BothDomains,
                metadata?.MaxParallel ?? 0,
                Builtin: false);
        }

        var name = connectable.Name!;
        if (_configured.TryGetValue(name, out var configured))
        {
            return configured;
        }

        if (Builtins.TryGetValue(name, out var builtin))
        {
            return builtin;
        }

        throw new KeyNotFoundException(
            $"Unknown engine '{name}'. Known engines: {string.Join(", ", KnownNames)}.");
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "FullyQualifiedName~EngineRegistryTests" --logger "console;verbosity=minimal"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/BattleScribeSpec.TestKit/Engines/ tests/Features/EngineRegistryTests.cs
git commit -m "feat(engines): registry with builtins and engines.json config (#271)"
```

---

### Task 9: Extract the batch pipeline into TestKit; slim the Runner to a shell

**Files:**
- Create: `src/BattleScribeSpec.TestKit/Batch/SpecSuiteOptions.cs`
- Create: `src/BattleScribeSpec.TestKit/Batch/SpecSuiteRunner.cs`
- Create: `src/BattleScribeSpec.TestKit/Batch/SpecSuiteOutput.cs`
- Create: `src/BattleScribeSpec.TestKit/Batch/SuiteJsonContext.cs` (absorbs `JsonRunReport`/`JsonSpecEntry` + json context from the Runner)
- Modify: `src/BattleScribeSpec.Runner/Program.cs` (becomes arg-parsing shell)
- Delete: `src/BattleScribeSpec.Runner/RunnerJsonContext.cs`
- Test: `tests/Features/SpecSuiteRunnerTests.cs` (new)

**Interfaces:**
- Consumes: everything the Runner uses today (`SpecLoader`, `TagFilter`, `RosterRunner`, `JsonProtocolEngine`, `AdapterProcess`, `SpecResultSummary`, `ConformanceReport`) — all already in TestKit — plus Task 2's `IAdapterConnection`.
- Produces (PR 2's `run --all` consumes exactly these):

```csharp
namespace BattleScribeSpec.Batch;

public sealed class SpecSuiteOptions
{
    /// <summary>Specs directory; null → SpecLoader.FindRosterSpecsDirectory() then embedded fallback.</summary>
    public string? SpecsDirectory { get; init; }
    public IReadOnlyList<string>? FilterPatterns { get; init; }
    public TagFilter? TagFilter { get; init; }
    public string? EngineFilter { get; init; }
    public string? ExpectedFailuresEngine { get; init; }
    public string? AssertionEngine { get; init; }
    public int Workers { get; init; } = 1;
    /// <summary>Creates one adapter process per worker. Disposed by the runner.</summary>
    public required Func<AdapterProcess> AdapterFactory { get; init; }
}

public sealed class SpecSuiteResult
{
    public required IReadOnlyList<SpecResult> Results { get; init; }
    public required IReadOnlyList<SpecResultSummary> ReportResults { get; init; }
    public required IReadOnlyDictionary<SpecResult, SpecFile> SpecsByResult { get; init; }
    public required int TotalSpecs { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public int Passed { get; }        // computed in ctor-like factory; see implementation note
    public int Failed { get; }
    public int ExpectedFailures { get; }
    public int UnexpectedPasses { get; }
    public int ExitCode => Failed > 0 ? 1 : 0;
}

public static class SpecSuiteRunner
{
    public static Task<SpecSuiteResult> RunAsync(SpecSuiteOptions options, TextWriter? progressWriter = null);
}

public static class SpecSuiteOutput
{
    public static void WriteSummary(SpecSuiteResult result, string? engineLabel, TextWriter output);
    public static void WriteJson(SpecSuiteResult result, string? engineLabel, TextWriter output);
    public static void WriteGitHubActions(SpecSuiteResult result, string? engineLabel, TextWriter output);
    public static void WriteConformanceReport(SpecSuiteResult result, string path, string? engineFilter, string? assertionEngine, TextWriter console);
}
```

- [ ] **Step 1: Extract the pipeline (behavior-preserving move)**

This is a refactor-move of `src/BattleScribeSpec.Runner/Program.cs`, not a rewrite. Mapping:

| Runner `Program.cs` lines | Destination |
|---|---|
| 106–138 (spec discovery incl. embedded fallback) | `SpecSuiteRunner.RunAsync` — discovery phase |
| 160–220 (pre-filtering: filter/tags/engine, load errors, skip records) | `SpecSuiteRunner.RunAsync` — filter phase |
| 222–320 (sequential + `--workers` parallel execution, status classification) | `SpecSuiteRunner.RunAsync` — execution phase |
| 325–356 (passed/failed/xfail/upass counting) | `SpecSuiteResult` factory (`SpecSuiteResult.Create(...)` internal static) |
| 381–464 (`OutputSummary`/`OutputJson`/`OutputGitHubActions`) | `SpecSuiteOutput` (take `TextWriter output` parameter instead of `Console`) |
| 500–550 (`OutputConformanceReport`) | `SpecSuiteOutput.WriteConformanceReport` |
| `RunnerJsonContext.cs` (whole file) | `src/BattleScribeSpec.TestKit/Batch/SuiteJsonContext.cs`, types made `public`, context named `SuiteJsonContext` |

Transformation rules during the move:
- `adapterExe`/`adapterArgs` + `AdapterProcess.Start(...)` calls → `options.AdapterFactory()` (the shell keeps the `dotnet:` splitting — Task 7's `EngineConnectable.Parse` does it: `EngineConnectable.Parse(adapter.Contains(':') && !File.Exists(adapter) ? adapter : $"exec:{adapter}")` is WRONG — keep it simple and faithful: the shell parses `dotnet:` prefix exactly as today (lines 146–156) and builds `() => AdapterProcess.Start(adapterExe, adapterArgs)`).
- `Console.Error.WriteLine($"Running {n} specs with {workers} workers...")` → `progressWriter?.WriteLine(...)`.
- Local capture variables (`passed`, `failed`, `expectedFailureCount`, `unexpectedPassCount`) become computed properties on `SpecSuiteResult` via an internal static factory `Create(results, reportResults, specsByResult, totalSpecs, elapsed, expectedFailuresEngine)` that runs the lines 325–356 logic once and stores the counts.
- The parallel path (lines 222–291) keeps its `Channel`-based process pool verbatim, with `AdapterProcess.Start` replaced by `options.AdapterFactory()`.
- `--matrix` mode (lines 72–90) does NOT move — it is 15 lines over the already-shared `CompatibilityMatrix`; the Runner shell keeps it (PR 2 gives `run --matrix` its own copy of those 15 lines against the same TestKit API).

- [ ] **Step 2: Rewrite the Runner shell**

`src/BattleScribeSpec.Runner/Program.cs` keeps: the arg-parsing `for` loop (lines 8–70), `--matrix` handling (72–90), `dotnet:` adapter splitting (146–156), `PrintUsage` (466–498). Everything between becomes:

```csharp
var result = await SpecSuiteRunner.RunAsync(
    new SpecSuiteOptions
    {
        SpecsDirectory = specsDir,
        FilterPatterns = filterPatterns,
        TagFilter = tagFilter,
        EngineFilter = engineFilter,
        ExpectedFailuresEngine = expectedFailuresEngine,
        AssertionEngine = assertionEngine,
        Workers = workers,
        AdapterFactory = () => AdapterProcess.Start(adapterExe, adapterArgs),
    },
    progressWriter: Console.Error);

switch (output)
{
    case "json":
        SpecSuiteOutput.WriteJson(result, engineFilter, Console.Out);
        break;
    case "github-actions":
        SpecSuiteOutput.WriteGitHubActions(result, engineFilter, Console.Out);
        break;
    default:
        SpecSuiteOutput.WriteSummary(result, engineFilter, Console.Out);
        break;
}

if (reportPath is not null)
{
    SpecSuiteOutput.WriteConformanceReport(result, reportPath, engineFilter, assertionEngine, Console.Out);
}

return result.ExitCode;
```

Note: today the Runner validates the specs directory and errors when no specs are found (lines 110–138); move those checks into `SpecSuiteRunner.RunAsync` as thrown `InvalidOperationException`s and have the shell catch them: `catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }` around the whole block — same messages, same exit code.

- [ ] **Step 3: Write the pipeline test**

Create `tests/Features/SpecSuiteRunnerTests.cs` — the suite runner against the reference adapter *in-process* is not possible (it needs `AdapterProcess`), so test the pure parts plus a filtered end-to-end run against the built reference adapter dll, marked as integration:

```csharp
using BattleScribeSpec.Batch;
using BattleScribeSpec.Protocol;
using Xunit;

namespace BattleScribeSpec.Tests.Features;

public sealed class SpecSuiteRunnerTests
{
    private static string FindAdapterDll()
    {
        // Tests run from artifacts/bin/BattleScribeSpec.Tests/<pivot>/ — walk up to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BattleScribeSpec.slnx")))
        {
            dir = dir.Parent!;
        }

        Assert.NotNull(dir);
        var dll = Path.Combine(dir.FullName, "artifacts", "bin",
            "BattleScribeSpec.ReferenceAdapter", "debug", "bs-reference-adapter.dll");
        Assert.True(File.Exists(dll), $"Reference adapter not built: {dll}");
        return dll;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FilteredSuite_RunsAgainstReferenceAdapter()
    {
        var dll = FindAdapterDll();

        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            FilterPatterns = ["protocol/protocol-kitchen-sink"],
            EngineFilter = "battlescribe",
            ExpectedFailuresEngine = "battlescribe",
            AssertionEngine = "battlescribe",
            AdapterFactory = () => AdapterProcess.Start("dotnet", dll),
        });

        Assert.True(result.TotalSpecs > 0);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.ReportResults, r => r.Status == "passed");
    }

    [Fact]
    public async Task MissingSpecsDirectory_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            SpecsDirectory = Path.Combine(Path.GetTempPath(), "does-not-exist-bsspec"),
            AdapterFactory = () => throw new UnreachableException(),
        }));
    }
}
```

(Add `using System.Diagnostics;` for `UnreachableException`. Check how existing integration-flavored tests are categorized — `grep -rn "Trait(\"Category\"" tests/ | head` — and match the convention so CI placement is right; the offline CI lane runs `Category!=Conformance`, so `Integration` runs there and needs the adapter built, which `dotnet build` at solution level guarantees.)

- [ ] **Step 4: Build + run tests**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "FullyQualifiedName~SpecSuiteRunnerTests" --logger "console;verbosity=minimal"`
Expected: PASS (2 tests).

- [ ] **Step 5: Runner parity check (the real gate for this task)**

Run the CI invocation shape before/after and compare:

```bash
dotnet artifacts/bin/BattleScribeSpec.Runner/debug/bs-spec-runner.dll \
  --adapter "dotnet:artifacts/bin/BattleScribeSpec.ReferenceAdapter/debug/bs-reference-adapter.dll" \
  --specs specs/roster \
  --filter "protocol/protocol-kitchen-sink,category/" \
  --engine battlescribe \
  --expected-failures battlescribe \
  --output summary \
  --workers 2; echo "exit: $?"
```

Expected: identical "Results: N passed, M failed…" line and exit code as the same command run on `main` (run it on `main` first via `git stash` or note the counts from the latest CI run). Also verify `--output json` still serializes: pipe to `head -c 300` and eyeball the shape, and `--report /tmp/r.json` writes the conformance report with the same summary line.

- [ ] **Step 6: Full offline suite**

Run: `dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "Category!=Conformance" --logger "console;verbosity=minimal"`
Expected: PASS, no regressions.

- [ ] **Step 7: Commit**

```bash
git add src/BattleScribeSpec.TestKit/Batch/ src/BattleScribeSpec.Runner/ tests/Features/SpecSuiteRunnerTests.cs
git commit -m "refactor(runner): extract batch pipeline into TestKit SpecSuiteRunner (#271)"
```

---

### Task 10: PR polish — AOT analyzer check, docs cross-links, push, open PR

**Files:**
- Modify: `docs/adapter-protocol.md` (verify Tasks 3/4/6 sections read coherently as one v1.1 story)
- Modify: `README.md` (only if it names protocol v1.0 explicitly — check with `grep -n "1.0" README.md`)

**Interfaces:** none — verification and delivery.

- [ ] **Step 1: AOT analyzer sanity**

TestKit's `IsAotCompatible=true` makes trim/AOT analyzer warnings build **warnings** (elevated per repo settings). Confirm zero new warnings:

Run: `dotnet build src/BattleScribeSpec.TestKit/BattleScribeSpec.TestKit.csproj -warnaserror 2>&1 | tail -5`
Expected: build succeeds. If IL2026/IL3050 appear, the offending call is a reflection-based `JsonSerializer` use — route it through `ProtocolJsonContext`/`SuiteJsonContext`/`EnginesConfigJsonContext` instead.

- [ ] **Step 2: Full build + full offline suite, once, from clean**

Run: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "Category!=Conformance" --logger "console;verbosity=minimal"`
Expected: PASS.

- [ ] **Step 3: Re-read the diff with fresh eyes**

Run: `git diff main --stat` then `git diff main -- docs/`
Check: protocol doc tells one coherent v1.1 story (describe → optional commands → gamedata); no stray TODOs; public API has XML docs.

- [ ] **Step 4: Push and open the PR**

```bash
git push -u origin feat/271-unified-cli
gh pr create --title "feat: TestKit foundations for unified bs-spec CLI (#271, PR 1/3)" --body "$(cat <<'EOF'
Part of #271 (PR 1 of 3 — see docs/superpowers/specs/2026-07-07-unified-cli-design.md).

- Adapter protocol v1.1: `describe` capability handshake (legacy v1.0 fallback), optional `screenshot`/`exportRosterXml`/`recordStart`/`recordStop`
- Gamedata over the NDJSON wire: `gamedataSetup`/`gamedataAction`/`gamedataGetState`/`gamedataGetErrors` + `JsonProtocolGameDataEngine`; reference adapter now serves both domains
- Engine connectables (`exec:`, `dotnet:`, `name=connectable`) + registry with `engines.json` config
- Batch pipeline extracted from `bs-spec-runner` into TestKit `SpecSuiteRunner`/`SpecSuiteOutput`; Runner is now a thin arg-parsing shell with identical behavior (parity-checked against the CI invocation)

PR 2 will add `bs-engine-host` + rewire the CLI over the protocol; PR 3 migrates CI/docker/docs and deletes the Runner.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 5: Verify CI is green**

Watch the PR checks (`gh pr checks --watch`). The fast lane must pass including the two `bs-spec-runner` invocations — they exercise the extracted pipeline end-to-end.
