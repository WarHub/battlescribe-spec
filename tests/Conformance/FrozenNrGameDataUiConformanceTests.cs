using System.Collections.Concurrent;
using BattleScribeSpec.GameData;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Runs all declarative GameData YAML specs against the NR Editor UI driver in frozen (static file
/// serving) mode. Actions are executed through real Playwright UI interactions (context menus, tree
/// clicks, property panel edits); state is read from NR Editor's Pinia editorStore after each
/// mutation.
///
/// Uses parallel execution with a browser-context pool (mirrors <see cref="FrozenNrRosterConformanceTests"/>):
/// each spec runs on its own engine/context acquired from the pool, so specs are isolated and the
/// suite scales with <c>NR_PARALLEL</c> instead of running serially on one page.
///
/// Skipped when .testdata/nr-editor/ is missing (run setup.ps1) or NR_EDITOR_UI_FROZEN_SKIP=true.
/// </summary>
[Collection("FrozenNrGameDataUi")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrGameDataUi")]
public sealed class FrozenNrGameDataUiConformanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly FrozenNrGameDataUiFixture _fixture;
    private const string EngineName = "newrecruit-ui";
    private const string LogPrefix = "[FROZEN-NR-EDITOR-UI] ";

    public FrozenNrGameDataUiConformanceTests(
        ITestOutputHelper output,
        FrozenNrGameDataUiFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    [Fact]
    public async Task AllSpecs()
    {
        Assert.SkipWhen(!_fixture.Available,
            "NR Editor static files not found (run setup.ps1) or NR_EDITOR_UI_FROZEN_SKIP=true " +
            "— skipping frozen NR Editor GameData UI tests");

        var specsDir = SpecLoader.FindGameDataSpecsDirectory();
        Assert.SkipWhen(specsDir is null || !Directory.Exists(specsDir),
            "GameData specs directory not found — skipping");

        var pool = _fixture.EnginePool!;
        var failures = new ConcurrentBag<string>();
        var passed = 0;
        var skipped = 0;
        var expectedFailures = 0;

        // NR_UI_SMOKE=1 restricts the run to kitchen-sink spec(s) — the fast CI lane proves the
        // engine wires up without running the full suite (which the thorough lane covers).
        var smoke = Environment.GetEnvironmentVariable("NR_UI_SMOKE") == "1";

        // Load every spec upfront so parsing happens before parallel execution.
        var loadedSpecs = SpecLoader.DiscoverGameDataSpecs(specsDir!)
            .Where(s => !smoke || $"{s.Category}/{s.Id}".Contains("kitchen-sink", StringComparison.Ordinal))
            .Select(s => (
                s.Path,
                Name: $"{s.Category}/{s.Id}",
                spec: SpecLoader.LoadGameData(s.Path)
            )).ToList();

        await Parallel.ForEachAsync(
            loadedSpecs,
            new ParallelOptions { MaxDegreeOfParallelism = pool.Size },
            async (item, ct) =>
            {
                var (specPath, specName, spec) = item;

                if (!spec.IsApplicableTo(EngineName))
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }

                var expectedToFail = spec.IsExpectedToFail(EngineName);

                using var pooled = await _fixture.AcquireAsync(ct);
                var engine = pooled.Engine;

                var runner = new GameDataRunner(engine, EngineName);
                var result = runner.Run(spec);

                if (result.Passed && expectedToFail)
                {
                    failures.Add($"Spec '{specName}' was expected to fail on {EngineName} but now passes! " +
                        "Update the spec's engines field to remove the 'fail' expectation.");
                    return;
                }

                if (!result.Passed && expectedToFail)
                {
                    Interlocked.Increment(ref expectedFailures);
                    return;
                }

                if (!result.Passed)
                {
                    var msg = $"Spec '{specName}' failed with {result.Failures.Count} error(s):\n" +
                        string.Join("\n", result.Failures.Select((f, i) => $"  [{i + 1}] {f}"));
                    failures.Add(msg);
                    return;
                }

                Interlocked.Increment(ref passed);
            });

        _output.WriteLine($"{LogPrefix}Results: {passed} passed, {skipped} skipped, {expectedFailures} expected failures, {failures.Count} failures");
        _output.WriteLine($"{LogPrefix}Pool size: {pool.Size} contexts");

        if (!failures.IsEmpty)
        {
            var message = $"{LogPrefix}{failures.Count} spec(s) failed:\n\n" +
                string.Join("\n\n", failures);
            _output.WriteLine(message);
            Assert.Fail(message);
        }
    }
}
