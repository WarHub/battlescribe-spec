using System.Diagnostics.Metrics;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.NrGameDataUiDriver;
using BattleScribeSpec.Telemetry;
using Microsoft.Playwright;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// Closes the gap the Task 8 implementer flagged but left out of scope: the xUnit-facing engine
/// pools (<see cref="NewRecruitEnginePool"/>, <see cref="NrGameDataUiEnginePool"/>) launch Chromium
/// and create browser contexts <b>directly</b> — bypassing <see cref="NrBrowserHost"/> and the
/// context-owning path in <see cref="NewRecruitBrowser"/> that Task 8 instrumented — because they
/// hand the raw page/context to <c>NewRecruitBrowser.CreateFromContext</c> /
/// <c>NrGameDataUiEngine.CreateFrozenInContextAsync</c>, which by design does not own (and so does
/// not instrument) the resources it's given. Without this test, <c>harness.resource.count</c> would
/// silently under-report exactly where the harness's unbounded parallelism actually lives: the
/// per-collection browser-context pools that xUnit spins up concurrently.
/// </summary>
/// <remarks>
/// Uses a real Chromium via Playwright — the pools have no other lifecycle to instrument. Both
/// tests are gated (<c>Assert.SkipWhen</c>) on the same test data / browser-availability checks the
/// existing frozen NR fixtures use, and tagged with the matching <c>Engine</c> trait so they are
/// excluded from the <c>core</c> profile exactly like every other real-browser test in this repo —
/// <c>core</c> is the fast, browser-independent gate; these run under <c>nr-frozen</c> /
/// <c>nr-editor-ui-frozen</c> instead. See <see cref="ResourceMetricsTests"/> for the
/// <see cref="IsThisTest"/> rationale (an <see cref="AsyncLocal{T}"/> flag distinguishing this
/// test's own emissions from a concurrently running, unrelated test's on the same process-wide
/// static <see cref="Meter"/>).
/// </remarks>
public sealed class NewRecruitEnginePoolResourceMetricsTests
{
    private static readonly AsyncLocal<bool> IsThisTest = new();

    [Fact]
    [Trait("Category", "Conformance")]
    [Trait("Engine", "FrozenNrRoster")]
    public async Task CreateFrozenAsync_TracksBrowserAndContextLifecycle_ReturnsToZeroAfterDispose()
    {
        var harFile = HarRecorder.FindFrozenHarFile();
        Assert.SkipWhen(harFile is null,
            "Frozen HAR file not found (run setup.ps1) — skipping frozen NR pool resource-metrics test");

        var events = new List<(string Kind, int Delta)>();
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

            foreach (var tag in tags)
            {
                if (tag.Key == "harness.resource.kind" &&
                    tag.Value is string kind &&
                    (kind == "browser" || kind == "browser-context"))
                {
                    lock (events)
                    {
                        events.Add((kind, measurement));
                    }

                    break;
                }
            }
        });
        listener.Start();

        IsThisTest.Value = true;
        NewRecruitEnginePool? pool = null;
        try
        {
            pool = await NewRecruitEnginePool.CreateFrozenAsync(harFile!, concurrency: 1, headless: true);
        }
        catch (PlaywrightException)
        {
            // Playwright browsers not installed — handled below via SkipWhen.
        }
        finally
        {
            IsThisTest.Value = false;
        }

        Assert.SkipWhen(pool is null,
            "Playwright browsers not installed — skipping frozen NR pool resource-metrics test");

        IsThisTest.Value = true;
        await pool!.DisposeAsync();
        IsThisTest.Value = false;

        // concurrency: 1 makes the sequence deterministic: one browser launch, one context created
        // for it, then (in DisposeAsync) the context released before the browser.
        Assert.Equal(
            [("browser", 1), ("browser-context", 1), ("browser-context", -1), ("browser", -1)],
            events);
        Assert.Equal(0, events.Where(e => e.Kind == "browser").Sum(e => e.Delta));
        Assert.Equal(0, events.Where(e => e.Kind == "browser-context").Sum(e => e.Delta));
    }
}

/// <summary>See <see cref="NewRecruitEnginePoolResourceMetricsTests"/> — same gap, the NR Editor GameData UI pool.</summary>
public sealed class NrGameDataUiEnginePoolResourceMetricsTests
{
    private static readonly AsyncLocal<bool> IsThisTest = new();

    [Fact]
    [Trait("Category", "Conformance")]
    [Trait("Engine", "FrozenNrGameDataUi")]
    public async Task CreateFrozenAsync_TracksBrowserAndContextLifecycle_ReturnsToZeroAfterDispose()
    {
        var staticDir = NrGameDataUiEngine.FindFrozenStaticDir();
        Assert.SkipWhen(staticDir is null,
            "NR Editor static files not found (run setup.ps1) — skipping frozen NR Editor UI pool resource-metrics test");

        var events = new List<(string Kind, int Delta)>();
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

            foreach (var tag in tags)
            {
                if (tag.Key == "harness.resource.kind" &&
                    tag.Value is string kind &&
                    (kind == "browser" || kind == "browser-context"))
                {
                    lock (events)
                    {
                        events.Add((kind, measurement));
                    }

                    break;
                }
            }
        });
        listener.Start();

        IsThisTest.Value = true;
        NrGameDataUiEnginePool? pool = null;
        try
        {
            pool = await NrGameDataUiEnginePool.CreateFrozenAsync(staticDir!, concurrency: 1, headless: true);
        }
        catch (PlaywrightException)
        {
            // Playwright browsers not installed — handled below via SkipWhen.
        }
        finally
        {
            IsThisTest.Value = false;
        }

        Assert.SkipWhen(pool is null,
            "Playwright browsers not installed — skipping frozen NR Editor UI pool resource-metrics test");

        IsThisTest.Value = true;
        await pool!.DisposeAsync();
        IsThisTest.Value = false;

        // concurrency: 1 makes the sequence deterministic: one browser launch, one context created
        // for it, then (in DisposeAsync) the context released before the browser.
        Assert.Equal(
            [("browser", 1), ("browser-context", 1), ("browser-context", -1), ("browser", -1)],
            events);
        Assert.Equal(0, events.Where(e => e.Kind == "browser").Sum(e => e.Delta));
        Assert.Equal(0, events.Where(e => e.Kind == "browser-context").Sum(e => e.Delta));
    }
}
