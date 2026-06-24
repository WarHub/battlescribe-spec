namespace BattleScribeSpec.GameData;

/// <summary>
/// Resolves snapshot / side-file paths for file-export assertions and file loads. Files live next to
/// the spec, keyed by the step's id. Layout is either flat (preferred) — <c>{specId}.{key}.{ext}</c> —
/// or a per-spec folder — <c>{specId}/{key}.{ext}</c> — each with an optional per-engine override that
/// adds a <c>.{engine}</c> infix. The base file (no engine infix) is the NewRecruit output; other
/// engines (e.g. <c>battlescribe</c>) get override files only where their serialization diverges.
/// </summary>
public static class GameDataSnapshotResolver
{
    /// <summary>Engine whose output is the base/default snapshot (written without an engine infix).</summary>
    public const string BaseEngineName = "newrecruit";

    /// <summary>
    /// True for the NewRecruit-family engines whose output IS the base (store-direct and the NR UI
    /// produce the same NR serialization). Either may write the base file; other engines write overrides.
    /// </summary>
    public static bool IsBaseEngine(string engine)
        => engine == BaseEngineName || engine == BaseEngineName + "-ui";

    /// <summary>
    /// Resolve the existing snapshot file for an engine, preferring a per-engine override over the
    /// base, and the flat layout over the folder layout. Returns null when none exists.
    /// </summary>
    public static string? Resolve(string specDir, string specId, string key, string engine, string ext)
    {
        foreach (var candidate in Candidates(specDir, specId, key, engine, ext))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Candidate paths in resolution order: per-engine override (flat, folder) then base (flat, folder).</summary>
    public static IEnumerable<string> Candidates(string specDir, string specId, string key, string engine, string ext)
    {
        yield return Flat(specDir, specId, key, engine, ext);
        yield return Folder(specDir, specId, key, engine, ext);
        yield return Flat(specDir, specId, key, null, ext);
        yield return Folder(specDir, specId, key, null, ext);
    }

    /// <summary>Flat base path (no engine infix) — the preferred write location for the base snapshot.</summary>
    public static string BasePath(string specDir, string specId, string key, string ext)
        => Flat(specDir, specId, key, null, ext);

    /// <summary>Flat per-engine override path — the preferred write location for an engine override.</summary>
    public static string OverridePath(string specDir, string specId, string key, string engine, string ext)
        => Flat(specDir, specId, key, engine, ext);

    private static string Flat(string dir, string specId, string key, string? engine, string ext)
        => Path.Combine(dir, engine is null ? $"{specId}.{key}.{ext}" : $"{specId}.{key}.{engine}.{ext}");

    private static string Folder(string dir, string specId, string key, string? engine, string ext)
        => Path.Combine(dir, specId, engine is null ? $"{key}.{ext}" : $"{key}.{engine}.{ext}");
}
