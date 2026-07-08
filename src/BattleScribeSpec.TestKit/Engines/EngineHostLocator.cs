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
    /// Headed/keep-alive for non-builtin (launchable) entries are NOT conveyed here — the
    /// CLI sets BSSPEC_HEADED=1 / BSSPEC_KEEP_ALIVE=1 on the child process env when spawning
    /// launchable adapters instead.
    ///
    /// The default <paramref name="verb"/> is <c>serve</c> (the NDJSON adapter protocol on
    /// stdio). The interactive verbs (<c>probe</c>, <c>discover</c>) pass their full argument
    /// tail via <paramref name="verbArgs"/> — the host command owns those options — and this
    /// method just prefixes the verb and quotes any element containing whitespace. For those
    /// verbs the <paramref name="headed"/>/<paramref name="keepAlive"/> flags are not composed
    /// here; the caller places them in <paramref name="verbArgs"/> at the position the host
    /// command expects (e.g. after a discover subcommand token).
    /// </summary>
    public static EngineLaunch Resolve(
        EngineEntry entry,
        bool headed = false,
        bool keepAlive = false,
        string verb = "serve",
        IReadOnlyList<string>? verbArgs = null)
    {
        if (!entry.Builtin)
        {
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
            var flags = (headed ? " --headed" : "") + (keepAlive ? " --keep-alive" : "");
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
