namespace BattleScribeSpec.BsRosterUiDriver;

/// <summary>
/// Resolves the repo-local BattleScribe UI runtime artifacts that <c>setup.ps1</c> provisions,
/// so callers don't need environment variables in the common case.
/// </summary>
public static class BsUiPaths
{
    /// <summary>
    /// Resolves the Java executable used to launch the BattleScribe UI (must include JavaFX),
    /// in order:
    /// <list type="number">
    ///   <item><c>BS_UI_JAVA_PATH</c> — explicit override.</item>
    ///   <item>Repo-local Liberica full JDK at <c>lib/liberica-jdk/bin/java[.exe]</c> — installed
    ///     by <c>setup.ps1</c> (normalized so <c>bin/</c> is directly under it on every OS).</item>
    ///   <item><c>JAVA_HOME</c> — e.g. CI's <c>actions/setup-java</c> (<c>jdk+fx</c>), where the
    ///     repo-local download is skipped.</item>
    /// </list>
    /// Returns <c>null</c> if none is present.
    /// </summary>
    public static string? ResolveJavaPath(string repoRoot)
    {
        var fromEnv = Environment.GetEnvironmentVariable("BS_UI_JAVA_PATH");
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return fromEnv;
        }

        var exe = OperatingSystem.IsWindows() ? "java.exe" : "java";

        var liberica = Path.Combine(repoRoot, "lib", "liberica-jdk", "bin", exe);
        if (File.Exists(liberica))
        {
            return liberica;
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
        {
            var fromHome = Path.Combine(javaHome, "bin", exe);
            if (File.Exists(fromHome))
            {
                return fromHome;
            }
        }

        return null;
    }
}
