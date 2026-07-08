using System.CommandLine;
using System.Runtime.Loader;
using BattleScribeSpec.EngineHost;

// IKVM-compiled assemblies aren't in deps.json for transitive project references.
// Resolve them from the app directory so Assembly.Load("DataUtils") etc. succeed.
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};

var root = new RootCommand("bs-engine-host — built-in BattleScribe/NewRecruit engines behind the NDJSON adapter protocol.");
root.Subcommands.Add(ServeCommand.Create());
root.Subcommands.Add(ProbeCommand.Create());
root.Subcommands.Add(DiscoverCommand.Create());

return await root.Parse(args).InvokeAsync();
