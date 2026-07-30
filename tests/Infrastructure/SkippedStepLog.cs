using System.Collections.Concurrent;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Collects the steps specs opted an engine out of (<see cref="SpecResult.SkippedSteps"/>) across an
/// aggregate conformance run, and prints them next to the pass count.
/// <para>
/// The aggregate suites collapse every spec into one <c>[Fact]</c> that reports "N passed". A spec
/// carrying <c>skipEngines</c> for the engine under test proves strictly less than that line implies,
/// and nothing else in the output distinguishes the two — the same shape of silence that let
/// <c>expectedFile</c> byte-compares pass while verifying nothing. So the count is reported whether
/// or not anything failed, and the entries name the step.
/// </para>
/// <para>
/// Thread-safe: the NR suites run specs through <c>Parallel.ForEachAsync</c>.
/// </para>
/// </summary>
public sealed class SkippedStepLog
{
    private readonly ConcurrentBag<string> _entries = [];

    /// <summary>Record whatever <paramref name="result"/> skipped, labelled with the spec name.</summary>
    public void Record(string specName, SpecResult result)
    {
        foreach (var skipped in result.SkippedSteps)
        {
            _entries.Add($"{specName}: {skipped}");
        }
    }

    public int Count => _entries.Count;

    /// <summary>
    /// Write the tally and each entry, or nothing at all when no spec skipped a step — which is the
    /// normal case, and one that should not add noise to every run.
    /// </summary>
    public void WriteTo(ITestOutputHelper output, string logPrefix)
    {
        if (_entries.IsEmpty)
        {
            return;
        }

        output.WriteLine($"{logPrefix}Skipped steps: {_entries.Count} (declared via skipEngines — not verified here)");
        foreach (var entry in _entries.Order(StringComparer.Ordinal))
        {
            output.WriteLine($"{logPrefix}  [SKIPPED] {entry}");
        }
    }
}
