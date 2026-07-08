using System.CommandLine;

namespace BattleScribeSpec.Cli;

/// <summary>
/// <c>bs-spec probe &lt;spec&gt; --ui</c> — open the real desktop/browser app with a spec
/// loaded for interactive inspection (no assertions). The CLI itself carries no engines:
/// this resolves the engine selection, then forwards to <c>bs-engine-host probe</c> with
/// inherited stdio so the interactive session reaches the console.
/// </summary>
internal static class ProbeForwardCommand
{
    public static Command Create()
    {
        var spec = new Argument<string>("spec")
        {
            Description = "Spec file path or ID to load into the inspected app.",
        };
        var engineOptions = new EngineOptions();

        var command = new Command("probe", "Open a UI engine with a spec loaded for interactive inspection.");
        command.Arguments.Add(spec);
        engineOptions.AddTo(command);

        command.SetAction(async (parseResult, _) =>
        {
            var specInput = parseResult.GetValue(spec)!;
            try
            {
                var selection = engineOptions.Resolve(parseResult, specInput);

                // Probe inspects the real desktop/browser app — reject non-UI engines here
                // (before spawning the host), preserving the historical UX/message.
                if (selection.EngineName is not { } engineName
                    || !engineName.EndsWith("-ui", StringComparison.Ordinal))
                {
                    Ui.Error("probe requires --ui (it inspects the real desktop/browser app).");
                    return 1;
                }

                var domainFlag = selection.Domain == EngineDomain.Gamedata ? "--gamedata" : "--roster";
                var verbArgs = new[] { "--engine", engineName, "--headed", specInput, domainFlag };
                return await HostForwarder.ForwardAsync(selection.Entry, "probe", verbArgs);
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
