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

await AdapterHandler.RunAsync(
    new AdapterOptions
    {
        RosterEngineFactory = () => new ForceFailRosterEngine(new BattleScribeRosterEngine()),
        GameDataEngineFactory = () => new ForceFailGameDataEngine(new BattleScribeGameDataEngine()),
        Name = "battlescribe",
        Version = typeof(BattleScribeRosterEngine).Assembly.GetName().Version?.ToString(),
    },
    input: Console.In,
    output: Console.Out);
