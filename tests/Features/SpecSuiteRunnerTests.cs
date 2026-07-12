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
            AdapterFactory = _ => AdapterProcess.Start("dotnet", dll),
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
            AdapterFactory = _ => throw new UnreachableException(),
        }));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GameDataDomain_RunsOverTheSameAdapterPool()
    {
        var dll = FindAdapterDll();

        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            Domains = ["roster", "gamedata"],
            FilterPatterns = ["entry/add-entry-basic"],
            AdapterFactory = _ => AdapterProcess.Start("dotnet", dll),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.ReportResults, r => r.Category == "entry" && r.SpecId == "add-entry-basic" && r.Status == "passed");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MixedDomains_ParallelWorkers_RunBothDomains()
    {
        var dll = FindAdapterDll();

        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            Domains = ["roster", "gamedata"],
            Workers = 2,
            FilterPatterns = ["protocol/protocol-kitchen-sink", "entry/add-entry-basic"],
            EngineFilter = "battlescribe",
            ExpectedFailuresEngine = "battlescribe",
            AssertionEngine = "battlescribe",
            AdapterFactory = _ => AdapterProcess.Start("dotnet", dll),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.ReportResults, r => r.Category == "protocol" && r.SpecId == "protocol-kitchen-sink" && r.Status == "passed");
        Assert.Contains(result.ReportResults, r => r.Category == "entry" && r.SpecId == "add-entry-basic" && r.Status == "passed");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LegacyDefaultDomains_ExcludeGameData()
    {
        var dll = FindAdapterDll();

        var result = await SpecSuiteRunner.RunAsync(new SpecSuiteOptions
        {
            // Domains left at its default (roster-only) — the Runner shell's exact current behavior.
            FilterPatterns = ["entry/add-entry-basic"],
            AdapterFactory = _ => AdapterProcess.Start("dotnet", dll),
        });

        Assert.DoesNotContain(result.ReportResults, r => r.Category == "entry" && r.SpecId == "add-entry-basic");
    }
}
