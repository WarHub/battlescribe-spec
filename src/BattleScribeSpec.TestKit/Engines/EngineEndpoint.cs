using BattleScribeSpec.Concurrency;

namespace BattleScribeSpec.Engines;

/// <summary>How an engine's <b>service</b> is located — the fact that decides <see cref="LoadTarget"/>.</summary>
public enum EngineEndpointKind
{
    /// <summary>
    /// <b>Nothing is declared.</b> The default, and it is the <em>unsafe-looking</em> answer on purpose:
    /// an engine that has not said where its service lives resolves to
    /// <see cref="LoadTarget.ThirdPartyLive"/>. See <see cref="EngineEndpoint.ResolveLoadTarget"/>.
    /// </summary>
    Undeclared = 0,

    /// <summary>
    /// <b>This machine, unconditionally.</b> An in-process engine, a desktop app, a frozen HAR or a
    /// static directory served off local disk. No environment can point it at a third party.
    /// </summary>
    OnThisMachine,

    /// <summary>
    /// <b>A third party's live service, always.</b> Declared, not merely unestablished — the engine says
    /// it drives someone else's production site. Resolves to <see cref="LoadTarget.ThirdPartyLive"/>, the
    /// same answer as <see cref="Undeclared"/>, but it is a different <em>statement</em> and the harness
    /// reports it as one.
    /// </summary>
    ThirdPartyLive,

    /// <summary>
    /// <b>An environment variable names the endpoint.</b> Unset ⇒ the engine falls back to its local
    /// frozen replay; set ⇒ the URL itself decides (loopback is this machine; anything else is not).
    /// </summary>
    UrlVariable,
}

/// <summary>
/// <b>What service an engine drives, declared by the engine.</b> The missing fact on the CLI path:
/// <c>bs-spec run --all --engine newrecruit</c> replays a HAR file off local disk, or drives
/// <c>newrecruit.eu</c> — <em>the same engine, the same <see cref="EngineProfile"/></em> — and the only
/// thing that differs is an endpoint variable the parent never looked at. So it spawned
/// <c>ceil(cpuCount × k)</c> browsers (12 on a 32-core box) at someone else's production website.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is where the load target is derived, and it is deliberately NOT in the policy.</b>
/// <see cref="ConcurrencyPolicy"/> stays a pure function of
/// <c>(MachineProfile, EngineProfile, LoadTarget)</c> that never string-matches an engine name — so
/// something else has to answer "is this one live?", and the honest place is the engine's own
/// declaration, read at engine-resolution time, before a single process is spawned.
/// </para>
/// <para>
/// <b>Why a declared variable name and not "is <c>NR_ENGINE_URL</c> set?".</b> That question is
/// meaningless for <c>battlescribe</c> (an in-process IKVM engine that could not reach the network if
/// it tried) and it would throttle it anyway, in any shell that happened to export the variable. An
/// engine declares the variable that configures <em>its own</em> endpoint; nothing else's environment
/// can speak for it. It is declared <b>per domain</b> for the same reason: the NewRecruit <em>roster</em>
/// engine reads <c>NR_ENGINE_URL</c> (<c>HostEngineFactory.CreateRosterEngineAsync</c>) and the
/// NewRecruit <em>gamedata</em> engine does not — it is always a frozen static dir — so a gamedata run
/// keeps its measured worker count even in a shell that has the variable set.
/// </para>
/// <para>
/// <b>Endpoint configuration is not a performance knob.</b> Reading <c>NR_ENGINE_URL</c> here does not
/// reintroduce the environment-variable knobs the concurrency model retired
/// (<c>ConcurrencyConfigurationDriftTests.RetiredKnobs</c>: <c>NR_PARALLEL</c>,
/// <c>BS_UI_KEEP_ALIVE</c>, <c>BSSPEC_DISABLE_WARM_REUSE</c>). Those were a <em>second answer</em> to a
/// question the policy owns — how parallel, and whether to reuse. This variable answers a question the
/// policy cannot ask and has no other source for: <em>which server</em>. It does not set the worker
/// count; it is an input from which the worker count is derived, exactly once, by the one policy.
/// </para>
/// </remarks>
/// <param name="Kind">How the service is located.</param>
/// <param name="UrlVariable">
/// For <see cref="EngineEndpointKind.UrlVariable"/>: the environment variable that names the endpoint.
/// Null/empty for the other kinds — and a <see cref="EngineEndpointKind.UrlVariable"/> endpoint with no
/// variable to read is a declaration that says nothing, so it resolves to
/// <see cref="LoadTarget.ThirdPartyLive"/> like any other.
/// </param>
public sealed record EngineEndpoint(EngineEndpointKind Kind, string? UrlVariable = null)
{
    /// <summary>The engine has not declared where its service lives ⇒ <see cref="LoadTarget.ThirdPartyLive"/>.</summary>
    public static EngineEndpoint Undeclared { get; } = new(EngineEndpointKind.Undeclared);

    /// <summary>The engine never leaves this machine, whatever the environment says.</summary>
    public static EngineEndpoint OnThisMachine { get; } = new(EngineEndpointKind.OnThisMachine);

    /// <summary>The engine drives a third party's live service, always.</summary>
    public static EngineEndpoint ThirdPartyLive { get; } = new(EngineEndpointKind.ThirdPartyLive);

    /// <summary>The engine's endpoint is named by <paramref name="variable"/>; unset ⇒ its local frozen replay.</summary>
    /// <param name="variable">Environment variable holding the endpoint URL (e.g. <c>NR_ENGINE_URL</c>).</param>
    public static EngineEndpoint FromUrlVariable(string variable) =>
        new(EngineEndpointKind.UrlVariable, variable);

    /// <summary>
    /// Parse a <b>written declaration</b>: <c>"local"</c>, <c>"third-party-live"</c> or
    /// <c>"url-var:NAME"</c>. The one grammar, used by both channels a human can declare an endpoint
    /// through — <c>engines.json</c>'s <c>"endpoint"</c> (<see cref="EngineRegistry"/>) and the CLI's
    /// <c>--engine-endpoint</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One grammar, one parser, deliberately.</b> Two implementations of "what does this word mean"
    /// is how <c>--config-a "nr_engine_url=…"</c> came to mean one thing to the parent and another to
    /// the child. There is no <c>"undeclared"</c> spelling on purpose: absence is the only way to be
    /// undeclared, and it is the caller's business (an omitted <c>engines.json</c> key, an unpassed
    /// flag), never a word somebody types.
    /// </para>
    /// </remarks>
    /// <param name="declaration">The declared endpoint.</param>
    /// <returns>The parsed endpoint.</returns>
    /// <exception cref="FormatException"><paramref name="declaration"/> is not one of the three forms.</exception>
    public static EngineEndpoint Parse(string declaration)
    {
        ArgumentException.ThrowIfNullOrEmpty(declaration);

        return declaration switch
        {
            "local" => OnThisMachine,
            "third-party-live" => ThirdPartyLive,
            _ when declaration.StartsWith("url-var:", StringComparison.Ordinal)
                && declaration["url-var:".Length..] is { Length: > 0 } variable => FromUrlVariable(variable),

            // An unrecognized value is rejected outright rather than quietly read as "undeclared" — a
            // declaration the reader silently ignores is a lie the author cannot see.
            _ => throw new FormatException(
                $"endpoint must be \"local\" (the engine's service runs on this machine), " +
                $"\"third-party-live\" (it drives someone else's production site, so it is held to a load " +
                $"limit), or \"url-var:NAME\" (live iff the NAME environment variable holds a non-loopback " +
                $"URL) — got \"{declaration}\". Leave it out to declare nothing, which is treated as " +
                $"third-party-live: declaring \"local\" is how an engine opts into this machine's full " +
                $"worker count."),
        };
    }

    /// <summary>
    /// <b>Where this engine's load lands</b>, given the environment the engine process will actually
    /// see. <b>Fail-safe by construction: only positive evidence yields <see cref="LoadTarget.Local"/>.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The asymmetry is the whole design. Wrongly deciding <see cref="LoadTarget.Local"/> spends
    /// <em>someone else's</em> production capacity — 12 headless browsers on a 32-core box, with no
    /// retry, backoff, throttle or 429 handling anywhere in
    /// <c>src/BattleScribeSpec.NewRecruit/</c> to soften it. Wrongly deciding
    /// <see cref="LoadTarget.ThirdPartyLive"/> spends <em>our own</em> wall-clock. So the unsafe answer
    /// is the one that must be earned: an undeclared engine, or a URL this method cannot parse, is
    /// treated as live.
    /// </para>
    /// <para>
    /// The three <see cref="LoadTarget.Local"/> verdicts, each on positive evidence:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="EngineEndpointKind.OnThisMachine"/> — the engine has no code path to a remote host.
    /// </description></item>
    /// <item><description>
    /// A <see cref="EngineEndpointKind.UrlVariable"/> whose variable is <b>unset or empty</b> — which is
    /// exactly the condition under which the engine host loads its frozen HAR from disk instead
    /// (<c>HostEngineFactory</c>: <c>url is { Length: &gt; 0 }</c>, the same test, so the parent's verdict
    /// and the child's behaviour cannot disagree).
    /// </description></item>
    /// <item><description>
    /// A URL that is a <b>loopback</b> host or a <c>file:</c> URI — <c>localhost</c>, <c>127.0.0.1</c>,
    /// <c>[::1]</c>. A locally-served mirror is this machine's load, and a developer who stands one up
    /// should not be throttled to a stranger's courtesy limit. Note what is <em>not</em> here: a private
    /// LAN address is somebody's box, not this one, and gets no such credit.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="environment">
    /// Lookup for the environment the <em>engine process</em> will see — the parent's own environment,
    /// plus whatever it layers on top of the child's (<c>compare --config-a/--config-b</c>). Returns null
    /// for an unset variable.
    /// </param>
    /// <returns>The load target to hand <see cref="ConcurrencyPolicy.For"/>.</returns>
    public LoadTarget ResolveLoadTarget(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return Kind switch
        {
            EngineEndpointKind.OnThisMachine => LoadTarget.Local,

            EngineEndpointKind.ThirdPartyLive => LoadTarget.ThirdPartyLive,

            EngineEndpointKind.UrlVariable when UrlVariable is { Length: > 0 } variable =>
                ServedByThisMachine(environment(variable)) ? LoadTarget.Local : LoadTarget.ThirdPartyLive,

            // Undeclared — and a UrlVariable endpoint that names no variable, which declares nothing
            // either. Neither can be established as local, so neither is.
            _ => LoadTarget.ThirdPartyLive,
        };
    }

    /// <summary>Is <paramref name="url"/> positive evidence that the engine's load lands on this machine?</summary>
    private static bool ServedByThisMachine(string? url)
    {
        // Unset ⇒ the engine host falls back to its frozen local replay. Same test as HostEngineFactory's.
        if (string.IsNullOrEmpty(url))
        {
            return true;
        }

        // Not a URL we can reason about (a bare host, a typo, a scheme we don't know). We do not get to
        // guess in the direction that costs a third party.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.IsFile || uri.IsLoopback;
    }
}
