namespace BattleScribeSpec.Tests;

/// <summary>
/// Where the tests find the checkout they were built in, and the external test data under it.
/// Default: .testdata/ directory relative to the repo root.
/// Override: set environment variables (e.g., WH40K_DATA_DIR).
/// </summary>
/// <remarks>
/// <para>
/// <b>One root, from the binaries.</b> Every path a test anchors at the checkout — specs, schemas, the CI
/// definition, <c>.testdata/</c>, <c>artifacts/</c> — comes from <see cref="RepoRoot.FromBinaries"/>: the
/// nearest <c>BattleScribeSpec.slnx</c> above this assembly, which is the tree it was built from. Nullable
/// callers (an artifact path with a fallback, a diagnostics default) read it directly; a test that cannot
/// run without a checkout reads <see cref="Root"/>, which says why when there is none.
/// </para>
/// <para>
/// <b>Never the working directory.</b> xunit sets it to the test assembly's output folder before any test
/// runs, under every runner and entry point (<c>xunit.v3.core</c> calls <c>SetCurrentDirectory</c>;
/// <c>dotnet test</c>, <c>dotnet run</c> and the exe started from elsewhere all report the output folder).
/// So a walk up from it reaches the same checkout today — because of a choice inside xunit that nothing here
/// states, and a walk that escapes the checkout lands in whatever tree encloses it: a worktree under
/// <c>.claude/worktrees/</c> sits inside the main checkout. <c>tests/BannedSymbols.txt</c> makes the
/// working-directory APIs, and the production overloads that read the working directory on the CLI's behalf,
/// compile errors in both test projects (<see cref="BannedSymbolsTests"/>).
/// </para>
/// </remarks>
internal static class TestPaths
{
    /// <summary>
    /// The checkout these test binaries were built in (<see cref="RepoRoot.FromBinaries"/>), for a test that
    /// reads it and cannot run without it. Throws, naming the binaries' folder, when they are not inside a
    /// checkout.
    /// </summary>
    public static string Root => RepoRoot.FromBinaries
        ?? throw new DirectoryNotFoundException(
            $"No {RepoRoot.MarkerFileName} above {AppContext.BaseDirectory}: this test reads the checkout its binaries "
            + "were built in, and they are not inside one.");

    /// <summary>
    /// Points a driver's diagnostics directory at the repo root's <c>artifacts/</c>, not the test
    /// app's output folder, by setting <paramref name="environmentVariable"/> when the caller has not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The UI drivers default to <c>artifacts/&lt;folder&gt;</c> <b>under the process's working
    /// directory</b>, which is right for <c>bs-spec</c> and wrong in the test app: xunit sets the
    /// test app's working directory to the test assembly's output folder, so a
    /// failing spec writes its screenshot, DOM and Pinia dump to
    /// <c>artifacts/bin/BattleScribeSpec.Tests/debug/artifacts/nr-ui-diagnostics/</c> — a path no
    /// CI upload step looks at (measured for the BS roster lane: 19 dumps there against 1 at the repo
    /// root). The same trap, with the same cause and the same fix, is written down in
    /// <see cref="TelemetryAssemblyFixture"/>.
    /// </para>
    /// <para>
    /// Done test-side rather than in the drivers because the CWD-relative default is correct for
    /// every other caller, and because knowing that this process is a test app whose working directory
    /// xunit chose is the test project's business. An explicit environment variable still wins — this only
    /// replaces the default, and only when the repo root can be found.
    /// </para>
    /// <para>
    /// Called from <see cref="UiArtifactPathsAssemblyFixture"/>, which explains why it is an
    /// assembly fixture and not the UI fixtures that need it.
    /// </para>
    /// </remarks>
    public static void AnchorDiagnosticsAtRepoRoot(string environmentVariable, string folderName)
    {
        if (Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 })
        {
            return;
        }

        if (RepoRoot.FromBinaries is { } repoRoot)
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

        if (RepoRoot.FromBinaries is not { } repoRoot)
        {
            return null;
        }

        var candidate = Path.GetFullPath(Path.Combine(repoRoot, ".testdata", "wh40k-9e"));
        return Directory.Exists(candidate) ? candidate : null;
    }
}
