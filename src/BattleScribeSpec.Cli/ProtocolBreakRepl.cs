using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Cli;

/// <summary>
/// The interactive pause triggered by <c>run --break &lt;n&gt;</c>. Speaks the adapter
/// protocol directly, so it is engine-agnostic: inspect roster state, list validation
/// errors, capture a screenshot, or send a raw JSON command line straight to the adapter.
/// One uniform prompt regardless of which engine is driving the run.
/// </summary>
internal static class ProtocolBreakRepl
{
    /// <summary>
    /// Drop into the inspection prompt before <paramref name="stepIndex"/>.
    /// Returns <c>true</c> to resume the run, <c>false</c> to abort it (the runner stops
    /// stepping). Commands: <c>state</c>, <c>errors</c>, <c>screenshot &lt;file.png&gt;</c>,
    /// a raw <c>{...}</c> JSON line (sent verbatim), <c>continue</c>/empty to resume,
    /// <c>quit</c> to abort.
    /// </summary>
    public static bool Run(AdapterProcess connection, int stepIndex, string stepDescription)
    {
        Ui.Blank();
        Ui.Rule($"Stopped before step {stepIndex}: {stepDescription}");
        Ui.Info("Commands: state | errors | screenshot <file.png> | {\"type\":\"...\"} raw JSON | continue | quit");

        // One adapter connection, used sequentially. This engine is deliberately never
        // disposed here: JsonProtocolEngine.Dispose sends a teardown that would kill the
        // adapter mid-run — the owning process controls the connection's lifetime.
        var engine = new JsonProtocolEngine(connection);

        Console.Error.Write("> ");
        while (Console.In.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            switch (trimmed)
            {
                case "" or "continue" or "c":
                    return true;
                case "quit" or "q":
                    return false;
                case "state":
                    TryRun(() =>
                    {
                        var state = engine.GetRosterState();
                        var errors = engine.GetValidationErrors();
                        StateDumper.Dump(state, errors, Console.Out, new DumpOptions());
                        Console.Out.Flush();
                    });
                    break;
                case "errors":
                    TryRun(() =>
                    {
                        var errors = engine.GetValidationErrors();
                        if (errors.Count == 0)
                        {
                            Ui.Info("(no validation errors)");
                        }
                        else
                        {
                            foreach (var err in errors)
                            {
                                Console.Out.WriteLine($"  - {err.Message}");
                            }

                            Console.Out.Flush();
                        }
                    });
                    break;
                default:
                    if (trimmed.StartsWith("screenshot", StringComparison.Ordinal))
                    {
                        HandleScreenshot(engine, trimmed);
                    }
                    else if (trimmed.StartsWith('{'))
                    {
                        TryRun(() =>
                        {
                            var response = connection.SendAsync(trimmed).GetAwaiter().GetResult();
                            Console.Out.WriteLine(response);
                            Console.Out.Flush();
                        });
                    }
                    else
                    {
                        Ui.Warn($"unknown command: '{trimmed}' (try state | errors | screenshot <file.png> | continue | quit)");
                    }

                    break;
            }

            Console.Error.Write("> ");
        }

        // Stdin closed (EOF): resume rather than hang the run.
        return true;
    }

    private static void HandleScreenshot(JsonProtocolEngine engine, string command)
    {
        var rest = command["screenshot".Length..].Trim();
        if (rest.Length == 0)
        {
            Ui.Warn("usage: screenshot <file.png>");
            return;
        }

        TryRun(() =>
        {
            var png = engine.CaptureScreenshot();
            var dir = Path.GetDirectoryName(Path.GetFullPath(rest));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(rest, png);
            Ui.Info($"Screenshot written: {rest}");
        });
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Ui.Error(ex.Message);
        }
    }
}
