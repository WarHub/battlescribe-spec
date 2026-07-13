using System.Diagnostics;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using BattleScribeSpec.Telemetry;
using BattleScribeSpec.Tests.Infrastructure;

namespace BattleScribeSpec.Tests.Features;

public sealed class AdapterHandlerTests
{
    private static InMemoryAdapterConnection Connect() => new(
        (input, output, ct) => AdapterHandler.RunAsync(
            () => new BattleScribeSpec.BattleScribeRosterEngine(), input, output, ct));

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
    public async Task Setup_GetState_Teardown_RoundTrips()
    {
        await using var connection = Connect();

        var ct = TestContext.Current.CancellationToken;

        var setup = await connection.SendCommandAsync(new SetupCommand
        {
            GameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" },
        }, ct);
        Assert.IsType<SetupResult>(setup);

        var state = await connection.SendCommandAsync(new GetStateCommand(), ct);
        Assert.IsType<StateResponse>(state);

        Assert.IsType<TeardownResult>(await connection.SendCommandAsync(new TeardownCommand(), ct));
    }

    [Fact]
    public async Task Describe_ReturnsIdentityAndDomains()
    {
        await using var connection = ConnectV11();

        var described = Assert.IsType<DescribeResult>(
            await connection.SendCommandAsync(new DescribeCommand(), TestContext.Current.CancellationToken));
        Assert.Equal("battlescribe", described.Name);
        Assert.Equal("1.1", described.ProtocolVersion);
        Assert.Equal(["roster"], described.Domains); // no gamedata factory registered
    }

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

    [Fact]
    public async Task AdapterDescriber_ReturnsRealDescription_OnV11Adapter()
    {
        await using var connection = ConnectV11();

        var described = await AdapterDescriber.DescribeAsync(connection);
        Assert.Equal("battlescribe", described.Name);
        Assert.Equal("1.1", described.ProtocolVersion);
    }

    [Fact]
    public async Task ParityCommands_WithoutProviders_AnswerNotSupported()
    {
        await using var connection = ConnectV11();
        await connection.SendCommandAsync(new SetupCommand
        {
            GameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" },
        }, TestContext.Current.CancellationToken);

        var error = Assert.IsType<ProtocolError>(
            await connection.SendCommandAsync(new ScreenshotCommand(), TestContext.Current.CancellationToken));
        Assert.Contains("not supported", error.Message);
    }

    /// <summary>
    /// Code-review follow-up (final whole-branch review): the SERVER-side command span used to be
    /// declared as `using var activity = ...` INSIDE the per-command try, so a throwing handler
    /// disposed the span during unwind before the catch could mark it — it recorded no error
    /// status at all, and every failed adapter command rendered exactly like a success in any
    /// backend (Jaeger/Tempo) that renders by span status. This pins that a genuinely-throwing
    /// command handler (not one that catches its own exception and returns Ok=false) now marks the
    /// span <see cref="ActivityStatusCode.Error"/>, mirroring <see cref="HarnessTelemetryTests"/>'s
    /// coverage of the spec-level span for the same property.
    /// </summary>
    [Fact]
    public async Task ServerSpan_RecordsErrorStatus_WhenTheCommandHandlerThrows()
    {
        var isThisTest = new AsyncLocal<bool>();
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == HarnessTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (isThisTest.Value)
                {
                    lock (captured)
                    {
                        captured.Add(activity);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        // IsThisTest must be set BEFORE the connection is constructed: InMemoryAdapterConnection's
        // handler loop is a Task.Run captured at construction time (see ResourceMetricsTests for
        // the same requirement), so noise from any concurrently-running, unrelated test on the
        // same process-wide ActivitySource is excluded rather than breaking an exact-count assertion.
        isThisTest.Value = true;
        var connection = new InMemoryAdapterConnection(
            (input, output, ct) => AdapterHandler.RunAsync(
                () => new CountingRosterEngine { ThrowOnSetup = true }, input, output, ct));

        var setup = await connection.SendCommandAsync(new SetupCommand
        {
            GameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" },
        }, TestContext.Current.CancellationToken);
        Assert.IsType<ProtocolError>(setup);

        await connection.DisposeAsync();
        isThisTest.Value = false;

        // connection.DisposeAsync() above already drains the adapter loop's Task to completion,
        // which sequences after every command's finally block (including the activity dispose) —
        // so the span is guaranteed present here. Still routed through SpanWait (rather than
        // Assert.Single) so the whole suite uses one idiom for span assertions; see SpanWait's
        // XML docs for why that idiom exists.
        var setupSpan = await SpanWait.ForAsync(captured, a => a.OperationName == "setup", TestContext.Current.CancellationToken);
        Assert.Equal(ActivityStatusCode.Error, setupSpan.Status);
        Assert.Single(captured, a => a.OperationName == "setup"); // exactly one, not merely at-least-one
    }

    /// <summary>
    /// Companion to <see cref="ServerSpan_RecordsErrorStatus_WhenTheCommandHandlerThrows"/>: a
    /// NON-throwing failure (a switch arm returning <see cref="ProtocolError"/> directly, e.g. an
    /// unsupported command) must mark the span exactly the same way — that path never touches the
    /// catch block at all, so hoisting `activity` alone would not have been sufficient.
    /// </summary>
    [Fact]
    public async Task ServerSpan_RecordsErrorStatus_WhenTheHandlerReturnsProtocolErrorWithoutThrowing()
    {
        var isThisTest = new AsyncLocal<bool>();
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == HarnessTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (isThisTest.Value)
                {
                    lock (captured)
                    {
                        captured.Add(activity);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        isThisTest.Value = true;
        var connection = ConnectV11(); // no ScreenshotProvider registered -> ProtocolError, no throw

        await connection.SendCommandAsync(new SetupCommand
        {
            GameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" },
        }, TestContext.Current.CancellationToken);
        var screenshot = await connection.SendCommandAsync(new ScreenshotCommand(), TestContext.Current.CancellationToken);
        Assert.IsType<ProtocolError>(screenshot);

        await connection.DisposeAsync();
        isThisTest.Value = false;

        // See ServerSpan_RecordsErrorStatus_WhenTheCommandHandlerThrows above for why
        // connection.DisposeAsync() already guarantees both spans are present here, and why
        // SpanWait is still used for a single consistent idiom across the suite.
        var ct = TestContext.Current.CancellationToken;
        var screenshotSpan = await SpanWait.ForAsync(captured, a => a.OperationName == "screenshot", ct);
        Assert.Equal(ActivityStatusCode.Error, screenshotSpan.Status);
        Assert.Single(captured, a => a.OperationName == "screenshot"); // exactly one, not merely at-least-one

        // Baseline: the setup call in the same run genuinely succeeded and must stay Unset —
        // proof this isn't just marking every span red regardless of outcome.
        var setupSpan = await SpanWait.ForAsync(captured, a => a.OperationName == "setup", ct);
        Assert.Equal(ActivityStatusCode.Unset, setupSpan.Status);
        Assert.Single(captured, a => a.OperationName == "setup"); // exactly one, not merely at-least-one
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
        }, TestContext.Current.CancellationToken);
        var engine = new JsonProtocolEngine(connection);
        Assert.Equal([1, 2, 3], engine.CaptureScreenshot());
    }

    [Fact]
    public async Task Reuse_KeepsOneEngine_AcrossSetupTeardownCycles()
    {
        CountingRosterEngine? created = null;
        var factoryCalls = 0;
        var connection = new InMemoryAdapterConnection(
            (input, output, ct) => AdapterHandler.RunAsync(
                new AdapterOptions
                {
                    RosterEngineFactory = () => { factoryCalls++; return created = new CountingRosterEngine(); },
                    Name = "newrecruit-ui",
                    ReuseRosterEngineAcrossSetups = true,
                },
                input, output, ct));

        var ct = TestContext.Current.CancellationToken;
        var gs = new ProtocolGameSystem { Id = "gs", Name = "GS" };

        // Two specs: setup → teardown → setup → teardown, on the same connection (same host loop).
        Assert.IsType<SetupResult>(await connection.SendCommandAsync(new SetupCommand { GameSystem = gs }, ct));
        Assert.IsType<TeardownResult>(await connection.SendCommandAsync(new TeardownCommand(), ct));
        Assert.IsType<SetupResult>(await connection.SendCommandAsync(new SetupCommand { GameSystem = gs }, ct));
        Assert.IsType<TeardownResult>(await connection.SendCommandAsync(new TeardownCommand(), ct));

        await connection.DisposeAsync();

        Assert.Equal(1, factoryCalls);            // engine created ONCE, not per spec
        Assert.Equal(2, created!.SetupCalls);     // Setup ran for both specs on the same instance
        Assert.Equal(2, created.CleanupCalls);    // reset between/after specs (per teardown)
        Assert.Equal(1, created.DisposeCalls);    // disposed once, at process end
    }

    [Fact]
    public async Task NoReuse_DisposesAndRecreates_PerSetup()
    {
        var engines = new List<CountingRosterEngine>();
        var connection = new InMemoryAdapterConnection(
            (input, output, ct) => AdapterHandler.RunAsync(
                new AdapterOptions
                {
                    RosterEngineFactory = () => { var e = new CountingRosterEngine(); engines.Add(e); return e; },
                    Name = "battlescribe",
                    // ReuseRosterEngineAcrossSetups / ReuseGameDataEngineAcrossSetups default false
                },
                input, output, ct));

        var ct = TestContext.Current.CancellationToken;
        var gs = new ProtocolGameSystem { Id = "gs", Name = "GS" };

        await connection.SendCommandAsync(new SetupCommand { GameSystem = gs }, ct);
        await connection.SendCommandAsync(new TeardownCommand(), ct);
        await connection.SendCommandAsync(new SetupCommand { GameSystem = gs }, ct);
        await connection.SendCommandAsync(new TeardownCommand(), ct);

        await connection.DisposeAsync();

        Assert.Equal(2, engines.Count);                       // recreated per setup
        Assert.All(engines, e => Assert.Equal(1, e.DisposeCalls)); // each disposed on its teardown
        Assert.All(engines, e => Assert.Equal(0, e.CleanupCalls)); // no warm reset when reuse is off
    }

    [Fact]
    public async Task Reuse_SelfHeals_WhenCleanupThrows()
    {
        var engines = new List<CountingRosterEngine>();
        var connection = new InMemoryAdapterConnection(
            (input, output, ct) => AdapterHandler.RunAsync(
                new AdapterOptions
                {
                    RosterEngineFactory = () => { var e = new CountingRosterEngine { ThrowOnCleanup = true }; engines.Add(e); return e; },
                    Name = "newrecruit-ui",
                    ReuseRosterEngineAcrossSetups = true,
                },
                input, output, ct));

        var ct = TestContext.Current.CancellationToken;
        var gs = new ProtocolGameSystem { Id = "gs", Name = "GS" };

        await connection.SendCommandAsync(new SetupCommand { GameSystem = gs }, ct);
        await connection.SendCommandAsync(new TeardownCommand(), ct);   // Cleanup throws → engine disposed
        await connection.SendCommandAsync(new SetupCommand { GameSystem = gs }, ct); // must recreate

        await connection.DisposeAsync();

        Assert.Equal(2, engines.Count);                 // reset failure forced a fresh engine
        Assert.Equal(1, engines[0].CleanupCalls);       // attempted reset
        Assert.Equal(1, engines[0].DisposeCalls);       // then disposed (self-heal)
    }

    [Fact]
    public async Task PerDomainFlags_AreIndependent()
    {
        var rosterEngines = new List<CountingRosterEngine>();
        var connection = new InMemoryAdapterConnection(
            (input, output, ct) => AdapterHandler.RunAsync(
                new AdapterOptions
                {
                    RosterEngineFactory = () => { var e = new CountingRosterEngine(); rosterEngines.Add(e); return e; },
                    Name = "battlescribe-ui",
                    ReuseRosterEngineAcrossSetups = false,      // roster stays cold
                    ReuseGameDataEngineAcrossSetups = true,     // gamedata warm (no gd factory here → roster-only proof)
                },
                input, output, ct));

        var ct = TestContext.Current.CancellationToken;
        var gs = new ProtocolGameSystem { Id = "gs", Name = "GS" };
        await connection.SendCommandAsync(new SetupCommand { GameSystem = gs }, ct);
        await connection.SendCommandAsync(new TeardownCommand(), ct);
        await connection.SendCommandAsync(new SetupCommand { GameSystem = gs }, ct);
        await connection.SendCommandAsync(new TeardownCommand(), ct);
        await connection.DisposeAsync();

        Assert.Equal(2, rosterEngines.Count);                      // roster recreated (reuse flag false for its domain)
        Assert.All(rosterEngines, e => Assert.Equal(1, e.DisposeCalls));
        Assert.All(rosterEngines, e => Assert.Equal(0, e.CleanupCalls));
    }

    private sealed class CountingRosterEngine : IRosterEngine
    {
        public int SetupCalls { get; private set; }
        public int CleanupCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool ThrowOnCleanup { get; init; }
        public bool ThrowOnSetup { get; init; }

        public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
        {
            SetupCalls++;
            if (ThrowOnSetup)
            {
                throw new InvalidOperationException("setup boom");
            }

            return [];
        }

        public void Cleanup()
        {
            CleanupCalls++;
            if (ThrowOnCleanup)
            {
                throw new InvalidOperationException("cleanup boom");
            }
        }

        public void Dispose() => DisposeCalls++;

        public ActionOutputs AddForce(string forceEntryId, string catalogueId)
            => new() { ForceId = "force-1" };

        public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId)
            => new() { ForceId = "child-force-1" };

        public void RemoveForce(string forceId)
        {
        }

        public ActionOutputs SelectEntry(string forceId, string entryId)
            => new() { SelectionId = "sel-1" };

        public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId)
            => new() { SelectionId = "child-sel-1" };

        public void DeselectSelection(string forceId, string selectionId)
        {
        }

        public void SetSelectionCount(string forceId, string selectionId, int count)
        {
        }

        public ActionOutputs DuplicateSelection(string forceId, string selectionId)
            => new() { SelectionId = "dup-sel-1" };

        public ActionOutputs DuplicateForce(string forceId)
            => new() { ForceId = "dup-force-1" };

        public void SetCostLimit(string costTypeId, decimal value)
        {
        }

        public RosterState GetRosterState() => new("roster", "gs", [], [], []);

        public IReadOnlyList<ValidationErrorState> GetValidationErrors() => [];
    }
}
