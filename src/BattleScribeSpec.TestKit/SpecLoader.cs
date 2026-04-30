using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BattleScribeSpec;

/// <summary>
/// Loads and validates spec YAML files from the specs/ directory.
/// </summary>
public static class SpecLoader
{
    private static readonly IDeserializer Deserializer = new StaticDeserializerBuilder(new SpecYamlStaticContext())
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>
    /// Tag that opts a spec out of setup ID uniqueness validation.
    /// </summary>
    public const string DuplicateIdsTag = "duplicate-ids";

    /// <summary>
    /// Load a single spec file.
    /// </summary>
    public static SpecFile Load(string yamlPath)
    {
        var yaml = File.ReadAllText(yamlPath);
        var spec = Deserializer.Deserialize<SpecFile>(yaml);
        if (string.IsNullOrEmpty(spec.Id))
        {
            spec.Id = Path.GetFileNameWithoutExtension(yamlPath);
        }

        ValidateIdUniqueness(spec);
        SpecValidator.Validate(spec);
        return spec;
    }

    /// <summary>
    /// Discover all spec YAML files under the given directory.
    /// </summary>
    public static IEnumerable<(string Path, string Id, string Category)> DiscoverSpecs(string specsDir)
    {
        if (!Directory.Exists(specsDir))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(specsDir, "*.yaml", SearchOption.AllDirectories))
        {
            var dir = Path.GetDirectoryName(file);
            // Skip files in the root specs directory
            if (string.Equals(Path.GetFullPath(dir!), Path.GetFullPath(specsDir), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var category = Path.GetFileName(dir) ?? "unknown";
            var id = Path.GetFileNameWithoutExtension(file);
            yield return (file, id, category);
        }
    }

    /// <summary>
    /// Load a spec from a YAML string.
    /// </summary>
    public static SpecFile LoadFromYaml(string yaml, string? defaultId = null)
    {
        var spec = Deserializer.Deserialize<SpecFile>(yaml);
        if (string.IsNullOrEmpty(spec.Id) && defaultId is not null)
        {
            spec.Id = defaultId;
        }

        ValidateIdUniqueness(spec);
        SpecValidator.Validate(spec);
        return spec;
    }

    /// <summary>
    /// Find the specs root directory by walking up from the test assembly location.
    /// </summary>
    public static string? FindSpecsDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var specsDir = Path.Combine(dir, "specs");
            if (Directory.Exists(specsDir))
            {
                return specsDir;
            }

            if (File.Exists(Path.Combine(dir, "BattleScribeSpec.slnx")))
            {
                return Path.Combine(dir, "specs");
            }

            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// Find the roster specs directory (specs/roster/).
    /// </summary>
    public static string? FindRosterSpecsDirectory()
    {
        var specsDir = FindSpecsDirectory();
        if (specsDir is null)
        {
            return null;
        }

        var rosterDir = Path.Combine(specsDir, "roster");
        return Directory.Exists(rosterDir) ? rosterDir : null;
    }

    /// <summary>
    /// Find the gamedata specs directory (specs/gamedata/).
    /// </summary>
    public static string? FindGameDataSpecsDirectory()
    {
        var specsDir = FindSpecsDirectory();
        if (specsDir is null)
        {
            return null;
        }

        var gameDataDir = Path.Combine(specsDir, "gamedata");
        return Directory.Exists(gameDataDir) ? gameDataDir : null;
    }

    /// <summary>
    /// Load a single GameData spec file.
    /// </summary>
    public static GameDataSpecFile LoadGameData(string yamlPath)
    {
        var yaml = File.ReadAllText(yamlPath);
        var spec = Deserializer.Deserialize<GameDataSpecFile>(yaml);
        if (string.IsNullOrEmpty(spec.Id))
        {
            spec.Id = Path.GetFileNameWithoutExtension(yamlPath);
        }
        return spec;
    }

    /// <summary>
    /// Load a GameData spec from a YAML string.
    /// </summary>
    public static GameDataSpecFile LoadGameDataFromYaml(string yaml, string? defaultId = null)
    {
        var spec = Deserializer.Deserialize<GameDataSpecFile>(yaml);
        if (string.IsNullOrEmpty(spec.Id) && defaultId is not null)
        {
            spec.Id = defaultId;
        }
        return spec;
    }

    /// <summary>
    /// Discover all GameData spec YAML files under the given directory.
    /// </summary>
    public static IEnumerable<(string Path, string Id, string Category)> DiscoverGameDataSpecs(string specsDir)
    {
        if (!Directory.Exists(specsDir))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(specsDir, "*.yaml", SearchOption.AllDirectories))
        {
            var dir = Path.GetDirectoryName(file);
            if (string.Equals(Path.GetFullPath(dir!), Path.GetFullPath(specsDir), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var category = Path.GetFileName(dir) ?? "unknown";
            var id = Path.GetFileNameWithoutExtension(file);
            yield return (file, id, category);
        }
    }

    /// <summary>
    /// Discover all spec YAML files embedded in the TestKit assembly.
    /// </summary>
    public static IEnumerable<(string ResourceName, string Id, string Category)> DiscoverEmbeddedSpecs()
    {
        var assembly = typeof(SpecLoader).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Resource names look like: BattleScribeSpec.specs.category.file.yaml
            var parts = name.Split('.');
            // Find "specs" segment, category is next, then filename
            var specsIdx = Array.IndexOf(parts, "specs");
            if (specsIdx < 0 || specsIdx + 2 >= parts.Length)
            {
                continue;
            }

            var category = parts[specsIdx + 1];
            // filename is everything between category and .yaml extension
            var id = string.Join(".", parts[(specsIdx + 2)..^1]);
            yield return (name, id, category);
        }
    }

    /// <summary>
    /// Load a spec from an embedded resource.
    /// </summary>
    public static SpecFile LoadEmbedded(string resourceName)
    {
        var assembly = typeof(SpecLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        var yaml = reader.ReadToEnd();
        return LoadFromYaml(yaml);
    }

    /// <summary>
    /// Extract setup data as Protocol types directly from the deserialized YAML.
    /// Requires plural 'catalogues' with at least one catalogue.
    /// </summary>
    public static (ProtocolGameSystem GameSystem, ProtocolCatalogue[] Catalogues) GetSetupData(SetupDef setup)
    {
        var gameSystem = setup.GameSystem
            ?? throw new InvalidOperationException("Setup requires 'gameSystem'.");
        var catalogues = setup.Catalogues;
        if (catalogues is null || catalogues.Count == 0)
        {
            throw new InvalidOperationException("Setup requires 'catalogues' with at least one catalogue.");
        }

        return (gameSystem, catalogues.ToArray());
    }

    private static void ValidateIdUniqueness(SpecFile spec)
    {
        if (spec.Tags?.Contains(DuplicateIdsTag) == true)
        {
            return;
        }

        SetupIdValidator.Validate(spec.Setup, spec.Id);
    }
}
