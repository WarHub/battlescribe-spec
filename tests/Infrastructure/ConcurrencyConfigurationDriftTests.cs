using System.Runtime.CompilerServices;
using System.Text.Json;
using BattleScribeSpec.Concurrency;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Ensures xunit.runner.json maxParallelThreads values stay in sync with
/// <see cref="ConcurrencyPolicy.ProvisionalUnmeasuredMemoryCap"/>.
///
/// Two static declarations of one bound: the policy constant and the xUnit runner JSON files.
/// This test mechanically links them, so when Task 9 retires the provisional cap,
/// the test goes red and forces a deliberate conversation about what the xUnit bound should become.
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
        var expectedCap = ConcurrencyPolicy.ProvisionalUnmeasuredMemoryCap;

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
                $"xunit.runner.json maxParallelThreads values do not match ConcurrencyPolicy.ProvisionalUnmeasuredMemoryCap ({expectedCap}):\n" +
                $"{string.Join("\n", mismatches)}\n" +
                $"\n" +
                $"These are two declarations of one bound. If the provisional cap was retired (plan Task 9), " +
                $"decide deliberately what the xUnit bound should be — do not just re-sync the literal.");
        }
    }
}
