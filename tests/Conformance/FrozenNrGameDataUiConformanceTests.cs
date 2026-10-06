using System.Collections.Concurrent;
using System.Diagnostics;
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
/// suite scales with the pool size <c>ConcurrencyPolicy</c> assigns to the <c>newrecruit-ui</c>
/// engine, instead of running serially on one page.
///
/// Skipped when .testdata/nr-editor/ or the Playwright browsers are missing (run setup.ps1).
///
/// Two <c>[Fact]</c> aggregates partition the suite: <see cref="KitchenSink"/>, which the every-push smoke
/// (<c>smoke-nr-editor-ui</c>) selects alone, and <see cref="OtherSpecs"/>; selecting the engine runs both.
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

    /// <summary>The kitchen-sink spec: proves the driver wires up, on every push.</summary>
    [Fact]
    public Task KitchenSink() => RunAsync(AggregateMode.KitchenSink);

    /// <summary>Every other applicable spec: with <see cref="KitchenSink"/>, the whole lane.</summary>
    [Fact]
    public Task OtherSpecs() => RunAsync(AggregateMode.OtherSpecs);

    private async Task RunAsync(AggregateMode part)
    {
        Assert.SkipWhen(!_fixture.Available,
            "NR Editor static files not found or Playwright browsers not installed (run setup.ps1) " +
            "— skipping frozen NR Editor GameData UI tests");

        var specsDir = SpecLoader.FindGameDataSpecsDirectory();
        Assert.SkipWhen(specsDir is null || !Directory.Exists(specsDir),
            "GameData specs directory not found — skipping");

        var pool = _fixture.EnginePool!;
        var failures = new ConcurrentBag<string>();
        var passed = 0;
        var skipped = 0;
        var expectedFailures = 0;

        // Load every spec upfront so parsing happens before parallel execution — the whole corpus for either
        // part, since it is what the [lane] line counts `applicable` from.
        var corpus = SpecLoader.DiscoverGameDataSpecs(specsDir!)
            .Select(s => (
                s.Path,
                Name: $"{s.Category}/{s.Id}",
                spec: SpecLoader.LoadGameData(s.Path)
            )).ToList();
        var loadedSpecs = corpus.Where(s => AggregateLaneRun.Drives(part, s.Name)).ToList();

        var lane = AggregateLaneRun.Start(_output, LogPrefix, "FrozenNrGameDataUi", part,
            selected: loadedSpecs.Count(s => s.spec.IsApplicableTo(EngineName)),
            applicable: corpus.Count(s => s.spec.IsApplicableTo(EngineName)));
        var stop = TestContext.Current.CancellationToken;

        try
        {
            await Parallel.ForEachAsync(
                loadedSpecs,
                new ParallelOptions { MaxDegreeOfParallelism = pool.Size, CancellationToken = stop },
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
                    var clock = Stopwatch.StartNew();

                    var runner = new GameDataRunner(engine, EngineName);
                    var result = runner.Run(spec);

                    if (result.Passed && expectedToFail)
                    {
                        failures.Add($"Spec '{specName}' was expected to fail on {EngineName} but now passes! " +
                            "Update the spec's engines field to remove the 'fail' expectation.");
                        lane.Completed(specName, AggregateLaneRun.UnexpectedPass, clock.Elapsed);
                        return;
                    }

                    if (!result.Passed && expectedToFail)
                    {
                        Interlocked.Increment(ref expectedFailures);
                        lane.Completed(specName, AggregateLaneRun.ExpectedFailure, clock.Elapsed);
                        return;
                    }

                    if (!result.Passed)
                    {
                        var msg = $"Spec '{specName}' failed with {result.Failures.Count} error(s):\n" +
                            string.Join("\n", result.Failures.Select((f, i) => $"  [{i + 1}] {f}"));
                        failures.Add(msg);
                        lane.Completed(specName, AggregateLaneRun.Failed, clock.Elapsed);
                        return;
                    }

                    Interlocked.Increment(ref passed);
                    lane.Completed(specName, AggregateLaneRun.Passed, clock.Elapsed);
                });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            lane.Stop(stop);
        }

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
