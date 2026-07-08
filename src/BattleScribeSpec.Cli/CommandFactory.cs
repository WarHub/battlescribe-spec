using System.CommandLine;

namespace BattleScribeSpec.Cli;

/// <summary>Builds the root command and registers the verb subcommands.</summary>
internal static class CommandFactory
{
    public static RootCommand CreateRootCommand()
    {
        var root = new RootCommand(
            "bs-spec — run, inspect, and format BattleScribe conformance specs across engines.");

        root.Subcommands.Add(RunCommand.Create());
        root.Subcommands.Add(VerifyCommand.Create());
        root.Subcommands.Add(ProbeForwardCommand.Create());
        root.Subcommands.Add(ExportXmlCommand.Create());
        root.Subcommands.Add(FormatCommand.Create());
        root.Subcommands.Add(DiscoverForwardCommand.Create());

        return root;
    }
}
