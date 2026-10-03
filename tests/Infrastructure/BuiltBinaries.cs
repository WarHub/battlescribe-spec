namespace BattleScribeSpec.TestSupport;

/// <summary>
/// Where a sibling project's build output is: the one rule both test projects use to find a binary they start
/// but do not reference (<c>bs-spec.dll</c>, <c>bs-engine-host.dll</c>, <c>bs-reference-adapter.dll</c>).
/// Cli.Tests links this file; <c>CliProcess</c> and <c>AdapterTestHost</c> each pass it their checkout.
/// </summary>
internal static class BuiltBinaries
{
    /// <summary>
    /// <c>artifacts/bin/&lt;project&gt;/&lt;pivot&gt;/&lt;file&gt;</c> under <paramref name="repoRoot"/>, trying this
    /// test assembly's own pivot first (debug/release) and then <c>debug</c>, the pivot CI builds under. Fails the
    /// test naming the path it expected when neither exists.
    /// </summary>
    /// <param name="repoRoot">The checkout the test binaries were built in (<see cref="RepoRoot.FromBinaries"/>).</param>
    /// <param name="project">The project's directory name under <c>artifacts/bin/</c>.</param>
    /// <param name="file">The file the project builds, e.g. <c>bs-engine-host.dll</c>.</param>
    public static string Find(string repoRoot, string project, string file)
    {
        var binRoot = Path.Combine(repoRoot, "artifacts", "bin");
        var ownPivot = Path.GetRelativePath(binRoot, AppContext.BaseDirectory)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .FirstOrDefault();

        foreach (var pivot in new[] { ownPivot, "debug" }.OfType<string>().Distinct())
        {
            var dll = Path.Combine(binRoot, project, pivot, file);
            if (File.Exists(dll))
            {
                return dll;
            }
        }

        var expected = Path.Combine(binRoot, project, ownPivot ?? "debug", file);
        Assert.Fail($"{file} not built: {expected}");
        return expected;
    }
}
