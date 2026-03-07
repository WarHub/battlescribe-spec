using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BattleScribeSpec;

/// <summary>
/// Loads and validates spec YAML files from the specs/ directory.
/// </summary>
public static class SpecLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Load a single spec file.
    /// </summary>
    public static SpecFile Load(string yamlPath)
    {
        var yaml = File.ReadAllText(yamlPath);
        var spec = Deserializer.Deserialize<SpecFile>(yaml);
        if (string.IsNullOrEmpty(spec.Id))
            spec.Id = Path.GetFileNameWithoutExtension(yamlPath);
        return spec;
    }

    /// <summary>
    /// Discover all spec YAML files under the given directory.
    /// </summary>
    public static IEnumerable<(string Path, string Id, string Category)> DiscoverSpecs(string specsDir)
    {
        if (!Directory.Exists(specsDir))
            yield break;

        foreach (var file in Directory.EnumerateFiles(specsDir, "*.yaml", SearchOption.AllDirectories))
        {
            var dir = Path.GetDirectoryName(file);
            // Skip files in the root specs directory (e.g. coverage-matrix.yaml)
            if (string.Equals(Path.GetFullPath(dir!), Path.GetFullPath(specsDir), StringComparison.OrdinalIgnoreCase))
                continue;
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
            spec.Id = defaultId;
        return spec;
    }

    /// <summary>
    /// Find the specs directory by walking up from the test assembly location.
    /// </summary>
    public static string? FindSpecsDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var specsDir = Path.Combine(dir, "specs");
            if (Directory.Exists(specsDir))
                return specsDir;
            if (File.Exists(Path.Combine(dir, "BattleScribeSpec.slnx")))
                return Path.Combine(dir, "specs");
            dir = Path.GetDirectoryName(dir);
        }
        return null;
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
                continue;

            // Resource names look like: BattleScribeSpec.specs.category.file.yaml
            var parts = name.Split('.');
            // Find "specs" segment, category is next, then filename
            var specsIdx = Array.IndexOf(parts, "specs");
            if (specsIdx < 0 || specsIdx + 2 >= parts.Length)
                continue;

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
    /// Convert YAML setup definitions to ScenarioSpec.
    /// Requires plural 'catalogues' with at least one catalogue.
    /// </summary>
    public static ScenarioSpec ToSpecModels(SetupDef setup)
    {
        var gs = new GameSystemSpec(
            Id: setup.GameSystem.Id,
            Name: setup.GameSystem.Name,
            ForceEntries: setup.GameSystem.ForceEntries?
                .Select(fe => ConvertForceEntry(fe)).ToArray(),
            CostTypes: setup.GameSystem.CostTypes?
                .Select(ct => new CostTypeSpec(ct.Id, ct.Name, ct.DefaultCostLimit, ct.Hidden, ct.Limit)).ToArray(),
            CategoryEntries: setup.GameSystem.CategoryEntries?
                .Select(ce => new CategoryEntrySpec(ce.Id, ce.Name)).ToArray(),
            ProfileTypes: setup.GameSystem.ProfileTypes?
                .Select(pt => new ProfileTypeSpec(pt.Id, pt.Name,
                    pt.CharacteristicTypes?.Select(ct => new CharacteristicTypeSpec(ct.Id, ct.Name)).ToArray())).ToArray());

        var catalogueDefs = setup.Catalogues;
        if (catalogueDefs is null || catalogueDefs.Count == 0)
            throw new InvalidOperationException("Setup requires 'catalogues' with at least one catalogue.");

        var catalogues = catalogueDefs.Select(ConvertCatalogue).ToArray();

        return new ScenarioSpec(gs, catalogues);
    }

    private static CatalogueSpec ConvertCatalogue(CatalogueDef def)
    {
        return new CatalogueSpec(
            Id: def.Id,
            Name: def.Name,
            GameSystemId: def.GameSystemId,
            SelectionEntries: def.SelectionEntries?
                .Select(ConvertSelectionEntry).ToArray(),
            SelectionEntryGroups: def.SelectionEntryGroups?
                .Select(ConvertSelectionEntryGroup).ToArray(),
            EntryLinks: def.EntryLinks?.Select(ConvertEntryLink).ToArray(),
            SharedSelectionEntries: def.SharedSelectionEntries?
                .Select(ConvertSelectionEntry).ToArray(),
            SharedSelectionEntryGroups: def.SharedSelectionEntryGroups?
                .Select(ConvertSelectionEntryGroup).ToArray(),
            SharedRules: def.SharedRules?.Select(ConvertRule).ToArray(),
            SharedProfiles: def.SharedProfiles?.Select(ConvertProfile).ToArray(),
            SharedInfoGroups: def.SharedInfoGroups?.Select(ConvertInfoGroup).ToArray(),
            InfoLinks: def.InfoLinks?.Select(ConvertInfoLink).ToArray(),
            CatalogueLinks: def.CatalogueLinks?.Select(cl =>
                new CatalogueLinkSpec(cl.Id, cl.Name, cl.TargetId, cl.ImportRootEntries)).ToArray(),
            Publications: def.Publications?.Select(p =>
                new PublicationSpec(p.Id, p.Name, p.ShortName, p.Publisher, p.PublicationDate, p.PublisherUrl)).ToArray());
    }

    private static SelectionEntrySpec ConvertSelectionEntry(SelectionEntryDef def)
    {
        return new SelectionEntrySpec(
            Id: def.Id,
            Name: def.Name,
            Type: def.Type,
            Hidden: def.Hidden,
            Costs: def.Costs?.Select(c => new CostSpec(c.Name, c.TypeId, c.Value)).ToArray(),
            Constraints: def.Constraints?.Select(c =>
                new ConstraintSpec(c.Id, c.Type, c.Value, c.Field, c.Scope,
                    c.Shared, c.IncludeChildSelections, c.IncludeChildForces, c.PercentValue)).ToArray(),
            Modifiers: def.Modifiers?.Select(ConvertModifier).ToArray(),
            ModifierGroups: def.ModifierGroups?.Select(ConvertModifierGroup).ToArray(),
            ChildEntries: def.SelectionEntries?.Select(ConvertSelectionEntry).ToArray(),
            SelectionEntryGroups: def.SelectionEntryGroups?.Select(ConvertSelectionEntryGroup).ToArray(),
            CategoryLinks: def.CategoryLinks?.Select(cl =>
                new CategoryLinkSpec(cl.Id, cl.TargetId, cl.Name, cl.Primary)).ToArray(),
            Collective: def.Collective,
            Rules: def.Rules?.Select(ConvertRule).ToArray(),
            Profiles: def.Profiles?.Select(ConvertProfile).ToArray(),
            InfoGroups: def.InfoGroups?.Select(ConvertInfoGroup).ToArray(),
            Page: def.Page,
            EntryLinks: def.EntryLinks?.Select(ConvertEntryLink).ToArray(),
            InfoLinks: def.InfoLinks?.Select(ConvertInfoLink).ToArray(),
            Import: def.Import,
            PublicationId: def.PublicationId);
    }

    private static ForceEntrySpec ConvertForceEntry(ForceEntryDef fe) =>
        new(fe.Id, fe.Name,
            fe.CategoryLinks?.Select(cl =>
                new CategoryLinkSpec(cl.Id, cl.TargetId, cl.Name, cl.Primary)).ToArray(),
            fe.ForceEntries?.Select(ConvertForceEntry).ToArray(),
            fe.Constraints?.Select(c =>
                new ConstraintSpec(c.Id, c.Type, c.Value, c.Field, c.Scope,
                    c.Shared, c.IncludeChildSelections, c.IncludeChildForces, c.PercentValue)).ToArray());

    private static SelectionEntryGroupSpec ConvertSelectionEntryGroup(SelectionEntryGroupDef def)
    {
        return new SelectionEntryGroupSpec(
            Id: def.Id,
            Name: def.Name,
            Hidden: def.Hidden,
            DefaultSelectionEntryId: def.DefaultSelectionEntryId,
            Constraints: def.Constraints?.Select(c =>
                new ConstraintSpec(c.Id, c.Type, c.Value, c.Field, c.Scope,
                    c.Shared, c.IncludeChildSelections, c.IncludeChildForces, c.PercentValue)).ToArray(),
            Modifiers: def.Modifiers?.Select(ConvertModifier).ToArray(),
            SelectionEntries: def.SelectionEntries?.Select(ConvertSelectionEntry).ToArray(),
            Import: def.Import);
    }

    private static ModifierSpec ConvertModifier(ModifierDef def)
    {
        return new ModifierSpec(
            Type: def.Type,
            Field: def.Field,
            Value: def.Value,
            Conditions: def.Conditions?.Select(ConvertCondition).ToArray(),
            ConditionGroups: def.ConditionGroups?.Select(ConvertConditionGroup).ToArray(),
            Repeats: def.Repeats?.Select(r =>
                new RepeatSpec(r.Value, r.Repeats, r.Field, r.Scope, r.ChildId,
                    r.RoundUp, r.Shared, r.IncludeChildSelections, r.IncludeChildForces, r.PercentValue)).ToArray());
    }

    private static ConditionSpec ConvertCondition(ConditionDef def) =>
        new(def.Type, def.Value, def.Field, def.Scope, def.ChildId, def.PercentValue,
            def.Shared, def.IncludeChildSelections, def.IncludeChildForces);

    private static ConditionGroupSpec ConvertConditionGroup(ConditionGroupDef def) =>
        new(def.Type,
            def.Conditions?.Select(ConvertCondition).ToArray(),
            def.ConditionGroups?.Select(ConvertConditionGroup).ToArray());

    private static ModifierGroupSpec ConvertModifierGroup(ModifierGroupDef def) =>
        new(def.Conditions?.Select(ConvertCondition).ToArray(),
            def.ConditionGroups?.Select(ConvertConditionGroup).ToArray(),
            def.Repeats?.Select(r =>
                new RepeatSpec(r.Value, r.Repeats, r.Field, r.Scope, r.ChildId,
                    r.RoundUp, r.Shared, r.IncludeChildSelections, r.IncludeChildForces, r.PercentValue)).ToArray(),
            def.Modifiers?.Select(ConvertModifier).ToArray(),
            def.ModifierGroups?.Select(ConvertModifierGroup).ToArray());

    private static RuleSpec ConvertRule(RuleDef def) =>
        new(def.Id, def.Name, def.Description, def.Hidden, def.Page,
            def.Modifiers?.Select(ConvertModifier).ToArray(),
            def.PublicationId);

    private static ProfileSpec ConvertProfile(ProfileDef def) =>
        new(def.Id, def.Name, def.TypeId, def.TypeName, def.Hidden,
            def.Characteristics?.Select(c => new CharacteristicSpec(c.Name, c.TypeId, c.Value)).ToArray(),
            def.Modifiers?.Select(ConvertModifier).ToArray(),
            def.Page, def.PublicationId);

    private static InfoGroupSpec ConvertInfoGroup(InfoGroupDef def) =>
        new(def.Id, def.Name, def.Hidden,
            def.Profiles?.Select(ConvertProfile).ToArray(),
            def.Rules?.Select(ConvertRule).ToArray(),
            def.Modifiers?.Select(ConvertModifier).ToArray(),
            def.InfoLinks?.Select(ConvertInfoLink).ToArray(),
            def.PublicationId, def.Page);

    private static EntryLinkSpec ConvertEntryLink(EntryLinkDef def) =>
        new(def.Id, def.Name, def.TargetId, def.Type, def.Hidden,
            def.Costs?.Select(c => new CostSpec(c.Name, c.TypeId, c.Value)).ToArray(),
            def.Constraints?.Select(c =>
                new ConstraintSpec(c.Id, c.Type, c.Value, c.Field, c.Scope,
                    c.Shared, c.IncludeChildSelections, c.IncludeChildForces, c.PercentValue)).ToArray(),
            def.Modifiers?.Select(ConvertModifier).ToArray(),
            def.CategoryLinks?.Select(cl =>
                new CategoryLinkSpec(cl.Id, cl.TargetId, cl.Name, cl.Primary)).ToArray(),
            Import: def.Import,
            PublicationId: def.PublicationId,
            Page: def.Page);

    private static InfoLinkSpec ConvertInfoLink(InfoLinkDef def) =>
        new(def.Id, def.Name, def.TargetId, def.Type, def.Hidden,
            def.Modifiers?.Select(ConvertModifier).ToArray(),
            def.PublicationId, def.Page);
}
