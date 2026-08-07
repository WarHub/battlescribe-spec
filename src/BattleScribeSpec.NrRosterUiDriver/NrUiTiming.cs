using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace BattleScribeSpec.NrRosterUiDriver;

/// <summary>
/// Per-phase wall-clock accounting for the NR UI driver, enabled by <c>NR_UI_TIMINGS=1</c>.
/// </summary>
/// <remarks>
/// <para>
/// Exists because the lane's cost was being argued from arithmetic — "seven sleeps totalling 6.3s
/// times 363 specs" — which says what the driver WAITS but not what it SPENDS. The two differ:
/// a sleep that overlaps work NR was doing anyway costs less than its constant, and a condition
/// wait that usually returns instantly can still have a long tail nobody sees in an average.
/// Removing a wait on the strength of its constant alone is guessing with extra steps.
/// </para>
/// <para>
/// Deliberately NOT <see cref="BattleScribeSpec.NewRecruit.NrPerfTimings"/>, which keeps one
/// <see cref="Stopwatch"/> and one current-phase field on the instance: nesting a phase inside
/// another restarts the shared stopwatch, so the outer phase records the inner one's duration.
/// Every measurement here is nested (a step inside setup inside a spec), so each call owns its
/// stopwatch instead.
/// </para>
/// <para>
/// Phase names are hierarchical by convention (<c>create-roster/select-faction</c>) so a report
/// reads as a breakdown. Percentages are of total measured time, not of the lane — nested phases
/// double-count against their parents by design, which is what makes a parent's unexplained
/// remainder visible.
/// </para>
/// </remarks>
public static class NrUiTiming
{
    private sealed class Bucket
    {
        public int Count;
        public double TotalMs;
        public double MaxMs;
        public double MinMs = double.MaxValue;
    }

    private static readonly ConcurrentDictionary<string, Bucket> Buckets = new();

    /// <summary>Whether measurement is on. Off by default: the lane must not pay for instrumentation.</summary>
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("NR_UI_TIMINGS") is "1" or "true";

    /// <summary>Times <paramref name="body"/> under <paramref name="phase"/> and returns its result.</summary>
    public static async Task<T> MeasureAsync<T>(string phase, Func<Task<T>> body)
    {
        if (!Enabled)
        {
            return await body();
        }

        var sw = Stopwatch.StartNew();
        try
        {
            return await body();
        }
        finally
        {
            sw.Stop();
            Record(phase, sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Times <paramref name="body"/> under <paramref name="phase"/>.</summary>
    public static async Task MeasureAsync(string phase, Func<Task> body)
        => await MeasureAsync(phase, async () => { await body(); return true; });

    private static void Record(string phase, double ms)
    {
        var b = Buckets.GetOrAdd(phase, _ => new Bucket());
        lock (b)
        {
            b.Count++;
            b.TotalMs += ms;
            b.MaxMs = Math.Max(b.MaxMs, ms);
            b.MinMs = Math.Min(b.MinMs, ms);
        }
    }

    /// <summary>Clears all recorded phases.</summary>
    public static void Reset() => Buckets.Clear();

    /// <summary>
    /// A breakdown ordered by total time spent — the order that answers "what do I fix first".
    /// </summary>
    public static string Report(int specCount = 0)
    {
        if (Buckets.IsEmpty)
        {
            return "NR UI timings: nothing recorded (set NR_UI_TIMINGS=1).";
        }

        var rows = Buckets
            .Select(kv =>
            {
                lock (kv.Value)
                {
                    return (Phase: kv.Key, kv.Value.Count, kv.Value.TotalMs, kv.Value.MaxMs, kv.Value.MinMs);
                }
            })
            .OrderByDescending(r => r.TotalMs)
            .ToList();

        var grand = rows.Where(r => !r.Phase.Contains('/', StringComparison.Ordinal)).Sum(r => r.TotalMs);
        var lines = new List<string>
        {
            $"NR UI timings — {rows.Count} phases"
                + (specCount > 0 ? $", {specCount} specs" : "")
                + $", top-level total {grand / 1000:F1}s",
            $"{"phase",-44} {"count",6} {"total s",9} {"avg ms",9} {"min",7} {"max",8} {"per spec",9}",
        };

        foreach (var r in rows)
        {
            var perSpec = specCount > 0
                ? (r.TotalMs / specCount).ToString("F0", CultureInfo.InvariantCulture) + "ms"
                : "-";
            lines.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0,-44} {1,6} {2,9:F1} {3,9:F1} {4,7:F0} {5,8:F0} {6,9}",
                r.Phase, r.Count, r.TotalMs / 1000, r.TotalMs / r.Count, r.MinMs, r.MaxMs, perSpec));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
