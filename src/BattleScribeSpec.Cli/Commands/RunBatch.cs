using BattleScribeSpec.Batch;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Cli;

/// <summary>
/// Batch execution for <c>bs-spec run</c>: <c>--all</c> runs the whole spec suite over an engine
/// (via <see cref="SpecSuiteRunner"/>), and <c>--matrix</c> renders a markdown compatibility
/// matrix from <c>*-conformance.json</c> reports. Dispatched from <see cref="RunCommand"/>.
/// </summary>
internal static class RunBatch
{
    /// <summary>Resolved inputs for an <c>--all</c> batch run.</summary>
    internal sealed record BatchOptions(
        EngineSelection Selection,
        IReadOnlyList<string> Domains,
        string Output,
        string? SpecsDir,
        string? Filter,
        string? Tags,
        string? ReportPath,
        string? ExpectedFailures,
        string? AssertionEngine,
        int Workers);

    /// <summary>Run the full spec suite over the selected engine (<c>bs-spec run --all</c>).</summary>
    public static async Task<int> ExecuteAsync(BatchOptions options)
    {
        var selection = options.Selection;
        var engineLabel = selection.EngineName ?? selection.Display;

        // Effective worker count: the registry's MaxParallel ceiling first, then a one-shot
        // describe probe (spawn one process, describe, dispose) refines it when the live adapter
        // advertises a lower ceiling. Both maxima use 0 = unlimited.
        var registryMax = selection.Entry.MaxParallel;
        var registryClamped = registryMax > 0 && options.Workers > registryMax ? registryMax : options.Workers;

        var describedMax = 0;
        if (registryClamped > 1)
        {
            try
            {
                using var probe = selection.StartProcess();
                var described = await AdapterDescriber.DescribeAsync(probe);
                describedMax = described.Capabilities.MaxParallel;
            }
            catch (Exception ex)
            {
                Ui.Warn($"describe probe failed ({ex.Message}); proceeding without a describe-based worker clamp.");
            }
        }

        var workers = ClampWorkers(options.Workers, registryMax, describedMax, Ui.Warn);

        var filterPatterns = options.Filter?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            is { Length: > 0 } patterns ? patterns : null;
        var assertionEngine = options.AssertionEngine ?? selection.AssertionEngineName;

        Ui.Info($"Engine: {engineLabel}");
        Ui.Info($"Domains: {string.Join(", ", options.Domains)}");

        SpecSuiteResult result;
        try
        {
            result = await SpecSuiteRunner.RunAsync(
                new SpecSuiteOptions
                {
                    SpecsDirectory = options.SpecsDir,
                    FilterPatterns = filterPatterns,
                    TagFilter = TagFilter.Parse(options.Tags),
                    EngineFilter = selection.EngineName,
                    ExpectedFailuresEngine = options.ExpectedFailures,
                    AssertionEngine = assertionEngine,
                    Workers = workers,
                    Domains = options.Domains,
                    AdapterFactory = selection.StartProcess,
                },
                progressWriter: Console.Error);
        }
        catch (Exception ex)
        {
            Ui.Error(ex.Message);
            return 1;
        }

        switch (options.Output)
        {
            case "json":
                SpecSuiteOutput.WriteJson(result, selection.EngineName, Console.Out);
                break;
            case "github-actions":
                SpecSuiteOutput.WriteGitHubActions(result, selection.EngineName, Console.Out);
                break;
            default:
                SpecSuiteOutput.WriteSummary(result, selection.EngineName, Console.Out);
                break;
        }

        if (options.ReportPath is not null)
        {
            SpecSuiteOutput.WriteConformanceReport(result, options.ReportPath, selection.EngineName, assertionEngine, Console.Out);
        }

        return result.ExitCode;
    }

    /// <summary>
    /// Clamp a requested worker count to the engine's parallelism ceilings. Both
    /// <paramref name="registryMax"/> and <paramref name="describedMax"/> use 0 = unlimited (no
    /// clamp). Warns once per clamp. Pure/testable: it never spawns a process.
    /// </summary>
    internal static int ClampWorkers(int requested, int registryMax, int describedMax, Action<string> warn)
    {
        var effective = requested;
        if (registryMax > 0 && effective > registryMax)
        {
            warn($"engine registry limits parallelism to {registryMax}; reducing --workers {requested} to {registryMax}.");
            effective = registryMax;
        }

        if (describedMax > 0 && effective > describedMax)
        {
            warn($"engine described a max parallelism of {describedMax}; reducing workers to {describedMax}.");
            effective = describedMax;
        }

        return effective;
    }

    /// <summary>
    /// Port of the Runner's <c>--matrix</c> block: read <c>*-conformance.json</c> reports from
    /// <paramref name="matrixDir"/> and print a markdown compatibility matrix to stdout.
    /// </summary>
    public static int ExecuteMatrix(string matrixDir)
    {
        if (!Directory.Exists(matrixDir))
        {
            Ui.Error($"matrix directory not found: {matrixDir}");
            return 1;
        }

        var files = Directory.GetFiles(matrixDir, "*-conformance.json");
        if (files.Length == 0)
        {
            Ui.Error($"no *-conformance.json files found in {matrixDir}");
            return 1;
        }

        var reports = files.Select(CompatibilityMatrix.LoadReport).ToArray();
        Console.WriteLine(CompatibilityMatrix.GenerateMarkdown(reports));
        return 0;
    }
}
