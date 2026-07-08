using System.CommandLine;
using BattleScribeSpec.Engines;

namespace BattleScribeSpec.Cli;

/// <summary>
/// <c>bs-spec discover</c> — automated discovery of the NewRecruit editor's schema surface.
/// The CLI carries no engines: each subcommand forwards to <c>bs-engine-host discover</c>
/// (always the built-in NR editor) with inherited stdio.
/// </summary>
internal static class DiscoverForwardCommand
{
    public static Command Create()
    {
        var command = new Command("discover", "Automated discovery of NewRecruit editor schema additions.");
        command.Subcommands.Add(CreateSubcommand("xml",
            "Capture the real .cat/.gst XML NewRecruit emits for a spec's data."));
        command.Subcommands.Add(CreateSubcommand("enums",
            "Dump every dropdown's option values across the NR editor's node editors."));
        command.Subcommands.Add(CreateSubcommand("nodes",
            "Enumerate every node type the NR editor can create (context-menu add items)."));
        return command;
    }

    private static Command CreateSubcommand(string name, string description)
    {
        var spec = new Argument<string>("spec")
        {
            Description = "GameData spec whose setup data seeds the NR Editor.",
        };
        var headed = new Option<bool>("--headed") { Description = "Run with a visible browser window." };
        var output = new Option<string?>("--output", "-o")
        {
            Description = "Output directory (default: artifacts/discover/<specId>).",
        };

        var command = new Command(name, description);
        command.Arguments.Add(spec);
        command.Options.Add(headed);
        command.Options.Add(output);
        command.SetAction(async (parseResult, _) =>
        {
            try
            {
                // discover is always the built-in NewRecruit editor (gamedata UI); resolve that
                // built-in entry purely to locate bs-engine-host.
                var entry = EngineRegistry.LoadDefault().Resolve(EngineConnectable.Parse("newrecruit-ui"));

                // Host surface: discover <sub> <spec> [--headed] [--output <dir>].
                var verbArgs = new List<string> { name, parseResult.GetValue(spec)! };
                if (parseResult.GetValue(headed))
                {
                    verbArgs.Add("--headed");
                }

                if (parseResult.GetValue(output) is { } dir)
                {
                    verbArgs.Add("--output");
                    verbArgs.Add(dir);
                }

                return await HostForwarder.ForwardAsync(entry, "discover", verbArgs);
            }
            catch (CliInputException ex)
            {
                Ui.Error(ex.Message);
                return 1;
            }
        });
        return command;
    }
}
