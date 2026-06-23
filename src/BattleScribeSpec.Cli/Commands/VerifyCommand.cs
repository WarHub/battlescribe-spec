using System.CommandLine;
using BattleScribeSpec.GameData;

namespace BattleScribeSpec.Cli;

/// <summary>
/// <c>bs-spec verify &lt;spec...&gt; [--engines a,b,c]</c> — run one or more gamedata specs across
/// several engines in a single process and print a pass/fail matrix. Built for the spec-authoring
/// loop: after restructuring a spec, confirm it still holds on every engine it touches with one
/// command instead of four <c>dotnet test</c> invocations.
///
/// Each engine is created once and reused across all specs (the runner does the full
/// setup/steps/cleanup lifecycle per spec). Per-engine <c>skip</c>/<c>fail</c> markers are honored:
/// a spec marked <c>newrecruit-ui: skip</c> reports SKIP, and one marked <c>fail</c> reports XFAIL
/// when it fails (or UPASS — a hard error — when it unexpectedly passes).
/// </summary>
internal static class VerifyCommand
{
    private static readonly string[] AllGameDataEngines =
        ["battlescribe", "battlescribe-ui", "newrecruit", "newrecruit-ui"];

    private enum Outcome { Pass, Fail, Skip, XFail, UnexpectedPass, Unavailable }

    private sealed record Cell(Outcome Outcome, IReadOnlyList<string> Failures);

    public static Command Create()
    {
        var specs = new Argument<string[]>("specs")
        {
            Description = "One or more spec file paths or IDs (e.g. \"condition/condition-all-types\").",
            Arity = ArgumentArity.OneOrMore,
        };
        var engines = new Option<string?>("--engines")
        {
            Description = "Comma-separated engines to run (default: battlescribe,battlescribe-ui,newrecruit,newrecruit-ui).",
        };
        var gamedata = new Option<bool>("--gamedata") { Description = "Force the gamedata domain (otherwise inferred)." };
        var roster = new Option<bool>("--roster") { Description = "Force the roster domain (otherwise inferred)." };
        var headed = new Option<bool>("--headed") { Description = "Show the browser/app window (UI engines; default headless)." };
        var diagnostics = new Option<bool>("--diagnostics")
        {
            Description = "On a newrecruit-ui failure, save a diagnostics bundle (screenshot/DOM/console/Pinia).",
        };

        var command = new Command("verify",
            "Run one or more specs across multiple engines and print a pass/fail matrix.");
        command.Arguments.Add(specs);
        foreach (var option in new Option[] { engines, gamedata, roster, headed, diagnostics })
        {
            command.Options.Add(option);
        }

        command.SetAction((parseResult, _) =>
        {
            var specInputs = parseResult.GetValue(specs)!;
            var gd = parseResult.GetValue(gamedata);
            var ros = parseResult.GetValue(roster);
            if (gd && ros)
            {
                Ui.Error("--gamedata and --roster are mutually exclusive.");
                return Task.FromResult(1);
            }

            var domain = gd ? "gamedata"
                : ros ? "roster"
                : SpecLoading.InferEngineType(specInputs[0]);
            if (domain != "gamedata")
            {
                Ui.Error("verify currently supports gamedata specs only; use 'run' per engine for roster.");
                return Task.FromResult(1);
            }

            var engineNames = ParseEngines(parseResult.GetValue(engines)) ?? AllGameDataEngines;
            if (parseResult.GetValue(diagnostics))
            {
                // The newrecruit-ui engine self-captures a diagnostics bundle at the failure point
                // (before the runner's Cleanup navigates away) when this is set.
                Environment.SetEnvironmentVariable("NR_GAMEDATA_UI_DIAGNOSTICS", "1");
            }

            return ExecuteGameDataAsync(specInputs, engineNames, !parseResult.GetValue(headed));
        });

        return command;
    }

    private static string[]? ParseEngines(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? null
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<int> ExecuteGameDataAsync(
        string[] specInputs, string[] engineNames, bool headless)
    {
        var specs = new List<GameDataSpecFile>();
        foreach (var input in specInputs)
        {
            try
            {
                specs.Add(SpecLoading.LoadGameDataSpec(input));
            }
            catch (Exception ex)
            {
                Ui.Error($"Error loading spec '{input}': {ex.Message}");
                return 1;
            }
        }

        // results[engineName][specId] = Cell
        var results = new Dictionary<string, Dictionary<string, Cell>>(StringComparer.Ordinal);

        foreach (var engineName in engineNames)
        {
            var column = new Dictionary<string, Cell>(StringComparer.Ordinal);
            results[engineName] = column;

            Ui.Info($"── engine: {engineName} ──");
            IGameDataEngine engine;
            try
            {
                engine = await EngineFactory.CreateGameDataEngineAsync(engineName, headless);
            }
            catch (Exception ex)
            {
                Ui.Warn($"engine '{engineName}' unavailable: {ex.Message}");
                foreach (var spec in specs)
                {
                    column[spec.Id] = new Cell(Outcome.Unavailable, []);
                }

                continue;
            }

            using (engine)
            {
                foreach (var spec in specs)
                {
                    if (!spec.IsApplicableTo(engineName))
                    {
                        column[spec.Id] = new Cell(Outcome.Skip, []);
                        Ui.Info($"  {spec.Id}: skip (not applicable)");
                        continue;
                    }

                    var xfail = spec.IsExpectedToFail(engineName);
                    SpecResult result;
                    try
                    {
                        result = new GameDataRunner(engine, engineName).Run(spec);
                    }
                    catch (Exception ex)
                    {
                        column[spec.Id] = new Cell(Outcome.Fail, [$"runner threw: {ex.GetType().Name}: {ex.Message}"]);
                        Ui.Fail($"  {spec.Id}: FAIL (runner threw)");
                        continue;
                    }

                    var outcome = (result.Passed, xfail) switch
                    {
                        (true, false) => Outcome.Pass,
                        (true, true) => Outcome.UnexpectedPass,
                        (false, true) => Outcome.XFail,
                        (false, false) => Outcome.Fail,
                    };
                    column[spec.Id] = new Cell(outcome, result.Failures);

                    if (outcome is Outcome.Pass or Outcome.XFail)
                    {
                        Ui.Pass($"  {spec.Id}: {Token(outcome)}");
                    }
                    else
                    {
                        Ui.Fail($"  {spec.Id}: {Token(outcome)}");
                    }
                }
            }
        }

        return Report(specs, engineNames, results);
    }

    private static string Token(Outcome outcome) => outcome switch
    {
        Outcome.Pass => "PASS",
        Outcome.Fail => "FAIL",
        Outcome.Skip => "SKIP",
        Outcome.XFail => "XFAIL",
        Outcome.UnexpectedPass => "UPASS",
        Outcome.Unavailable => "N/A",
        _ => "?",
    };

    /// <summary>Prints the matrix + a failure-detail section to stdout. Returns the process exit code.</summary>
    private static int Report(
        IReadOnlyList<GameDataSpecFile> specs,
        string[] engineNames,
        Dictionary<string, Dictionary<string, Cell>> results)
    {
        var specWidth = Math.Max(4, specs.Max(s => s.Id.Length));
        var colWidth = engineNames.Select(e => Math.Max(e.Length, 5)).ToArray();

        Console.Out.WriteLine();
        var header = "spec".PadRight(specWidth);
        for (var c = 0; c < engineNames.Length; c++)
        {
            header += "  " + engineNames[c].PadRight(colWidth[c]);
        }

        Console.Out.WriteLine(header);
        Console.Out.WriteLine(new string('-', header.Length));

        var hardFailures = 0;
        foreach (var spec in specs)
        {
            var line = spec.Id.PadRight(specWidth);
            for (var c = 0; c < engineNames.Length; c++)
            {
                var cell = results[engineNames[c]][spec.Id];
                if (cell.Outcome is Outcome.Fail or Outcome.UnexpectedPass)
                {
                    hardFailures++;
                }

                line += "  " + Token(cell.Outcome).PadRight(colWidth[c]);
            }

            Console.Out.WriteLine(line);
        }

        // Failure detail.
        foreach (var spec in specs)
        {
            foreach (var engineName in engineNames)
            {
                var cell = results[engineName][spec.Id];
                if (cell.Outcome is Outcome.Fail or Outcome.UnexpectedPass && cell.Failures.Count > 0)
                {
                    Console.Out.WriteLine();
                    Console.Out.WriteLine($"{spec.Id} @ {engineName} — {Token(cell.Outcome)}:");
                    foreach (var (failure, i) in cell.Failures.Select((f, i) => (f, i)))
                    {
                        Console.Out.WriteLine($"  [{i + 1}] {failure}");
                    }
                }
            }
        }

        Console.Out.Flush();
        Ui.Blank();
        if (hardFailures == 0)
        {
            Ui.Pass("verify: all engines green (skips/xfails are expected).");
            return 0;
        }

        Ui.Fail($"verify: {hardFailures} hard failure(s) across the matrix.");
        return 1;
    }
}
