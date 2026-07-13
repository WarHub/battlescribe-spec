using BattleScribeSpec.Concurrency;

namespace BattleScribeSpec.Engines;

/// <summary>Launch descriptor: what to Start() for an engine.</summary>
public sealed record EngineLaunch(string Executable, string Arguments);

/// <summary>
/// Resolves an <see cref="EngineEntry"/> to a concrete process launch.
/// </summary>
public static class EngineHostLocator
{
    private const string HostProject = "BattleScribeSpec.EngineHost";
    private const string HostDllName = "bs-engine-host.dll";
    private const string HostExeName = "bs-engine-host";

    /// <summary>
    /// Resolve an entry to a concrete launch. Launchable entries pass through
    /// (arguments verbatim). Built-in entries locate bs-engine-host:
    /// 1. env BSSPEC_ENGINE_HOST (path to bs-engine-host.dll or executable),
    /// 2. bs-engine-host.dll next to the current entry assembly,
    /// 3. artifacts/bin/BattleScribeSpec.EngineHost/&lt;pivot&gt;/bs-engine-host.dll relative
    ///    to the repo root (walk up from AppContext.BaseDirectory to a .git dir),
    ///    trying the same pivot as the current assembly's artifacts path, else "debug",
    /// 4. "bs-engine-host" on PATH.
    /// Throws InvalidOperationException naming all probed locations when not found.
    /// A .dll resolution launches via "dotnet"; an executable launches directly.
    /// Headed/keep-alive for non-builtin (launchable) entries are NOT conveyed at all — neither
    /// as launch arguments nor via any environment variable. <c>--headed</c>/<c>--keep-alive</c>
    /// are silently dropped for an <c>exec:</c>/<c>dotnet:</c> adapter. Tracked as
    /// <see href="https://github.com/WarHub/battlescribe-spec/issues/305">#305</see>. A
    /// <paramref name="plan"/>, by contrast, is never silently dropped: a launchable entry that
    /// receives one throws, because there is no channel to convey it (see below).
    ///
    /// The default <paramref name="verb"/> is <c>serve</c> (the NDJSON adapter protocol on
    /// stdio). The interactive verbs (<c>probe</c>, <c>discover</c>) pass their full argument
    /// tail via <paramref name="verbArgs"/> — the host command owns those options — and this
    /// method just prefixes the verb and quotes any element containing whitespace. For those
    /// verbs the <paramref name="headed"/>/<paramref name="keepAlive"/>/<paramref name="plan"/>
    /// are not composed here; the caller places them in <paramref name="verbArgs"/> at the
    /// position the host command expects (e.g. after a discover subcommand token).
    /// </summary>
    /// <param name="entry">The resolved engine entry (built-in or launchable).</param>
    /// <param name="headed">Show the browser/app window (serve verb only; presentation, not policy).</param>
    /// <param name="keepAlive">
    /// Legacy sugar for "force reuse on" (e.g. interactive debugging via <c>run --keep-alive</c>).
    /// Composed as <c>--policy reuse=on</c> (serve no longer has its own <c>--keep-alive</c> flag —
    /// two names for the same concept would be the disease this converged vocabulary cures).
    /// Ignored when <paramref name="plan"/> is given (the plan is the authoritative decision).
    /// </param>
    /// <param name="verb">The host subcommand to invoke (default <c>serve</c>).</param>
    /// <param name="verbArgs">Full argument tail for non-<c>serve</c> verbs (see remarks).</param>
    /// <param name="plan">
    /// The concurrency/reuse decision to hand the child, composed as <c>--policy
    /// workers=N,reuse-roster=on|off,reuse-gamedata=on|off</c> (serve verb only). The harness always
    /// supplies this for a built-in engine (see <c>EngineSelection.EffectivePlan</c>) — <b>the parent
    /// decides and the child is told</b>; the child never recomputes a policy, because as a separate
    /// process it may see a different machine and silently disagree. Null composes no <c>--policy</c>
    /// flag, which drives the host to its conservative no-reuse default; that is a hand-run
    /// convenience, not a second decision-maker.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="plan"/> is non-null and <paramref name="entry"/> is not a built-in — there
    /// is no channel to convey a policy override to a launchable (<c>exec:</c>/<c>dotnet:</c>)
    /// adapter, and unlike #305's headed/keep-alive gap, a policy override is never silently
    /// dropped.
    /// </exception>
    public static EngineLaunch Resolve(
        EngineEntry entry,
        bool headed = false,
        bool keepAlive = false,
        string verb = "serve",
        IReadOnlyList<string>? verbArgs = null,
        ConcurrencyPlan? plan = null)
    {
        if (!entry.Builtin)
        {
            if (plan is not null)
            {
                throw new InvalidOperationException(
                    $"Engine '{entry.Name}' is a launchable adapter (exec:/dotnet:) and cannot receive a " +
                    "ConcurrencyPlan override — there is no channel to convey --policy to it. " +
                    "Do not pass a plan for a non-builtin engine.");
            }

            return new EngineLaunch(entry.Executable ?? throw new InvalidOperationException($"Engine '{entry.Name}' has no executable configured."), entry.Arguments ?? string.Empty);
        }

        var probed = new List<string>();
        var hostPath = ProbeEnvOverride(probed)
            ?? ProbeSiblingDll(probed)
            ?? ProbeArtifactsWalk(probed)
            ?? ProbePath(probed)
            ?? throw new InvalidOperationException(
                $"Could not locate bs-engine-host for engine '{entry.Name}'. Probed: {string.Join("; ", probed)}");

        string hostArgs;
        if (verb == "serve")
        {
            var policyParts = new List<string>();
            if (plan is { } p)
            {
                policyParts.Add($"workers={p.Workers}");
                policyParts.Add($"reuse-roster={(p.ReuseRoster ? "on" : "off")}");
                policyParts.Add($"reuse-gamedata={(p.ReuseGameData ? "on" : "off")}");
            }
            else if (keepAlive)
            {
                policyParts.Add("reuse=on");
            }

            var policyFlag = policyParts.Count > 0 ? $" --policy {string.Join(',', policyParts)}" : "";
            var flags = (headed ? " --headed" : "") + policyFlag;
            hostArgs = $"serve --engine {entry.Name}{flags}";
        }
        else
        {
            var parts = new List<string> { verb };
            if (verbArgs is not null)
            {
                parts.AddRange(verbArgs.Select(QuoteIfNeeded));
            }

            hostArgs = string.Join(' ', parts);
        }

        return hostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? new EngineLaunch("dotnet", $"{hostPath} {hostArgs}")
            : new EngineLaunch(hostPath, hostArgs);
    }

    private static string QuoteIfNeeded(string arg) =>
        arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? $"\"{arg}\"" : arg;

    private static string? ProbeEnvOverride(List<string> probed)
    {
        var env = Environment.GetEnvironmentVariable("BSSPEC_ENGINE_HOST");
        probed.Add(env is null ? "env BSSPEC_ENGINE_HOST (unset)" : $"env BSSPEC_ENGINE_HOST ({env})");
        return env is { Length: > 0 } && File.Exists(env) ? env : null;
    }

    private static string? ProbeSiblingDll(List<string> probed)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, HostDllName);
        probed.Add(candidate);
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? ProbeArtifactsWalk(List<string> probed)
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is null)
        {
            probed.Add("artifacts walk (no .git ancestor found above " + AppContext.BaseDirectory + ")");
            return null;
        }

        var pivot = ExtractPivot(AppContext.BaseDirectory) ?? "debug";
        var candidate = Path.Combine(repoRoot, "artifacts", "bin", HostProject, pivot, HostDllName);
        probed.Add(candidate);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        // If the extracted pivot doesn't exist and it's not already "debug", also probe the debug pivot
        if (pivot != "debug")
        {
            var debugCandidate = Path.Combine(repoRoot, "artifacts", "bin", HostProject, "debug", HostDllName);
            probed.Add(debugCandidate);
            if (File.Exists(debugCandidate))
            {
                return debugCandidate;
            }
        }

        return null;
    }

    private static string? ProbePath(List<string> probed)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string[] names = OperatingSystem.IsWindows()
            ? [HostExeName + ".exe", HostExeName]
            : [HostExeName];

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    probed.Add(candidate);
                    return candidate;
                }
            }
        }

        probed.Add($"{HostExeName} on PATH");
        return null;
    }

    private static string? FindRepoRoot(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    private static string? ExtractPivot(string baseDirectory)
    {
        var segments = Path.GetFullPath(baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var binIndex = Array.FindLastIndex(segments, s => s.Equals("bin", StringComparison.OrdinalIgnoreCase));
        return binIndex >= 0 && binIndex + 2 < segments.Length ? segments[binIndex + 2] : null;
    }
}
