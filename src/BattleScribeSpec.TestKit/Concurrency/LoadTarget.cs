namespace BattleScribeSpec.Concurrency;

/// <summary>
/// <b>Where the load lands.</b> Not a performance input — the question every other input in this
/// namespace fails to ask: is the concurrency we are about to authorize paid for by <em>this
/// machine</em>, or by <em>a third party's production website</em>?
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the axis the engine profile cannot see.</b> <c>nr-frozen</c> (a HAR file replayed off
/// local disk) and <c>nr-live-conformance</c> (the real <c>newrecruit.eu</c>) resolve the <b>same
/// engine</b> — <c>"newrecruit"</c>, the same <see cref="EngineProfile"/>, the same measured
/// <see cref="EngineProfile.ContextPoolSize"/>. Nothing in <see cref="MachineProfile"/> and nothing
/// in <see cref="EngineProfile"/> can tell "a file on disk" from "someone else's server", so a policy
/// taking only those two inputs is <b>structurally incapable</b> of treating them differently — and
/// it did not: the live lane's concurrency was doubled (2 → 4) by a sweep that never sent a single
/// request to newrecruit.eu. This enum is the missing input. It has no default in
/// <c>FixtureConcurrency</c>, so no fixture can forget to answer it.
/// </para>
/// <para>
/// <b>It is not a fourth quantity to reconcile with the other three.</b> The history of this policy is
/// numbers that got conflated because they shared a name or a mirror (<c>PoolSize: workers</c>;
/// <c>maxParallelThreads</c> pinned to a memory cap). This one differs in <em>kind</em>, not in value:
/// <see cref="EngineProfile.OversubscriptionFactor"/>, <see cref="EngineProfile.ContextPoolSize"/> and
/// <see cref="ConcurrencyPolicy.MemoryHeadroomFactor"/> all answer "how fast can this machine go?".
/// <see cref="ThirdPartyLive"/> answers "how hard may we hit a stranger's server?", and the two
/// questions have no exchange rate.
/// </para>
/// <para>
/// (Unrelated to <c>SpecSetup.DataSource</c> / <c>DataSourceResolver</c>, which are about where a
/// <em>spec</em> gets its game data. This enum is about who serves the <em>engine</em>.)
/// </para>
/// </remarks>
public enum LoadTarget
{
    /// <summary>
    /// <b>This machine.</b> A frozen HAR replayed from disk, a statically-served local site, an
    /// in-process engine (IKVM), or a desktop app. Every request is paid for by the box the harness
    /// runs on, so concurrency here is a pure throughput question: sweep it, measure it, take the
    /// optimum. That is what <c>docs/concurrency-policy-measurements.md</c> §1–§8 did.
    /// </summary>
    Local,

    /// <summary>
    /// <b>A third party's live production website</b> (today: <c>newrecruit.eu</c>, run by volunteers,
    /// not by us). Every browser context here is a real visitor on someone else's server, and this
    /// harness has <em>no other brake</em> on it: a search of <c>src/BattleScribeSpec.NewRecruit/</c>
    /// for <c>retry|backoff|throttl|rate.?limit|429|Task.Delay|Thread.Sleep</c> returns <b>zero
    /// hits</b> — no pause between specs, no retry, no backoff, no 429 handling. Concurrency here is a
    /// <em>load</em> question, and <see cref="ConcurrencyPolicy.ThirdPartyLiveLoadLimit"/> is the only
    /// answer this harness has to it.
    /// </summary>
    ThirdPartyLive,
}
