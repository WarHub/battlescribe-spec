namespace BattleScribeSpec.Tests;

/// <summary>
/// Resolves paths to external test data.
/// Default: .testdata/ directory relative to the repo root.
/// Override: set environment variables (e.g., WH40K_DATA_DIR).
/// </summary>
internal static class TestPaths
{
    private static readonly Lazy<string?> RepoRoot = new(FindRepoRoot);

    /// <summary>
    /// The repository root directory (identified by <c>BattleScribeSpec.slnx</c>), resolved by
    /// walking up from the test assembly's own location. Null if it could not be found (e.g. the
    /// test binaries were copied somewhere outside a checkout). Public so any caller needing a
    /// path anchored at the repo root — regardless of the test host process's own working
    /// directory, which VSTest sets to the test assembly's output folder, not the repo root — can
    /// reuse this instead of re-deriving it (see <see cref="BattleScribeSpec.Tests.TelemetryAssemblyFixture"/>).
    /// </summary>
    public static string? RepoRootDirectory => RepoRoot.Value;

    /// <summary>
    /// Points a driver's diagnostics directory at the repo root's <c>artifacts/</c>, not the test
    /// host's, by setting <paramref name="environmentVariable"/> when the caller has not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The NR UI drivers default to <c>artifacts/&lt;folder&gt;</c> <b>relative to the process's
    /// working directory</b>, which is right for <c>bs-spec</c> and wrong under <c>dotnet test</c>:
    /// VSTest sets the test host's working directory to the test assembly's output folder, so a
    /// failing spec writes its screenshot, DOM and Pinia dump to
    /// <c>artifacts/bin/BattleScribeSpec.Tests/debug/artifacts/nr-ui-diagnostics/</c> — a path no
    /// CI upload step looks at. The same trap, with the same cause and the same fix, is written down
    /// in <see cref="BsRosterUiFixture"/> and <see cref="TelemetryAssemblyFixture"/>.
    /// </para>
    /// <para>
    /// Done test-side rather than in the drivers because the CWD-relative default is correct for
    /// every other caller, and because knowing that this process is a VSTest host is the test
    /// project's business. An explicit environment variable still wins — this only replaces the
    /// default, and only when the repo root can be found.
    /// </para>
    /// <para>
    /// Called from <see cref="UiArtifactPathsAssemblyFixture"/>, which explains why it is an
    /// assembly fixture and not the four NR UI fixtures that need it.
    /// </para>
    /// </remarks>
    public static void AnchorDiagnosticsAtRepoRoot(string environmentVariable, string folderName)
    {
        if (Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 })
        {
            return;
        }

        if (RepoRootDirectory is { } repoRoot)
        {
            Environment.SetEnvironmentVariable(
                environmentVariable, Path.Combine(repoRoot, "artifacts", folderName));
        }
    }

    /// <summary>
    /// Path to wh40k-9e data directory. Checks WH40K_DATA_DIR env var first,
    /// then falls back to .testdata/wh40k-9e relative to the repository root.
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
        {
            return envDir;
        }

        var repoRoot = RepoRoot.Value;
        if (repoRoot is null)
        {
            return null;
        }

        var candidate = Path.GetFullPath(Path.Combine(repoRoot, ".testdata", "wh40k-9e"));
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static string? FindRepoRoot()
    {
        // Walk up from the test assembly location looking for the .slnx file
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "BattleScribeSpec.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
