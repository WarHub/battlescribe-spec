using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using BattleScribeSpec.Scenario;
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
        .WithTypeConverter(new ExpectFailureYamlConverter())
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
        spec.SourcePath = Path.GetFullPath(yamlPath);
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
            // Skip files sitting directly in the root — they have no category folder. Asked as
            // "is there a folder between the root and the file", which needs no path comparison and
            // therefore no casing rule; the previous OrdinalIgnoreCase full-path compare could not
            // actually differ from Ordinal here (both operands derive from `specsDir`), but it was
            // one more hand-rolled path casing decision in the code that decides which specs exist.
            if (!Path.GetRelativePath(specsDir, file).Contains(Path.DirectorySeparatorChar))
            {
                continue;
            }

            var category = Path.GetFileName(Path.GetDirectoryName(file)) ?? "unknown";
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
    /// Find the scenario specs directory (specs/scenario/) — specs that drive a data editor and a roster
    /// editor in one run (docs/adr/002-scenario-specs.md).
    /// </summary>
    public static string? FindScenarioSpecsDirectory()
    {
        var specsDir = FindSpecsDirectory();
        if (specsDir is null)
        {
            return null;
        }

        var scenarioDir = Path.Combine(specsDir, "scenario");
        return Directory.Exists(scenarioDir) ? scenarioDir : null;
    }

    /// <summary>
    /// Load a single scenario spec file.
    /// </summary>
    public static ScenarioSpecFile LoadScenario(string yamlPath)
    {
        var yaml = File.ReadAllText(yamlPath);
        var spec = Deserializer.Deserialize<ScenarioSpecFile>(yaml);
        spec.SourcePath = Path.GetFullPath(yamlPath);
        if (string.IsNullOrEmpty(spec.Id))
        {
            spec.Id = Path.GetFileNameWithoutExtension(yamlPath);
        }

        return spec;
    }

    /// <summary>
    /// Load a scenario spec from a YAML string.
    /// </summary>
    public static ScenarioSpecFile LoadScenarioFromYaml(string yaml, string? defaultId = null)
    {
        var spec = Deserializer.Deserialize<ScenarioSpecFile>(yaml);
        if (string.IsNullOrEmpty(spec.Id) && defaultId is not null)
        {
            spec.Id = defaultId;
        }

        return spec;
    }

    /// <summary>
    /// Discover all scenario spec YAML files under the given directory. Same shape as
    /// <see cref="DiscoverSpecs"/>: a file directly in the root has no category and is skipped.
    /// </summary>
    public static IEnumerable<(string Path, string Id, string Category)> DiscoverScenarioSpecs(string specsDir)
        => DiscoverGameDataSpecs(specsDir);

    /// <summary>
    /// Load a single GameData spec file.
    /// </summary>
    public static GameDataSpecFile LoadGameData(string yamlPath)
    {
        var yaml = File.ReadAllText(yamlPath);
        var spec = Deserializer.Deserialize<GameDataSpecFile>(yaml);
        spec.SourcePath = Path.GetFullPath(yamlPath);
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
            // Same root-level skip as DiscoverSpecs — see the comment there.
            if (!Path.GetRelativePath(specsDir, file).Contains(Path.DirectorySeparatorChar))
            {
                continue;
            }

            var category = Path.GetFileName(Path.GetDirectoryName(file)) ?? "unknown";
            var id = Path.GetFileNameWithoutExtension(file);
            yield return (file, id, category);
        }
    }

    /// <summary>
    /// The name a spec is known by in test rows and reports: <c>category/id</c>, its folder and its
    /// file name, as <see cref="DiscoverSpecs"/> and <see cref="DiscoverGameDataSpecs"/> derive them.
    /// </summary>
    public static string SpecName(string category, string id) => $"{category}/{id}";

    /// <summary>
    /// The path of the roster spec named <paramref name="specName"/> (see <see cref="SpecName"/>)
    /// under <see cref="FindRosterSpecsDirectory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why test rows carry a name and not a path.</b> A theory row's arguments are part of the
    /// test's display name and its uid. When the conformance and lint rows carried the spec's path,
    /// every one of those names began with wherever the repo happened to be checked out, and xunit
    /// cuts each argument at 50 characters, so a spec name longer than that was cut too and
    /// <c>--filter "DisplayName~&lt;its id&gt;"</c> could not select it. The rows now carry the name,
    /// and the test asks here for the file.
    /// </para>
    /// <para>
    /// The index is built once per process from one walk of the directory. An unknown name throws,
    /// naming the directory searched; two files with the same name throw rather than one shadowing
    /// the other.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">No roster spec has that name.</exception>
    public static string ResolveRosterSpec(string specName) => Resolve(RosterSpecIndex.Value, specName, "roster");

    /// <summary>
    /// The path of the GameData spec named <paramref name="specName"/> (see <see cref="SpecName"/>)
    /// under <see cref="FindGameDataSpecsDirectory"/>. The same index as <see cref="ResolveRosterSpec"/>.
    /// </summary>
    /// <exception cref="ArgumentException">No GameData spec has that name.</exception>
    public static string ResolveGameDataSpec(string specName) => Resolve(GameDataSpecIndex.Value, specName, "gamedata");

    /// <summary>
    /// The path of the scenario spec named <paramref name="specName"/> (see <see cref="SpecName"/>)
    /// under <see cref="FindScenarioSpecsDirectory"/>. The same index as <see cref="ResolveRosterSpec"/>.
    /// </summary>
    /// <exception cref="ArgumentException">No scenario spec has that name.</exception>
    public static string ResolveScenarioSpec(string specName) => Resolve(ScenarioSpecIndex.Value, specName, "scenario");

    private static readonly Lazy<SpecIndex> RosterSpecIndex =
        new(() => SpecIndex.Build(FindRosterSpecsDirectory(), DiscoverSpecs));

    private static readonly Lazy<SpecIndex> GameDataSpecIndex =
        new(() => SpecIndex.Build(FindGameDataSpecsDirectory(), DiscoverGameDataSpecs));

    private static readonly Lazy<SpecIndex> ScenarioSpecIndex =
        new(() => SpecIndex.Build(FindScenarioSpecsDirectory(), DiscoverScenarioSpecs));

    private static string Resolve(SpecIndex index, string specName, string domain)
    {
        if (index.Paths.TryGetValue(specName, out var path))
        {
            return path;
        }

        throw new ArgumentException(
            index.Root is null
                ? $"No {domain} spec named '{specName}': no specs/{domain} directory was found above '{AppContext.BaseDirectory}'."
                : $"No {domain} spec named '{specName}' under '{index.Root}'. A spec's name is 'category/id' — its folder and its file name without '.yaml'.",
            nameof(specName));
    }

    private sealed record SpecIndex(string? Root, IReadOnlyDictionary<string, string> Paths)
    {
        public static SpecIndex Build(string? root, Func<string, IEnumerable<(string Path, string Id, string Category)>> discover)
        {
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root is null)
            {
                return new SpecIndex(null, paths);
            }

            foreach (var (path, id, category) in discover(root))
            {
                var name = SpecName(category, id);
                if (!paths.TryAdd(name, path))
                {
                    throw new InvalidOperationException(
                        $"Two spec files are both named '{name}': '{paths[name]}' and '{path}'. " +
                        "Test rows identify a spec by its name, so it must be unique.");
                }
            }

            return new SpecIndex(root, paths);
        }
    }

    /// <summary>
    /// Discover the roster spec YAML files embedded in the TestKit assembly.
    /// <para>
    /// Resource names look like <c>BattleScribeSpec.specs.{domain}.{category}.{file}.yaml</c>: every tree
    /// under <c>specs/</c> is embedded, and only <c>roster</c> is roster specs. This used to take the
    /// segment after <c>specs</c> as the category, so every embedded spec — gamedata included — came back
    /// as a "roster" spec in category <c>roster</c> or <c>gamedata</c>, and a gamedata file then failed to
    /// load as a roster model. The other domains have no embedded fallback; their callers find them on disk.
    /// </para>
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

            var parts = name.Split('.');
            var specsIdx = Array.IndexOf(parts, "specs");
            if (specsIdx < 0 || specsIdx + 3 >= parts.Length || parts[specsIdx + 1] != "roster")
            {
                continue;
            }

            var category = parts[specsIdx + 2];
            // The file name is everything between the category and the .yaml extension.
            var id = string.Join(".", parts[(specsIdx + 3)..^1]);
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
    /// Extract setup data as Protocol types from a GameData spec setup.
    /// </summary>
    public static (ProtocolGameSystem GameSystem, ProtocolCatalogue[] Catalogues) GetGameDataSetupData(GameData.GameDataSetupDef setup)
    {
        var gameSystem = setup.GameSystem
            ?? throw new InvalidOperationException("GameData setup requires 'gameSystem'.");
        var catalogues = setup.Catalogues ?? [];
        return (gameSystem, catalogues.ToArray());
    }

    /// <summary>
    /// Extract setup data as Protocol types directly from the deserialized YAML.
    /// Requires plural 'catalogues' with at least one catalogue.
    /// When <paramref name="specId"/> is provided, empty game system name/id and catalogue
    /// name/gameSystemId fields are filled with spec-derived defaults.
    /// </summary>
    public static (ProtocolGameSystem GameSystem, ProtocolCatalogue[] Catalogues) GetSetupData(SetupDef setup, string? specId = null)
    {
        var gameSystem = setup.GameSystem
            ?? throw new InvalidOperationException("Setup requires 'gameSystem'.");
        var catalogues = setup.Catalogues;
        if (catalogues is null || catalogues.Count == 0)
        {
            throw new InvalidOperationException("Setup requires 'catalogues' with at least one catalogue.");
        }

        if (specId is not null)
        {
            ApplySetupDefaults(gameSystem, catalogues, specId);
        }

        return (gameSystem, catalogues.ToArray());
    }

    /// <summary>
    /// Fills in empty name/id fields in game system and catalogues with spec-derived defaults.
    /// Only mutates fields that are empty — explicit YAML values are preserved.
    /// </summary>
    private static void ApplySetupDefaults(ProtocolGameSystem gameSystem, IList<ProtocolCatalogue> catalogues, string specId)
    {
        if (string.IsNullOrEmpty(gameSystem.Id))
        {
            gameSystem.Id = specId;
        }
        if (string.IsNullOrEmpty(gameSystem.Name))
        {
            gameSystem.Name = specId;
        }

        var multiCat = catalogues.Count > 1;
        for (var i = 0; i < catalogues.Count; i++)
        {
            var cat = catalogues[i];
            if (string.IsNullOrEmpty(cat.GameSystemId))
            {
                cat.GameSystemId = gameSystem.Id;
            }
            if (string.IsNullOrEmpty(cat.Name))
            {
                cat.Name = multiCat ? $"{specId}-{i + 1}" : specId;
            }
        }
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
