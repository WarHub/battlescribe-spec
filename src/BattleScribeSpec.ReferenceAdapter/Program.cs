using BattleScribeSpec;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.ReferenceAdapter;

// BattleScribe conformance spec reference adapter.
// Wraps the BattleScribe Java engine (via IKVM) with the JSON-line protocol.
// Usage: dotnet run -- (reads from stdin, writes to stdout)
//
// Roster/GameData engines are wrapped in the ForceFail* test hooks so BSSPEC_TEST_FORCE_FAIL can
// make this adapter deliberately diverge from another run's verdicts (see ForceFailEngines.cs) —
// used to red-test bs-spec compare's verdict-equality assertion. Unset, the hook is a no-op pass-through.
// BSSPEC_TEST_ROSTER_ONLY=1 makes it a roster-only adapter (see RosterOnlyHook in the same file).

// The protocol is UTF-8 without a byte-order mark on stdin, stdout and stderr (docs/adapter-protocol.md).
// Console.In/Console.Out below are re-opened that way here whenever they are pipes, which they are
// whenever a client drives this adapter; see Utf8Stdio for what goes wrong when only one end says so.
Utf8Stdio.UseForRedirectedStreams();

await AdapterHandler.RunAsync(
    new AdapterOptions
    {
        RosterEngineFactory = () => new ForceFailRosterEngine(new BattleScribeRosterEngine()),
        // Null under BSSPEC_TEST_ROSTER_ONLY=1, so describe omits "gamedata" (RosterOnlyHook).
        GameDataEngineFactory = RosterOnlyHook.IsSet ? null : () => new ForceFailGameDataEngine(new BattleScribeGameDataEngine()),
        Name = "battlescribe",
        Version = typeof(BattleScribeRosterEngine).Assembly.GetName().Version?.ToString(),

        // This adapter wraps BattleScribeRosterEngine, which exports. Leaving the exporter unwired
        // (a null RosterXmlExporter is AdapterHandler's "unsupported" signal) made every roster
        // `expectedFile` byte-compare a silent no-op on this path too: ProtocolError ->
        // JsonProtocolEngine's NotSupportedException -> RosterRunner.ExecuteFileAssertion catches
        // and returns, passing the step. CI drives this adapter as the `battlescribe` identity
        // (`--engine "battlescribe=dotnet:…/bs-reference-adapter.dll"`), so the assertions the
        // kitchen-sink spec carries were never actually running there. Same fix as ServeCommand.
        Capabilities = new AdapterCapabilities { RosterXml = true },
        RosterXmlExporter = static engine =>
        {
            try
            {
                return engine.ExportRosterXml();
            }
            catch (NotSupportedException)
            {
                // Null means "the engine genuinely does not offer this" — the one answer the runner
                // may ignore. Every other failure propagates and becomes a loud ProtocolError.
                return null;
            }
        },
    },
    input: Console.In,
    output: Console.Out);
