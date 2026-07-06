using System.Runtime.Loader;
using BattleScribeSpec;
using BattleScribeSpec.Protocol;

// BattleScribe conformance spec reference adapter.
// Wraps the BattleScribe Java engine (via IKVM) with the JSON-line protocol.
// Usage: dotnet run -- (reads from stdin, writes to stdout)

// IKVM-compiled assemblies aren't in deps.json for transitive project references.
// Resolve them from the app directory so Assembly.Load("DataUtils") etc. succeed.
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};

await AdapterHandler.RunAsync(
    new AdapterOptions
    {
        RosterEngineFactory = () => new BattleScribeRosterEngine(),
        Name = "battlescribe",
        Version = typeof(BattleScribeRosterEngine).Assembly.GetName().Version?.ToString(),
    },
    input: Console.In,
    output: Console.Out);
