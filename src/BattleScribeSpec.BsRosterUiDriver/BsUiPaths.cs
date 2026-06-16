namespace BattleScribeSpec.BsRosterUiDriver;

/// <summary>
/// Resolves the repo-local BattleScribe UI runtime artifacts that <c>setup.ps1</c> provisions,
/// so callers don't need environment variables in the common case.
/// </summary>
public static class BsUiPaths
{
    /// <summary>
    /// Resolves the Java executable used to launch the BattleScribe UI (must include JavaFX):
    /// the repo-local Liberica full JDK that <c>setup.ps1</c> installs at
    /// <c>lib/liberica-jdk/bin/java[.exe]</c> (normalized so <c>bin/</c> is directly under it on
    /// every OS). <c>BS_UI_JAVA_PATH</c> overrides this — CI sets it, since CI skips the
    /// repo-local download. Returns <c>null</c> if neither is present.
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
        return File.Exists(liberica) ? liberica : null;
    }
}
