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
/// <c>RosterXml = name is "battlescribe-ui"</c>. All four built-ins export — three implement
/// <see cref="IRosterEngine.ExportRosterXml"/> directly and only <c>BsUiRosterEngine</c> is
/// async-only — so for <c>battlescribe</c>, <c>newrecruit</c> and <c>newrecruit-ui</c> the
/// delegate returned null, <c>AdapterHandler</c> read null as "unsupported" and answered
/// <c>ProtocolError</c>, <c>JsonProtocolEngine.ExportRosterXml</c> mapped that to
/// <see cref="NotSupportedException"/>, and <c>RosterRunner.ExecuteFileAssertion</c> caught it and
/// <c>return</c>ed — so every <c>expectedFile</c> byte-compare <b>silently passed</b> on the
/// protocol path (i.e. under <c>bs-spec run</c>, which is how <c>--report</c> matrices and all
/// external adapters run). The xUnit conformance tests construct engines in-process and never touch
/// the protocol, which is why CI stayed green.
/// </para>
/// <para>
/// These gates are protocol-level on purpose: the defect was invisible to every in-process test.
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
            $"exports rosters. RunCommand gates --save-roster on this flag and RosterRunner treats an " +
            $"unsupported export as 'skip the expectedFile assertion', so a false negative here is a " +
            $"silently-passing byte-compare, not a loud failure.");
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
            $"IRosterEngine.ExportRosterXml (or an ExportRosterXmlAsync the host can route to), or make " +
            $"the capability a real per-engine declaration now that engines genuinely differ.");
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
    /// <see cref="NotSupportedException"/>)? Both shapes the host routes count: the sync interface
    /// member, and <c>BsUiRosterEngine</c>'s async-only <c>ExportRosterXmlAsync</c>.
    /// </summary>
    private static bool ProvidesRosterXmlExport(Type engineType)
    {
        if (engineType.GetMethod("ExportRosterXmlAsync", Type.EmptyTypes) is not null)
        {
            return true;
        }

        // The interface map, not GetMethod: an explicit interface implementation is a real override
        // and must count, while the inherited default implementation must NOT — and the default is
        // reported here as a target method still declared on IRosterEngine itself.
        var map = engineType.GetInterfaceMap(typeof(IRosterEngine));
        var index = Array.FindIndex(
            map.InterfaceMethods, m => m.Name == nameof(IRosterEngine.ExportRosterXml));

        return index >= 0 && map.TargetMethods[index].DeclaringType != typeof(IRosterEngine);
    }
}
