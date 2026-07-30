using BattleScribeSpec.GameData;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.EngineHost;

/// <summary>
/// Host-local copy of the spec-resolution helpers the interactive verbs need: resolve a
/// file path, a <c>category/id</c>, a bare <c>id</c>, or <c>-</c> (stdin) into a loaded
/// roster/gamedata spec. Mirrors the CLI's <c>SpecLoading</c> — the host is engine-free of
/// the CLI so it carries its own copy (only the methods probe/discover use).
/// </summary>
internal static class HostSpecLoading
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

        // Anchor at specs/roster so a "category/id" resolves directly — the category is the
        // immediate parent folder, above the roster grouping.
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
}
