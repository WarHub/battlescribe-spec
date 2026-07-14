using BattleScribeSpec.Concurrency;
using BattleScribeSpec.Engines;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// <b>The fact the CLI was missing.</b> <c>ConcurrencyPolicy</c> takes a <see cref="LoadTarget"/> and
/// clamps a live run to <c>ThirdPartyLiveLoadLimit</c> — but only if somebody tells it. On the CLI path
/// nobody did, so <c>bs-spec run --all --engine newrecruit</c> with <c>NR_ENGINE_URL</c> set planned
/// <c>ceil(cpuCount × 0.375)</c> = <b>12 adapter processes, each with its own browser</b>, against
/// newrecruit.eu. These tests pin the derivation that answers it: the engine declares where its service
/// lives, and <see cref="EngineEndpoint.ResolveLoadTarget"/> turns that plus the environment into the
/// answer — a pure function, no policy involvement, no engine names.
/// </summary>
[Trait("Category", "Unit")]
public sealed class EngineEndpointTests
{
    /// <summary>An environment with nothing in it — every variable unset.</summary>
    private static readonly Func<string, string?> Nothing = _ => null;

    /// <summary>An environment where <paramref name="value"/> is what the engine's URL variable holds.</summary>
    private static Func<string, string?> NrEngineUrl(string? value) =>
        variable => string.Equals(variable, "NR_ENGINE_URL", StringComparison.Ordinal) ? value : null;

    /// <summary>
    /// <b>The regression, in one assertion.</b> The same engine, the same profile, the same machine —
    /// and the only difference is a URL. It must be enough.
    /// </summary>
    /// <remarks>
    /// Falsifiable: make <see cref="EngineEndpoint.ResolveLoadTarget"/> ignore the environment (return
    /// <see cref="LoadTarget.Local"/> for a <see cref="EngineEndpointKind.UrlVariable"/> endpoint, i.e.
    /// the behaviour the CLI had) and the live row goes red.
    /// </remarks>
    [Fact]
    public void UrlVariable_Set_ToAThirdPartySite_IsThirdPartyLive()
    {
        var endpoint = EngineEndpoint.FromUrlVariable("NR_ENGINE_URL");

        Assert.Equal(
            LoadTarget.ThirdPartyLive,
            endpoint.ResolveLoadTarget(NrEngineUrl("https://www.newrecruit.eu")));
    }

    /// <summary>
    /// <b>And the 14.3× must survive.</b> An unset (or empty) URL variable is the frozen HAR replay —
    /// local disk, nobody else's bandwidth — and it keeps the machine's full measured worker count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assertion that stops the fix from being "throttle everything, call it safe". A blanket
    /// <see cref="LoadTarget.ThirdPartyLive"/> default on the CLI path would hold every frozen
    /// <c>run --all</c> to 2 workers and cost the branch its headline win.
    /// </para>
    /// <para>
    /// The empty-string row is not pedantry: <c>HostEngineFactory</c> tests <c>url is { Length: &gt; 0 }</c>,
    /// so an empty variable means "load the frozen HAR" to the child. The parent must read the same
    /// variable the same way, or it would throttle a run that never leaves the box.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void UrlVariable_UnsetOrEmpty_IsLocal_BecauseTheEngineReplaysAFrozenHar(string? value)
    {
        var endpoint = EngineEndpoint.FromUrlVariable("NR_ENGINE_URL");

        Assert.Equal(LoadTarget.Local, endpoint.ResolveLoadTarget(NrEngineUrl(value)));
    }

    /// <summary>
    /// A locally-served endpoint is this machine's load. A developer who stands up a local mirror is
    /// spending their own CPU and should get their own machine's width.
    /// </summary>
    /// <remarks>
    /// Falsifiable in the direction that matters: make the check "is the variable set at all?" — the
    /// obvious shortcut — and every row here goes red. Note what is deliberately NOT credited as local: a
    /// private LAN address (<c>192.168.x</c>) is somebody's box, just not this one.
    /// </remarks>
    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://[::1]:8080")]
    [InlineData("file:///C:/testdata/newrecruit")]
    public void UrlVariable_LoopbackOrFile_IsLocal(string url)
    {
        var endpoint = EngineEndpoint.FromUrlVariable("NR_ENGINE_URL");

        Assert.Equal(LoadTarget.Local, endpoint.ResolveLoadTarget(NrEngineUrl(url)));
    }

    /// <summary>
    /// <b>Fail safe: only positive evidence buys <see cref="LoadTarget.Local"/>.</b> A URL this code
    /// cannot parse is not a licence to spawn a machine's worth of browsers at whatever it turns out to
    /// be.
    /// </summary>
    /// <remarks>
    /// Falsifiable: swap the <c>Uri.TryCreate</c> failure branch to return "local" (the "if in doubt,
    /// keep it fast" reading) and every row goes red. Each of these strings is something a real shell
    /// export could produce — a bare host, a scheme typo, a quoted value.
    /// </remarks>
    [Theory]
    [InlineData("www.newrecruit.eu")]
    [InlineData("newrecruit.eu/app")]
    [InlineData("htps://www.newrecruit.eu")]
    [InlineData("  ")]
    public void UrlVariable_Unparseable_IsThirdPartyLive_NotLocal(string url)
    {
        var endpoint = EngineEndpoint.FromUrlVariable("NR_ENGINE_URL");

        Assert.Equal(LoadTarget.ThirdPartyLive, endpoint.ResolveLoadTarget(NrEngineUrl(url)));
    }

    /// <summary>
    /// <b>The fail-safe default itself.</b> An engine that has not declared where its service lives — any
    /// <c>exec:</c>/<c>dotnet:</c> adapter we did not write — is treated as driving a third party's live
    /// site. It costs that adapter wall-clock; the opposite mistake costs somebody else's website.
    /// </summary>
    /// <remarks>
    /// Falsifiable: change <see cref="EngineEndpoint.ResolveLoadTarget"/>'s default arm to
    /// <see cref="LoadTarget.Local"/> — or renumber <see cref="EngineEndpointKind"/> so that
    /// <see cref="EngineEndpointKind.OnThisMachine"/> becomes the zero value, which is the same bug
    /// wearing a nicer hat — and this goes red. The <c>Undeclared = 0</c> pinning is the point: a
    /// default-constructed declaration must mean "I don't know", never "it's fine".
    /// </remarks>
    [Fact]
    public void Undeclared_IsThirdPartyLive_AndIsTheZeroValue()
    {
        Assert.Equal(LoadTarget.ThirdPartyLive, EngineEndpoint.Undeclared.ResolveLoadTarget(Nothing));

        // The zero value of the enum — what an undeclared field defaults to — is the safe answer.
        Assert.Equal(EngineEndpointKind.Undeclared, default);
        Assert.Equal(LoadTarget.ThirdPartyLive, new EngineEndpoint(default).ResolveLoadTarget(Nothing));

        // A UrlVariable endpoint that names no variable has declared nothing either.
        Assert.Equal(
            LoadTarget.ThirdPartyLive,
            new EngineEndpoint(EngineEndpointKind.UrlVariable, null).ResolveLoadTarget(Nothing));
    }

    /// <summary>An engine with no network code is local whatever the environment says.</summary>
    /// <remarks>
    /// This is why the load target cannot be "is <c>NR_ENGINE_URL</c> set?": that question would throttle
    /// the in-process <c>battlescribe</c> engine in any shell that happened to export the variable for
    /// live NewRecruit work, for a service <c>battlescribe</c> does not have. Falsifiable: derive the
    /// target from the environment alone, without asking the engine, and this goes red.
    /// </remarks>
    [Fact]
    public void OnThisMachine_IgnoresTheEnvironment_EvenAVariableSomeOtherEngineWouldGoLiveOn()
    {
        Assert.Equal(
            LoadTarget.Local,
            EngineEndpoint.OnThisMachine.ResolveLoadTarget(NrEngineUrl("https://www.newrecruit.eu")));
    }

    /// <summary>A declared always-live engine needs no variable to be held to the load limit.</summary>
    [Fact]
    public void ThirdPartyLive_Declared_NeedsNoVariable()
    {
        Assert.Equal(LoadTarget.ThirdPartyLive, EngineEndpoint.ThirdPartyLive.ResolveLoadTarget(Nothing));
    }

    // ===== What the built-in registry actually declares =====

    /// <summary>
    /// <b>The two NewRecruit engines are the ones that can go live, and only in the roster domain.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Falsifiable, and it is the declaration the whole fix rests on: delete
    /// <c>RosterEndpoint: EngineEndpoint.FromUrlVariable("NR_ENGINE_URL")</c> from either NR engine and
    /// the first block goes red — the engine would be undeclared, and (because undeclared fails safe) it
    /// would be throttled even when frozen, which the second block then catches as well.
    /// </para>
    /// <para>
    /// The gamedata rows are the carve-out that keeps the 14.3×: <c>HostEngineFactory</c>'s gamedata
    /// switch never reads <c>NR_ENGINE_URL</c> — the NR gamedata engine is always a frozen static dir —
    /// so a gamedata run keeps its full measured worker count even in a shell that has the variable set
    /// for live roster work. Declare the gamedata endpoint as a URL variable "for symmetry" and the
    /// second block goes red.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("newrecruit")]
    [InlineData("newrecruit-ui")]
    public void Registry_NewRecruitEngines_DeclareTheirRosterEndpointLive_AndTheirGameDataEndpointLocal(string name)
    {
        var entry = EngineRegistry.LoadDefault().Resolve(EngineConnectable.Parse(name));

        var live = NrEngineUrl("https://www.newrecruit.eu");

        // Roster: the URL variable decides.
        Assert.Equal(LoadTarget.ThirdPartyLive, entry.EndpointFor("roster").ResolveLoadTarget(live));
        Assert.Equal(LoadTarget.Local, entry.EndpointFor("roster").ResolveLoadTarget(Nothing));

        // Gamedata: this machine, always — the variable is not read by that engine at all.
        Assert.Equal(LoadTarget.Local, entry.EndpointFor("gamedata").ResolveLoadTarget(live));
        Assert.Equal(LoadTarget.Local, entry.EndpointFor("gamedata").ResolveLoadTarget(Nothing));
    }

    /// <summary>The BattleScribe engines never leave this machine, in either domain.</summary>
    [Theory]
    [InlineData("battlescribe")]
    [InlineData("battlescribe-ui")]
    public void Registry_BattleScribeEngines_AreLocalInBothDomains(string name)
    {
        var entry = EngineRegistry.LoadDefault().Resolve(EngineConnectable.Parse(name));
        var live = NrEngineUrl("https://www.newrecruit.eu");

        Assert.Equal(LoadTarget.Local, entry.EndpointFor("roster").ResolveLoadTarget(live));
        Assert.Equal(LoadTarget.Local, entry.EndpointFor("gamedata").ResolveLoadTarget(live));
    }

    /// <summary>
    /// An ad-hoc <c>exec:</c>/<c>dotnet:</c> adapter declares nothing, so it gets the fail-safe answer.
    /// </summary>
    /// <remarks>
    /// Falsifiable: give <c>EngineEntry</c>'s endpoint parameters a default of
    /// <see cref="EngineEndpoint.OnThisMachine"/> instead of null/<see cref="EngineEndpoint.Undeclared"/>
    /// — the "sensible default" that would make this whole file pass except here — and this goes red.
    /// </remarks>
    [Fact]
    public void Registry_AdHocLaunchableAdapter_IsUndeclared_AndThereforeThirdPartyLive()
    {
        var entry = EngineRegistry.LoadDefault().Resolve(EngineConnectable.Parse("exec:./some-third-party-adapter"));

        Assert.Equal(EngineEndpointKind.Undeclared, entry.EndpointFor("roster").Kind);
        Assert.Equal(LoadTarget.ThirdPartyLive, entry.EndpointFor("roster").ResolveLoadTarget(Nothing));
        Assert.Equal(LoadTarget.ThirdPartyLive, entry.EndpointFor("gamedata").ResolveLoadTarget(Nothing));
    }

    // ===== engines.json: declaring "local" is the opt-in to the machine's full width =====

    [Theory]
    [InlineData("local", LoadTarget.Local)]
    [InlineData("third-party-live", LoadTarget.ThirdPartyLive)]
    [InlineData(null, LoadTarget.ThirdPartyLive)]
    public void EnginesJson_EndpointDeclaration_DecidesTheLoadTarget(string? declared, LoadTarget expected)
    {
        var entry = LoadConfigured(declared is null ? "" : $",\"endpoint\":\"{declared}\"");

        Assert.Equal(expected, entry.EndpointFor("roster").ResolveLoadTarget(Nothing));
    }

    /// <summary>A third-party adapter can use the same URL-variable mechanism the NR engines do.</summary>
    [Fact]
    public void EnginesJson_UrlVariableForm_IsLiveOnlyWhenTheVariableNamesARemoteUrl()
    {
        var entry = LoadConfigured(",\"endpoint\":\"url-var:WHAM_URL\"");

        Assert.Equal(
            LoadTarget.ThirdPartyLive,
            entry.EndpointFor("roster").ResolveLoadTarget(_ => "https://wham.example.com"));
        Assert.Equal(LoadTarget.Local, entry.EndpointFor("roster").ResolveLoadTarget(Nothing));
    }

    /// <summary>
    /// An unrecognized <c>endpoint</c> value is rejected at load, not silently read as "undeclared".
    /// A config that says something the loader ignores is the failure mode the rest of the validation
    /// exists to close.
    /// </summary>
    [Fact]
    public void EnginesJson_UnknownEndpointValue_IsRejected()
    {
        var ex = Assert.Throws<InvalidDataException>(() => LoadConfigured(",\"endpoint\":\"localhost\""));

        Assert.Contains("endpoint must be", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Write a one-entry engines.json to a temp dir and load it.</summary>
    private static EngineEntry LoadConfigured(string extraJson)
    {
        var dir = Directory.CreateTempSubdirectory("bsspec-endpoint-test");
        try
        {
            var path = Path.Combine(dir.FullName, "engines.json");
            File.WriteAllText(
                path,
                "{\"engines\":{\"wham\":{\"exec\":\"node adapters/wham.js\"" + extraJson + "}}}");

            return EngineRegistry.Load(path).Resolve(EngineConnectable.Parse("wham"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
