using BattleScribeSpec.GameData;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared base class for running declarative YAML GameData specs against any IGameDataEngine.
/// Parallel to <see cref="ConformanceTestBase"/> for roster specs.
/// </summary>
public abstract class GameDataConformanceTestBase
{
    private readonly ITestOutputHelper _output;

    protected GameDataConformanceTestBase(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Engine name used in spec YAML 'engines' field for applicability checks.</summary>
    protected abstract string EngineName { get; }

    /// <summary>Optional prefix for log messages.</summary>
    protected virtual string LogPrefix => "";

    /// <summary>
    /// Return the engine to run the spec against, or null to skip the test.
    /// </summary>
    protected abstract IGameDataEngine? GetEngine();

    /// <summary>
    /// Number of shards the GameData UI suites can be split across in CI (see the
    /// <c>Shard</c> trait below and the matrix in <c>.github/workflows/ci.yml</c>).
    /// Changing this MUST be mirrored in the CI matrix.
    /// </summary>
    public const int ShardCount = 2;

    public static TheoryDataRow<string, string>[] AllGameDataSpecs()
    {
        var specsDir = SpecLoader.FindGameDataSpecsDirectory();
        if (specsDir is null || !Directory.Exists(specsDir))
        {
            return [];
        }

        return [.. SpecLoader.DiscoverGameDataSpecs(specsDir).Select(s =>
        {
            var specName = $"{s.Category}/{s.Id}";
            var row = new TheoryDataRow<string, string>(s.Path, specName);
            // Stable, process-independent shard assignment (string.GetHashCode is randomized
            // per run and would make Shard filters non-deterministic across CI matrix jobs).
            row.Traits.Add("Shard", [(StableHash(specName) % ShardCount).ToString()]);
            try
            {
                var spec = SpecLoader.LoadGameData(s.Path);
                if (spec.Tags is { Count: > 0 })
                {
                    row.Traits.Add("Tag", [.. spec.Tags]);
                }
            }
            catch
            {
                // Spec load failure during discovery — emit untagged row
            }
            return row;
        })];
    }

    /// <summary>Deterministic FNV-1a 32-bit hash, stable across processes and platforms.</summary>
    private static int StableHash(string value)
    {
        unchecked
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            var hash = offset;
            foreach (var c in value)
            {
                hash = (hash ^ c) * prime;
            }
            return (int)(hash & 0x7FFFFFFF);
        }
    }

    protected void RunSpec(string specPath, string specName)
    {
        var spec = SpecLoader.LoadGameData(specPath);

        if (!spec.IsApplicableTo(EngineName))
        {
            _output.WriteLine($"{LogPrefix}Skipping spec: {specName} — not applicable to {EngineName} engine");
            return;
        }

        var expectedToFail = spec.IsExpectedToFail(EngineName);
        _output.WriteLine($"{LogPrefix}Running spec: {specName} — {spec.Description}{(expectedToFail ? " [EXPECTED FAILURE]" : "")}");

        var engine = GetEngine();
        if (engine is null)
        {
            return;
        }

        var runner = new GameDataRunner(engine, EngineName);
        var result = runner.Run(spec);

        if (result.Passed && expectedToFail)
        {
            Assert.Fail($"{LogPrefix}Spec '{specName}' was expected to fail on {EngineName} but now passes! " +
                "Update the spec's engines field to remove the 'fail' expectation.");
        }

        if (!result.Passed && expectedToFail)
        {
            _output.WriteLine($"{LogPrefix}[EXPECTED FAILURE] Spec '{specName}' failed as expected on {EngineName}:");
            foreach (var (f, i) in result.Failures.Select((f, i) => (f, i)))
            {
                _output.WriteLine($"  [{i + 1}] {f}");
            }
            return;
        }

        if (!result.Passed)
        {
            var message = $"{LogPrefix}Spec '{specName}' failed with {result.Failures.Count} error(s):\n" +
                string.Join("\n", result.Failures.Select((f, i) => $"  [{i + 1}] {f}"));
            _output.WriteLine(message);
            Assert.Fail(message);
        }
    }
}
