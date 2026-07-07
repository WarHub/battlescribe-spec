namespace BattleScribeSpec.Cli.Tests;

/// <summary>
/// Covers the rewired <c>run</c> roster path (#271 PR 2): the CLI spawns the engine as a
/// child adapter process and drives it entirely over the JSON-line protocol via
/// <c>JsonProtocolEngine</c>, with artifact options gated by the describe handshake.
/// The end-to-end case drives the BattleScribe reference adapter as the <c>battlescribe</c>
/// identity (<c>battlescribe=dotnet:bs-reference-adapter.dll</c>).
/// </summary>
public sealed class RunProtocolTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_RosterSpec_OverReferenceAdapter_Passes()
    {
        var repoRoot = FindRepoRoot();
        var spec = Path.Combine(repoRoot, "specs", "roster", "protocol", "protocol-kitchen-sink.yaml");
        Assert.True(File.Exists(spec), $"Spec not found: {spec}");

        var adapterDll = FindReferenceAdapterDll(repoRoot);

        var exitCode = await Program.RunAsync(
            "run", spec, "--engine", $"battlescribe=dotnet:{adapterDll}");

        Assert.Equal(0, exitCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Run_BreakOnGamedataSpec_ParsesWithoutErrors()
    {
        // --break stays an engine-agnostic run option; on a gamedata target the run path
        // warns-and-ignores it (unchanged by the roster protocol rewire), so the invocation
        // must still parse cleanly rather than becoming a parse error.
        string[] args = ["run", "gamedata/entry/add-entry-basic", "--engine", "battlescribe", "--break", "2"];
        var parse = CommandFactory.CreateRootCommand().Parse(args);

        Assert.Empty(parse.Errors);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BattleScribeSpec.slnx")))
        {
            dir = dir.Parent!;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }

    private static string FindReferenceAdapterDll(string repoRoot)
    {
        // The reference adapter builds under the same pivot (debug/release) as this test; try
        // that pivot first, then fall back to debug (the pivot CI builds tests under).
        var pivot = ExtractPivot(AppContext.BaseDirectory);
        foreach (var candidatePivot in new[] { pivot, "debug" }.Where(p => p is not null).Distinct())
        {
            var dll = Path.Combine(repoRoot, "artifacts", "bin",
                "BattleScribeSpec.ReferenceAdapter", candidatePivot!, "bs-reference-adapter.dll");
            if (File.Exists(dll))
            {
                return dll;
            }
        }

        var expected = Path.Combine(repoRoot, "artifacts", "bin",
            "BattleScribeSpec.ReferenceAdapter", pivot ?? "debug", "bs-reference-adapter.dll");
        Assert.Fail($"Reference adapter not built: {expected}");
        return expected;
    }

    private static string? ExtractPivot(string baseDirectory)
    {
        var segments = Path.GetFullPath(baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var binIndex = Array.FindLastIndex(segments, s => s.Equals("bin", StringComparison.OrdinalIgnoreCase));
        return binIndex >= 0 && binIndex + 2 < segments.Length ? segments[binIndex + 2] : null;
    }
}
