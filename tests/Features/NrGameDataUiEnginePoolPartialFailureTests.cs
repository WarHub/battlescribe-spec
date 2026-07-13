using System.Diagnostics.Metrics;
using BattleScribeSpec.NrGameDataUiDriver;
using BattleScribeSpec.Telemetry;
using Microsoft.Playwright;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// #304 deferred hygiene finding 1: a construction-time exception partway through
/// <see cref="NrGameDataUiEnginePool.CreateFrozenAsync"/>'s per-context loop must not leak the
/// already-launched Chromium/contexts, nor leave <c>harness.resource.count</c> permanently
/// inflated with resources that no longer exist — no pool object is ever returned on failure, so
/// the caller has nothing to <c>DisposeAsync</c> through.
/// </summary>
/// <remarks>
/// <para>
/// This forces a REAL mid-loop failure against a real Chromium — there is no hermetic way to do
/// this, and a fake substitute here would prove nothing. The injection point:
/// <c>NrGameDataUiEngine.CreateFrozenInContextAsync</c> re-checks <c>File.Exists(index.html)</c> on
/// EVERY loop iteration (not just once, up front, the way the pool's own directory check runs).
/// Deleting a private copy's <c>index.html</c> the instant the SECOND context is acquired therefore
/// causes the second context's engine construction to throw a real
/// <see cref="FileNotFoundException"/>, deterministically, after the FIRST context/engine has
/// already fully succeeded — the exact "context 3 of 5" shape the fix's remarks describe (here with
/// N=2 rather than 5: fewer iterations makes for a faster, equally honest test).
/// </para>
/// <para>
/// <b>Why the deletion is race-free, not just "usually fine":</b> <c>ResourceMetrics.Acquired</c>
/// invokes registered <see cref="MeterListener"/> callbacks SYNCHRONOUSLY, on the very thread/
/// continuation that called it — i.e. the pool's own loop. So deleting the file inside this test's
/// measurement callback happens strictly BEFORE control returns to the pool loop's next statement
/// (<c>await NrGameDataUiEngine.CreateFrozenInContextAsync(...)</c>), whose very first two
/// statements — <c>Directory.Exists</c>/<c>File.Exists</c> — run before that method's own first
/// <c>await</c>. There is no timing window to get wrong.
/// </para>
/// <para>
/// <b>Coverage note (from the #304 hygiene cleanup pass):</b> this proves the pattern for
/// <c>NrGameDataUiEnginePool.CreateFrozenAsync</c> only. <c>NewRecruitEnginePool.CreateFrozenAsync</c>/
/// <c>CreateLiveAsync</c> share byte-for-byte the same guarded shape (launch browser, loop creating
/// contexts, <c>try</c>/<c>catch</c> → <c>DisposePartialConstructionAsync</c> → rethrow) but are NOT
/// independently covered by an equally direct real-mid-loop-failure test: unlike the GameData UI
/// engine, <c>NewRecruitRosterEngine</c>'s per-iteration work has no equivalently reliable,
/// synchronous, re-checked-every-iteration fault point to hook without depending on Playwright's
/// internal HAR-parsing/network timing, which was not confident-enough to build a non-flaky test
/// on. See the hygiene report for the explicit statement of this gap.
/// </para>
/// </remarks>
public sealed class NrGameDataUiEnginePoolPartialFailureTests
{
    private static readonly AsyncLocal<bool> IsThisTest = new();

    [Fact]
    [Trait("Category", "Conformance")]
    [Trait("Engine", "FrozenNrGameDataUi")]
    public async Task CreateFrozenAsync_MidLoopFailure_LeaksNothing_AndResourceMetricsReturnToZero()
    {
        var staticDir = NrGameDataUiEngine.FindFrozenStaticDir();
        Assert.SkipWhen(staticDir is null,
            "NR Editor static files not found (run setup.ps1) — skipping partial-failure pool test");

        // Work on a private COPY: deleting index.html to inject the fault must never touch the
        // shared fixture data other tests (possibly running concurrently, in other collections)
        // rely on.
        var copyDir = Path.Combine(Path.GetTempPath(), $"bsspec-nreditor-partial-{Guid.NewGuid():N}");
        CopyDirectory(staticDir!, copyDir);
        var indexHtml = Path.Combine(copyDir, "index.html");

        var events = new List<(string Kind, int Delta)>();
        var contextAcquiredCount = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HarnessTelemetry.MeterName &&
                    instrument.Name == "harness.resource.count")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<int>((_, measurement, tags, _) =>
        {
            if (!IsThisTest.Value)
            {
                return; // noise from a concurrently running, unrelated test
            }

            var kind = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "harness.resource.kind" && tag.Value is string k)
                {
                    kind = k;
                    break;
                }
            }

            if (kind != "browser" && kind != "browser-context")
            {
                return;
            }

            lock (events)
            {
                events.Add((kind, measurement));
            }

            // Fault injection — see the class remarks for why this is race-free.
            if (kind == "browser-context" && measurement > 0)
            {
                var acquired = Interlocked.Increment(ref contextAcquiredCount);
                if (acquired == 2 && File.Exists(indexHtml))
                {
                    File.Delete(indexHtml);
                }
            }
        });
        listener.Start();

        await BrowserResourceRaceGate.FrozenNrGameDataUi.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            IsThisTest.Value = true;
            Exception? thrown = null;
            var playwrightMissing = false;
            try
            {
                // concurrency: 3 — iteration 0 succeeds, iteration 1 is where the injected fault
                // fires, iteration 2 never starts (the loop aborts on iteration 1's exception).
                await NrGameDataUiEnginePool.CreateFrozenAsync(copyDir, concurrency: 3, headless: true);
            }
            catch (PlaywrightException)
            {
                playwrightMissing = true;
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
            finally
            {
                IsThisTest.Value = false;
            }

            Assert.SkipWhen(playwrightMissing,
                "Playwright browsers not installed — skipping partial-failure pool test");

            // The injected fault must actually have fired and propagated as the (uncleaned,
            // unwrapped) original exception — otherwise this test proves nothing about the
            // partial-failure path. A DIFFERENT exception type here would mean cleanup itself threw
            // and clobbered the original — see DisposePartialConstructionAsync's per-resource
            // try/catch, which exists precisely so cleanup failures can't do that.
            Assert.NotNull(thrown);
            Assert.IsType<FileNotFoundException>(thrown);

            // The load-bearing assertion: every resource this construction attempt acquired must
            // have been released too — net zero. Before the fix, DisposePartialConstructionAsync
            // did not exist: the browser and its first context were launched/created (their
            // Acquired() calls already fired), and nothing ever called Released() for them, because
            // the exception unwound straight out of the static factory method with no pool object
            // for the caller to dispose. That leaked a real OS Chromium process and permanently
            // inflated harness.resource.count.
            Assert.NotEmpty(events);
            Assert.Equal(0, events.Where(e => e.Kind == "browser").Sum(e => e.Delta));
            Assert.Equal(0, events.Where(e => e.Kind == "browser-context").Sum(e => e.Delta));

            // Specifically: one browser was launched and released, and both contexts that were
            // actually created (the first, fully-succeeded one, AND the second, whose engine
            // construction is where the fault fired) were released during cleanup — even the
            // second one, which never became a usable engine.
            Assert.Equal(1, events.Count(e => e.Kind == "browser" && e.Delta == 1));
            Assert.Equal(1, events.Count(e => e.Kind == "browser" && e.Delta == -1));
            Assert.Equal(2, events.Count(e => e.Kind == "browser-context" && e.Delta == 1));
            Assert.Equal(2, events.Count(e => e.Kind == "browser-context" && e.Delta == -1));
        }
        finally
        {
            BrowserResourceRaceGate.FrozenNrGameDataUi.Release();
            try
            { Directory.Delete(copyDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destFile = Path.Combine(destDir, relative);
            var destFileDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destFileDir))
            {
                Directory.CreateDirectory(destFileDir);
            }

            File.Copy(file, destFile, overwrite: true);
        }
    }
}
