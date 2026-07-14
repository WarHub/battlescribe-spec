using BattleScribeSpec.NewRecruit;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Single-engine fixture for sequential live NR tests and smoke tests.
/// Gated by NR_ENGINE_URL env var (same as before).
/// Shared by <see cref="SequentialLiveNrRosterConformanceTests"/>,
/// <see cref="LiveNrRosterSmokeTests"/> and <see cref="LiveNrRosterIntegrationTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The engine is created on first use, not in <see cref="InitializeAsync"/>.</b> This fixture's
/// largest consumer by far is <see cref="SequentialLiveNrRosterConformanceTests"/> — 363 specs, every
/// one of which <c>Assert.Skip</c>s unless <c>NR_SEQUENTIAL=true</c>, which nothing in CI sets. But
/// those 363 tests are <c>Category=Conformance, Engine=LiveNrRoster</c>, so the
/// <c>nr-live-conformance</c> lane selects them, xUnit constructs their collection fixture, and an
/// eager <c>InitializeAsync</c> then launched a whole second browser against <b>newrecruit.eu</b> —
/// separate from <see cref="LiveNrRosterFixture"/>'s pool, and additional to it — loaded a third
/// party's site with it, and skipped every test that could have used it. Lazy construction means the
/// live site sees traffic only from tests that actually run.
/// </para>
/// <para>
/// <b><see cref="Available"/> therefore means "a live NR site is configured", not "a browser is
/// already running".</b> That is the question its callers were really asking — each pairs
/// <c>Assert.SkipWhen(!Available, "NR_ENGINE_URL not set")</c> with a use of <see cref="Engine"/> — and
/// <see cref="SequentialLiveNrRosterConformanceTests"/> checks its <c>NR_SEQUENTIAL</c> skip
/// <em>before</em> it touches <see cref="Engine"/>, which is what makes the laziness worth anything.
/// </para>
/// </remarks>
public sealed class SequentialLiveNrRosterFixture : IAsyncLifetime
{
    private readonly Lock _gate = new();
    private string? _baseUrl;
    private NewRecruitRosterEngine? _engine;
    private LiveLoadLease? _lease;

    /// <summary>
    /// The live engine, launched on first access. Null only when <see cref="Available"/> is false
    /// (NR_ENGINE_URL unset) — every caller checks that first and skips.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The session is drawn from <see cref="LiveLoadBudget"/>, and it may be refused.</b> This
    /// fixture's engine is a real visitor on <c>newrecruit.eu</c>, and so are
    /// <see cref="LiveNrRosterFixture"/>'s pooled contexts — both collections carry
    /// <c>Engine=LiveNrRoster</c>, so <c>-p:TestProfile=nr-live</c> selects both and xUnit runs them in
    /// parallel: <b>2 + 1 = 3 concurrent sessions</b> on a volunteer-run site whose limit is 2. That
    /// went unnoticed because this fixture never asked anyone whether it could have one.
    /// </para>
    /// <para>
    /// It asks now, at the moment it would actually open the browser (which is why the laziness above
    /// matters twice over: a fixture that never opens a session never spends the budget, so CI's
    /// conformance lane still gets its full pool of 2). When the budget is spent, the test SKIPS with a
    /// message naming the holder — rather than quietly becoming the third visitor.
    /// </para>
    /// </remarks>
    public NewRecruitRosterEngine? Engine
    {
        get
        {
            var baseUrl = _baseUrl;
            if (baseUrl is null)
            {
                return null;
            }

            lock (_gate)
            {
                if (_engine is not null)
                {
                    return _engine;
                }

                _lease = LiveLoadBudget.Reserve(nameof(SequentialLiveNrRosterFixture), baseUrl, 1);
                _lease.EnsureGranted();

                return _engine = CreateEngine(baseUrl);
            }
        }
    }

    /// <summary>
    /// Whether a live NR site is configured (NR_ENGINE_URL). Does <b>not</b> mean a browser is running:
    /// nothing is launched until <see cref="Engine"/> is read — see the class remarks.
    /// </summary>
    public bool Available => _baseUrl is not null;

    public ValueTask InitializeAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("NR_ENGINE_URL");
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? null : baseUrl;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _engine?.Dispose();
            _engine = null;

            // The session is closed; hand it back to the site's budget.
            _lease?.Dispose();
            _lease = null;
        }

        _baseUrl = null;
        return ValueTask.CompletedTask;
    }

    private static NewRecruitRosterEngine CreateEngine(string baseUrl)
    {
        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        var visual = Environment.GetEnvironmentVariable("NR_VISUAL") == "true";
        float? slowMo = float.TryParse(Environment.GetEnvironmentVariable("NR_SLOW_MO"), out var sm) ? sm : null;

        using var span = FixtureTelemetry.StartInit(nameof(SequentialLiveNrRosterFixture));

        // Blocking here rather than awaiting in InitializeAsync is the point of the change: this runs
        // only when a test that needs the engine actually runs. Every test on this fixture is a
        // synchronous [Fact] (NewRecruitRosterEngine's own API is synchronous), so there is no async
        // caller to hand the wait back to.
        var engine = NewRecruitRosterEngine.CreateAsync(baseUrl, headless, slowMo).GetAwaiter().GetResult();
        engine.Visual = visual;
        return engine;
    }
}

[CollectionDefinition("SequentialLiveNrRoster")]
public class SequentialLiveNrRosterCollection : ICollectionFixture<SequentialLiveNrRosterFixture>
{
}
