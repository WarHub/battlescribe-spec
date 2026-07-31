using System.CommandLine;
using System.Text.Json;
using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.Concurrency;
using BattleScribeSpec.Engines;
using BattleScribeSpec.NrRosterUiDriver;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.EngineHost;

/// <summary>
/// <c>bs-engine-host serve --engine X</c> — expose a built-in engine over the NDJSON
/// adapter protocol on stdio. One engine identity per process; the runner side pools
/// processes for parallelism.
/// </summary>
internal static class ServeCommand
{
    /// <summary>
    /// What <c>serve</c> does when it is handed no <c>--policy</c> at all: single worker, no reuse.
    /// <para>
    /// This is a <b>constant</b>, not a decision. The child MUST NOT compute a policy: it is a
    /// separate process that may see a different machine than the parent (container CPU limits,
    /// cgroup quotas), so a child that ran <see cref="ConcurrencyPolicy"/> for itself could silently
    /// disagree with the parent — two decision-makers for one decision, which is precisely the
    /// defect this design removes (see docs/superpowers/specs/2026-07-13-harness-concurrency-model-design.md,
    /// "The parent decides; the child is told").
    /// </para>
    /// <para>
    /// The harness always passes <c>--policy</c> (see <c>EngineHostLocator.Resolve</c>), so this
    /// path is reached only by a hand-run <c>bs-engine-host serve</c>. It is deliberately the
    /// conservative choice — no reuse means no warm-state failure modes for someone poking at the
    /// host directly.
    /// </para>
    /// </summary>
    private static readonly ConcurrencyPlan NoPolicyPlan =
        new(Workers: 1, PoolSize: 1, ReuseRoster: false, ReuseGameData: false);

    public static Command Create()
    {
        var engine = new Option<string>("--engine")
        {
            Description = "Built-in engine: battlescribe, battlescribe-ui, newrecruit, newrecruit-ui.",
            Required = true,
        };
        var headed = new Option<bool>("--headed") { Description = "Show the browser/app window." };
        var policy = new Option<string?>("--policy")
        {
            Description = "The concurrency/reuse decision, comma-separated KEY=VALUE: workers=N, " +
                "reuse=on|off, reuse-roster=on|off, reuse-gamedata=on|off. The client ALWAYS sends " +
                "this (it owns the decision — see ConcurrencyPolicy); serve obeys it and computes " +
                "nothing itself. Omitted only for a hand-run host: then no reuse, one worker.",
        };

        var command = new Command("serve", "Serve a built-in engine over the NDJSON adapter protocol on stdio.");
        command.Options.Add(engine);
        command.Options.Add(headed);
        command.Options.Add(policy);

        command.SetAction(async (parseResult, ct) =>
        {
            var name = parseResult.GetValue(engine)!;
            var headless = !parseResult.GetValue(headed);

            // The child is TOLD; it does not decide. No MachineProfile.Current() here, no
            // ConcurrencyPolicy.For() here — just parse what the parent sent and obey it.
            // NoPolicyPlan is a conservative constant for the hand-run case, not a second opinion.
            var plan = PolicyOverride.Apply(parseResult.GetValue(policy), NoPolicyPlan);

            await AdapterHandler.RunAsync(BuildOptions(name, headless, plan), Console.In, Console.Out, ct);
            return 0;
        });

        return command;
    }

    /// <summary>
    /// Build the adapter options for one engine identity. The reuse decision arrives as a
    /// <paramref name="plan"/> — parsed from the <c>--policy</c> the parent sent — rather than being
    /// decided here by matching <paramref name="name"/> against a string or reading an environment
    /// variable. The parent (which owns <see cref="ConcurrencyPolicy"/>) is the single
    /// decision-maker; <c>serve</c> is only ever told the answer.
    /// </summary>
    /// <param name="name">Built-in engine identity (e.g. "battlescribe-ui").</param>
    /// <param name="headless">Whether to run without showing the browser/app window.</param>
    /// <param name="plan">The concurrency/reuse decision the parent made and sent.</param>
    internal static AdapterOptions BuildOptions(string name, bool headless, ConcurrencyPlan plan)
    {
        // MaxParallel has exactly one declaration — the engine's own EngineProfile (see
        // EngineRegistry.Builtins) — not a string-match here, and not the plan's chosen worker
        // count either: this is the engine's hard CEILING, reported to the client so it knows how
        // many adapter processes it may spawn; a --policy workers=N override must not leak into it.
        var maxParallel = EngineRegistry.LoadDefault().Resolve(EngineConnectable.Parse(name)).Profile.MaxParallel;

        return new()
        {
            Name = name,
            Version = typeof(ServeCommand).Assembly.GetName().Version?.ToString(),
            RosterEngineFactory = () =>
                HostEngineFactory.CreateRosterEngineAsync(name, headless, plan.ReuseRoster).GetAwaiter().GetResult(),
            GameDataEngineFactory = () =>
                HostEngineFactory.CreateGameDataEngineAsync(name, headless, plan.ReuseGameData).GetAwaiter().GetResult(),
            Capabilities = new AdapterCapabilities
            {
                Screenshot = name is "battlescribe-ui" or "newrecruit-ui",
                Record = name is "battlescribe-ui",

                // NOT `name is "battlescribe-ui"`. ALL FOUR built-ins implement
                // IRosterEngine.ExportRosterXml, so the name match advertised a difference that does
                // not exist — and it did so on the ONE capability whose false negative was silent:
                // RosterRunner treated "export unsupported" as "the expectedFile byte-compare does
                // not apply" and returned, passing the step. Three of four engines had every
                // expectedFile assertion skipped on the protocol path. (RunCommand also gates
                // --save-roster on this flag, so the same lie disabled a flag the user explicitly
                // passed.) The runner no longer swallows that signal — an undeclared capability gap
                // now FAILS the step — so this flag being wrong is loud rather than invisible.
                //
                // There is deliberately NO per-engine declaration behind this in
                // EngineRegistry.Builtins: 4 of 4 export, so there is no variation to declare, and
                // inventing one is precisely what the name match did. What keeps the claim honest
                // is a falsifiable gate over the code instead of a transcribed table —
                // ServeCommandCapabilityTests.EveryRosterEngineTheHostCanServe_ProvidesAnExport
                // reflects over every concrete IRosterEngine in the assemblies this host
                // references. Add an engine that genuinely cannot export and it goes red; that is
                // the moment to introduce a real per-engine declaration, not before.
                RosterXml = true,
                MaxParallel = maxParallel,
            },
            // Reuse is enabled ONLY where it is measured both CORRECT (per-spec verdicts identical
            // to cold) and FASTER (bs-spec compare — see docs/warm-reuse.md); that evidence lives in
            // EngineProfile.ReuseSafeRoster/ReuseSafeGameData (EngineRegistry.Builtins) and
            // ConcurrencyPolicy.For folds it together with ColdStartCost to decide worth-it:
            //
            //   battlescribe-ui gamedata : 2.20x faster (54 specs), verdicts identical.  ENABLED.
            //   battlescribe-ui roster   : 1.79x faster (42 specs), verdicts identical.  ENABLED.
            //       Both pay off because the cold cost is a JVM + JavaFX launch per spec.
            //   newrecruit-ui   gamedata : verdicts identical, but 0.92x — NO benefit. Headless
            //                              Chromium relaunches in ~1.6s, about what NR's per-spec
            //                              reset costs. Left cold.
            //   newrecruit-ui   roster   : BROKEN — 7/8 warm-only failures. Left cold.
            //                              RE-MEASURED 2026-07-31, and the cause recorded here for a
            //                              year was WRONG. It said "the shared browser's leftover
            //                              list makes NR's Create List dropdown ambiguous". #336
            //                              fixed that leftover list — the per-spec reset had been
            //                              calling `listsStore.deleteList?.(key)`, an action the
            //                              store does not have, so it deleted nothing — and warm
            //                              reuse is still broken in exactly the same shape: only the
            //                              first roster-creating spec of a batch passes.
            //                              The dropdown is not ambiguous, it is EMPTY of the spec's
            //                              catalogue ("did not find some options"), so the residue
            //                              that matters is warm game-data/system state, not the list
            //                              row. See docs/warm-reuse.md for the compare output.
            //                              Do not promote ReuseSafeRoster off the back of a cleanup
            //                              fix — that is the exact inference this re-measurement
            //                              falsified. Fix the economics case first anyway: NR gains
            //                              nothing from warm reuse even when it works (gamedata
            //                              measured 0.92x); parallelism is the lever.
            //   battlescribe (in-process): engine construction is cheap; nothing to save.
            //
            // Known risk on battlescribe-ui: the app can intermittently self-terminate when kept
            // alive. BsUiRosterEngine self-heals (poison -> cold restart) for engine-level failures,
            // but a host-process death still fails the rest of the batch until #304 lands.
            ReuseRosterEngineAcrossSetups = plan.ReuseRoster,
            ReuseGameDataEngineAcrossSetups = plan.ReuseGameData,
            ScreenshotProvider = e => e switch
            {
                BsUiRosterEngine bs => bs.CaptureScreenshotAsync().GetAwaiter().GetResult(),
                NrRosterUiEngine nr => nr.CaptureScreenshotAsync().GetAwaiter().GetResult(),
                _ => null,
            },
            RosterXmlExporter = ExportRosterXml,
            RecordStarter = e =>
            {
                if (e is BsUiRosterEngine bs)
                {
                    bs.StartRecordingAsync().GetAwaiter().GetResult();
                }
            },
            RecordStopper = e => e is BsUiRosterEngine bs
                ? bs.StopRecordingAsync().GetAwaiter().GetResult()?.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                : null,
        };
    }

    /// <summary>
    /// Export <paramref name="engine"/>'s current roster as <c>.ros</c> XML for the
    /// <c>exportRosterXml</c> command. Every engine goes through here; the only fork is which
    /// member carries the export.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This used to be <c>e is BsUiRosterEngine bs ? … : null</c></b> — a type test that returned
    /// the <em>unsupported</em> signal for the other three engines, all of which export perfectly
    /// well. <c>AdapterHandler</c> reads a null exporter result as "this adapter cannot do that" and
    /// answers <c>ProtocolError</c>; <c>JsonProtocolEngine.ExportRosterXml</c> maps that to
    /// <see cref="NotSupportedException"/>; <c>RosterRunner.ExecuteFileAssertion</c> catches it and
    /// <c>return</c>s. Nothing fails, nothing warns — the byte-compare simply never happens.
    /// </para>
    /// <para>
    /// <b>Which is why only <see cref="NotSupportedException"/> may become null here.</b> Null means
    /// "the engine does not offer this", and it is the one answer the runner is entitled to ignore.
    /// A genuine export failure — the BS UI agent unreachable, a serializer blowing up, an engine
    /// that was never set up — must propagate: <c>AdapterHandler</c> catches it and returns a
    /// <c>ProtocolError</c> carrying the real message, which is a loud failure rather than a
    /// byte-compare that quietly did not run. Catching <see cref="Exception"/> here would rebuild
    /// the same silence one layer down.
    /// </para>
    /// </remarks>
    /// <param name="engine">The live roster engine for the current spec.</param>
    /// <returns>The roster XML, or null if this engine genuinely does not support export.</returns>
    private static string? ExportRosterXml(IRosterEngine engine)
    {
        try
        {
            // No type test: every engine implements the interface member. BsUiRosterEngine used to be
            // forked out here because its export was async-only; it now implements the sync member
            // (wrapping its own RPC) like NewRecruitRosterEngine and NrRosterUiEngine do, which also
            // makes it correct when driven in-process rather than through this host.
            return engine.ExportRosterXml();
        }
        catch (NotSupportedException)
        {
            // The engine declined the interface's default implementation — genuinely unsupported.
            return null;
        }
    }
}
