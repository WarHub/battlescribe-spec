using System.Text.Json;

namespace BattleScribeSpec.Engines;

/// <summary>Resolved engine selection: identity + launch info + metadata.</summary>
/// <param name="Name">Registry identity (spec applicability, report labels); null for anonymous ad-hoc adapters.</param>
/// <param name="Executable">Launch executable; null for built-ins (resolved by the engine host integration).</param>
/// <param name="Arguments">Launch arguments; null when none.</param>
/// <param name="Domains">Spec domains the engine claims; the describe handshake narrows this at runtime.</param>
/// <param name="MaxParallel">Max concurrent instances; 0 = unlimited.</param>
/// <param name="Builtin">True for the in-box engines.</param>
public sealed record EngineEntry(
    string? Name,
    string? Executable,
    string? Arguments,
    IReadOnlyList<string> Domains,
    int MaxParallel,
    bool Builtin);

/// <summary>
/// Maps engine names to launch info: built-in entries plus optional repo-level
/// <c>engines.json</c> registrations (eshost-style named host registry).
/// </summary>
public sealed class EngineRegistry
{
    private static readonly string[] BothDomains = ["roster", "gamedata"];

    private static readonly Dictionary<string, EngineEntry> Builtins = new()
    {
        ["battlescribe"] = new("battlescribe", null, null, BothDomains, 0, Builtin: true),
        ["battlescribe-ui"] = new("battlescribe-ui", null, null, BothDomains, 1, Builtin: true),
        ["newrecruit"] = new("newrecruit", null, null, BothDomains, 0, Builtin: true),
        ["newrecruit-ui"] = new("newrecruit-ui", null, null, BothDomains, 0, Builtin: true),
    };

    private readonly Dictionary<string, EngineEntry> _configured;

    private EngineRegistry(Dictionary<string, EngineEntry> configured) => _configured = configured;

    public IReadOnlyCollection<string> KnownNames =>
        [.. _configured.Keys.Union(Builtins.Keys).Order()];

    /// <summary>Load from an explicit engines.json path; null → built-ins only.</summary>
    public static EngineRegistry Load(string? configPath)
    {
        if (configPath is null)
        {
            return new EngineRegistry([]);
        }

        var config = JsonSerializer.Deserialize(
            File.ReadAllText(configPath), EnginesConfigJsonContext.Default.EnginesConfig)
            ?? throw new InvalidDataException($"Invalid engines config: {configPath}");

        var configured = new Dictionary<string, EngineEntry>();
        foreach (var (name, entry) in config.Engines)
        {
            var launch = entry.Exec is { Length: > 0 }
                ? EngineConnectable.Parse($"exec:{entry.Exec}")
                : null;
            configured[name] = new EngineEntry(
                name,
                launch?.Executable,
                launch?.Arguments,
                entry.Domains is { Count: > 0 } ? [.. entry.Domains] : BothDomains,
                entry.MaxParallel,
                Builtin: false);
        }

        return new EngineRegistry(configured);
    }

    /// <summary>Walk up from <paramref name="startDirectory"/> looking for engines.json.</summary>
    public static EngineRegistry LoadDefault(string? startDirectory = null)
    {
        var dir = new DirectoryInfo(startDirectory ?? Directory.GetCurrentDirectory());
        for (; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "engines.json");
            if (File.Exists(candidate))
            {
                return Load(candidate);
            }
        }

        return new EngineRegistry([]);
    }

    /// <summary>Resolve a parsed connectable to a full entry (see class doc for rules).</summary>
    public EngineEntry Resolve(EngineConnectable connectable)
    {
        if (connectable.IsLaunchable)
        {
            // Ad-hoc launch; merge metadata when the identity is a configured name.
            var metadata = connectable.Name is not null && _configured.TryGetValue(connectable.Name, out var known)
                ? known
                : null;
            return new EngineEntry(
                connectable.Name,
                connectable.Executable,
                connectable.Arguments,
                metadata?.Domains ?? BothDomains,
                metadata?.MaxParallel ?? 0,
                Builtin: false);
        }

        var name = connectable.Name!;
        if (_configured.TryGetValue(name, out var configured))
        {
            return configured;
        }

        if (Builtins.TryGetValue(name, out var builtin))
        {
            return builtin;
        }

        throw new KeyNotFoundException(
            $"Unknown engine '{name}'. Known engines: {string.Join(", ", KnownNames)}.");
    }
}
