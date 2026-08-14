using BattleScribeSpec.GameData;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Cli;

/// <summary>
/// Locating and loading specs: resolves a file path, a <c>category/id</c>, a bare
/// <c>id</c>, or <c>-</c> (stdin) into a loaded spec, and infers the engine domain
/// from a spec's location.
/// </summary>
internal static class SpecLoading
{
    public static SpecFile LoadSpec(string input)
    {
        if (input == "-")
        {
            var yaml = Console.In.ReadToEnd();
            return SpecLoader.LoadFromYaml(yaml, defaultId: "stdin");
        }

        if (File.Exists(input))
        {
            return SpecLoader.Load(input);
        }

        // Anchor at specs/roster so a "category/id" (e.g. "cost/cost-hidden-limit-validation")
        // resolves directly — the category is the immediate parent folder, above the roster grouping.
        var specsDir = SpecLoader.FindRosterSpecsDirectory();
        if (specsDir is not null)
        {
            var candidate = Path.Combine(specsDir, input + ".yaml");
            if (File.Exists(candidate))
            {
                return SpecLoader.Load(candidate);
            }

            foreach (var file in Directory.EnumerateFiles(specsDir, "*.yaml", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var relative = Path.GetRelativePath(specsDir, file).Replace('\\', '/');
                if (name == input || relative == input || relative == input + ".yaml")
                {
                    return SpecLoader.Load(file);
                }
            }
        }

        throw new FileNotFoundException($"Spec not found: '{input}'. Provide a file path, category/id, or id.");
    }

    public static GameDataSpecFile LoadGameDataSpec(string input)
    {
        if (input == "-")
        {
            var yaml = Console.In.ReadToEnd();
            return SpecLoader.LoadGameDataFromYaml(yaml, defaultId: "stdin");
        }

        if (File.Exists(input))
        {
            return SpecLoader.LoadGameData(input);
        }

        var specsDir = SpecLoader.FindGameDataSpecsDirectory();
        if (specsDir is not null)
        {
            var candidate = Path.Combine(specsDir, input + ".yaml");
            if (File.Exists(candidate))
            {
                return SpecLoader.LoadGameData(candidate);
            }

            foreach (var file in Directory.EnumerateFiles(specsDir, "*.yaml", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var relative = Path.GetRelativePath(specsDir, file).Replace('\\', '/');
                if (name == input || relative == input || relative == input + ".yaml")
                {
                    return SpecLoader.LoadGameData(file);
                }
            }
        }

        throw new FileNotFoundException($"GameData spec not found: '{input}'. Provide a file path, category/id, or id.");
    }

    /// <summary>
    /// Infer the engine domain ("gamedata" or "roster") for a spec named by file path, by
    /// <c>category/id</c>, or by bare id. Defaults to roster.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A file's domain is decided by where it SITS, not by what its path spells.</b> This used to
    /// substring-scan <c>Path.GetFullPath(input)</c> for "gamedata" — the <em>absolute</em> path,
    /// which carries every directory above the checkout: a home directory, a CI workspace, an agent
    /// worktree. A roster spec checked out under any of them containing that substring
    /// (<c>/home/runner/work/gamedata-tools/repo/specs/roster/…</c>) was routed to the wrong engine,
    /// on every platform. The absolute path is the machine's, and none of it is about the spec.
    /// </para>
    /// <para>
    /// So an existing file is classified by containment in <c>specs/gamedata</c> or
    /// <c>specs/roster</c>, via <see cref="Path.GetRelativePath"/> — which applies the running
    /// platform's own path-comparison rule (case-insensitive on Windows, case-sensitive on Linux)
    /// rather than a hard-coded one. Hard-coding either is the trap #311 was filed about: folding
    /// case is wrong on ext4, and forcing <c>Ordinal</c> would be the mirror-image bug, correct on
    /// ext4 and wrong on the NTFS box this repo is developed on.
    /// </para>
    /// <para>
    /// Only when the file is outside the specs tree entirely — an ad-hoc spec passed by path — does
    /// the hint apply, and it matches a path <em>segment</em> rather than a substring, so
    /// <c>gamedata-tools/</c> is no longer the same thing as <c>gamedata/</c>. Case-insensitively:
    /// <c>specs/GameData/x.yaml</c> is a statement of intent, not a filesystem lookup, and reading it
    /// on any platform is the point — see <see cref="DomainHintIn"/>.
    /// </para>
    /// </remarks>
    public static string InferEngineType(string? input)
    {
        if (input is null or "-")
        {
            return "roster";
        }

        if (File.Exists(input))
        {
            var full = Path.GetFullPath(input);
            if (IsUnder(SpecLoader.FindGameDataSpecsDirectory(), full))
            {
                return "gamedata";
            }

            return IsUnder(SpecLoader.FindRosterSpecsDirectory(), full) ? "roster" : DomainHintIn(input);
        }

        // Not a file on disk: a bare id or category/id. A gamedata id resolves against specs/gamedata,
        // and everything else falls through to the hint in what was typed.
        var gameDataDir = SpecLoader.FindGameDataSpecsDirectory();
        if (gameDataDir is not null)
        {
            foreach (var file in Directory.EnumerateFiles(gameDataDir, "*.yaml", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var relative = Path.GetRelativePath(gameDataDir, file).Replace('\\', '/');
                if (name == input || relative == input || relative == input + ".yaml")
                {
                    return "gamedata";
                }
            }
        }

        return DomainHintIn(input);
    }

    /// <summary>
    /// Whether <paramref name="fullPath"/> lies inside <paramref name="root"/>. Delegates the
    /// comparison to <see cref="Path.GetRelativePath"/> so the platform's own casing rule applies —
    /// see <see cref="InferEngineType"/>'s remarks for why neither casing may be hard-coded.
    /// </summary>
    private static bool IsUnder(string? root, string fullPath)
    {
        if (root is null)
        {
            return false;
        }

        var relative = Path.GetRelativePath(Path.GetFullPath(root), fullPath);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// The domain a spec reference hints at: a path <em>segment</em> named "gamedata" means gamedata,
    /// anything else means roster.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Segment, not substring.</b> A substring scan cannot tell the directory that organises the
    /// specs from a directory that merely shares four letters with it, so a checkout under
    /// <c>gamedata-tools/</c> made every roster spec in it a gamedata spec. A segment is the unit
    /// that actually carries the meaning, and it costs one <c>Split</c> to ask for.
    /// </para>
    /// <para>
    /// Roster is the default rather than a detection: the old tail read
    /// <c>Contains("roster") ? "roster" : "roster"</c> — both arms identical, the probe evaluated and
    /// discarded. It has never been possible for this method to fail to answer.
    /// </para>
    /// </remarks>
    private static string DomainHintIn(string input) =>
        input.Split('/', '\\').Any(segment => segment.Equals("gamedata", StringComparison.OrdinalIgnoreCase))
            ? "gamedata"
            : "roster";
}
