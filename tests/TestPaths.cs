namespace BattleScribeSpec.Tests;

/// <summary>
/// Resolves paths to external test data repositories.
/// Default: sibling directories relative to the repo root.
/// Override: set environment variables (e.g., WH40K_DATA_DIR).
/// </summary>
internal static class TestPaths
{
    private static readonly Lazy<string?> _repoRoot = new(FindRepoRoot);

    /// <summary>
    /// Path to wh40k-9e data directory. Checks WH40K_DATA_DIR env var first,
    /// then falls back to ../wh40k-9e relative to the repository root.
    /// Returns null if neither is available.
    /// </summary>
    public static string? Wh40kDataDir { get; } = ResolveWh40kDataDir();

    /// <summary>
    /// Whether wh40k-9e data is available and contains at least one .gst file.
    /// </summary>
    public static bool Wh40kDataAvailable =>
        Wh40kDataDir is not null
        && Directory.Exists(Wh40kDataDir)
        && Directory.GetFiles(Wh40kDataDir, "*.gst").Length > 0;

    private static string? ResolveWh40kDataDir()
    {
        var envDir = Environment.GetEnvironmentVariable("WH40K_DATA_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return envDir;

        var repoRoot = _repoRoot.Value;
        if (repoRoot is null)
            return null;

        var candidate = Path.GetFullPath(Path.Combine(repoRoot, "..", "wh40k-9e"));
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static string? FindRepoRoot()
    {
        // Walk up from the test assembly location looking for the .slnx file
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "BattleScribeSpec.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
