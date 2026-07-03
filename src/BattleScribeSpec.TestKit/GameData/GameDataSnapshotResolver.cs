namespace BattleScribeSpec.GameData;

/// <summary>
/// Resolves snapshot / side-file paths for file-export assertions and file loads. Files live next to
/// the spec, keyed by the step's id. Layout is either flat (preferred) — <c>{specId}.{key}.{ext}</c> —
/// or a per-spec folder — <c>{specId}/{key}.{ext}</c> — each with an optional override that adds a
/// <c>.{engine}</c> infix.
///
/// <para><b>Reading / comparison is engine-agnostic.</b> For any engine we prefer, in order: an
/// exact-engine override (<c>.{engine}.</c>, including any <c>-ui</c> suffix), then a shared
/// <em>family</em> override (the name with <c>-ui</c> stripped, so <c>newrecruit</c> and
/// <c>newrecruit-ui</c> share one file), then the base file (no infix). The base engine name is
/// <em>not</em> consulted when reading: whichever engine runs must match the base unless it owns an
/// override. So when a new engine is introduced it is held to the base file until someone adds an
/// override for it.</para>
///
/// <para><b>The base engine name matters only when generating/updating snapshots</b> (see
/// <see cref="ExportSnapshotAssertion"/>): the base (no-infix) file holds the
/// <see cref="BaseEngineName"/> output, and every other engine gets an override only where it
/// diverges from that base.</para>
/// </summary>
public static class GameDataSnapshotResolver
{
    /// <summary>
    /// Engine family whose output is written to the base/default snapshot (no engine infix). Consulted
    /// <em>only</em> when generating/updating snapshots — never when reading or comparing.
    /// </summary>
    public const string BaseEngineName = "newrecruit";

    /// <summary>
    /// The snapshot family an engine belongs to: its name with any trailing <c>-ui</c> removed. Engines
    /// in the same family share a serializer (the editor's headless and UI surfaces) and so share one
    /// override file. A divergence between variants therefore fails the byte compare instead of hiding.
    /// </summary>
    public static string Family(string engine)
        => engine.EndsWith("-ui", StringComparison.Ordinal) ? engine[..^"-ui".Length] : engine;

    /// <summary>
    /// True for the engines whose output IS the base (the base engine family's headless and UI
    /// surfaces). Only these engines write the base file when a snapshot is generated; every other
    /// engine writes an override.
    /// </summary>
    public static bool IsBaseEngine(string engine) => Family(engine) == BaseEngineName;

    /// <summary>
    /// Resolve the existing snapshot file for an engine, preferring a per-engine override over the
    /// base, and the flat layout over the folder layout. Returns null when none exists. A null engine
    /// resolves the base file only.
    /// </summary>
    public static string? Resolve(string specDir, string specId, string key, string? engine, string ext)
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
    /// The existing override file this engine owns (exact-engine first, then the shared family), or
    /// null if the engine currently has no override and is served by the base. Used by snapshot updates
    /// to prefer rewriting an override that already exists over touching the base.
    /// </summary>
    public static string? ExistingOverride(string specDir, string specId, string key, string engine, string ext)
    {
        foreach (var candidate in OverrideCandidates(specDir, specId, key, engine, ext))
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
    /// family override, then the base — each tried flat before folder. A null engine yields the base
    /// candidates only.
    /// </summary>
    public static IEnumerable<string> Candidates(string specDir, string specId, string key, string? engine, string ext)
    {
        if (engine is not null)
        {
            foreach (var o in OverrideCandidates(specDir, specId, key, engine, ext))
            {
                yield return o;
            }
        }

        yield return Flat(specDir, specId, key, null, ext);
        yield return Folder(specDir, specId, key, null, ext);
    }

    /// <summary>Override candidate paths (no base): exact-engine, then family — each flat before folder.</summary>
    private static IEnumerable<string> OverrideCandidates(string specDir, string specId, string key, string engine, string ext)
    {
        yield return Flat(specDir, specId, key, engine, ext);
        yield return Folder(specDir, specId, key, engine, ext);

        var family = Family(engine);
        if (family != engine)
        {
            yield return Flat(specDir, specId, key, family, ext);
            yield return Folder(specDir, specId, key, family, ext);
        }
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
