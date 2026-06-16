using System.Text.Json;
using BattleScribeSpec.NrRosterUiDriver;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Cli;

/// <summary>
/// The interactive pause triggered by <c>run --break &lt;n&gt;</c>. For engines that can
/// evaluate expressions (NR UI) it offers a JS REPL; for the rest it waits for Enter.
/// One uniform entry point regardless of engine.
/// </summary>
internal static class BreakRepl
{
    public static void Run(IRosterEngine engine, int stepIndex, string stepDescription)
    {
        Ui.Blank();
        Ui.Rule($"Stopped before step {stepIndex}: {stepDescription}");

        if (engine is NrRosterUiEngine nr)
        {
            Ui.Info("NR UI page available. Enter JS expressions (exit/quit to continue):");
            Console.Error.Write("> ");
            while (Console.In.ReadLine() is { } line && line is not ("exit" or "quit"))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    Console.Error.Write("> ");
                    continue;
                }

                try
                {
                    var result = nr.EvaluateAsync<JsonElement>(line).GetAwaiter().GetResult();
                    Console.Out.WriteLine(result.ToString());
                }
                catch (Exception ex)
                {
                    Ui.Error(ex.Message);
                }

                Console.Error.Write("> ");
            }

            return;
        }

        Ui.Info("Press Enter to continue execution, or Ctrl+C to abort...");
        Console.In.ReadLine();
    }
}
