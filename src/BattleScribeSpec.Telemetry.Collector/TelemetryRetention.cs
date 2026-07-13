namespace BattleScribeSpec.Telemetry.Collector;

/// <summary>
/// Bounds <c>artifacts/telemetry/</c>'s growth. Every <c>bs-spec run --all</c>/<c>compare</c> and
/// every <c>dotnet test</c> writes a fresh, GUID/timestamp-named artifact SET (<c>.traces.pb</c> /
/// <c>.metrics.pb</c> / <c>.logs.pb</c>) and nothing ever cleaned them up — a developer's directory
/// grew forever. <see cref="Sweep"/> deletes the oldest sets, keeping the most recent
/// <see cref="DefaultKeepRuns"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Count-based, not age-based.</b> A day-based policy ("delete anything older than N days") does
/// not bound disk usage under a tight edit/test loop or a busy CI matrix — a developer can produce
/// far more than N days' worth of runs within a single day. Keeping the newest N SETS bounds the
/// directory regardless of how often runs happen, and N runs of history is already enough to answer
/// "did the last few changes regress something" without weeks of clutter. 20 is small (each set is a
/// handful of small protobuf files) but comfortably covers a normal debugging session.
/// </para>
/// <para>
/// <b>Fail-open, unconditionally.</b> Telemetry housekeeping must never fail — or even risk
/// slowing down — a real run. Every failure (missing directory, a locked file, a permissions
/// error) is caught and logged to stderr; nothing here ever throws out of <see cref="Sweep"/>.
/// </para>
/// <para>
/// <b>Never touches a currently-running process's artifact.</b> Two things make this safe without
/// any process/PID tracking: (1) callers invoke this BEFORE their own <see cref="OtlpArtifactWriter"/>
/// opens its files, so a run's own artifact set does not exist yet at sweep time and is never a
/// deletion candidate; (2) <see cref="OtlpArtifactWriter"/> opens all three of a set's files with
/// the default exclusive share mode, and only closes them together (on dispose) — so any OTHER
/// process still writing a set holds an OS-level lock on all three at once. Deleting a locked file
/// throws <see cref="IOException"/>, caught per-file and skipped, so a live set is left alone in
/// its entirety for a later sweep rather than partially deleted.
/// </para>
/// <para>
/// <b>Deliberately NOT wired into every <see cref="HarnessCollector.StartAsync"/> call.</b> Unit
/// tests (e.g. <c>TelemetryCollectorTests</c>) call <c>StartAsync</c> directly against ad hoc paths
/// under <see cref="Path.GetTempPath"/>, which many unrelated parallel tests share as a directory.
/// A directory-wide sweep run automatically on every such call would race those tests: a sibling
/// test's artifact can be fully written and closed, but not yet read back by its own assertions or
/// deleted by its own cleanup, at the moment an unrelated test's <c>StartAsync</c> call fires a
/// sweep of the same shared temp directory — the lock-based protection above only guards artifacts
/// that are still OPEN, not ones that are closed-but-pending-use. Scoping <see cref="Sweep"/> to an
/// explicit call at the three real production call sites (<c>RunBatch</c>, <c>CompareCommand</c>,
/// <c>TelemetryAssemblyFixture</c>) — each of which owns the entire <c>artifacts/telemetry/</c>
/// directory as its own well-known location — avoids that race entirely while still satisfying "on
/// collector start" for every real run.
/// </para>
/// </remarks>
public static class TelemetryRetention
{
    /// <summary>Default number of most-recent artifact sets to keep.</summary>
    public const int DefaultKeepRuns = 20;

    private static readonly string[] Suffixes = [".traces.pb", ".metrics.pb", ".logs.pb"];

    /// <summary>
    /// Delete artifact sets in <paramref name="artifactDirectory"/> beyond the most recent
    /// <paramref name="keepRuns"/>, by last-write time. Never throws.
    /// </summary>
    /// <param name="artifactDirectory">The telemetry artifact directory, e.g. <c>artifacts/telemetry</c>.</param>
    /// <param name="keepRuns">How many of the most recent artifact sets to retain.</param>
    public static void Sweep(string artifactDirectory, int keepRuns = DefaultKeepRuns)
    {
        try
        {
            SweepCore(artifactDirectory, keepRuns);
        }
        catch (Exception ex)
        {
            // Fail-open: a housekeeping bug must never be seen to threaten, let alone fail, a real
            // run. Logged (not silently swallowed) so a genuine problem is still discoverable.
            Console.Error.WriteLine($"[telemetry] retention sweep failed: {ex.Message}");
        }
    }

    private static void SweepCore(string artifactDirectory, int keepRuns)
    {
        if (!Directory.Exists(artifactDirectory))
        {
            return;
        }

        // Group the three sibling files of each run/compare-arm/xunit-run by their shared base
        // name (path minus the ".traces.pb"/".metrics.pb"/".logs.pb" suffix), keyed by the newest
        // last-write time among whichever of the three currently exist (a set can be incomplete —
        // e.g. it is still being written, or a prior sweep already lost a race on one file).
        var newestWriteUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var suffix in Suffixes)
        {
            foreach (var file in Directory.EnumerateFiles(artifactDirectory, "*" + suffix))
            {
                var baseName = file[..^suffix.Length];
                var writeUtc = File.GetLastWriteTimeUtc(file);
                if (!newestWriteUtc.TryGetValue(baseName, out var existing) || writeUtc > existing)
                {
                    newestWriteUtc[baseName] = writeUtc;
                }
            }
        }

        if (newestWriteUtc.Count <= keepRuns)
        {
            return;
        }

        var staleBaseNames = newestWriteUtc
            .OrderByDescending(kv => kv.Value)
            .Skip(keepRuns)
            .Select(kv => kv.Key);

        foreach (var baseName in staleBaseNames)
        {
            foreach (var suffix in Suffixes)
            {
                DeleteIfUnlocked(baseName + suffix);
            }
        }
    }

    private static void DeleteIfUnlocked(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Locked by whatever process still has it open (a currently-running run, or — on
            // Windows — an indexer/AV/preview-handler transiently holding it). Caught PER FILE
            // (not just by Sweep's outer fail-open wrapper) so one locked file can never abort
            // sweeping of later, unrelated, perfectly-deletable candidates — see
            // Sweep_SkipsALockedSet_WithoutAbortingOtherStaleSets_ThenCollectsItOnceUnlocked.
        }
        catch (UnauthorizedAccessException)
        {
            // Same treatment: a permissions/AV denial is not this sweep's problem to solve.
        }
    }
}
