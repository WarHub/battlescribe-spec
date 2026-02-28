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
            var category = Path.GetFileName(Path.GetDirectoryName(file)) ?? "unknown";
            var id = Path.GetFileNameWithoutExtension(file);
            yield return (file, id, category);
        }
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
    /// Convert YAML setup definitions to existing SpecModels records
    /// for use with IRosterEngine.Setup().
    /// </summary>
    public static (GameSystemSpec, CatalogueSpec) ToSpecModels(SetupDef setup)
    {
        var gs = new GameSystemSpec(
            Id: setup.GameSystem.Id,
            Name: setup.GameSystem.Name,
            ForceEntries: setup.GameSystem.ForceEntries?
                .Select(fe => new ForceEntrySpec(fe.Id, fe.Name,
                    fe.CategoryLinks?.Select(cl =>
                        new CategoryLinkSpec(cl.Id, cl.TargetId, cl.Name, cl.Primary)).ToArray())).ToArray(),
            CostTypes: setup.GameSystem.CostTypes?
                .Select(ct => new CostTypeSpec(ct.Id, ct.Name, ct.DefaultCostLimit)).ToArray(),
            CategoryEntries: setup.GameSystem.CategoryEntries?
                .Select(ce => new CategoryEntrySpec(ce.Id, ce.Name)).ToArray());

        var cat = new CatalogueSpec(
            Id: setup.Catalogue.Id,
            Name: setup.Catalogue.Name,
            GameSystemId: setup.Catalogue.GameSystemId,
            SelectionEntries: setup.Catalogue.SelectionEntries?
                .Select(ConvertSelectionEntry).ToArray(),
            SelectionEntryGroups: setup.Catalogue.SelectionEntryGroups?
                .Select(ConvertSelectionEntryGroup).ToArray());

        return (gs, cat);
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
                new CategoryLinkSpec(cl.Id, cl.TargetId, cl.Name, cl.Primary)).ToArray());
    }

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
            SelectionEntries: def.SelectionEntries?.Select(ConvertSelectionEntry).ToArray());
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
            def.Modifiers?.Select(ConvertModifier).ToArray());
}
