using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.NrRosterUiDriver;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Cli;

/// <summary>
/// Central, uniform answer to "can this roster engine do X?" so the CLI can accept every
/// artifact option for every engine and report unsupported ones consistently (rather than
/// silently no-op'ing). Also unifies the per-engine screenshot capture call.
/// </summary>
internal static class EngineCapabilities
{
    public static bool SupportsScreenshots(IRosterEngine engine) =>
        engine is BsUiRosterEngine or NrRosterUiEngine;

    public static bool SupportsRecording(IRosterEngine engine) =>
        engine is BsUiRosterEngine;

    public static bool SupportsRosterXmlExport(IRosterEngine engine) =>
        engine is BsUiRosterEngine;

    public static async Task<byte[]?> CaptureScreenshotAsync(IRosterEngine engine) => engine switch
    {
        BsUiRosterEngine bs => await bs.CaptureScreenshotAsync(),
        NrRosterUiEngine nr => await nr.CaptureScreenshotAsync(),
        _ => null,
    };
}
