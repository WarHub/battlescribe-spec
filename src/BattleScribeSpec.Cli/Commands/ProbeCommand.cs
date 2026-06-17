using System.CommandLine;
using BattleScribeSpec.BsGameDataUiDriver;
using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.NrGameDataUiDriver;
using BattleScribeSpec.NrRosterUiDriver;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Cli;

/// <summary>
/// <c>bs-spec probe &lt;spec&gt; --ui</c> — open the real desktop/browser app with a spec
/// loaded for interactive inspection (no assertions). One verb for all four UI engines.
/// </summary>
internal static class ProbeCommand
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

        command.SetAction((parseResult, _) =>
        {
            var specInput = parseResult.GetValue(spec)!;
            try
            {
                var engine = engineOptions.Resolve(parseResult, specInput);
                if (!engine.Ui)
                {
                    Ui.Error("probe requires --ui (it inspects the real desktop/browser app).");
                    return Task.FromResult(1);
                }

                return (engine.Product, engine.Domain) switch
                {
                    (EngineProduct.Battlescribe, EngineDomain.Roster) => ProbeBsRosterAsync(specInput),
                    (EngineProduct.Newrecruit, EngineDomain.Roster) => ProbeNrRosterAsync(specInput),
                    (EngineProduct.Battlescribe, EngineDomain.Gamedata) => ProbeBsGameDataAsync(specInput),
                    _ => ProbeNrGameDataAsync(specInput),
                };
            }
            catch (CliInputException ex)
            {
                Ui.Error(ex.Message);
                return Task.FromResult(1);
            }
        });

        return command;
    }

    private static async Task<int> ProbeBsRosterAsync(string specInput)
    {
        SpecFile spec;
        try
        {
            spec = SpecLoading.LoadSpec(specInput);
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

        var options = EngineFactory.ResolveBsUiOptions();
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
        Ui.Rule("Scene Graph Dump");
        await probe.DumpTreeAsync(Console.Out);

        Ui.Blank();
        Ui.Rule("Windows");
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
            spec = SpecLoading.LoadSpec(specInput);
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
        var url = Environment.GetEnvironmentVariable("NR_ENGINE_URL") ?? "https://newrecruit.eu";
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
            spec = SpecLoading.LoadGameDataSpec(specInput);
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
            spec = SpecLoading.LoadGameDataSpec(specInput);
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
