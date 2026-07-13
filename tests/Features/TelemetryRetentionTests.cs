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

            TelemetryRetention.Sweep(dir, keepRuns: 2);

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

            TelemetryRetention.Sweep(dir, keepRuns: 3);

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
    /// currently-running process", PLUS the reason the per-file skip has to live inside the sweep
    /// loop rather than only as an outer fail-open wrapper around the whole sweep: a locked set
    /// (mirroring exactly how <c>OtlpArtifactWriter</c> holds a live run's three files open with
    /// the default exclusive share mode) must not only survive itself, it must not abort sweeping
    /// of OTHER, unrelated, perfectly-deletable stale sets that sort before it in the deletion
    /// order. An outer-only try/catch around the whole sweep would satisfy the first half (nothing
    /// throws out of <c>Sweep</c>) while silently failing the second half — the locked file's
    /// exception would unwind out of the loop entirely and leave every later candidate un-swept.
    /// Once the "process" releases its files, a later sweep is free to collect it too — proving the
    /// survival above was a deferral, not a rule that nothing ever gets deleted (which would make
    /// this test pass vacuously).
    /// </summary>
    [Fact]
    public void Sweep_SkipsALockedSet_WithoutAbortingOtherStaleSets_ThenCollectsItOnceUnlocked()
    {
        var dir = MakeTestDirectory();
        try
        {
            var keptA = Path.Combine(dir, "run-kept-a");
            var keptB = Path.Combine(dir, "run-kept-b");
            var live = Path.Combine(dir, "run-live"); // 2nd-oldest — processed BEFORE staleUnlocked below
            var staleUnlocked = Path.Combine(dir, "run-stale-unlocked"); // oldest of all four
            CreateSet(staleUnlocked, DateTime.UtcNow.AddMinutes(-10));
            CreateSet(live, DateTime.UtcNow.AddMinutes(-8));
            CreateSet(keptA, DateTime.UtcNow.AddMinutes(-2));
            CreateSet(keptB, DateTime.UtcNow.AddMinutes(-1));

            // Simulate a currently-running process: hold all three of "live"'s files open
            // exclusively, exactly as OtlpArtifactWriter does for the run that owns them. Sweep
            // processes stale candidates newest-first, so "live" (2nd-oldest) is attempted BEFORE
            // "staleUnlocked" (oldest) — the exact ordering needed to prove a lock on one candidate
            // doesn't stop the sweep from reaching the next one.
            var locks = SuffixesForTest.Select(suffix =>
                File.Open(live + suffix, FileMode.Open, FileAccess.Read, FileShare.None)).ToList();
            try
            {
                var exception = Record.Exception(() => TelemetryRetention.Sweep(dir, keepRuns: 2));

                Assert.Null(exception); // fail-open: a locked file must never surface as a thrown exception
                AssertSetExists(live); // the locked set must survive, even though it's a deletion candidate
                AssertSetGone(staleUnlocked); // ...but a later, unrelated, unlocked candidate must still go
                AssertSetExists(keptA);
                AssertSetExists(keptB);
            }
            finally
            {
                foreach (var l in locks)
                {
                    l.Dispose();
                }
            }

            // Now unlocked: a later sweep (still keepRuns: 2, still the oldest of the three left) collects it.
            TelemetryRetention.Sweep(dir, keepRuns: 2);
            AssertSetGone(live);
            AssertSetExists(keptA);
            AssertSetExists(keptB);
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
