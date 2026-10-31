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

    /// <summary>The engine this lane drives, as specs address it — <c>battlescribe-ui</c>, not <c>battlescribe</c>.</summary>
    protected abstract string EngineName { get; }

    /// <summary>
    /// <see cref="EngineName"/> in its family: the spec's <c>engines:</c> and every per-engine override resolve
    /// through it — the driver's own entry, else its family's (<see cref="ConformanceTestBase.Engine"/>).
    /// </summary>
    /// <remarks>
    /// This base used to read the exact name alone, and hand the runner no family, so a <c>battlescribe:</c>
    /// override never reached the <c>battlescribe-ui</c> lane and <c>battlescribe: skip</c> did not skip it —
    /// the lane defect <see cref="ConformanceTestBase"/> had already fixed for roster specs, one base over
    /// (#573). Specs coped by restating the family's entry under the driver's name.
    /// </remarks>
    protected EngineIdentity Engine => EngineIdentity.Of(EngineName);

    /// <summary>Optional prefix for log messages.</summary>
    protected virtual string LogPrefix => "";

    /// <summary>
    /// Return the engine to run the spec against. An environment-gated engine calls <c>Assert.Skip</c> with
    /// the reason; a <see langword="null"/> returned without one is reported as a skip too, never as a pass
    /// (see <c>ConformanceTestBase.EngineOrSkip</c> for why a row that drove no engine must not return).
    /// </summary>
    protected abstract IGameDataEngine? GetEngine();

    /// <summary>
    /// One row per GameData spec, carrying and labelled with its name (<c>category/id</c>) — see
    /// <see cref="ConformanceTestBase.AllSpecs"/> for why a row never carries the path.
    /// </summary>
    public static TheoryDataRow<string>[] AllGameDataSpecs()
        => [.. GameDataSpecTags.Value.Select(s =>
        {
            var row = new TheoryDataRow<string>(s.Name) { Label = s.Name };
            if (s.Tags.Length > 0)
            {
                row.Traits.Add("Tag", [.. s.Tags]);
            }

            return row;
        })];

    /// <summary>Each GameData spec's name and tags, read once per process — see <c>ConformanceTestBase.RosterSpecTags</c>.</summary>
    private static readonly Lazy<IReadOnlyList<(string Name, string[] Tags)>> GameDataSpecTags = new(() =>
    {
        var specsDir = SpecLoader.FindGameDataSpecsDirectory();
        if (specsDir is null || !Directory.Exists(specsDir))
        {
            return [];
        }

        return [.. SpecLoader.DiscoverGameDataSpecs(specsDir).Select(s =>
        {
            string[] tags;
            try
            {
                tags = SpecLoader.LoadGameData(s.Path).Tags?.ToArray() ?? [];
            }
            catch
            {
                // Spec load failure during discovery — emit untagged row
                tags = [];
            }

            return (SpecLoader.SpecName(s.Category, s.Id), tags);
        })];
    });

    /// <summary>Run the GameData spec named <paramref name="specName"/> (<c>category/id</c>) on this lane.</summary>
    protected void RunSpec(string specName) => RunSpec(SpecLoader.ResolveGameDataSpec(specName), specName);

    /// <summary>
    /// Run a GameData spec file that is not in the corpus — <c>ConformanceLaneEngineIdentityTests</c> writes its
    /// own — on this lane, reporting it as <paramref name="specName"/>.
    /// </summary>
    protected void RunSpec(string specPath, string specName)
    {
        var spec = SpecLoader.LoadGameData(specPath);

        var expectation = spec.ExpectationFor(Engine);

        // A skip, not a return: a returned row is Passed (ConformanceTestBase.EngineOrSkip's remarks).
        if (expectation == EngineExpectation.Skip)
        {
            Assert.Skip($"{LogPrefix}{specName} is not applicable to the {EngineName} engine (the spec says engines: {{{spec.DeclaringKey(Engine)}: skip}})");
        }

        var expectedToFail = expectation == EngineExpectation.Fail;
        _output.WriteLine($"{LogPrefix}Running spec: {specName} — {spec.Description}{(expectedToFail ? " [EXPECTED FAILURE]" : "")}");

        var engine = GetEngine();
        if (engine is null)
        {
            Assert.Skip($"{LogPrefix}no {EngineName} engine on this machine ({GetType().Name}.GetEngine returned none)");
        }

        var runner = new GameDataRunner(engine, Engine);
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
