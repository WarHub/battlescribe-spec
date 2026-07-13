using System.Text.Json;
using BattleScribeSpec.Concurrency;

namespace BattleScribeSpec.Engines;

/// <summary>Resolved engine selection: identity + launch info + metadata.</summary>
/// <param name="Name">Registry identity (spec applicability, report labels); null for anonymous ad-hoc adapters.</param>
/// <param name="Executable">Launch executable; null for built-ins (resolved by the engine host integration).</param>
/// <param name="Arguments">Launch arguments; null when none.</param>
/// <param name="Domains">Spec domains the engine claims; the describe handshake narrows this at runtime.</param>
/// <param name="Profile">What the engine declares about itself — the single source of <c>MaxParallel</c> etc.</param>
/// <param name="Builtin">True for the in-box engines.</param>
public sealed record EngineEntry(
    string? Name,
    string? Executable,
    string? Arguments,
    IReadOnlyList<string> Domains,
    EngineProfile Profile,
    bool Builtin);

/// <summary>
/// Maps engine names to launch info: built-in entries plus optional repo-level
/// <c>engines.json</c> registrations (eshost-style named host registry).
/// </summary>
public sealed class EngineRegistry
{
    private static readonly string[] BothDomains = ["roster", "gamedata"];

    // Conservative default for engines that declare nothing: no parallelism ceiling assumed
    // beyond serial, cheap to construct, and no reuse claimed (reuse must be earned — see
    // EngineProfile's remarks).
    private static readonly EngineProfile DefaultProfile = new(
        MaxParallel: 0, ColdStartCost.Cheap, ReuseSafeRoster: false, ReuseSafeGameData: false);

    // Values transcribed from what has been MEASURED (see docs/warm-reuse.md) — never invented.
    private static readonly Dictionary<string, EngineEntry> Builtins = new()
    {
        ["battlescribe"] = new(
            "battlescribe", null, null, BothDomains,
            new EngineProfile(MaxParallel: 0, ColdStartCost.Cheap, ReuseSafeRoster: false, ReuseSafeGameData: false),
            Builtin: true),
        ["battlescribe-ui"] = new(
            "battlescribe-ui", null, null, BothDomains,
            new EngineProfile(MaxParallel: 1, ColdStartCost.Expensive, ReuseSafeRoster: true, ReuseSafeGameData: true),
            Builtin: true),
        ["newrecruit"] = new(
            "newrecruit", null, null, BothDomains,
            new EngineProfile(MaxParallel: 0, ColdStartCost.Cheap, ReuseSafeRoster: false, ReuseSafeGameData: false),
            Builtin: true),
        ["newrecruit-ui"] = new(
            "newrecruit-ui", null, null, BothDomains,
            new EngineProfile(MaxParallel: 0, ColdStartCost.Cheap, ReuseSafeRoster: false, ReuseSafeGameData: false),
            Builtin: true),
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
            EngineConnectable? launch = null;
            if (entry.Exec is { Length: > 0 })
            {
                try
                {
                    launch = EngineConnectable.Parse($"exec:{entry.Exec}");
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException(
                        $"Invalid engines config '{configPath}', entry '{name}': {ex.Message}", ex);
                }
            }
            configured[name] = new EngineEntry(
                name,
                launch?.Executable,
                launch?.Arguments,
                entry.Domains is { Count: > 0 } ? [.. entry.Domains] : BothDomains,
                new EngineProfile(
                    entry.MaxParallel,
                    entry.ColdStartCost,
                    entry.ReuseSafeRoster,
                    entry.ReuseSafeGameData,
                    entry.MemPerInstanceBytes,
                    entry.OversubscriptionFactor),
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
                metadata?.Profile ?? DefaultProfile,
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
