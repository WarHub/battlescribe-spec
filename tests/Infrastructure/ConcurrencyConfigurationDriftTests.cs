using System.Runtime.CompilerServices;
using System.Text.Json;
using BattleScribeSpec.Concurrency;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Ensures xunit.runner.json maxParallelThreads values stay in sync with
/// <see cref="ConcurrencyPolicy.UndeclaredMemoryWorkerCap"/>.
///
/// Two static declarations of one bound: the policy constant and the xUnit runner JSON files.
/// The JSON is read by the xUnit runner before any of our code runs, so it cannot call the policy;
/// this test mechanically links them instead, so the literal cannot drift away from the constant
/// unnoticed. Note the two govern *different quantities* that currently share a value — the JSON
/// bounds the test suite's own xUnit thread count, the constant bounds an engine's worker count —
/// so if this goes red, decide deliberately what the xUnit bound should be rather than re-syncing
/// the literal reflexively.
/// </summary>
[Trait("Category", "Lint")]
public sealed class ConcurrencyConfigurationDriftTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot([CallerFilePath] string callerFilePath = "")
    {
        var dir = Path.GetDirectoryName(callerFilePath);
        while (dir is not null)
        {
            if (Directory.EnumerateFiles(dir, "*.slnx").Any())
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Could not find repository root (no *.slnx marker found) while traversing parents of '{callerFilePath}'.");
    }

    [Fact]
    public void XunitRunnerJsonMaxParallelThreadsMatchesConcurrencyPolicy()
    {
        var expectedCap = ConcurrencyPolicy.UndeclaredMemoryWorkerCap;

        var xunitFiles = new[]
        {
            Path.Combine(RepoRoot, "tests", "xunit.runner.json"),
            Path.Combine(RepoRoot, "tests", "BattleScribeSpec.Cli.Tests", "xunit.runner.json"),
        };

        var mismatches = new List<string>();

        foreach (var filePath in xunitFiles)
        {
            if (!File.Exists(filePath))
            {
                Assert.Fail($"Expected xunit.runner.json not found: {filePath}");
            }

            var jsonText = File.ReadAllText(filePath);
            var doc = JsonDocument.Parse(jsonText);

            if (!doc.RootElement.TryGetProperty("maxParallelThreads", out var maxParallelProp))
            {
                Assert.Fail($"maxParallelThreads property not found in {filePath}");
            }

            if (!maxParallelProp.TryGetInt32(out var maxParallel))
            {
                Assert.Fail($"maxParallelThreads is not an integer in {filePath}: {maxParallelProp}");
            }

            if (maxParallel != expectedCap)
            {
                mismatches.Add(
                    $"  {Path.GetRelativePath(RepoRoot, filePath)}: " +
                    $"maxParallelThreads = {maxParallel} (expected {expectedCap})");
            }
        }

        if (mismatches.Count > 0)
        {
            Assert.Fail(
                $"xunit.runner.json maxParallelThreads values do not match ConcurrencyPolicy.UndeclaredMemoryWorkerCap ({expectedCap}):\n" +
                $"{string.Join("\n", mismatches)}\n" +
                $"\n" +
                $"These are two declarations of one bound — and they govern different quantities that " +
                $"currently share a value (xUnit's own thread count vs an engine's worker count). " +
                $"Decide deliberately what the xUnit bound should be — do not just re-sync the literal.");
        }
    }
}
