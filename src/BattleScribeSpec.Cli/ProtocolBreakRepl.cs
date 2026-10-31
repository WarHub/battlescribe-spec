using System.Text.Json;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using BattleScribeSpec.Scenario;

namespace BattleScribeSpec.Cli;

/// <summary>The editor a <c>--break</c> command addresses.</summary>
internal enum BreakEditor
{
    /// <summary>The data editor: the gamedata engine, which only a scenario drives beside the roster editor.</summary>
    Data,

    /// <summary>The roster editor: the roster engine.</summary>
    Roster,
}

/// <summary>
/// What a <c>--break</c> pause can inspect. A roster spec has one editor. A scenario has a data editor and a
/// roster editor on one adapter connection — the adapter keeps them in separate slots — so the REPL can
/// address either, by name.
/// </summary>
/// <param name="HasDataEditor">The run drives a data editor: true for a scenario only.</param>
/// <param name="RosterEditorStarted">
/// The roster editor has read data, so there is a roster to inspect. Always true in a roster spec, whose
/// engine is set up before its first step; in a scenario, true from the first <c>newRoster</c> or
/// <c>openRoster</c> on (<see cref="ScenarioStepView.RosterEditorStarted"/>).
/// </param>
/// <param name="Current">
/// The editor the paused step runs in, which bare <c>state</c> and <c>errors</c> address; null when the
/// step is a hand-off between the editors and so names neither.
/// </param>
internal sealed record BreakScope(bool HasDataEditor, bool RosterEditorStarted, BreakEditor? Current)
{
    /// <summary>A roster spec's pause: the roster editor, holding the spec's roster.</summary>
    public static BreakScope RosterSpec { get; } = new(HasDataEditor: false, RosterEditorStarted: true, Current: BreakEditor.Roster);

    /// <summary>A scenario's pause before <paramref name="step"/>.</summary>
    public static BreakScope ForScenario(ScenarioStepView step) => new(
        HasDataEditor: true,
        step.RosterEditorStarted,
        step.Kind switch
        {
            ScenarioStepKind.Data => BreakEditor.Data,
            ScenarioStepKind.Roster => BreakEditor.Roster,
            _ => null,
        });
}

/// <summary>
/// The interactive pause triggered by <c>run --break &lt;n&gt;</c>. Speaks the adapter
/// protocol directly, so it is engine-agnostic: inspect roster state, list validation
/// errors, capture a screenshot, or send a raw JSON command line straight to the adapter.
/// One uniform prompt regardless of which engine is driving the run.
/// <para>
/// In a scenario the same prompt reaches both editors: <c>data state</c>/<c>data errors</c> and
/// <c>roster state</c>/<c>roster errors</c> name one, and bare <c>state</c>/<c>errors</c> mean the editor the
/// paused step runs in. A command the pause cannot answer is refused with the reason — the roster editor
/// before it holds a roster, an unnamed editor at a hand-off — never sent to whichever engine would take it.
/// </para>
/// </summary>
internal static class ProtocolBreakRepl
{
    /// <summary>
    /// Drop into the inspection prompt before <paramref name="stepIndex"/>.
    /// Returns <c>true</c> to resume the run, <c>false</c> to abort it (the runner stops
    /// stepping). Commands: <c>state</c>, <c>errors</c> (each optionally after <c>data</c> or <c>roster</c>),
    /// <c>screenshot &lt;file.png&gt;</c>, a raw <c>{...}</c> JSON line (sent verbatim),
    /// <c>continue</c>/empty to resume, <c>quit</c> to abort.
    /// </summary>
    /// <param name="connection">The adapter connection to speak the protocol over.</param>
    /// <param name="stepIndex">Index of the step the run is paused before.</param>
    /// <param name="stepDescription">Human-readable description of that step, for the prompt banner.</param>
    /// <param name="scope">What the pause can inspect: <see cref="BreakScope.RosterSpec"/> for a roster spec.</param>
    /// <param name="requestTimeout">
    /// Request timeout for the REPL's own protocol engines. Pass the same
    /// longer window used for setup (e.g. 5 minutes for dataSource specs) so REPL commands
    /// against a slow-to-respond engine don't spuriously time out; defaults to the engine's
    /// standard 30s timeout when null.
    /// </param>
    public static bool Run(AdapterProcess connection, int stepIndex, string stepDescription, BreakScope scope, TimeSpan? requestTimeout = null)
    {
        Ui.Blank();
        Ui.Rule($"Stopped before step {stepIndex}: {stepDescription}");
        if (scope.HasDataEditor)
        {
            Ui.Info(
                $"Editors: data editor | roster editor ({(scope.RosterEditorStarted ? "has a roster" : "no roster yet: it reads the data directory at the first newRoster or openRoster")})");
            Ui.Info(scope.Current is { } current
                ? $"state and errors mean the {Name(current)} here; prefix data or roster to ask the other"
                : "This step is a hand-off between the editors: name one (data state, roster state)");
        }

        Ui.Info($"Commands: {CommandList(scope)}");

        // One adapter connection, used sequentially. These engines are deliberately never
        // disposed here: their Dispose sends a teardown that would kill the adapter's engines
        // mid-run — the owning process controls the connection's lifetime.
        var roster = new JsonProtocolEngine(connection, requestTimeout);
        var data = new JsonProtocolGameDataEngine(connection, requestTimeout);

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
                default:
                    if (trimmed.StartsWith('{'))
                    {
                        TryRun(() =>
                        {
                            var command = ProtocolSerializer.DeserializeCommand(trimmed)
                                ?? throw new InvalidOperationException($"Could not parse command JSON: {trimmed}");
                            var response = connection.SendCommandAsync(command).GetAwaiter().GetResult();
                            Console.Out.WriteLine(ProtocolSerializer.SerializeResponse(response));
                            Console.Out.Flush();
                        });
                    }
                    else if (trimmed.StartsWith("screenshot", StringComparison.Ordinal))
                    {
                        // The protocol's screenshot command is the roster engine's.
                        if (Refusal(scope, stepIndex, BreakEditor.Roster) is { } refused)
                        {
                            Ui.Warn(refused);
                        }
                        else
                        {
                            HandleScreenshot(roster, trimmed);
                        }
                    }
                    else
                    {
                        HandleInspect(trimmed, scope, stepIndex, roster, data);
                    }

                    break;
            }

            Console.Error.Write("> ");
        }

        // Stdin closed (EOF): resume rather than hang the run.
        return true;
    }

    /// <summary><c>[data|roster] state</c> and <c>[data|roster] errors</c>; anything else is an unknown command.</summary>
    private static void HandleInspect(string command, BreakScope scope, int stepIndex, JsonProtocolEngine roster, JsonProtocolGameDataEngine data)
    {
        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        BreakEditor? named = words[0] switch
        {
            "data" => BreakEditor.Data,
            "roster" => BreakEditor.Roster,
            _ => null,
        };

        var verb = named is null ? words[0] : words.ElementAtOrDefault(1) ?? "state";
        if (verb is not ("state" or "errors") || words.Length > (named is null ? 1 : 2))
        {
            Ui.Warn($"unknown command: '{command}' (try {CommandList(scope)})");
            return;
        }

        var editor = named ?? scope.Current;
        if (editor is not { } target)
        {
            Ui.Warn(
                $"step {stepIndex} is a hand-off between the editors, so '{verb}' names neither: " +
                $"say 'data {verb}' or 'roster {verb}'.");
            return;
        }

        if (Refusal(scope, stepIndex, target) is { } refused)
        {
            Ui.Warn(refused);
            return;
        }

        TryRun(() =>
        {
            if (scope.HasDataEditor)
            {
                // Two editors answer at this prompt; say which one this is.
                Ui.Info($"{Name(target)}:");
            }

            if (target == BreakEditor.Data)
            {
                if (verb == "state")
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(data.GetState(), GameDataDumpJsonContext.Default.GameDataState));
                    Console.Out.Flush();
                }
                else
                {
                    WriteErrors(data.GetValidationErrors());
                }
            }
            else if (verb == "state")
            {
                StateDumper.Dump(roster.GetRosterState(), roster.GetValidationErrors(), Console.Out, new DumpOptions());
                Console.Out.Flush();
            }
            else
            {
                WriteErrors(roster.GetValidationErrors());
            }
        });
    }

    /// <summary>Why the pause cannot ask <paramref name="editor"/> anything, or null when it can.</summary>
    private static string? Refusal(BreakScope scope, int stepIndex, BreakEditor editor) => editor switch
    {
        BreakEditor.Data when !scope.HasDataEditor =>
            "this run has no data editor: a roster spec drives the roster editor alone.",
        BreakEditor.Roster when !scope.RosterEditorStarted =>
            $"the roster editor holds no roster before step {stepIndex}: it reads the data directory at the first " +
            "newRoster or openRoster, and has read nothing yet.",
        _ => null,
    };

    private static string Name(BreakEditor editor) => editor == BreakEditor.Data ? "data editor" : "roster editor";

    private static string CommandList(BreakScope scope) => scope.HasDataEditor
        ? "[data|roster] state | [data|roster] errors | screenshot <file.png> (roster editor) | {\"type\":\"...\"} raw JSON | continue | quit"
        : "state | errors | screenshot <file.png> | {\"type\":\"...\"} raw JSON | continue | quit";

    private static void WriteErrors(IReadOnlyList<ValidationErrorState> errors)
    {
        if (errors.Count == 0)
        {
            Ui.Info("(no validation errors)");
            return;
        }

        foreach (var err in errors)
        {
            Console.Out.WriteLine($"  - {err.Message}");
        }

        Console.Out.Flush();
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
