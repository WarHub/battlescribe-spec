using BattleScribeSpec;
using BattleScribeSpec.Protocol;

// BattleScribe conformance spec reference adapter.
// Wraps the BattleScribe Java engine (via IKVM) with the JSON-line protocol.
// Usage: dotnet run -- (reads from stdin, writes to stdout)

await AdapterHandler.RunAsync(
    new AdapterOptions
    {
        RosterEngineFactory = () => new BattleScribeRosterEngine(),
        GameDataEngineFactory = () => new BattleScribeGameDataEngine(),
        Name = "battlescribe",
        Version = typeof(BattleScribeRosterEngine).Assembly.GetName().Version?.ToString(),
    },
    input: Console.In,
    output: Console.Out);
