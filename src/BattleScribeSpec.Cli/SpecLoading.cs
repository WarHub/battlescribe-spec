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
            var tempFile = Path.GetTempFileName();
            File.WriteAllText(tempFile, yaml);
            try
            {
                return SpecLoader.Load(tempFile);
            }
            finally
            {
                File.Delete(tempFile);
            }
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
    /// Infer the engine domain ("gamedata" or "roster") from the resolved spec path/id:
    /// a path containing "gamedata" → gamedata, "roster" → roster; default roster.
    /// </summary>
    public static string InferEngineType(string? input)
    {
        if (input is null or "-")
        {
            return "roster";
        }

        var resolved = input;
        if (!File.Exists(input))
        {
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
        }
        else
        {
            resolved = Path.GetFullPath(input);
        }

        var normalized = resolved.Replace('\\', '/').ToLowerInvariant();
        if (normalized.Contains("gamedata"))
        {
            return "gamedata";
        }

        return normalized.Contains("roster") ? "roster" : "roster";
    }

    public static string? FindRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
