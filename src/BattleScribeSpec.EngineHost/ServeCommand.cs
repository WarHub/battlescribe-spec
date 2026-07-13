using System.CommandLine;
using System.Text.Json;
using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.Engines;
using BattleScribeSpec.NrRosterUiDriver;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.EngineHost;

/// <summary>
/// <c>bs-engine-host serve --engine X</c> — expose a built-in engine over the NDJSON
/// adapter protocol on stdio. One engine identity per process; the runner side pools
/// processes for parallelism.
/// </summary>
internal static class ServeCommand
{
    public static Command Create()
    {
        var engine = new Option<string>("--engine")
        {
            Description = "Built-in engine: battlescribe, battlescribe-ui, newrecruit, newrecruit-ui.",
            Required = true,
        };
        var headed = new Option<bool>("--headed") { Description = "Show the browser/app window." };
        var keepAlive = new Option<bool>("--keep-alive") { Description = "Keep the BattleScribe app alive between runs (battlescribe-ui)." };

        var command = new Command("serve", "Serve a built-in engine over the NDJSON adapter protocol on stdio.");
        command.Options.Add(engine);
        command.Options.Add(headed);
        command.Options.Add(keepAlive);

        command.SetAction(async (parseResult, ct) =>
        {
            var name = parseResult.GetValue(engine)!;
            var headless = !parseResult.GetValue(headed);
            var keep = parseResult.GetValue(keepAlive);

            await AdapterHandler.RunAsync(BuildOptions(name, headless, keep), Console.In, Console.Out, ct);
            return 0;
        });

        return command;
    }

    internal static AdapterOptions BuildOptions(string name, bool headless, bool keepAlive)
    {
        // Ablation toggle for the warm-reuse benchmark (bs-spec compare --config-b
        // BSSPEC_DISABLE_WARM_REUSE=1) and for diagnosing warm-vs-cold behavior differences:
        // forces every domain cold, regardless of engine identity. See docs/warm-reuse.md.
        var reuseDisabled = Environment.GetEnvironmentVariable("BSSPEC_DISABLE_WARM_REUSE") == "1";

        // MaxParallel has exactly one declaration — the engine's own EngineProfile (see
        // EngineRegistry.Builtins) — not a string-match here. This mirrors what EngineRegistry
        // already knows about the same built-in name.
        var maxParallel = EngineRegistry.LoadDefault().Resolve(EngineConnectable.Parse(name)).Profile.MaxParallel;

        return new()
        {
            Name = name,
            Version = typeof(ServeCommand).Assembly.GetName().Version?.ToString(),
            RosterEngineFactory = () =>
                HostEngineFactory.CreateRosterEngineAsync(name, headless, keepAlive).GetAwaiter().GetResult(),
            GameDataEngineFactory = () =>
                HostEngineFactory.CreateGameDataEngineAsync(name, headless).GetAwaiter().GetResult(),
            Capabilities = new AdapterCapabilities
            {
                Screenshot = name is "battlescribe-ui" or "newrecruit-ui",
                Record = name is "battlescribe-ui",
                RosterXml = name is "battlescribe-ui",
                MaxParallel = maxParallel,
            },
            // Warm-reuse is enabled ONLY where it is measured both CORRECT (per-spec verdicts
            // identical to cold) and FASTER (bs-spec compare — see docs/warm-reuse.md):
            //
            //   battlescribe-ui gamedata : 2.20x faster (54 specs), verdicts identical.  ENABLED.
            //   battlescribe-ui roster   : 1.79x faster (42 specs), verdicts identical.  ENABLED.
            //       Both pay off because the cold cost is a JVM + JavaFX launch per spec.
            //   newrecruit-ui   gamedata : verdicts identical, but 0.92x — NO benefit. Headless
            //                              Chromium relaunches in ~1.6s, about what NR's per-spec
            //                              reset costs. Left cold.
            //   newrecruit-ui   roster   : BROKEN — 6/8 warm-only failures (the shared browser's
            //                              leftover list makes NR's Create List dropdown ambiguous)
            //                              and 1.8x slower. Left cold.
            //   battlescribe (in-process): engine construction is cheap; nothing to save.
            //
            // Known risk on battlescribe-ui: the app can intermittently self-terminate when kept
            // alive. BsUiRosterEngine self-heals (poison -> cold restart) for engine-level failures,
            // but a host-process death still fails the rest of the batch until #304 lands.
            ReuseRosterEngineAcrossSetups = !reuseDisabled && name is "battlescribe-ui",
            ReuseGameDataEngineAcrossSetups = !reuseDisabled && name is "battlescribe-ui",
            ScreenshotProvider = e => e switch
            {
                BsUiRosterEngine bs => bs.CaptureScreenshotAsync().GetAwaiter().GetResult(),
                NrRosterUiEngine nr => nr.CaptureScreenshotAsync().GetAwaiter().GetResult(),
                _ => null,
            },
            RosterXmlExporter = e => e is BsUiRosterEngine bs ? bs.ExportRosterXmlAsync().GetAwaiter().GetResult() : null,
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
}
