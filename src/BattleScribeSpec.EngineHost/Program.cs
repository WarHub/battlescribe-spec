using System.CommandLine;
using BattleScribeSpec.EngineHost;

var root = new RootCommand("bs-engine-host — built-in BattleScribe/NewRecruit engines behind the NDJSON adapter protocol.");
root.Subcommands.Add(ServeCommand.Create());
root.Subcommands.Add(ProbeCommand.Create());
root.Subcommands.Add(DiscoverCommand.Create());

return await root.Parse(args).InvokeAsync();
