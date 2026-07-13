using BattleScribeSpec.Telemetry.Collector;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// Tests for <see cref="TelemetryRetention.Sweep"/> — the hygiene fix for artifacts/telemetry/
/// growing without bound (#304 deferred finding #2). Uses a private, uniquely-named directory per
/// test (never the shared OS temp root other collector tests write into directly) so these tests
/// can safely exercise real deletion without any risk of racing unrelated parallel tests.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TelemetryRetentionTests
{
    [Fact]
    public void Sweep_KeepsNewestSets_DeletesOlder()
    {
        var dir = MakeTestDirectory();
        try
        {
            // Five sets, oldest to newest, with distinct (explicitly stamped) write times — real
            // file creation can land within the same filesystem-timestamp tick, which would make
            // "oldest N" ambiguous, so the test controls time explicitly rather than relying on
            // real wall-clock spacing between File.Create calls.
            var bases = Enumerable.Range(0, 5).Select(i => Path.Combine(dir, $"run-{i}")).ToList();
            for (var i = 0; i < bases.Count; i++)
            {
                CreateSet(bases[i], DateTime.UtcNow.AddMinutes(-(bases.Count - i)));
            }

            // minAge: TimeSpan.Zero isolates the count-based ("keep newest N") behavior under
            // test here from the separate recency protection (covered by its own tests below) —
            // otherwise these deliberately-recent (minutes-old) timestamps would themselves fall
            // inside the production recency window and nothing would be deleted.
            TelemetryRetention.Sweep(dir, keepRuns: 2, minAge: TimeSpan.Zero);

            // Newest two (index 3, 4) survive in full...
            AssertSetExists(bases[3]);
            AssertSetExists(bases[4]);

            // ...and every file of the three oldest sets is gone.
            for (var i = 0; i < 3; i++)
            {
                AssertSetGone(bases[i]);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Sweep_AtOrBelowKeepRuns_DeletesNothing()
    {
        var dir = MakeTestDirectory();
        try
        {
            var bases = Enumerable.Range(0, 3).Select(i => Path.Combine(dir, $"run-{i}")).ToList();
            foreach (var b in bases)
            {
                CreateSet(b, DateTime.UtcNow);
            }

            TelemetryRetention.Sweep(dir, keepRuns: 3, minAge: TimeSpan.Zero);

            foreach (var b in bases)
            {
                AssertSetExists(b);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Sweep_MissingDirectory_DoesNotThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bsspec-retention-missing-{Guid.NewGuid():N}");
        Assert.False(Directory.Exists(dir));

        var exception = Record.Exception(() => TelemetryRetention.Sweep(dir, keepRuns: 5));

        Assert.Null(exception);
    }

    /// <summary>
    /// The falsifiable proof behind "must never delete an artifact belonging to a
    /// currently-running process" — reworked to test the actual, platform-agnostic mechanism
    /// (recency of last write), not Windows-only file-locking semantics. The original version of
    /// this test simulated a "live" set by holding its files open with <see cref="FileShare.None"/>
    /// and relying on <see cref="File.Delete(string)"/> throwing <see cref="IOException"/> — which
    /// is exactly the assumption that shipped the production bug: POSIX <c>unlink</c> succeeds on
    /// an open file (no exception, silent deletion), so that version of this test could never have
    /// failed on Linux even though the code it was meant to protect was already broken there. A
    /// "live" set is now simulated the way it actually differs from a stale one everywhere: a
    /// recent last-write time — no open handles, no platform-specific delete semantics, so this
    /// passes identically on Windows and Linux.
    /// </summary>
    /// <remarks>
    /// This also proves the per-file skip has to live inside the sweep loop rather than only as an
    /// outer fail-open wrapper: the recently-modified set must not only survive itself, it must not
    /// abort sweeping of OTHER, unrelated, perfectly-deletable stale sets that sort before it in the
    /// deletion order. And the final re-sweep (with time advanced past the recency window, via an
    /// explicit later <c>nowUtc</c> rather than a real sleep) proves the survival above was a
    /// deferral — exactly what "a crashed run's artifacts must eventually become collectable"
    /// requires — not a rule that nothing ever gets deleted (which would make this test pass
    /// vacuously).
    /// </remarks>
    [Fact]
    public void Sweep_SkipsARecentlyModifiedSet_WithoutAbortingOtherStaleSets_ThenCollectsItOnceAged()
    {
        var dir = MakeTestDirectory();
        try
        {
            var now = DateTime.UtcNow;
            var keptA = Path.Combine(dir, "run-kept-a");
            var keptB = Path.Combine(dir, "run-kept-b");
            var live = Path.Combine(dir, "run-live"); // 3rd-newest by write time — beyond keepRuns: 2
            var staleOld = Path.Combine(dir, "run-stale-old"); // oldest of all four, well past minAge
            CreateSet(staleOld, now.AddDays(-1));
            // "live" is modified recently enough to fall inside minAge (below), simulating a
            // concurrently-running process whose writer keeps flushing — but its rank by write
            // time still sorts it beyond keepRuns: 2, so it is a genuine deletion CANDIDATE that
            // only survives because of the recency check, not because it's one of the newest 2.
            CreateSet(live, now.AddMinutes(-2));
            CreateSet(keptA, now.AddMinutes(-1));
            CreateSet(keptB, now);

            var minAge = TimeSpan.FromMinutes(5);
            var exception = Record.Exception(() => TelemetryRetention.Sweep(dir, keepRuns: 2, minAge: minAge, nowUtc: now));

            Assert.Null(exception); // fail-open
            AssertSetExists(live); // recently modified — must survive even though it's a deletion candidate
            AssertSetGone(staleOld); // ...but a later, unrelated, genuinely-stale candidate must still go
            AssertSetExists(keptA);
            AssertSetExists(keptB);

            // Advance time (deterministically — no real sleep) past minAge: the same set, now
            // outside the recency window, is free to be collected by a later sweep, proving the
            // survival above was a deferral rather than permanent protection (no crash-leak).
            var later = now.Add(minAge).AddMinutes(1);
            TelemetryRetention.Sweep(dir, keepRuns: 2, minAge: minAge, nowUtc: later);
            AssertSetGone(live);
            AssertSetExists(keptA);
            AssertSetExists(keptB);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The other half of "never delete a currently-running process's artifact": the caller's own
    /// path is excluded outright, independent of the recency check above — proven here by making
    /// the "own" set the single oldest, best-aged deletion candidate in the directory (minAge:
    /// zero, so recency contributes nothing) and confirming it alone survives a sweep that deletes
    /// every other equally-stale candidate beyond keepRuns.
    /// </summary>
    [Fact]
    public void Sweep_NeverDeletesCurrentProcessOwnSet_RegardlessOfAgeOrKeepRuns()
    {
        var dir = MakeTestDirectory();
        try
        {
            var now = DateTime.UtcNow;
            var own = Path.Combine(dir, "run-own"); // oldest — would normally be the first deleted
            var otherStale = Path.Combine(dir, "run-other-stale");
            var newest = Path.Combine(dir, "run-newest");
            CreateSet(own, now.AddDays(-2));
            CreateSet(otherStale, now.AddDays(-1));
            CreateSet(newest, now);

            TelemetryRetention.Sweep(dir, keepRuns: 1, currentArtifactBasePath: own, minAge: TimeSpan.Zero, nowUtc: now);

            AssertSetExists(own); // excluded explicitly, despite being the oldest/best deletion candidate
            AssertSetGone(otherStale); // an equally-stale but unrelated candidate is still collected
            AssertSetExists(newest); // kept by keepRuns regardless
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static readonly string[] SuffixesForTest = [".traces.pb", ".metrics.pb", ".logs.pb"];

    private static string MakeTestDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bsspec-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CreateSet(string basePath, DateTime writeUtc)
    {
        foreach (var suffix in SuffixesForTest)
        {
            var path = basePath + suffix;
            File.WriteAllBytes(path, []);
            File.SetLastWriteTimeUtc(path, writeUtc);
        }
    }

    private static void AssertSetExists(string basePath)
    {
        foreach (var suffix in SuffixesForTest)
        {
            Assert.True(File.Exists(basePath + suffix), $"expected {basePath + suffix} to still exist");
        }
    }

    private static void AssertSetGone(string basePath)
    {
        foreach (var suffix in SuffixesForTest)
        {
            Assert.False(File.Exists(basePath + suffix), $"expected {basePath + suffix} to have been deleted");
        }
    }
}
