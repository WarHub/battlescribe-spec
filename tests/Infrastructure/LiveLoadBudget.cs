using BattleScribeSpec.Concurrency;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>The process-wide budget of concurrent browser sessions this test host may open at one third
/// party's live site.</b> Sized by <see cref="ConcurrencyPolicy.ThirdPartyLiveLoadLimit"/>, held per
/// host, and drawn on by <em>every</em> fixture that opens such a session — which is the point.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists: the limit had one enforcer and five fixtures.</b>
/// <see cref="ConcurrencyPolicy.ThirdPartyLiveLoadLimit"/> calls itself "the only thing standing
/// between a 363-spec conformance run and a volunteer-run website", and exactly one fixture
/// (<see cref="LiveNrRosterFixture"/>) asked it for a number. The other four — the sequential live
/// roster fixture, the live NR-UI roster fixture, and the two live NR-Editor gamedata fixtures —
/// opened sessions at <c>newrecruit.eu</c> and <c>giloushaker.github.io</c> without consulting
/// anything. Each opens only one, so no single lane exceeded the limit; but that is a coincidence of
/// how they are written, not something the policy enforced, and coincidences are what this branch
/// keeps finding at the bottom of its bugs.
/// </para>
/// <para>
/// <b>The composed breach, concretely.</b> <c>dotnet test -p:TestProfile=nr-live</c> selects
/// <c>Engine=LiveNrRoster</c>, which is BOTH the pooled collection (2 contexts) and the sequential one
/// (1 engine) — and xUnit runs collections in parallel. That is <b>3 concurrent sessions on
/// newrecruit.eu</b>, 50% over a limit whose own docstring forbids raising it by 1 for a measured
/// speed-up. Nothing was watching, because nothing could: the bound lived in a policy that only one of
/// the two callers invoked.
/// </para>
/// <para>
/// <b>What it does, and what it deliberately does not.</b> A fixture states the host it is about to
/// drive and how many sessions it wants; it gets what is left of that host's budget, and it opens no
/// more than that. It never blocks: the pooled fixture holds its contexts for its whole lifetime, so a
/// blocking budget would deadlock any fixture that asked for one afterwards. A fixture granted zero
/// does not open a session — it skips, loudly, naming the fixture that holds the budget. Wall-clock is
/// ours to lose; the sessions are not ours to spend.
/// </para>
/// <para>
/// <b>Per host, because "a third party" is not one party.</b> <c>newrecruit.eu</c> and
/// <c>giloushaker.github.io</c> are different people's servers, and a courtesy limit on one says
/// nothing about the other. Two budgets, one constant.
/// </para>
/// <para>
/// This does not solve the <em>local</em> composed bound (issue #314 — the frozen pools' 4 + 16, which
/// cost nobody but us). It is the live one, which is a different quantity with a different owner, and
/// tracking them together is how the live 2 got replaced by a number fitted against a HAR file in the
/// first place.
/// </para>
/// </remarks>
internal static class LiveLoadBudget
{
    /// <summary>Sessions this harness may hold at any ONE third party's live site, at once.</summary>
    /// <remarks>
    /// Not a second copy of the number: it IS
    /// <see cref="ConcurrencyPolicy.ThirdPartyLiveLoadLimit"/>. The constant is public precisely so the
    /// harness can say it out loud rather than restate it — read its remarks before you touch it, and
    /// then do not touch it.
    /// </remarks>
    public static int PerHostLimit => ConcurrencyPolicy.ThirdPartyLiveLoadLimit;

    private static readonly Lock Gate = new();

    // host -> (sessions held, who holds them). The holder list is for the skip message: "you cannot
    // have a session because X has them" is actionable; "budget exhausted" is not.
    private static readonly Dictionary<string, List<(string Fixture, int Sessions)>> Held =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reserve up to <paramref name="wanted"/> concurrent sessions at <paramref name="endpointUrl"/>'s
    /// host. Returns what is actually available — possibly fewer, possibly <b>zero</b>. The caller must
    /// open no more sessions than <see cref="LiveLoadLease.Sessions"/> says, and must dispose the lease
    /// when it closes them.
    /// </summary>
    /// <param name="fixtureName">The fixture reserving, for the diagnostic when someone is denied.</param>
    /// <param name="endpointUrl">The URL the sessions will drive; its host keys the budget.</param>
    /// <param name="wanted">Sessions the caller would like to open.</param>
    public static LiveLoadLease Reserve(string fixtureName, string endpointUrl, int wanted)
    {
        ArgumentException.ThrowIfNullOrEmpty(fixtureName);
        ArgumentException.ThrowIfNullOrEmpty(endpointUrl);
        ArgumentOutOfRangeException.ThrowIfNegative(wanted);

        var host = HostOf(endpointUrl);

        lock (Gate)
        {
            var holders = Held.TryGetValue(host, out var existing) ? existing : Held[host] = [];
            var alreadyHeld = holders.Sum(h => h.Sessions);
            var granted = Math.Max(0, Math.Min(wanted, PerHostLimit - alreadyHeld));

            var denialContext = granted < wanted
                ? string.Join(", ", holders.Select(h => $"{h.Fixture} holds {h.Sessions}"))
                : "";

            if (granted > 0)
            {
                holders.Add((fixtureName, granted));
            }

            return new LiveLoadLease(host, fixtureName, granted, wanted, denialContext);
        }
    }

    /// <summary>The host a live endpoint URL names — the key a budget is held under.</summary>
    /// <remarks>
    /// A URL we cannot parse is keyed by its raw text rather than being waved through: an unparseable
    /// endpoint is not evidence that nobody is on the other end, and every other decision on this axis
    /// (<c>EngineEndpoint.ResolveLoadTarget</c>) fails safe the same way.
    /// </remarks>
    internal static string HostOf(string endpointUrl) =>
        Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri) && uri.Host is { Length: > 0 } host
            ? host
            : endpointUrl;

    internal static void Return(string host, string fixtureName, int sessions)
    {
        if (sessions <= 0)
        {
            return;
        }

        lock (Gate)
        {
            if (Held.TryGetValue(host, out var holders))
            {
                holders.Remove((fixtureName, sessions));
            }
        }
    }

    /// <summary>Sessions currently held at <paramref name="host"/>. For the budget's own tests.</summary>
    internal static int HeldAt(string host)
    {
        lock (Gate)
        {
            return Held.TryGetValue(host, out var holders) ? holders.Sum(h => h.Sessions) : 0;
        }
    }
}

/// <summary>
/// A grant from <see cref="LiveLoadBudget"/>: how many sessions the holder may open at a third party's
/// site. Dispose to give them back.
/// </summary>
/// <remarks>
/// <see cref="Sessions"/> can be <b>zero</b>, and zero is not an error — it means another fixture in
/// this process is already holding the whole budget for that host. Call <see cref="EnsureGranted"/> to
/// turn it into an xUnit skip with a message that names the holder.
/// </remarks>
internal sealed class LiveLoadLease(string host, string fixtureName, int sessions, int wanted, string denialContext)
    : IDisposable
{
    private bool _returned;

    /// <summary>Sessions the holder may open. Zero = none; open nothing.</summary>
    public int Sessions => sessions;

    /// <summary>True when the full request was granted.</summary>
    public bool Full => sessions == wanted;

    /// <summary>
    /// The message for a fixture that was granted fewer sessions than it asked for — names the budget,
    /// the host, and who is holding it.
    /// </summary>
    public string Explanation =>
        $"{fixtureName} asked for {wanted} concurrent session(s) at {host} and was granted {sessions}: " +
        $"this harness holds at most {LiveLoadBudget.PerHostLimit} concurrent sessions at any one third " +
        $"party's live site (ConcurrencyPolicy.ThirdPartyLiveLoadLimit)" +
        (denialContext.Length > 0 ? $", and in this test process {denialContext}. " : ". ") +
        "Run the lanes in separate processes (that is what CI does: -p:TestProfile=nr-live-smoke and " +
        "-p:TestProfile=nr-live-conformance are two `dotnet test` invocations, not one) rather than " +
        "raising the limit — it bounds traffic to someone else's website, and no measurement of ours is " +
        "entitled to fit it.";

    /// <summary>Skip the calling test when the budget granted nothing, explaining who holds it.</summary>
    public void EnsureGranted()
    {
        if (sessions == 0)
        {
            Assert.Skip(Explanation);
        }
    }

    public void Dispose()
    {
        if (!_returned)
        {
            _returned = true;
            LiveLoadBudget.Return(host, fixtureName, sessions);
        }
    }
}
