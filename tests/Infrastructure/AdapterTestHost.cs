using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared helpers for tests that need a live adapter process. Centralizes the "walk up to the
/// repo root, then down into artifacts/bin" resolution that several suites duplicated inline
/// (e.g. the former <c>FindAdapterDll</c> in <c>SpecSuiteRunnerTests</c>).
/// </summary>
internal static class AdapterTestHost
{
    /// <summary>
    /// Starts the in-repo reference adapter (<c>src/BattleScribeSpec.ReferenceAdapter</c>,
    /// <c>bs-reference-adapter.dll</c>), which advertises unlimited parallelism (<c>MaxParallel = 0</c>
    /// in its engine registration) — it is the standard adapter used for
    /// <see cref="BattleScribeSpec.Batch.SpecSuiteRunner"/> integration tests.
    /// </summary>
    public static AdapterProcess StartReferenceAdapter() => AdapterProcess.Start("dotnet", FindAdapterDll());

    private static string FindAdapterDll()
    {
        // Tests run from artifacts/bin/BattleScribeSpec.Tests/<pivot>/ — walk up to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BattleScribeSpec.slnx")))
        {
            dir = dir.Parent!;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (BattleScribeSpec.slnx) from " + AppContext.BaseDirectory);
        }

        var dll = Path.Combine(dir.FullName, "artifacts", "bin",
            "BattleScribeSpec.ReferenceAdapter", "debug", "bs-reference-adapter.dll");
        if (!File.Exists(dll))
        {
            throw new InvalidOperationException($"Reference adapter not built: {dll}");
        }

        return dll;
    }
}
