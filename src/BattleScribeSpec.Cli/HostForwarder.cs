using System.Diagnostics;
using BattleScribeSpec.Engines;

namespace BattleScribeSpec.Cli;

/// <summary>
/// Shared launcher for the CLI's interactive forwarders (<c>probe</c>/<c>discover</c>).
/// The CLI is engine-free, so these verbs run in the out-of-box <c>bs-engine-host</c>
/// process: locate the host for a built-in engine, spawn it with <b>inherited</b> stdio
/// (no redirects — the probe's tree/JSON dumps and REPLs must reach the user's console),
/// and relay its exit code.
/// </summary>
internal static class HostForwarder
{
    public static async Task<int> ForwardAsync(EngineEntry entry, string verb, IReadOnlyList<string> verbArgs)
    {
        if (!entry.Builtin)
        {
            throw new CliInputException(
                "probe/discover require a built-in engine; adapters expose no probe surface.");
        }

        var launch = EngineHostLocator.Resolve(entry, verb: verb, verbArgs: verbArgs);
        var psi = new ProcessStartInfo
        {
            FileName = launch.Executable,
            Arguments = launch.Arguments,
            UseShellExecute = false,
        };

        using var child = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start bs-engine-host.");
        await child.WaitForExitAsync();
        return child.ExitCode;
    }
}
