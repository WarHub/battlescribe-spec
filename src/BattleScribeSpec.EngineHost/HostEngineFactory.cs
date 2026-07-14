using BattleScribeSpec.BsGameDataUiDriver;
using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.GameData;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.NrGameDataUiDriver;
using BattleScribeSpec.NrRosterUiDriver;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.EngineHost;

/// <summary>
/// Creates concrete roster/gamedata engines from a resolved engine name, and resolves the
/// BattleScribe UI artifact paths. Status lines go to stderr (protocol rule: stdout is
/// protocol-only).
/// </summary>
internal static class HostEngineFactory
{
    /// <summary>Create the roster engine named by <paramref name="name"/>.</summary>
    /// <param name="name">Built-in roster engine identity.</param>
    /// <param name="headless">Whether to run without showing the browser/app window.</param>
    /// <param name="reuseRoster">
    /// Whether this engine should stay alive across setups (battlescribe-ui only; ignored by
    /// engines that don't support reuse). Comes straight from the caller's <c>ConcurrencyPlan</c> —
    /// this factory does not decide it, and reads no environment variable to override it.
    /// </param>
    public static async Task<IRosterEngine> CreateRosterEngineAsync(string name, bool headless, bool reuseRoster)
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
                    return CreateBsUiRosterEngine(options, reuseRoster);
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

    /// <summary>Create the gamedata engine named by <paramref name="name"/>.</summary>
    /// <param name="name">Built-in gamedata engine identity.</param>
    /// <param name="headless">Whether to run without showing the browser/app window.</param>
    /// <param name="reuseGameData">
    /// Whether this engine should stay alive across setups (battlescribe-ui only; ignored by
    /// engines that don't support reuse). Comes straight from the caller's <c>ConcurrencyPlan</c> —
    /// this factory does not decide it, and reads no environment variable to override it.
    /// </param>
    public static async Task<IGameDataEngine> CreateGameDataEngineAsync(string name, bool headless, bool reuseGameData)
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
                    return CreateBsUiGameDataEngine(options, reuseGameData);
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
    /// Construct the BS-UI roster engine from already-resolved artifact paths, with
    /// <c>KeepAlive</c> set from the caller's reuse decision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>KeepAlive means exactly "the plan says reuse this engine"</b> — no OR with a separate
    /// <c>--keep-alive</c> flag, no environment-variable override. Reuse is one decision, made once
    /// by the parent (<c>ConcurrencyPlan</c>), not two mechanisms that can disagree.
    /// </para>
    /// <para>
    /// <b>Why this is a separate seam from <see cref="CreateRosterEngineAsync"/>:</b> so the rule
    /// above can be tested in <em>every</em> CI job. The engine name is the only thing that decides
    /// whether a <c>BsUiRosterEngine</c> is built, and <see cref="ResolveBsUiOptions"/> throws
    /// ("Agent JAR not found") on any machine without the Java agent jar — which is every CI job
    /// except <c>smoke</c> and <c>thorough-ui-bs</c> (<c>setup.ps1</c> skips the jar when
    /// <c>CI=true</c>). Testing the reuse rule through the discovery path would therefore mean a
    /// test that only runs where the artifacts happen to exist. <c>KeepAlive</c> exists only on the
    /// two BS-UI engines, so the rule cannot be retargeted to a cheaper engine either. Splitting
    /// construction from discovery lets the test hand in a <see cref="BsUiOptions"/> directly (as
    /// <c>BsUiSetupFailureTeardownTests</c> already does) and assert the rule with no artifacts at
    /// all — a gate that runs everywhere beats a gate that is skipped where it matters.
    /// </para>
    /// </remarks>
    /// <param name="options">Resolved artifact paths.</param>
    /// <param name="reuseRoster">The plan's roster-reuse decision; becomes <c>KeepAlive</c> verbatim.</param>
    internal static IRosterEngine CreateBsUiRosterEngine(BsUiOptions options, bool reuseRoster) =>
        new BsUiRosterEngine(options) { KeepAlive = reuseRoster };

    /// <summary>
    /// Construct the BS-UI gamedata engine from already-resolved artifact paths, with
    /// <c>KeepAlive</c> set from the caller's reuse decision. See
    /// <see cref="CreateBsUiRosterEngine"/> for why this is a seam.
    /// </summary>
    /// <param name="options">Resolved artifact paths.</param>
    /// <param name="reuseGameData">The plan's gamedata-reuse decision; becomes <c>KeepAlive</c> verbatim.</param>
    internal static IGameDataEngine CreateBsUiGameDataEngine(BsUiOptions options, bool reuseGameData) =>
        new BsGameDataUiEngine(options) { KeepAlive = reuseGameData };

    /// <summary>
    /// Resolve the BattleScribe Roster Editor UI options (Java runtime, app jar, agent jar)
    /// from environment variables or conventional repo-local locations.
    /// </summary>
    public static BsUiOptions ResolveBsUiOptions()
    {
        var appDir = Environment.GetEnvironmentVariable("BS_UI_APP_DIR");
        var agentJar = Environment.GetEnvironmentVariable("BS_UI_AGENT_JAR");

        var repoRoot = FindRepoRoot();

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

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (; dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}
