using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared base class for running declarative YAML spec files against any IRosterEngine.
/// Eliminates duplication between BattleScribe, NewRecruit, and FrozenNewRecruit conformance tests.
/// </summary>
public abstract class ConformanceTestBase
{
    private readonly ITestOutputHelper _output;

    protected ConformanceTestBase(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Engine name used in spec YAML 'engines' field for applicability/expectation checks.</summary>
    protected abstract string EngineName { get; }

    /// <summary>Optional prefix for log messages (e.g., "[FROZEN]").</summary>
    protected virtual string LogPrefix => "";

    /// <summary>
    /// Return the engine to run the spec against, or null to skip the test.
    /// Implementations should call Assert.SkipWhen() for environment-gated engines.
    /// </summary>
    protected abstract IRosterEngine? GetEngine();

    /// <summary>
    /// Number of shards the roster UI suites can be split across in CI (see the <c>Shard</c> trait
    /// below and the matrix in <c>.github/workflows/ci.yml</c>). Changing this MUST be mirrored in
    /// the CI matrix.
    /// </summary>
    /// <remarks>
    /// Matches <see cref="GameDataConformanceTestBase.ShardCount"/> so one matrix can drive both
    /// halves of the BS UI stack: <c>thorough-ui-bs</c> already runs two shards for the gamedata
    /// lane, and the roster lane joins them rather than adding a third dimension.
    /// </remarks>
    public const int ShardCount = 2;

    public static TheoryDataRow<string, string>[] AllSpecs()
    {
        var specsDir = SpecLoader.FindRosterSpecsDirectory();
        if (specsDir is null || !Directory.Exists(specsDir))
        {
            return [];
        }

        return [.. SpecLoader.DiscoverSpecs(specsDir).Select(s =>
        {
            var specName = $"{s.Category}/{s.Id}";
            var row = new TheoryDataRow<string, string>(s.Path, specName);
            // Stable, process-independent shard assignment — string.GetHashCode is randomized per
            // run, which would make Shard filters disagree between CI matrix jobs and silently
            // drop or duplicate specs.
            row.Traits.Add("Shard", [(StableHash(specName) % ShardCount).ToString()]);
            try
            {
                var spec = SpecLoader.Load(s.Path);
                if (spec.Tags is { Count: > 0 })
                {
                    row.Traits.Add("Tag", [.. spec.Tags]);
                }
            }
            catch
            {
                // Spec load failure during discovery — emit untagged row
                // so execution reports the load error normally.
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

    /// <summary>
    /// Returns spec discovery data as simple tuples for use outside xUnit theory data.
    /// Used by parallel NR test runners.
    /// </summary>
    public static List<(string Path, string Name)> AllSpecPaths()
    {
        var specsDir = SpecLoader.FindRosterSpecsDirectory();
        if (specsDir is null || !Directory.Exists(specsDir))
        {
            return [];
        }

        return [.. SpecLoader.DiscoverSpecs(specsDir).Select(s => (s.Path, Name: $"{s.Category}/{s.Id}"))];
    }

    protected void RunSpec(string specPath, string specName)
    {
        var spec = SpecLoader.Load(specPath);

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

        var runner = new RosterRunner(engine, new DataSourceResolver(), EngineName);
        var result = runner.Run(spec);

        // What this engine was opted out of, printed on pass as well as fail — a green spec that
        // skipped steps proved less than a green spec that didn't, and the verdict alone won't say so.
        foreach (var skipped in result.SkippedSteps)
        {
            _output.WriteLine($"{LogPrefix}[SKIPPED] {skipped}");
        }

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
