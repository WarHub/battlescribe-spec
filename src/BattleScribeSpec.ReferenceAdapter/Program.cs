using BattleScribeSpec;
using BattleScribeSpec.Protocol;

// BattleScribe conformance spec reference adapter.
// Wraps the BattleScribe Java engine (via IKVM) with the JSON-line protocol.
// Usage: dotnet run -- (reads from stdin, writes to stdout)

await AdapterHandler.RunAsync(
    engineFactory: () => new OracleRosterEngine(),
    input: Console.In,
    output: Console.Out);
