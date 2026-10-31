using BattleScribeSpec.GameData;
using BattleScribeSpec.Roster;
using BattleScribeSpec.Scenario;

namespace BattleScribeSpec.Tests;

/// <summary>
/// One engine family's data editor and roster editor, as a scenario lane hands them to
/// <see cref="ScenarioRunner"/>.
/// </summary>
/// <param name="Data">The family's data editor.</param>
/// <param name="Roster">The family's roster editor.</param>
/// <param name="DisposeAfterSpec">
/// Whether the pair was made for this one spec and is disposed when it ends. A pair a fixture owns across
/// specs says <see langword="false"/>: the runner's <c>Cleanup</c> on both after every spec is what makes it
/// safe to share (see <see cref="IRosterEngine.Cleanup"/>).
/// </param>
public sealed record ScenarioEngines(IGameDataEngine Data, IRosterEngine Roster, bool DisposeAfterSpec = false);

/// <summary>
/// Shared base class for running scenario specs (<c>specs/scenario/</c>) against one engine family's
/// <see cref="IGameDataEngine"/> and <see cref="IRosterEngine"/>. Parallel to <see cref="ConformanceTestBase"/>
/// for roster specs and <see cref="GameDataConformanceTestBase"/> for GameData specs; see
/// <c>docs/adr/002-scenario-specs.md</c> for what a scenario is.
/// </summary>
public abstract class ScenarioConformanceTestBase
{
    private readonly ITestOutputHelper _output;

    protected ScenarioConformanceTestBase(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// The family's concrete engine name — <c>battlescribe-ui</c>, not <c>battlescribe</c> — which a scenario's
    /// <c>engines:</c> entries and per-engine overrides are addressed to.
    /// </summary>
    protected abstract string EngineName { get; }

    /// <summary>
    /// <see cref="EngineName"/> in its family: the spec's <c>engines:</c>, both kinds of block and the hand-offs
    /// resolve through it — the driver's own entry, else its family's (<see cref="ConformanceTestBase.Engine"/>).
    /// </summary>
    protected EngineIdentity Engine => EngineIdentity.Of(EngineName);

    /// <summary>Optional prefix for log messages (e.g., <c>[BS-SCENARIO] </c>).</summary>
    protected virtual string LogPrefix => "";

    /// <summary>
    /// Return the family's pair to run the spec against. An environment-gated lane calls <c>Assert.Skip</c>
    /// with the reason; a <see langword="null"/> returned without one is reported as a skip too, never as a
    /// pass (see <c>ConformanceTestBase.EngineOrSkip</c> for why a row that drove no engine must not return).
    /// </summary>
    protected abstract ScenarioEngines? GetEngines();

    /// <summary>
    /// One row per scenario spec, carrying and labelled with its name (<c>category/id</c>) — see
    /// <see cref="ConformanceTestBase.AllSpecs"/> for why a row never carries the path. Its tags become
    /// <c>Tag</c> traits; a row never carries an <c>Engine</c> or <c>Category</c> trait, which belong to the
    /// lane class (<c>TestProfileRegistryTests.NoDataSourceOrRow_CarriesAnEngineOrCategoryTrait</c>).
    /// </summary>
    public static TheoryDataRow<string>[] AllScenarioSpecs()
        => [.. ScenarioSpecTags.Value.Select(s =>
        {
            var row = new TheoryDataRow<string>(s.Name) { Label = s.Name };
            if (s.Tags.Length > 0)
            {
                row.Traits.Add("Tag", [.. s.Tags]);
            }

            return row;
        })];

    /// <summary>Each scenario spec's name and tags, read once per process — see <c>ConformanceTestBase.RosterSpecTags</c>.</summary>
    private static readonly Lazy<IReadOnlyList<(string Name, string[] Tags)>> ScenarioSpecTags = new(() =>
    {
        var specsDir = SpecLoader.FindScenarioSpecsDirectory();
        if (specsDir is null || !Directory.Exists(specsDir))
        {
            return [];
        }

        return [.. SpecLoader.DiscoverScenarioSpecs(specsDir).Select(s =>
        {
            string[] tags;
            try
            {
                tags = SpecLoader.LoadScenario(s.Path).Tags?.ToArray() ?? [];
            }
            catch
            {
                // Spec load failure during discovery — emit an untagged row, so execution reports the
                // load error normally.
                tags = [];
            }

            return (SpecLoader.SpecName(s.Category, s.Id), tags);
        })];
    });

    /// <summary>Run the scenario spec named <paramref name="specName"/> (<c>category/id</c>) on this lane.</summary>
    protected void RunSpec(string specName)
    {
        var spec = SpecLoader.LoadScenario(SpecLoader.ResolveScenarioSpec(specName));
        var expectation = spec.ExpectationFor(Engine);

        // A skip, not a return: a returned row is Passed (ConformanceTestBase.EngineOrSkip's remarks).
        if (expectation == EngineExpectation.Skip)
        {
            Assert.Skip($"{LogPrefix}{specName} is not applicable to the {EngineName} engine (the spec says engines: {{{spec.DeclaringKey(Engine)}: skip}})");
        }

        var expectedToFail = expectation == EngineExpectation.Fail;
        _output.WriteLine($"{LogPrefix}Running scenario: {specName} — {spec.Description}{(expectedToFail ? " [EXPECTED FAILURE]" : "")}");

        var engines = GetEngines();
        if (engines is null)
        {
            Assert.Skip($"{LogPrefix}no {EngineName} engine pair on this machine ({GetType().Name}.GetEngines returned none)");
        }

        SpecResult result;
        try
        {
            result = new ScenarioRunner(engines.Data, engines.Roster, Engine).Run(spec);
        }
        finally
        {
            if (engines.DisposeAfterSpec)
            {
                engines.Data.Dispose();
                engines.Roster.Dispose();
            }
        }

        // What this engine was opted out of, printed on pass as well as fail — a green spec that skipped
        // steps proved less than a green spec that didn't (ConformanceTestBase.RunSpec).
        foreach (var skipped in result.SkippedSteps)
        {
            _output.WriteLine($"{LogPrefix}[SKIPPED] {skipped}");
        }

        if (result.Passed && expectedToFail)
        {
            Assert.Fail($"{LogPrefix}Scenario '{specName}' was expected to fail on {EngineName} but now passes! " +
                "Update the spec's engines field to remove the 'fail' expectation.");
        }

        if (!result.Passed && expectedToFail)
        {
            _output.WriteLine($"{LogPrefix}[EXPECTED FAILURE] Scenario '{specName}' failed as expected on {EngineName}:");
            foreach (var (f, i) in result.Failures.Select((f, i) => (f, i)))
            {
                _output.WriteLine($"  [{i + 1}] {f}");
            }

            return;
        }

        if (!result.Passed)
        {
            var message = $"{LogPrefix}Scenario '{specName}' failed on {EngineName} with {result.Failures.Count} error(s):\n" +
                string.Join("\n", result.Failures.Select((f, i) => $"  [{i + 1}] {f}"));
            _output.WriteLine(message);
            Assert.Fail(message);
        }
    }
}
