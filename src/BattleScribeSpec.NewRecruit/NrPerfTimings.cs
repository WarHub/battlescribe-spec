using System.Collections.Concurrent;
using System.Diagnostics;

namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Lightweight per-phase timing collector for NR test performance analysis.
/// Thread-safe for concurrent use (future parallel context pool).
/// </summary>
public sealed class NrPerfTimings
{
    private readonly ConcurrentDictionary<string, List<double>> _timings = new();
    private readonly Stopwatch _sw = new();
    private string? _currentPhase;

    /// <summary>
    /// Start timing a named phase. Call <see cref="EndPhase"/> to record.
    /// </summary>
    public void StartPhase(string phase)
    {
        _currentPhase = phase;
        _sw.Restart();
    }

    /// <summary>
    /// End the current phase and record its duration.
    /// </summary>
    public void EndPhase()
    {
        _sw.Stop();
        if (_currentPhase is not null)
        {
            var list = _timings.GetOrAdd(_currentPhase, _ => new List<double>());
            lock (list)
            {
                list.Add(_sw.Elapsed.TotalMilliseconds);
            }
        }
        _currentPhase = null;
    }

    /// <summary>
    /// Execute an action and record its duration under the given phase name.
    /// </summary>
    public void Time(string phase, Action action)
    {
        StartPhase(phase);
        try { action(); }
        finally { EndPhase(); }
    }

    /// <summary>
    /// Execute an async action and record its duration under the given phase name.
    /// </summary>
    public async Task TimeAsync(string phase, Func<Task> action)
    {
        StartPhase(phase);
        try { await action(); }
        finally { EndPhase(); }
    }

    /// <summary>
    /// Execute an async function and record its duration under the given phase name.
    /// </summary>
    public async Task<T> TimeAsync<T>(string phase, Func<Task<T>> func)
    {
        StartPhase(phase);
        try { return await func(); }
        finally { EndPhase(); }
    }

    /// <summary>
    /// Get a summary report of all recorded timings.
    /// </summary>
    public string GetReport()
    {
        var lines = new List<string> { "NR Performance Timings:" };
        foreach (var (phase, durations) in _timings.OrderBy(kv => kv.Key))
        {
            List<double> snapshot;
            lock (durations)
            {
                snapshot = [.. durations];
            }
            if (snapshot.Count == 0) continue;
            var avg = snapshot.Average();
            var total = snapshot.Sum();
            var min = snapshot.Min();
            var max = snapshot.Max();
            lines.Add($"  {phase}: count={snapshot.Count}, avg={avg:F1}ms, min={min:F1}ms, max={max:F1}ms, total={total:F0}ms");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Reset all timings.
    /// </summary>
    public void Reset() => _timings.Clear();
}
