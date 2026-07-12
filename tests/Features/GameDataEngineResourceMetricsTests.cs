using System.Diagnostics.Metrics;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.NrGameDataUiDriver;
using BattleScribeSpec.Telemetry;
using Microsoft.Playwright;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// Closes the last resource-instrumentation gap: <see cref="NewRecruitGameDataEngine"/> and
/// <see cref="NrGameDataUiEngine"/> each launch their own Chromium and (in frozen mode) their own
/// browser context directly — used by the single-engine fixtures <c>FrozenNrGameDataFixture</c> and
/// <c>LiveNrGameDataUiFixture</c> — completely bypassing <see cref="NrBrowserHost"/>/
/// <see cref="NewRecruitBrowser"/> and the already-instrumented engine pools
/// (<see cref="NewRecruitEnginePool"/>, <see cref="NrGameDataUiEnginePool"/>). Without this,
/// <c>harness.resource.count</c> silently under-reports every single-engine (non-pooled) NR
/// GameData spec run.
/// </summary>
/// <remarks>
/// Uses a real Chromium via Playwright — these engines have no other lifecycle to instrument. Both
/// tests exercise the <b>frozen</b> creation path (<c>CreateFrozenAsync</c>), which is the only
/// self-owned path that is hermetically testable without live network access (see the "not
/// covered" note below for the live path). Gated with <c>Assert.SkipWhen</c> on the same static
/// test-data / Playwright-availability checks the existing frozen NR fixtures use, and tagged with
/// a matching <c>Engine</c> trait so they are excluded from the <c>core</c> profile exactly like
/// every other real-browser test in this repo. See
/// <see cref="NewRecruitEnginePoolResourceMetricsTests"/> for the <see cref="AsyncLocal{T}"/>
/// <c>IsThisTest</c> rationale (distinguishing this test's own emissions from a concurrently
/// running, unrelated test's on the same process-wide static <see cref="Meter"/>).
/// </remarks>
public sealed class NewRecruitGameDataEngineResourceMetricsTests
{
    private static readonly AsyncLocal<bool> IsThisTest = new();

    [Fact]
    [Trait("Category", "Conformance")]
    [Trait("Engine", "FrozenNrGameData")]
    public async Task CreateFrozenAsync_TracksBrowserAndContextLifecycle_ReturnsToZeroAfterDispose()
    {
        var staticDir = NewRecruitGameDataEngine.FindFrozenStaticDir();
        Assert.SkipWhen(staticDir is null,
            "NR Editor static files not found (run setup.ps1) — skipping NewRecruitGameDataEngine resource-metrics test");

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
        NewRecruitGameDataEngine? engine = null;
        try
        {
            engine = await NewRecruitGameDataEngine.CreateFrozenAsync(staticDir!, headless: true);
        }
        catch (PlaywrightException)
        {
            // Playwright browsers not installed — handled below via SkipWhen.
        }
        finally
        {
            IsThisTest.Value = false;
        }

        Assert.SkipWhen(engine is null,
            "Playwright browsers not installed — skipping NewRecruitGameDataEngine resource-metrics test");

        IsThisTest.Value = true;
        engine!.Dispose();
        IsThisTest.Value = false;

        // Deterministic single-engine sequence: one browser launch, one context created for it,
        // then (in Dispose/DisposeAsync) the context and browser released together in the finally.
        Assert.Equal(
            [("browser", 1), ("browser-context", 1), ("browser-context", -1), ("browser", -1)],
            events);
        Assert.Equal(0, events.Where(e => e.Kind == "browser").Sum(e => e.Delta));
        Assert.Equal(0, events.Where(e => e.Kind == "browser-context").Sum(e => e.Delta));
    }
}

/// <summary>
/// See <see cref="NewRecruitGameDataEngineResourceMetricsTests"/> — same gap, the NR Editor UI
/// driver's self-owned frozen path (<c>CreateFrozenAsync</c>), as distinct from the pool path
/// (<c>CreateFrozenInContextAsync</c>) which is already covered by
/// <see cref="NrGameDataUiEnginePoolResourceMetricsTests"/> and must NOT double-count here.
/// </summary>
public sealed class NrGameDataUiEngineResourceMetricsTests
{
    private static readonly AsyncLocal<bool> IsThisTest = new();

    [Fact]
    [Trait("Category", "Conformance")]
    [Trait("Engine", "FrozenNrGameDataUi")]
    public async Task CreateFrozenAsync_TracksBrowserAndContextLifecycle_ReturnsToZeroAfterDispose()
    {
        var staticDir = NrGameDataUiEngine.FindFrozenStaticDir();
        Assert.SkipWhen(staticDir is null,
            "NR Editor static files not found (run setup.ps1) — skipping NrGameDataUiEngine resource-metrics test");

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
        NrGameDataUiEngine? engine = null;
        try
        {
            engine = await NrGameDataUiEngine.CreateFrozenAsync(staticDir!, headless: true);
        }
        catch (PlaywrightException)
        {
            // Playwright browsers not installed — handled below via SkipWhen.
        }
        finally
        {
            IsThisTest.Value = false;
        }

        Assert.SkipWhen(engine is null,
            "Playwright browsers not installed — skipping NrGameDataUiEngine resource-metrics test");

        IsThisTest.Value = true;
        engine!.Dispose();
        IsThisTest.Value = false;

        // Deterministic single-engine sequence: one browser launch, one context created for it,
        // then (in Dispose) the context and browser released together in the finally.
        Assert.Equal(
            [("browser", 1), ("browser-context", 1), ("browser-context", -1), ("browser", -1)],
            events);
        Assert.Equal(0, events.Where(e => e.Kind == "browser").Sum(e => e.Delta));
        Assert.Equal(0, events.Where(e => e.Kind == "browser-context").Sum(e => e.Delta));
    }
}
