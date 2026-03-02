using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs all declarative YAML spec files against the Oracle (BattleScribe Java) engine.
/// To add a new engine, create another test class with the same pattern and a different IRosterEngine.
/// </summary>
public sealed class SpecConformanceTests
{
    private readonly ITestOutputHelper _output;

    public SpecConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static IEnumerable<object[]> AllSpecs()
    {
        var specsDir = SpecLoader.FindSpecsDirectory();
        if (specsDir is null || !Directory.Exists(specsDir))
            yield break;
        foreach (var (path, id, category) in SpecLoader.DiscoverSpecs(specsDir))
        {
            yield return [path, $"{category}/{id}"];
        }
    }

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void OracleEngine(string specPath, string specName)
    {
        var spec = SpecLoader.Load(specPath);

        // Skip specs not applicable to the BattleScribe oracle engine
        if (!spec.IsApplicableTo("battlescribe"))
        {
            _output.WriteLine($"Skipping spec: {specName} — not applicable to battlescribe engine");
            return;
        }

        _output.WriteLine($"Running spec: {specName} — {spec.Description}");

        using var engine = new OracleRosterEngine();
        var runner = new SpecRunner(engine);
        var result = runner.Run(spec);

        if (!result.Passed)
        {
            var message = $"Spec '{specName}' failed with {result.Failures.Count} error(s):\n" +
                string.Join("\n", result.Failures.Select((f, i) => $"  [{i + 1}] {f}"));
            _output.WriteLine(message);
            Assert.Fail(message);
        }
    }
}
