namespace BattleScribeSpec.GameData;

/// <summary>
/// Resolves snapshot / side-file paths for file-export assertions and file loads. Files live next to
/// the spec, keyed by the step's id. Layout is either flat (preferred) — <c>{specId}.{key}.{ext}</c> —
/// or a per-spec folder — <c>{specId}/{key}.{ext}</c> — each with an optional override that adds a
/// <c>.{engine}</c> infix.
///
/// <para>The base file (no infix) is the NewRecruit output. Other engines get an override only where
/// their serialization diverges, and the override is keyed by the engine <em>family</em> (the name with
/// any <c>-ui</c> suffix stripped) so the headless and UI variants of one editor — which share a
/// serializer and therefore emit identical files — share a single override file (e.g.
/// <c>battlescribe</c> and <c>battlescribe-ui</c> both use <c>{specId}.{key}.battlescribe.{ext}</c>).
/// An exact-engine override (full <c>{engine}</c> infix, including the <c>-ui</c>) is still honored
/// first as an escape hatch should a variant ever genuinely diverge.</para>
/// </summary>
public static class GameDataSnapshotResolver
{
    /// <summary>Engine family whose output is the base/default snapshot (written without an engine infix).</summary>
    public const string BaseEngineName = "newrecruit";

    /// <summary>
    /// The snapshot family an engine belongs to: its name with any trailing <c>-ui</c> removed. Engines
    /// in the same family share a serializer (the editor's headless and UI surfaces) and so share one
    /// override file. A divergence between variants therefore fails the byte compare instead of hiding.
    /// </summary>
    public static string Family(string engine)
        => engine.EndsWith("-ui", StringComparison.Ordinal) ? engine[..^"-ui".Length] : engine;

    /// <summary>
    /// True for the NewRecruit-family engines whose output IS the base (store-direct and the NR UI
    /// produce the same NR serialization). Either may write the base file; other engines write overrides.
    /// </summary>
    public static bool IsBaseEngine(string engine) => Family(engine) == BaseEngineName;

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

    /// <summary>
    /// Candidate paths in resolution order: exact-engine override (escape hatch), then the shared
    /// family override, then the base — each tried flat before folder.
    /// </summary>
    public static IEnumerable<string> Candidates(string specDir, string specId, string key, string engine, string ext)
    {
        yield return Flat(specDir, specId, key, engine, ext);
        yield return Folder(specDir, specId, key, engine, ext);

        var family = Family(engine);
        if (family != engine)
        {
            yield return Flat(specDir, specId, key, family, ext);
            yield return Folder(specDir, specId, key, family, ext);
        }

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
