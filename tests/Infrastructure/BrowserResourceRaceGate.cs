namespace BattleScribeSpec.Tests;

/// <summary>
/// Mutual exclusion between a frozen browser-owning fixture and the resource-metrics tests that
/// launch their <b>own</b>, independent Chromium of the same resource kind (code review follow-up
/// to #271 Task 8/9).
/// </summary>
/// <remarks>
/// <para>
/// <c>NewRecruitEnginePoolResourceMetricsTests</c> (and its three siblings) exist to prove that
/// <c>CreateFrozenAsync</c> instruments <c>harness.resource.count</c> correctly when called
/// directly — a real Chromium launch/dispose is the whole point, so it can't be avoided. Simply
/// putting these tests in the <b>same xUnit collection</b> as the fixture that owns the
/// equivalent pool (e.g. <c>[Collection("FrozenNrRoster")]</c>) looked like it would serialize
/// them, but does not: a shared collection fixture (<c>ICollectionFixture&lt;T&gt;</c>) is
/// constructed once, before the first test in the collection runs, and disposed once, after the
/// last — spanning every test in the collection regardless of execution order. Measured directly
/// (temporary diagnostic logging around <c>FrozenNrRosterFixture.InitializeAsync</c>/
/// <c>DisposeAsync</c> and the resource-metrics test's own <c>CreateFrozenAsync</c>/
/// <c>DisposeAsync</c>): the fixture's pool browser was reliably alive for the <i>entire</i>
/// window the resource-metrics test's own browser existed, collection membership notwithstanding.
/// Collection membership only changes how likely the periodic 2-second metrics export is to
/// <i>sample</i> the overlap (a shorter window is less likely to be caught) — it does not remove
/// the overlap itself, so it cannot be trusted to make <c>harness.resource.count</c>'s peak
/// deterministic.
/// </para>
/// <para>
/// A named <see cref="System.Threading.SemaphoreSlim"/> per resource kind is the one thing that
/// actually works: the fixture holds its gate for its <i>entire</i> alive window (acquired before
/// the pool/engine is created in <c>InitializeAsync</c>, released only after it is fully torn
/// down in <c>DisposeAsync</c>), and each resource-metrics test acquires the same gate before its
/// own <c>CreateFrozenAsync</c> and releases it only after its own dispose — guaranteeing the two
/// browsers of the same kind are never concurrently alive, independent of xUnit scheduling or
/// export sampling. The fixture and the resource-metrics test(s) for one resource kind are
/// deliberately kept in <b>different</b> xUnit collections (no shared <c>[Collection(...)]</c>):
/// combining collection-membership serialization with this gate would deadlock, since a shared
/// collection fixture's <c>DisposeAsync</c> (which releases the gate) cannot run until every test
/// in the collection — including one blocked waiting on that very gate — has completed.
/// </para>
/// </remarks>
internal static class BrowserResourceRaceGate
{
    /// <summary>Guards <c>FrozenNrRosterFixture</c>'s pool vs. <c>NewRecruitEnginePoolResourceMetricsTests</c>.</summary>
    public static readonly SemaphoreSlim FrozenNrRoster = new(1, 1);

    /// <summary>Guards <c>FrozenNrGameDataFixture</c>'s engine vs. <c>NewRecruitGameDataEngineResourceMetricsTests</c>.</summary>
    public static readonly SemaphoreSlim FrozenNrGameData = new(1, 1);

    /// <summary>
    /// Guards <c>FrozenNrGameDataUiFixture</c>'s pool vs. <b>both</b>
    /// <c>NrGameDataUiEnginePoolResourceMetricsTests</c> and <c>NrGameDataUiEngineResourceMetricsTests</c>
    /// — both create an independent NR Editor UI browser of the same kind, so both must share one gate.
    /// </summary>
    public static readonly SemaphoreSlim FrozenNrGameDataUi = new(1, 1);
}
