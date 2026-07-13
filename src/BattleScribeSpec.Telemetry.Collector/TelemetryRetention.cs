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
/// slowing down — a real run. Every failure (missing directory, a permissions error) is caught and
/// logged to stderr; nothing here ever throws out of <see cref="Sweep"/>.
/// </para>
/// <para>
/// <b>Never touches a currently-running process's artifact — WITHOUT relying on file-locking.</b>
/// An earlier version of this type used <see cref="File.Delete(string)"/> throwing
/// <see cref="IOException"/> against an open, exclusively-shared <see cref="OtlpArtifactWriter"/>
/// handle as its only protection. That is a Windows-only guarantee: POSIX <c>unlink</c> detaches a
/// directory entry from its inode and succeeds even while another process still holds the file
/// open and keeps writing through its own descriptor — no exception, no signal, just silent data
/// loss for whichever live run owned that "deleted" file. Linux CI caught this
/// (<c>Sweep_SkipsALockedSet_WithoutAbortingOtherStaleSets_ThenCollectsItOnceUnlocked</c> failed
/// there while passing on Windows) because the two platforms' delete-of-an-open-file semantics are
/// fundamentally different, not just differently timed.
/// </para>
/// <para>
/// The replacement is two platform-agnostic checks, layered:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>The caller's own in-flight set is named and excluded explicitly.</b> Every real call site
/// already knows <c>artifactPath</c> before it calls <see cref="Sweep"/> (it's computed earlier in
/// the same method, then handed to <see cref="HarnessCollector.StartAsync"/>) and passes it as
/// <c>currentArtifactBasePath</c>. That base name is never a deletion candidate, full stop —
/// regardless of file timestamps, regardless of whether its files exist yet.
/// </description></item>
/// <item><description>
/// <b>Any set modified more recently than <c>minAge</c> is skipped, even beyond
/// <c>keepRuns</c>.</b> This is what protects OTHER, concurrently-running processes'
/// sets — the case file-locking never covered on Linux and the case no "current process" exclusion
/// can cover either, because a sweep has no way to know another process's path in advance. A set
/// under active writing has its last-write time advancing continuously (every OTLP export flushes
/// its file), so "recently modified" and "currently live" are, for practical purposes, the same
/// condition on both platforms. <see cref="DefaultMinAge"/> (5 minutes) is comfortably longer than
/// any real gap between flushes in a live run, while still short enough that a genuinely crashed
/// run's artifacts become collectable well within a normal debugging session rather than lingering
/// forever.
/// </description></item>
/// </list>
/// <para>
/// This is deliberately count-and-recency, not a marker/lock file: it needs no extra file, no
/// staleness protocol for a crashed run's marker, and "recently written" is already exactly the
/// signal a marker file would exist to approximate.
/// </para>
/// <para>
/// <b>Deliberately NOT wired into every <see cref="HarnessCollector.StartAsync"/> call.</b> Unit
/// tests (e.g. <c>TelemetryCollectorTests</c>) call <c>StartAsync</c> directly against ad hoc paths
/// under <see cref="Path.GetTempPath"/>, which many unrelated parallel tests share as a directory.
/// A directory-wide sweep run automatically on every such call would race those tests: a sibling
/// test's artifact can be fully written and closed, but not yet read back by its own assertions or
/// deleted by its own cleanup, at the moment an unrelated test's <c>StartAsync</c> call fires a
/// sweep of the same shared temp directory. Scoping <see cref="Sweep"/> to an explicit call at the
/// three real production call sites (<c>RunBatch</c>, <c>CompareCommand</c>,
/// <c>TelemetryAssemblyFixture</c>) — each of which owns the entire <c>artifacts/telemetry/</c>
/// directory as its own well-known location — avoids that race entirely while still satisfying "on
/// collector start" for every real run.
/// </para>
/// </remarks>
public static class TelemetryRetention
{
    /// <summary>Default number of most-recent artifact sets to keep.</summary>
    public const int DefaultKeepRuns = 20;

    /// <summary>
    /// Default recency window: a set whose newest file was written more recently than this is
    /// treated as possibly still being actively written by a live run and is never deleted,
    /// regardless of <see cref="DefaultKeepRuns"/>. See the type-level remarks for why this
    /// replaces file-locking as the platform-agnostic "is this live" signal.
    /// </summary>
    public static readonly TimeSpan DefaultMinAge = TimeSpan.FromMinutes(5);

    private static readonly string[] Suffixes = [".traces.pb", ".metrics.pb", ".logs.pb"];

    /// <summary>
    /// Delete artifact sets in <paramref name="artifactDirectory"/> beyond the most recent
    /// <paramref name="keepRuns"/>, by last-write time. Never throws.
    /// </summary>
    /// <param name="artifactDirectory">The telemetry artifact directory, e.g. <c>artifacts/telemetry</c>.</param>
    /// <param name="keepRuns">How many of the most recent artifact sets to retain.</param>
    /// <param name="currentArtifactBasePath">
    /// This process's own artifact base path (the same value about to be, or already, passed to
    /// <see cref="HarnessCollector.StartAsync"/>), if known. Never deleted, regardless of age or
    /// <paramref name="keepRuns"/>. Optional because callers that sweep a directory they don't
    /// themselves own (or before deciding their own path) can still rely solely on the recency
    /// check below.
    /// </param>
    /// <param name="minAge">
    /// How recently a set may have been modified before it becomes eligible for deletion at all.
    /// Defaults to <see cref="DefaultMinAge"/>.
    /// </param>
    /// <param name="nowUtc">The current time, for deterministic testing. Defaults to <see cref="DateTime.UtcNow"/>.</param>
    public static void Sweep(
        string artifactDirectory,
        int keepRuns = DefaultKeepRuns,
        string? currentArtifactBasePath = null,
        TimeSpan? minAge = null,
        DateTime? nowUtc = null)
    {
        try
        {
            SweepCore(artifactDirectory, keepRuns, currentArtifactBasePath, minAge ?? DefaultMinAge, nowUtc ?? DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // Fail-open: a housekeeping bug must never be seen to threaten, let alone fail, a real
            // run. Logged (not silently swallowed) so a genuine problem is still discoverable.
            Console.Error.WriteLine($"[telemetry] retention sweep failed: {ex.Message}");
        }
    }

    private static void SweepCore(
        string artifactDirectory, int keepRuns, string? currentArtifactBasePath, TimeSpan minAge, DateTime nowUtc)
    {
        if (!Directory.Exists(artifactDirectory))
        {
            return;
        }

        // Group the three sibling files of each run/compare-arm/xunit-run by their shared base
        // name (path minus the ".traces.pb"/".metrics.pb"/".logs.pb" suffix), keyed by the newest
        // last-write time among whichever of the three currently exist (a set can be incomplete —
        // e.g. it is still being written, or a prior sweep already lost a race on one file).
        //
        // Ordinal, NOT OrdinalIgnoreCase: every baseName here comes verbatim from
        // Directory.EnumerateFiles (real, on-disk casing), and ext4/most Linux filesystems are
        // case-sensitive — "run-A" and "run-a" are two different files there. Case-folding the key
        // would incorrectly conflate two distinct artifact sets into one on a case-sensitive
        // filesystem (a Windows-only assumption, same class of bug as the locking one this file
        // used to have).
        var newestWriteUtc = new Dictionary<string, DateTime>(StringComparer.Ordinal);
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

        var normalizedCurrent = currentArtifactBasePath is null ? null : Path.GetFullPath(currentArtifactBasePath);

        var staleCandidates = newestWriteUtc
            .OrderByDescending(kv => kv.Value)
            .Skip(keepRuns);

        foreach (var (baseName, writeUtc) in staleCandidates)
        {
            if (normalizedCurrent is not null
                && string.Equals(Path.GetFullPath(baseName), normalizedCurrent, StringComparison.Ordinal))
            {
                continue; // this process's own set — never a deletion candidate.
            }

            if (nowUtc - writeUtc < minAge)
            {
                // Recently modified: indistinguishable from "a live run is still writing this",
                // so treat it as such and defer to a later sweep. This is the platform-agnostic
                // replacement for the old lock-based skip — it protects a concurrently-running
                // OTHER process's set too, which no "current process" exclusion alone could ever
                // cover (a sweep has no way to know another process's path in advance).
                continue;
            }

            foreach (var suffix in Suffixes)
            {
                DeleteIfPossible(baseName + suffix);
            }
        }
    }

    private static void DeleteIfPossible(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Genuinely unexpected at this point (recency/current-path already excluded live
            // sets) — e.g. a Windows indexer/AV/preview-handler transiently holding the file open.
            // Caught PER FILE (not just by Sweep's outer fail-open wrapper) so one such failure can
            // never abort sweeping of later, unrelated, perfectly-deletable candidates.
        }
        catch (UnauthorizedAccessException)
        {
            // Same treatment: a permissions/AV denial is not this sweep's problem to solve.
        }
    }
}
