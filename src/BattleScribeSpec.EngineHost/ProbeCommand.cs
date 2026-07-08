using System.CommandLine;
using BattleScribeSpec.BsGameDataUiDriver;
using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.NrGameDataUiDriver;
using BattleScribeSpec.NrRosterUiDriver;
using BattleScribeSpec.Roster;
using BattleScribeSpec.XmlGen;

namespace BattleScribeSpec.EngineHost;

/// <summary>
/// <c>bs-engine-host probe &lt;spec&gt; --engine X</c> — open the real desktop/browser app
/// with a spec loaded for interactive inspection (no assertions). One verb for all four UI
/// engines. Status lines go to stderr; state dumps and the REPL use stdout/stdin, which the
/// CLI forwarder inherits so the interactive session reaches the user's console.
/// </summary>
internal static class ProbeCommand
{
    public static Command Create()
    {
        var spec = new Argument<string>("spec")
        {
            Description = "Spec file path or ID to load into the inspected app.",
        };
        var engine = new Option<string>("--engine")
        {
            Description = "UI engine: battlescribe-ui or newrecruit-ui.",
            Required = true,
        };
        var headed = new Option<bool>("--headed") { Description = "Show the app/browser window (probe is inherently headed)." };
        var gamedata = new Option<bool>("--gamedata") { Description = "Probe the gamedata (editor) surface." };
        var roster = new Option<bool>("--roster") { Description = "Probe the roster surface (default)." };

        var command = new Command("probe", "Open a UI engine with a spec loaded for interactive inspection.");
        command.Arguments.Add(spec);
        command.Options.Add(engine);
        command.Options.Add(headed);
        command.Options.Add(gamedata);
        command.Options.Add(roster);

        command.SetAction((parseResult, _) =>
        {
            var specInput = parseResult.GetValue(spec)!;
            var engineName = parseResult.GetValue(engine)!;
            var isGamedata = parseResult.GetValue(gamedata);

            return (engineName, isGamedata) switch
            {
                ("battlescribe-ui", false) => ProbeBsRosterAsync(specInput),
                ("newrecruit-ui", false) => ProbeNrRosterAsync(specInput),
                ("battlescribe-ui", true) => ProbeBsGameDataAsync(specInput),
                ("newrecruit-ui", true) => ProbeNrGameDataAsync(specInput),
                _ => ProbeUnsupportedEngine(engineName),
            };
        });

        return command;
    }

    private static Task<int> ProbeUnsupportedEngine(string engineName)
    {
        Ui.Error($"probe does not support engine '{engineName}' yet.");
        return Task.FromResult(1);
    }

    private static async Task<int> ProbeBsRosterAsync(string specInput)
    {
        SpecFile spec;
        try
        {
            spec = HostSpecLoading.LoadSpec(specInput);
        }
        catch (Exception ex)
        {
            Ui.Error($"Error loading spec: {ex.Message}");
            return 1;
        }

        if (spec.Setup.DataSource is { Length: > 0 })
        {
            Ui.Error("battlescribe-ui probe does not support dataSource specs yet.");
            return 1;
        }

        var options = HostEngineFactory.ResolveBsUiOptions();
        var (gameSystem, catalogues) = SpecLoader.GetSetupData(spec.Setup, spec.Id);

        var xmlFiles = new List<(string FileName, string Content)>
        {
            ($"{gameSystem.Id}.gst", CatXmlGenerator.GenerateGameSystemXml(gameSystem)),
        };
        foreach (var (fileName, xml) in CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues))
        {
            xmlFiles.Add((fileName, xml));
        }

        Ui.Info($"BS UI Probe — launching with {xmlFiles.Count} data file(s)");

        await using var probe = new BsUiProbe(options);
        await probe.LaunchAsync(gameSystem, catalogues, xmlFiles, Console.Error);

        Ui.Blank();
        Ui.Info("── Scene Graph Dump ──");
        await probe.DumpTreeAsync(Console.Out);

        Ui.Blank();
        Ui.Info("── Windows ──");
        await probe.DumpWindowsAsync(Console.Out);

        Ui.Blank();
        Ui.Info("BS UI probe complete. BattleScribe is running. Press Enter to shut down...");
        Console.In.ReadLine();
        return 0;
    }

    private static async Task<int> ProbeNrRosterAsync(string specInput)
    {
        SpecFile spec;
        try
        {
            spec = HostSpecLoading.LoadSpec(specInput);
        }
        catch (Exception ex)
        {
            Ui.Error($"Error loading spec: {ex.Message}");
            return 1;
        }

        if (spec.Setup.DataSource is { Length: > 0 })
        {
            Ui.Error("newrecruit-ui probe does not support dataSource specs yet.");
            return 1;
        }

        var (gameSystem, catalogues) = SpecLoader.GetSetupData(spec.Setup, spec.Id);

        Ui.Info($"NR UI Probe — launching with {catalogues.Length + 1} data file(s)");

        await using var probe = new NrUiProbe();
        var url = Environment.GetEnvironmentVariable("NR_ENGINE_URL") ?? "https://www.newrecruit.eu";
        await probe.LaunchAsync(gameSystem, catalogues, url, Console.Error);

        Ui.Blank();
        Ui.Info("NR UI probe ready. Browser is open. Enter JS expressions to evaluate, 'exit' to quit:");
        await probe.RunReplAsync(Console.In, Console.Out);
        return 0;
    }

    private static async Task<int> ProbeBsGameDataAsync(string specInput)
    {
        GameData.GameDataSpecFile spec;
        try
        {
            spec = HostSpecLoading.LoadGameDataSpec(specInput);
            Ui.Info($"Loaded GameData spec: {spec.Category}/{spec.Id} — {spec.Description}");
        }
        catch (Exception ex)
        {
            Ui.Error($"Error loading GameData spec: {ex.Message}");
            return 1;
        }

        var (gameSystem, catalogues) = SpecLoader.GetGameDataSetupData(spec.Setup);

        // Resolve the *Data Editor* jar (DataEditor.jar) — the artifacts the gamedata engine uses.
        var options = BsGameDataUiEngine.FindOptions() ?? throw new InvalidOperationException(
            "BS UI artifacts not found — run setup.ps1 (installs the Liberica JDK and builds the agent jar), " +
            "or set BS_UI_JAVA_PATH and ensure DataEditor.jar + the agent jar exist.");
        Ui.Info($"BattleScribe Data Editor UI: {options.RosterEditorJarPath}");
        Ui.Info($"BS GameData UI Probe — launching with {catalogues.Length + 1} data file(s)");

        await using var probe = new BsGameDataUiProbe(options);
        await probe.LaunchAsync(gameSystem, catalogues, Console.Error);

        Ui.Blank();
        Ui.Info("BS GameData UI probe complete. BattleScribe is running. Press Enter to shut down...");
        Console.In.ReadLine();
        return 0;
    }

    private static async Task<int> ProbeNrGameDataAsync(string specInput)
    {
        GameData.GameDataSpecFile spec;
        try
        {
            spec = HostSpecLoading.LoadGameDataSpec(specInput);
            Ui.Info($"Loaded GameData spec: {spec.Category}/{spec.Id} — {spec.Description}");
        }
        catch (Exception ex)
        {
            Ui.Error($"Error loading GameData spec: {ex.Message}");
            return 1;
        }

        var (gameSystem, catalogues) = SpecLoader.GetGameDataSetupData(spec.Setup);

        Ui.Info($"NR Editor GameData UI Probe — launching with {catalogues.Length + 1} data file(s)");

        await using var probe = new NrGameDataUiProbe();

        var staticDir = NewRecruitGameDataEngine.FindFrozenStaticDir();
        if (staticDir is not null)
        {
            Ui.Info($"  Using frozen NR Editor static files: {staticDir}");
            await probe.LaunchFrozenAsync(staticDir, gameSystem, catalogues, Console.Error);
        }
        else
        {
            var baseUrl = Environment.GetEnvironmentVariable("NR_EDITOR_URL") ?? "https://giloushaker.github.io/nr-editor";
            Ui.Info($"  Using live NR Editor: {baseUrl}");
            await probe.LaunchAsync(gameSystem, catalogues, baseUrl, Console.Error);
        }

        Ui.Blank();
        Ui.Info("NR Editor GameData UI probe ready. Browser is open. Enter JS expressions to evaluate, 'exit' to quit:");
        await probe.RunReplAsync(Console.In, Console.Out);
        return 0;
    }
}
