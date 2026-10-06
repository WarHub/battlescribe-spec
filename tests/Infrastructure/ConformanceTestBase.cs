using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared base class for running declarative YAML spec files against any IRosterEngine.
/// The per-spec roster lanes derive from it (BsRoster, BsRosterUi), and the aggregate lanes take their
/// spec list from it.
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

    /// <summary>
    /// The engine whose expectations this lane inherits when a spec says nothing about
    /// <see cref="EngineName"/> specifically. Defaults to <see cref="EngineName"/>; a UI lane
    /// overrides it with the engine it drives.
    /// </summary>
    /// <remarks>
    /// A UI driver <em>produces</em> what its base engine produces but does not necessarily
    /// <em>support</em> what its base engine supports, so specs address the two separately and the
    /// most specific one wins — the rule <see cref="RosterRunner"/> and
    /// <c>FrozenNrUiRosterConformanceTests</c> already apply.
    /// <para>
    /// Without this, a lane running <c>battlescribe-ui</c> resolved every per-engine
    /// <c>expectedState</c> under its own name alone, found none, and fell through to the BASE
    /// assertion — the one written for the engine whose behaviour differs. Twenty roster specs
    /// carry a <c>battlescribe:</c> override precisely because BattleScribe diverges there; each
    /// was being asserted against the divergence rather than against it. That is a lane defect,
    /// not a driver one, and it is invisible from the failure text: the spec reports a plain
    /// value mismatch with no hint that a correct expectation for this engine exists in the file.
    /// </para>
    /// </remarks>
    protected virtual string BaseEngineName => EngineName;

    /// <summary>
    /// The spec's expectation for this lane — <c>pass</c>, <c>fail</c> or <c>skip</c> — resolved
    /// most-specific-first: this driver's own entry, else the base engine's.
    /// </summary>
    private string ExpectationFor(SpecFile spec)
        => spec.Engines is not null && spec.Engines.ContainsKey(EngineName)
            ? spec.GetExpectation(EngineName)
            : spec.GetExpectation(BaseEngineName);

    /// <summary>Optional prefix for log messages (e.g., "[FROZEN]").</summary>
    protected virtual string LogPrefix => "";

    /// <summary>
    /// Return the engine to run the spec against. An environment-gated engine calls
    /// <c>Assert.Skip</c>/<c>Assert.SkipWhen</c> with the reason; a <see langword="null"/> returned without
    /// one is reported as a skip too (<see cref="EngineOrSkip"/>), never as a pass.
    /// </summary>
    protected abstract IRosterEngine? GetEngine();

    /// <summary>
    /// <see cref="GetEngine"/>, or a skip: a row that drove no engine must not report Passed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a row that does not run is a skip, never a quiet return.</b> xunit records a test that
    /// returns as Passed. The strict zero-tests policy counts a Passed row as executed, and the
    /// engine-composition check counts it as the lane having run (<c>LaneComposition</c>) — so a row that
    /// returned without driving an engine was a phantom pass in both. That is not hypothetical: a filter for
    /// the since-deleted per-spec frozen NR roster class selected 403 rows whose engine was gated off, and
    /// exited 0 on the two specs that opt out of <c>newrecruit</c>, which returned before the gate was ever asked. Every not-applicable spec and every missing engine is now
    /// <c>Assert.Skip</c> with its reason.
    /// </para>
    /// </remarks>
    private IRosterEngine EngineOrSkip()
    {
        var engine = GetEngine();
        if (engine is null)
        {
            Assert.Skip($"{LogPrefix}no {EngineName} engine on this machine ({GetType().Name}.GetEngine returned none)");
        }

        return engine;
    }

    /// <summary>
    /// One row per roster spec. A row carries the spec's name (<c>category/id</c>) and nothing else,
    /// and is labelled with it, so the test reads
    /// <c>&lt;Namespace&gt;.&lt;Class&gt;.&lt;Method&gt; [category/id]</c> — never a checkout path, never cut
    /// short, and selectable with <c>--filter "DisplayName~&lt;id&gt;"</c> whatever the id's length.
    /// <see cref="TheoryRowIdentityTests"/> holds every data-driven theory to that.
    /// </summary>
    public static TheoryDataRow<string>[] AllSpecs()
        => [.. RosterSpecTags.Value.Select(s =>
        {
            var row = new TheoryDataRow<string>(s.Name) { Label = s.Name };
            if (s.Tags.Length > 0)
            {
                row.Traits.Add("Tag", [.. s.Tags]);
            }

            return row;
        })];

    /// <summary>
    /// Each roster spec's name and tags, read once per process: every per-spec roster lane takes its
    /// rows from <see cref="AllSpecs"/>, and discovery (and <see cref="TheoryRowIdentityTests"/>) asks
    /// once per lane — which parsed all 400-odd specs each time. The rows are still built fresh per call.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<(string Name, string[] Tags)>> RosterSpecTags = new(() =>
    {
        var specsDir = SpecLoader.FindRosterSpecsDirectory();
        if (specsDir is null || !Directory.Exists(specsDir))
        {
            return [];
        }

        return [.. SpecLoader.DiscoverSpecs(specsDir).Select(s =>
        {
            string[] tags;
            try
            {
                tags = SpecLoader.Load(s.Path).Tags?.ToArray() ?? [];
            }
            catch
            {
                // Spec load failure during discovery — emit untagged row
                // so execution reports the load error normally.
                tags = [];
            }

            return (SpecLoader.SpecName(s.Category, s.Id), tags);
        })];
    });

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

        return [.. SpecLoader.DiscoverSpecs(specsDir).Select(s => (s.Path, Name: SpecLoader.SpecName(s.Category, s.Id)))];
    }

    /// <summary>Run one <see cref="AddressingScenarios"/> scenario on this lane and require the addressing verdict.</summary>
    protected void RunAddressingScenario(string scenario)
    {
        var engine = EngineOrSkip();
        var result = new RosterRunner(engine, new DataSourceResolver(), BaseEngineName, EngineName)
            .Run(AddressingScenarios.Load(scenario));
        foreach (var failure in result.Failures)
        {
            _output.WriteLine($"{LogPrefix}{failure}");
        }

        if (AddressingScenarios.Judge(scenario, result) is { } wrong)
        {
            Assert.Fail($"{LogPrefix}{wrong}");
        }
    }

    /// <summary>Run the roster spec named <paramref name="specName"/> (<c>category/id</c>) on this lane.</summary>
    protected void RunSpec(string specName) => RunSpec(SpecLoader.ResolveRosterSpec(specName), specName);

    /// <summary>
    /// Run a spec file that is not in the corpus — <c>ConformanceLaneEngineIdentityTests</c> writes its
    /// own — on this lane, reporting it as <paramref name="specName"/>.
    /// </summary>
    protected void RunSpec(string specPath, string specName)
    {
        var spec = SpecLoader.Load(specPath);
        var expectation = ExpectationFor(spec);

        // A skip, not a return: a returned row is Passed, and a lane whose every other row skipped would
        // count as executed on this one (EngineOrSkip's remarks).
        if (string.Equals(expectation, "skip", StringComparison.OrdinalIgnoreCase))
        {
            var opted = spec.Engines?.ContainsKey(EngineName) == true ? EngineName : BaseEngineName;
            Assert.Skip($"{LogPrefix}{specName} is not applicable to the {EngineName} engine (the spec says engines: {{{opted}: skip}})");
        }

        var expectedToFail = string.Equals(expectation, "fail", StringComparison.OrdinalIgnoreCase);
        _output.WriteLine($"{LogPrefix}Running spec: {specName} — {spec.Description}{(expectedToFail ? " [EXPECTED FAILURE]" : "")}");

        var engine = EngineOrSkip();
        var runner = new RosterRunner(engine, new DataSourceResolver(), BaseEngineName, EngineName);
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
