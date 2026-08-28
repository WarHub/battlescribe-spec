using System.Reflection;
using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.Concurrency;
using BattleScribeSpec.EngineHost;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.NrRosterUiDriver;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using BattleScribeSpec.Tests.Infrastructure;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// <b><c>exportRosterXml</c> must work for every engine <c>bs-engine-host</c> serves.</b>
/// <para>
/// <c>ServeCommand.BuildOptions</c> used to wire the exporter as
/// <c>e =&gt; e is BsUiRosterEngine bs ? bs.ExportRosterXmlAsync()… : null</c> and advertise
/// <c>RosterXml = name is "battlescribe-ui"</c>. All four built-ins export, so for
/// <c>battlescribe</c>, <c>newrecruit</c> and <c>newrecruit-ui</c> the delegate returned null,
/// <c>AdapterHandler</c> read null as "unsupported" and answered <c>ProtocolError</c>,
/// <c>JsonProtocolEngine.ExportRosterXml</c> mapped that to <see cref="NotSupportedException"/>, and
/// <c>RosterRunner.ExecuteFileAssertion</c> caught it and <c>return</c>ed — so every
/// <c>expectedFile</c> byte-compare <b>silently passed</b> on the protocol path (i.e. under
/// <c>bs-spec run</c>, which is how <c>--report</c> matrices and all external adapters run). The
/// xUnit conformance tests construct engines in-process and never touch the protocol, which is why
/// CI stayed green.
/// </para>
/// <para>
/// These gates are protocol-level on purpose: the defect was invisible to every in-process test.
/// They are now belt-and-braces rather than the only line of defence — the runner no longer converts
/// "engine reports no export" into a pass, so the same miswiring would fail loudly today. Keeping
/// them means a capability lie is caught where it starts, with a message naming the engine, rather
/// than as an export failure on whichever spec happens to carry an <c>expectedFile</c> step.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class ServeCommandCapabilityTests
{
    private static readonly ConcurrencyPlan ColdPlan =
        new(Workers: 1, PoolSize: 1, ReuseRoster: false, ReuseGameData: false);

    /// <summary>
    /// <b>The regression gate.</b> Drives a real <c>ServeCommand.BuildOptions</c> host loop over the
    /// wire and asserts <c>exportRosterXml</c> comes back as XML for a <em>non</em>-<c>battlescribe-ui</c>
    /// engine. Before the fix this threw <see cref="NotSupportedException"/> ("exportRosterXml is not
    /// supported by this adapter") — the exact response that made the runner skip the assertion.
    /// <c>battlescribe</c> is the engine used because it is in-process IKVM: no browser, no JVM, no
    /// downloaded artifacts, so this gate runs in every CI job rather than only where artifacts exist.
    /// </summary>
    [Fact]
    public async Task ExportRosterXml_OverTheProtocol_ReturnsXml_ForANonBsUiEngine()
    {
        await using var connection = new InMemoryAdapterConnection(
            (input, output, ct) => AdapterHandler.RunAsync(
                ServeCommand.BuildOptions("battlescribe", headless: true, ColdPlan), input, output, ct));

        var ct = TestContext.Current.CancellationToken;
        Assert.IsType<SetupResult>(await connection.SendCommandAsync(
            new SetupCommand { GameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" } }, ct));

        // Through JsonProtocolEngine (not the raw response) because that is the seam RosterRunner
        // sits behind: it is the mapping ProtocolError -> NotSupportedException that turned an
        // unwired delegate into a silently-skipped byte-compare.
        var engine = new JsonProtocolEngine(connection);
        var xml = engine.ExportRosterXml();

        Assert.Contains("<roster", xml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The advertised capability gates <c>--save-roster</c> (<c>RunCommand</c>'s <c>Gate</c>), so a
    /// false negative there silently disables a flag the user passed. Every built-in exports, so
    /// every built-in must say so.
    /// </summary>
    [Theory]
    [InlineData("battlescribe")]
    [InlineData("battlescribe-ui")]
    [InlineData("newrecruit")]
    [InlineData("newrecruit-ui")]
    public void BuildOptions_AdvertisesRosterXml_ForEveryBuiltinEngine(string name)
    {
        var options = ServeCommand.BuildOptions(name, headless: true, ColdPlan);

        Assert.True(
            options.Capabilities.RosterXml,
            $"bs-engine-host serving '{name}' does not advertise capabilities.rosterXml, but that engine " +
            $"exports rosters. RunCommand gates --save-roster on this flag, so a false negative here " +
            $"disables a flag the user passed; it also makes every expectedFile byte-compare fail with " +
            $"'engine reports no export' on a spec whose engine is perfectly capable of one.");
    }

    /// <summary>
    /// <b>What keeps the unconditional <c>RosterXml = true</c> honest.</b> The capability is not a
    /// per-engine declaration in <c>EngineRegistry.Builtins</c> because there is no per-engine
    /// variation to declare — all four export — and inventing a variation that does not exist is how
    /// the name match got in. Instead the claim is pinned against the code: every concrete
    /// <see cref="IRosterEngine"/> in an assembly <c>bs-engine-host</c> references must genuinely
    /// provide an export. Add a fifth engine that cannot export and this goes red, at which point the
    /// right move is either to implement export or to introduce a real per-engine declaration —
    /// then, when there is finally something to declare.
    /// </summary>
    [Fact]
    public void EveryRosterEngineTheHostCanServe_ProvidesAnExport()
    {
        var engines = RosterEngineTypesTheHostReferences();

        // Non-vacuous: reflection must actually have found the four built-ins. A discovery bug that
        // returned an empty set would otherwise let this pass while asserting nothing.
        Assert.Contains(typeof(BattleScribeRosterEngine), engines);
        Assert.Contains(typeof(NewRecruitRosterEngine), engines);
        Assert.Contains(typeof(NrRosterUiEngine), engines);
        Assert.Contains(typeof(BsUiRosterEngine), engines);

        var withoutExport = engines.Where(t => !ProvidesRosterXmlExport(t)).ToArray();

        Assert.True(
            withoutExport.Length == 0,
            $"bs-engine-host advertises capabilities.rosterXml unconditionally and routes every engine " +
            $"through ServeCommand's exporter, but these roster engines provide no export: " +
            $"[{string.Join(", ", withoutExport.Select(t => t.Name))}]. Implement " +
            $"IRosterEngine.ExportRosterXml (an async-only export no longer counts — the host calls the " +
            $"interface member and nothing else), or make the capability a real per-engine declaration " +
            $"now that engines genuinely differ.");
    }

    /// <summary>
    /// Every engine the host can serve implements roster load, which is the state #450 left the
    /// suite in and the state a spec's <c>expectFailure</c> now depends on: a defaulted member
    /// throws <see cref="NotSupportedException"/>, the runner classifies that as
    /// <see cref="ActionFailureKind.Unsupported"/>, and an unimplemented engine therefore fails
    /// every roundtrip spec rather than passing one.
    /// <para>
    /// Worth a gate rather than a note because of how the gap arrives: adding a member to
    /// <see cref="IRosterEngine"/> with a default body compiles everywhere and warns nowhere, so a
    /// fifth engine — or a refactor that drops an override — reads as "still supported" until a
    /// spec run says otherwise. This is the same falsifiability the export test above buys, applied
    /// to the pair that had no engines behind it at all a release ago.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryRosterEngineTheHostCanServe_LoadsAndReloads()
    {
        var engines = RosterEngineTypesTheHostReferences();

        Assert.Contains(typeof(BattleScribeRosterEngine), engines);
        Assert.Contains(typeof(NewRecruitRosterEngine), engines);
        Assert.Contains(typeof(NrRosterUiEngine), engines);
        Assert.Contains(typeof(BsUiRosterEngine), engines);

        foreach (var member in new[] { nameof(IRosterEngine.LoadRoster), nameof(IRosterEngine.ReloadRoster) })
        {
            var missing = engines.Where(t => !Overrides(t, member)).ToArray();

            Assert.True(
                missing.Length == 0,
                $"These roster engines inherit IRosterEngine.{member}'s throwing default: "
                + $"[{string.Join(", ", missing.Select(t => t.Name))}]. Implement it, or opt the engine "
                + "out of the roundtrip specs explicitly ('engines: {<engine>: skip}') and say so here — "
                + "an engine that cannot load must FAIL those specs, never pass them (#309, #450).");
        }
    }

    /// <summary>
    /// Concrete <see cref="IRosterEngine"/> implementations in the <c>BattleScribeSpec.*</c> assemblies
    /// <c>bs-engine-host</c> references — i.e. exactly the engines <c>HostEngineFactory</c> can hand to
    /// the adapter loop, discovered rather than transcribed into a list that can go stale.
    /// </summary>
    private static Type[] RosterEngineTypesTheHostReferences() =>
        [.. typeof(ServeCommand).Assembly
            .GetReferencedAssemblies()
            .Where(a => a.Name?.StartsWith("BattleScribeSpec.", StringComparison.Ordinal) == true)
            .Select(Assembly.Load)
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsAssignableTo(typeof(IRosterEngine)))
            .Distinct()];

    /// <summary>
    /// Does <paramref name="engineType"/> genuinely export, or would it fall through to
    /// <see cref="IRosterEngine.ExportRosterXml"/>'s default implementation (which throws
    /// <see cref="NotSupportedException"/>)?
    /// <para>
    /// Only the interface member counts. An <c>ExportRosterXmlAsync</c> used to count too, because
    /// <c>ServeCommand</c> type-tested for <c>BsUiRosterEngine</c> and routed it; that engine now
    /// implements the sync member (wrapping its own RPC) and the host's fork is gone, so an
    /// async-only engine would <em>not</em> be exported by the host — accepting one here would put
    /// the lie back into the capability flag from the other side.
    /// </para>
    /// </summary>
    private static bool ProvidesRosterXmlExport(Type engineType)
        => Overrides(engineType, nameof(IRosterEngine.ExportRosterXml));

    /// <summary>
    /// Does <paramref name="engineType"/> genuinely implement <paramref name="member"/>, or would it
    /// fall through to the interface's default body — which, for every member asked about here,
    /// throws <see cref="NotSupportedException"/>?
    /// </summary>
    private static bool Overrides(Type engineType, string member)
    {
        // The interface map, not GetMethod: an explicit interface implementation is a real override
        // and must count, while the inherited default implementation must NOT — and the default is
        // reported here as a target method still declared on IRosterEngine itself.
        var map = engineType.GetInterfaceMap(typeof(IRosterEngine));
        var index = Array.FindIndex(map.InterfaceMethods, m => m.Name == member);

        return index >= 0 && map.TargetMethods[index].DeclaringType != typeof(IRosterEngine);
    }
}
