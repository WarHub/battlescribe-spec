using System.Diagnostics;
using BattleScribeSpec.Batch;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests.Features;

public sealed class SpecSuiteRunnerTests
{
    private static string FindAdapterDll()
    {
        // Tests run from artifacts/bin/BattleScribeSpec.Tests/<pivot>/ — walk up to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BattleScribeSpec.slnx")))
        {
            dir = dir.Parent!;
        }

        Assert.NotNull(dir);
        var dll = Path.Combine(dir.FullName, "artifacts", "bin",
            "BattleScribeSpec.ReferenceAdapter", "debug", "bs-reference-adapter.dll");
        Assert.True(File.Exists(dll), $"Reference adapter not built: {dll}");
        return dll;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FilteredSuite_RunsAgainstReferenceAdapter()
    {
        var dll = FindAdapterDll();

        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            FilterPatterns = ["protocol/protocol-kitchen-sink"],
            EngineFilter = "battlescribe",
            ExpectedFailuresEngine = "battlescribe",
            AssertionEngine = "battlescribe",
            AdapterFactory = () => AdapterProcess.Start("dotnet", dll),
        });

        Assert.True(result.TotalSpecs > 0);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.ReportResults, r => r.Status == "passed");
    }

    [Fact]
    public async Task MissingSpecsDirectory_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            SpecsDirectory = Path.Combine(Path.GetTempPath(), "does-not-exist-bsspec"),
            AdapterFactory = () => throw new UnreachableException(),
        }));
    }
}
