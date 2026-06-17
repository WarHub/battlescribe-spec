using BattleScribeSpec.BsGameDataUiDriver;
using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.GameData;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.NrGameDataUiDriver;
using BattleScribeSpec.NrRosterUiDriver;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Cli;

/// <summary>
/// Creates concrete roster/gamedata engines from a resolved engine name, and resolves the
/// BattleScribe UI artifact paths. Status lines go to stderr via <see cref="Ui"/>.
/// </summary>
internal static class EngineFactory
{
    public static async Task<IRosterEngine> CreateRosterEngineAsync(string name, bool headless, bool keepAlive)
    {
        switch (name)
        {
            case "battlescribe":
                return new BattleScribeRosterEngine();

            case "newrecruit":
                {
                    var url = Environment.GetEnvironmentVariable("NR_ENGINE_URL");
                    if (url is { Length: > 0 })
                    {
                        Ui.Info($"NR live mode: {url}");
                        return await NewRecruitRosterEngine.CreateAsync(url, headless);
                    }

                    var har = HarRecorder.FindFrozenHarFile() ?? throw new InvalidOperationException(
                        "NR engine requires NR_ENGINE_URL env var (live mode) or .testdata/newrecruit-har/newrecruit.har (frozen mode).");
                    Ui.Info($"NR frozen mode: {har}");
                    return await NewRecruitRosterEngine.CreateFrozenAsync(har, headless: headless);
                }

            case "battlescribe-ui":
                {
                    var options = ResolveBsUiOptions();
                    Ui.Info($"BS UI mode: {options.RosterEditorJarPath}");
                    return new BsUiRosterEngine(options) { KeepAlive = keepAlive };
                }

            case "newrecruit-ui":
                {
                    var url = Environment.GetEnvironmentVariable("NR_ENGINE_URL");
                    if (url is { Length: > 0 })
                    {
                        Ui.Info($"NR UI live mode: {url}");
                        return await NrRosterUiEngine.CreateAsync(url, headless);
                    }

                    var har = HarRecorder.FindFrozenHarFile() ?? throw new InvalidOperationException(
                        "NR UI engine requires NR_ENGINE_URL env var (live mode) or .testdata/newrecruit-har/newrecruit.har (frozen mode).");
                    Ui.Info($"NR UI frozen mode: {har}");
                    return await NrRosterUiEngine.CreateFrozenAsync(har, headless: headless);
                }

            default:
                throw new ArgumentException($"Unknown roster engine: '{name}'.");
        }
    }

    public static async Task<IGameDataEngine> CreateGameDataEngineAsync(string name, bool headless)
    {
        switch (name)
        {
            case "newrecruit-ui":
                {
                    var staticDir = NrGameDataUiEngine.FindFrozenStaticDir() ?? throw new InvalidOperationException(
                        "NR Editor frozen static dir not found (.testdata/nr-editor) — run setup.ps1.");
                    Ui.Info($"NR Editor GameData UI (frozen): {staticDir}");
                    return await NrGameDataUiEngine.CreateFrozenAsync(staticDir, headless);
                }

            case "battlescribe-ui":
                {
                    var options = BsGameDataUiEngine.FindOptions() ?? throw new InvalidOperationException(
                        "BS UI artifacts not found — run setup.ps1 (installs the Liberica JDK and builds the agent jar), " +
                        "or set BS_UI_JAVA_PATH and ensure DataEditor.jar + the agent jar exist.");
                    Ui.Info($"BattleScribe Data Editor UI: {options.RosterEditorJarPath}");
                    return new BsGameDataUiEngine(options);
                }

            case "newrecruit":
                {
                    var staticDir = NewRecruitGameDataEngine.FindFrozenStaticDir() ?? throw new InvalidOperationException(
                        "NR Editor frozen static dir not found (.testdata/nr-editor) — run setup.ps1.");
                    Ui.Info($"NewRecruit GameData (frozen): {staticDir}");
                    return await NewRecruitGameDataEngine.CreateFrozenAsync(staticDir, headless);
                }

            case "battlescribe":
                Ui.Info("BattleScribe GameData (in-process)");
                return new BattleScribeGameDataEngine();

            default:
                throw new ArgumentException($"Unknown gamedata engine: '{name}'.");
        }
    }

    /// <summary>
    /// Resolve the BattleScribe Roster Editor UI options (Java runtime, app jar, agent jar)
    /// from environment variables or conventional repo-local locations.
    /// </summary>
    public static BsUiOptions ResolveBsUiOptions()
    {
        var appDir = Environment.GetEnvironmentVariable("BS_UI_APP_DIR");
        var agentJar = Environment.GetEnvironmentVariable("BS_UI_AGENT_JAR");

        var repoRoot = SpecLoading.FindRepoRoot();

        // BS_UI_JAVA_PATH → repo-local Liberica JDK → bundled platform JRE. See BsUiPaths.
        var javaPath = repoRoot is not null
            ? BsUiPaths.ResolveJavaPath(repoRoot)
            : Environment.GetEnvironmentVariable("BS_UI_JAVA_PATH");

        if (appDir is null && repoRoot is not null)
        {
            var candidate = Path.Combine(repoRoot, "lib", "battlescribe");
            if (Directory.Exists(candidate))
            {
                appDir = candidate;
            }
        }

        if (agentJar is null && repoRoot is not null)
        {
            var candidate = Path.Combine(repoRoot, "src", "bs-ui-java-agent", "bs-ui-java-agent.jar");
            if (File.Exists(candidate))
            {
                agentJar = candidate;
            }
        }

        if (javaPath is null)
        {
            throw new InvalidOperationException(
                "Java runtime not found. Run setup.ps1 to install the repo-local Liberica JDK " +
                "(lib/liberica-jdk), or set BS_UI_JAVA_PATH to a JavaFX-capable java.");
        }

        var rosterEditorJar = appDir is not null
            ? Path.Combine(appDir, "RosterEditor.jar")
            : throw new InvalidOperationException(
                "BS app directory not found. Set BS_UI_APP_DIR env var or place app at lib/battlescribe/");

        if (!File.Exists(rosterEditorJar))
        {
            throw new InvalidOperationException($"RosterEditor.jar not found at: {rosterEditorJar}");
        }

        if (agentJar is null || !File.Exists(agentJar))
        {
            throw new InvalidOperationException(
                "Agent JAR not found. Set BS_UI_AGENT_JAR env var or build with: pwsh -File src/bs-ui-java-agent/build.ps1");
        }

        Ui.Info($"  Java: {javaPath}");
        Ui.Info($"  App: {rosterEditorJar}");
        Ui.Info($"  Agent: {agentJar}");

        return new BsUiOptions
        {
            JavaPath = javaPath,
            RosterEditorJarPath = rosterEditorJar,
            AgentJarPath = agentJar,
        };
    }
}
