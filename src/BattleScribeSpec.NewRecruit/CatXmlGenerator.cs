using BattleScribeSpec;
using BattleScribeSpec.Protocol;
using WarHub.ArmouryModel.Source;
using WarHub.ArmouryModel.Source.BattleScribe;
using static WarHub.ArmouryModel.Source.NodeFactory;

namespace BattleScribeSpec.NewRecruit;

public static class CatXmlGenerator
{
    public static string GenerateGameSystemXml(ProtocolGameSystem gameSystem) =>
        SerializeNode(MapGameSystem(gameSystem));

    public static string GenerateCatalogueXml(ProtocolGameSystem gameSystem, ProtocolCatalogue catalogue)
    {
        var gamesystem = MapGameSystem(gameSystem);
        return SerializeNode(MapCatalogue(gamesystem, catalogue));
    }

    public static string GenerateCatalogueXml(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        if (catalogues.Length == 0)
        {
            throw new ArgumentException("At least one catalogue is required.", nameof(catalogues));
        }

        return GenerateCatalogueXml(gameSystem, catalogues[0]);
    }

    /// <summary>
    /// Generate XML for all catalogues. Returns (filename, xml) pairs.
    /// </summary>
    public static IReadOnlyList<(string FileName, string Xml)> GenerateAllCatalogueXml(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        if (catalogues.Length == 0)
            throw new ArgumentException("At least one catalogue is required.", nameof(catalogues));

        var gamesystem = MapGameSystem(gameSystem);
        var result = new List<(string, string)>();
        for (var i = 0; i < catalogues.Length; i++)
        {
            var xml = SerializeNode(MapCatalogue(gamesystem, catalogues[i]));
            result.Add(($"catalogue{i}.cat", xml));
        }
        return result;
    }

    private static GamesystemNode MapGameSystem(ProtocolGameSystem gameSystem)
    {
        var node = Gamesystem(name: gameSystem.Name, id: gameSystem.Id);

        if (gameSystem.CostTypes is { } costTypes)
            node = node.AddCostTypes(costTypes.Select(MapCostType));

        if (gameSystem.ForceEntries is { } forceEntries)
            node = node.AddForceEntries(forceEntries.Select(MapForceEntry));

        if (gameSystem.CategoryEntries is { } categoryEntries)
            node = node.AddCategoryEntries(categoryEntries.Select(MapCategoryEntry));

        if (gameSystem.ProfileTypes is { } profileTypes)
            node = node.AddProfileTypes(profileTypes.Select(MapProfileType));

        if (gameSystem.Publications is { } publications)
            node = node.AddPublications(publications.Select(MapPublication));

        if (gameSystem.SelectionEntries is { } selectionEntries)
            node = node.AddSelectionEntries(selectionEntries.Select(MapSelectionEntry));

        if (gameSystem.EntryLinks is { } entryLinks)
            node = node.AddEntryLinks(entryLinks.Select(MapEntryLink));

        if (gameSystem.Rules is { } rules)
            node = node.AddRules(rules.Select(MapRule));

        if (gameSystem.InfoLinks is { } infoLinks)
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));

        if (gameSystem.SharedSelectionEntries is { } sharedSelectionEntries)
            node = node.AddSharedSelectionEntries(sharedSelectionEntries.Select(MapSelectionEntry));

        if (gameSystem.SharedSelectionEntryGroups is { } sharedSelectionEntryGroups)
            node = node.AddSharedSelectionEntryGroups(sharedSelectionEntryGroups.Select(MapSelectionEntryGroup));

        if (gameSystem.SharedRules is { } sharedRules)
            node = node.AddSharedRules(sharedRules.Select(MapRule));

        if (gameSystem.SharedProfiles is { } sharedProfiles)
            node = node.AddSharedProfiles(sharedProfiles.Select(MapProfile));

        if (gameSystem.SharedInfoGroups is { } sharedInfoGroups)
            node = node.AddSharedInfoGroups(sharedInfoGroups.Select(MapInfoGroup));

        return node;
    }

    private static CatalogueNode MapCatalogue(GamesystemNode gamesystem, ProtocolCatalogue catalogue)
    {
        var node = Catalogue(gamesystem: gamesystem, name: catalogue.Name, id: catalogue.Id);

        if (catalogue.SelectionEntries is { } selectionEntries)
            node = node.AddSelectionEntries(selectionEntries.Select(MapSelectionEntry));

        if (catalogue.EntryLinks is { } entryLinks)
            node = node.AddEntryLinks(entryLinks.Select(MapEntryLink));

        if (catalogue.SharedSelectionEntries is { } sharedSelectionEntries)
            node = node.AddSharedSelectionEntries(sharedSelectionEntries.Select(MapSelectionEntry));

        if (catalogue.SharedSelectionEntryGroups is { } sharedSelectionEntryGroups)
            node = node.AddSharedSelectionEntryGroups(sharedSelectionEntryGroups.Select(MapSelectionEntryGroup));

        if (catalogue.SharedRules is { } sharedRules)
            node = node.AddSharedRules(sharedRules.Select(MapRule));

        if (catalogue.SharedProfiles is { } sharedProfiles)
            node = node.AddSharedProfiles(sharedProfiles.Select(MapProfile));

        if (catalogue.SharedInfoGroups is { } sharedInfoGroups)
            node = node.AddSharedInfoGroups(sharedInfoGroups.Select(MapInfoGroup));

        if (catalogue.Rules is { } rules)
            node = node.AddRules(rules.Select(MapRule));

        if (catalogue.InfoLinks is { } infoLinks)
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));

        if (catalogue.CatalogueLinks is { } catalogueLinks)
            node = node.AddCatalogueLinks(catalogueLinks.Select(MapCatalogueLink));

        if (catalogue.Publications is { } publications)
            node = node.AddPublications(publications.Select(MapPublication));

        if (catalogue.CostTypes is { } costTypes)
            node = node.AddCostTypes(costTypes.Select(MapCostType));

        if (catalogue.ProfileTypes is { } profileTypes)
            node = node.AddProfileTypes(profileTypes.Select(MapProfileType));

        if (catalogue.CategoryEntries is { } categoryEntries)
            node = node.AddCategoryEntries(categoryEntries.Select(MapCategoryEntry));

        if (catalogue.ForceEntries is { } forceEntries)
            node = node.AddForceEntries(forceEntries.Select(MapForceEntry));

        return node;
    }

    private static CostTypeNode MapCostType(ProtocolCostType spec) =>
        CostType(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            defaultCostLimit: spec.DefaultCostLimit is { } dcl ? (decimal)dcl : -1m,
            hidden: spec.Hidden);

    private static ForceEntryNode MapForceEntry(ProtocolForceEntry spec)
    {
        var page = string.IsNullOrWhiteSpace(spec.Page) ? null : spec.Page;
        var pubId = string.IsNullOrWhiteSpace(spec.PublicationId) ? null : spec.PublicationId;
        var node = ForceEntry(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: pubId,
            page: page,
            hidden: spec.Hidden);

        if (spec.Constraints is { } constraints)
            node = node.AddConstraints(constraints.Select(MapConstraint));

        if (spec.Modifiers is { } modifiers)
            node = node.AddModifiers(modifiers.Select(MapModifier));

        if (spec.ModifierGroups is { } modifierGroups)
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));

        if (spec.CategoryLinks is { } categoryLinks)
            node = node.AddCategoryLinks(categoryLinks.Select(MapCategoryLink));

        if (spec.ForceEntries is { } forceEntries)
            node = node.AddForceEntries(forceEntries.Select(MapForceEntry));

        if (spec.Rules is { } rules)
            node = node.AddRules(rules.Select(MapRule));

        if (spec.Profiles is { } profiles)
            node = node.AddProfiles(profiles.Select(MapProfile));

        if (spec.InfoGroups is { } infoGroups)
            node = node.AddInfoGroups(infoGroups.Select(MapInfoGroup));

        if (spec.InfoLinks is { } infoLinks)
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));

        return node;
    }

    private static CategoryEntryNode MapCategoryEntry(ProtocolCategoryEntry spec)
    {
        var page = string.IsNullOrWhiteSpace(spec.Page) ? null : spec.Page;
        var pubId = string.IsNullOrWhiteSpace(spec.PublicationId) ? null : spec.PublicationId;
        var node = CategoryEntry(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: pubId,
            page: page,
            hidden: spec.Hidden);

        if (spec.Constraints is { } constraints)
            node = node.AddConstraints(constraints.Select(MapConstraint));

        if (spec.Modifiers is { } modifiers)
            node = node.AddModifiers(modifiers.Select(MapModifier));

        if (spec.ModifierGroups is { } modifierGroups)
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));

        if (spec.Rules is { } rules)
            node = node.AddRules(rules.Select(MapRule));

        if (spec.Profiles is { } profiles)
            node = node.AddProfiles(profiles.Select(MapProfile));

        if (spec.InfoGroups is { } infoGroups)
            node = node.AddInfoGroups(infoGroups.Select(MapInfoGroup));

        if (spec.InfoLinks is { } infoLinks)
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));

        return node;
    }

    private static ProfileTypeNode MapProfileType(ProtocolProfileType spec)
    {
        var node = ProfileType(comment: null, id: spec.Id, name: spec.Name);
        if (spec.CharacteristicTypes is { } characteristicTypes)
            node = node.AddCharacteristicTypes(characteristicTypes.Select(MapCharacteristicType));

        return node;
    }

    private static CharacteristicTypeNode MapCharacteristicType(ProtocolCharacteristicType spec) =>
        CharacteristicType(comment: null, id: spec.Id, name: spec.Name);

    private static SelectionEntryNode MapSelectionEntry(ProtocolSelectionEntry spec)
    {
        var page = string.IsNullOrWhiteSpace(spec.Page) ? null : spec.Page;
        var pubId = string.IsNullOrWhiteSpace(spec.PublicationId) ? null : spec.PublicationId;
        var node = SelectionEntry(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: pubId,
            page: page,
            hidden: spec.Hidden,
            collective: spec.Collective,
            exported: spec.Import,
            type: MapSelectionEntryKind(spec.Type));

        if (spec.Costs is { } costs)
            node = node.AddCosts(costs.Select(MapCost));

        if (spec.Constraints is { } constraints)
            node = node.AddConstraints(constraints.Select(MapConstraint));

        if (spec.Modifiers is { } modifiers)
            node = node.AddModifiers(modifiers.Select(MapModifier));

        if (spec.ModifierGroups is { } modifierGroups)
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));

        if (spec.SelectionEntries is { } selectionEntries)
            node = node.AddSelectionEntries(selectionEntries.Select(MapSelectionEntry));

        if (spec.SelectionEntryGroups is { } selectionEntryGroups)
            node = node.AddSelectionEntryGroups(selectionEntryGroups.Select(MapSelectionEntryGroup));

        if (spec.CategoryLinks is { } categoryLinks)
            node = node.AddCategoryLinks(categoryLinks.Select(MapCategoryLink));

        if (spec.Rules is { } rules)
            node = node.AddRules(rules.Select(MapRule));

        if (spec.Profiles is { } profiles)
            node = node.AddProfiles(profiles.Select(MapProfile));

        if (spec.InfoGroups is { } infoGroups)
            node = node.AddInfoGroups(infoGroups.Select(MapInfoGroup));

        if (spec.EntryLinks is { } entryLinks)
            node = node.AddEntryLinks(entryLinks.Select(MapEntryLink));

        if (spec.InfoLinks is { } infoLinks)
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));

        return node;
    }

    private static SelectionEntryGroupNode MapSelectionEntryGroup(ProtocolSelectionEntryGroup spec)
    {
        var defaultSelectionEntryId = string.IsNullOrWhiteSpace(spec.DefaultSelectionEntryId)
            ? null
            : spec.DefaultSelectionEntryId;
        var pubId = string.IsNullOrWhiteSpace(spec.PublicationId) ? null : spec.PublicationId;
        var page = string.IsNullOrWhiteSpace(spec.Page) ? null : spec.Page;
        var node = SelectionEntryGroup(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: pubId,
            page: page,
            hidden: spec.Hidden,
            collective: spec.Collective,
            exported: spec.Import,
            defaultSelectionEntryId: defaultSelectionEntryId);

        if (spec.Constraints is { } constraints)
            node = node.AddConstraints(constraints.Select(MapConstraint));

        if (spec.Modifiers is { } modifiers)
            node = node.AddModifiers(modifiers.Select(MapModifier));

        if (spec.ModifierGroups is { } modifierGroups)
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));

        if (spec.SelectionEntries is { } selectionEntries)
            node = node.AddSelectionEntries(selectionEntries.Select(MapSelectionEntry));

        if (spec.SelectionEntryGroups is { } selectionEntryGroups)
            node = node.AddSelectionEntryGroups(selectionEntryGroups.Select(MapSelectionEntryGroup));

        if (spec.EntryLinks is { } entryLinks)
            node = node.AddEntryLinks(entryLinks.Select(MapEntryLink));

        if (spec.CategoryLinks is { } categoryLinks)
            node = node.AddCategoryLinks(categoryLinks.Select(MapCategoryLink));

        if (spec.Profiles is { } profiles)
            node = node.AddProfiles(profiles.Select(MapProfile));

        if (spec.Rules is { } rules)
            node = node.AddRules(rules.Select(MapRule));

        if (spec.InfoGroups is { } infoGroups)
            node = node.AddInfoGroups(infoGroups.Select(MapInfoGroup));

        if (spec.InfoLinks is { } infoLinks)
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));

        return node;
    }

    private static EntryLinkNode MapEntryLink(ProtocolEntryLink spec)
    {
        var pubId = string.IsNullOrWhiteSpace(spec.PublicationId) ? null : spec.PublicationId;
        var page = string.IsNullOrWhiteSpace(spec.Page) ? null : spec.Page;
        var node = EntryLink(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: pubId,
            page: page,
            hidden: spec.Hidden,
            collective: spec.Collective,
            exported: spec.Import,
            targetId: spec.TargetId,
            type: MapEntryLinkKind(spec.Type));

        if (spec.Costs is { } costs)
            node = node.AddCosts(costs.Select(MapCost));

        if (spec.Constraints is { } constraints)
            node = node.AddConstraints(constraints.Select(MapConstraint));

        if (spec.Modifiers is { } modifiers)
            node = node.AddModifiers(modifiers.Select(MapModifier));

        if (spec.ModifierGroups is { } modifierGroups)
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));

        if (spec.CategoryLinks is { } categoryLinks)
            node = node.AddCategoryLinks(categoryLinks.Select(MapCategoryLink));

        if (spec.SelectionEntries is { } selectionEntries)
            node = node.AddSelectionEntries(selectionEntries.Select(MapSelectionEntry));

        if (spec.SelectionEntryGroups is { } selectionEntryGroups)
            node = node.AddSelectionEntryGroups(selectionEntryGroups.Select(MapSelectionEntryGroup));

        if (spec.EntryLinks is { } entryLinks)
            node = node.AddEntryLinks(entryLinks.Select(MapEntryLink));

        if (spec.Profiles is { } profiles)
            node = node.AddProfiles(profiles.Select(MapProfile));

        if (spec.Rules is { } rules)
            node = node.AddRules(rules.Select(MapRule));

        if (spec.InfoGroups is { } infoGroups)
            node = node.AddInfoGroups(infoGroups.Select(MapInfoGroup));

        if (spec.InfoLinks is { } infoLinks)
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));

        return node;
    }

    private static ConstraintNode MapConstraint(ProtocolConstraint spec) =>
        Constraint(
            comment: null,
            field: spec.Field,
            scope: spec.Scope,
            value: (decimal)spec.Value,
            isValuePercentage: spec.PercentValue,
            shared: spec.Shared,
            includeChildSelections: spec.IncludeChildSelections,
            includeChildForces: spec.IncludeChildForces,
            id: spec.Id,
            type: MapConstraintKind(spec.Type));

    private static ModifierNode MapModifier(ProtocolModifier spec)
    {
        var node = Modifier(comment: null, type: MapModifierKind(spec.Type), field: spec.Field, value: spec.Value);

        if (spec.Conditions is { } conditions)
            node = node.AddConditions(conditions.Select(MapCondition));

        if (spec.ConditionGroups is { } conditionGroups)
            node = node.AddConditionGroups(conditionGroups.Select(MapConditionGroup));

        if (spec.Repeats is { } repeats)
            node = node.AddRepeats(repeats.Select(MapRepeat));

        return node;
    }

    private static ModifierGroupNode MapModifierGroup(ProtocolModifierGroup spec)
    {
        var node = ModifierGroup();

        if (spec.Conditions is { } conditions)
            node = node.AddConditions(conditions.Select(MapCondition));

        if (spec.ConditionGroups is { } conditionGroups)
            node = node.AddConditionGroups(conditionGroups.Select(MapConditionGroup));

        if (spec.Repeats is { } repeats)
            node = node.AddRepeats(repeats.Select(MapRepeat));

        if (spec.Modifiers is { } modifiers)
            node = node.AddModifiers(modifiers.Select(MapModifier));

        if (spec.ModifierGroups is { } modifierGroups)
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));

        return node;
    }

    private static ConditionNode MapCondition(ProtocolCondition spec)
    {
        var childId = string.IsNullOrWhiteSpace(spec.ChildId) ? null : spec.ChildId;
        return Condition(
            comment: null,
            field: spec.Field,
            scope: spec.Scope,
            value: (decimal)spec.Value,
            isValuePercentage: spec.PercentValue,
            shared: spec.Shared,
            includeChildSelections: spec.IncludeChildSelections,
            includeChildForces: spec.IncludeChildForces,
            childId: childId,
            type: MapConditionKind(spec.Type));
    }

    private static ConditionGroupNode MapConditionGroup(ProtocolConditionGroup spec)
    {
        var node = ConditionGroup(type: MapConditionGroupKind(spec.Type));

        if (spec.Conditions is { } conditions)
            node = node.AddConditions(conditions.Select(MapCondition));

        if (spec.ConditionGroups is { } conditionGroups)
            node = node.AddConditionGroups(conditionGroups.Select(MapConditionGroup));

        return node;
    }

    private static RepeatNode MapRepeat(ProtocolRepeat spec)
    {
        var childId = string.IsNullOrWhiteSpace(spec.ChildId) ? null : spec.ChildId;
        return Repeat(
            comment: null,
            field: spec.Field,
            scope: spec.Scope,
            value: (decimal)spec.Value,
            isValuePercentage: spec.PercentValue,
            shared: spec.Shared,
            includeChildSelections: spec.IncludeChildSelections,
            includeChildForces: spec.IncludeChildForces,
            childId: childId,
            repeatCount: spec.Repeats,
            roundUp: spec.RoundUp);
    }

    private static CategoryLinkNode MapCategoryLink(ProtocolCategoryLink spec)
    {
        var page = string.IsNullOrWhiteSpace(spec.Page) ? null : spec.Page;
        var pubId = string.IsNullOrWhiteSpace(spec.PublicationId) ? null : spec.PublicationId;
        var node = CategoryLink(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: pubId,
            page: page,
            hidden: spec.Hidden,
            targetId: spec.TargetId,
            primary: spec.Primary);

        if (spec.Constraints is { } constraints)
            node = node.AddConstraints(constraints.Select(MapConstraint));

        if (spec.Modifiers is { } modifiers)
            node = node.AddModifiers(modifiers.Select(MapModifier));

        if (spec.ModifierGroups is { } modifierGroups)
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));

        if (spec.Rules is { } rules)
            node = node.AddRules(rules.Select(MapRule));

        if (spec.Profiles is { } profiles)
            node = node.AddProfiles(profiles.Select(MapProfile));

        if (spec.InfoGroups is { } infoGroups)
            node = node.AddInfoGroups(infoGroups.Select(MapInfoGroup));

        if (spec.InfoLinks is { } infoLinks)
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));

        return node;
    }

    private static RuleNode MapRule(ProtocolRule spec)
    {
        var page = string.IsNullOrWhiteSpace(spec.Page) ? null : spec.Page;
        var pubId = string.IsNullOrWhiteSpace(spec.PublicationId) ? null : spec.PublicationId;
        var node = Rule(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: pubId,
            page: page,
            hidden: spec.Hidden,
            description: spec.Description);

        if (spec.Modifiers is { } modifiers)
            node = node.AddModifiers(modifiers.Select(MapModifier));

        if (spec.ModifierGroups is { } modifierGroups)
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));

        return node;
    }

    private static ProfileNode MapProfile(ProtocolProfile spec)
    {
        var page = string.IsNullOrWhiteSpace(spec.Page) ? null : spec.Page;
        var pubId = string.IsNullOrWhiteSpace(spec.PublicationId) ? null : spec.PublicationId;
        var node = Profile(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: pubId,
            page: page,
            hidden: spec.Hidden,
            typeId: spec.TypeId,
            typeName: spec.TypeName);

        if (spec.Characteristics is { } characteristics)
            node = node.AddCharacteristics(characteristics.Select(MapCharacteristic));

        if (spec.Modifiers is { } modifiers)
            node = node.AddModifiers(modifiers.Select(MapModifier));

        if (spec.ModifierGroups is { } modifierGroups)
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));

        return node;
    }

    private static CharacteristicNode MapCharacteristic(ProtocolCharacteristic spec) =>
        Characteristic(name: spec.Name, typeId: spec.TypeId, value: spec.Value);

    private static InfoGroupNode MapInfoGroup(ProtocolInfoGroup spec)
    {
        var pubId = string.IsNullOrWhiteSpace(spec.PublicationId) ? null : spec.PublicationId;
        var page = string.IsNullOrWhiteSpace(spec.Page) ? null : spec.Page;
        var node = InfoGroup(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: pubId,
            page: page,
            hidden: spec.Hidden);

        if (spec.Profiles is { } profiles)
            node = node.AddProfiles(profiles.Select(MapProfile));

        if (spec.Rules is { } rules)
            node = node.AddRules(rules.Select(MapRule));

        if (spec.Modifiers is { } modifiers)
            node = node.AddModifiers(modifiers.Select(MapModifier));

        if (spec.ModifierGroups is { } modifierGroups)
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));

        if (spec.InfoLinks is { } infoLinks)
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));

        if (spec.InfoGroups is { } infoGroups)
            node = node.AddInfoGroups(infoGroups.Select(MapInfoGroup));

        return node;
    }

    private static CatalogueLinkNode MapCatalogueLink(ProtocolCatalogueLink spec) =>
        CatalogueLink(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            targetId: spec.TargetId,
            type: CatalogueLinkKind.Catalogue,
            importRootEntries: spec.ImportRootEntries);

    private static PublicationNode MapPublication(ProtocolPublication spec)
    {
        var shortName = string.IsNullOrWhiteSpace(spec.ShortName) ? null : spec.ShortName;
        var publisher = string.IsNullOrWhiteSpace(spec.Publisher) ? null : spec.Publisher;
        var publicationDate = string.IsNullOrWhiteSpace(spec.PublicationDate) ? null : spec.PublicationDate;
        var publisherUrl = string.IsNullOrWhiteSpace(spec.PublisherUrl) ? null : spec.PublisherUrl;
        return Publication(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            shortName: shortName,
            publisher: publisher,
            publicationDate: publicationDate,
            publisherUrl: publisherUrl);
    }

    private static InfoLinkNode MapInfoLink(ProtocolInfoLink spec)
    {
        var pubId = string.IsNullOrWhiteSpace(spec.PublicationId) ? null : spec.PublicationId;
        var page = string.IsNullOrWhiteSpace(spec.Page) ? null : spec.Page;
        var node = InfoLink(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: pubId,
            page: page,
            hidden: spec.Hidden,
            targetId: spec.TargetId,
            type: MapInfoLinkKind(spec.Type));

        if (spec.Modifiers is { } modifiers)
            node = node.AddModifiers(modifiers.Select(MapModifier));

        if (spec.ModifierGroups is { } modifierGroups)
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));

        return node;
    }

    private static CostNode MapCost(ProtocolCostValue spec) =>
        Cost(name: spec.Name, typeId: spec.TypeId, value: (decimal)spec.Value);

    private static ConstraintKind MapConstraintKind(string value) =>
        value switch
        {
            "min" or "atLeast" => ConstraintKind.Minimum,
            "max" or "atMost" => ConstraintKind.Maximum,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported constraint kind."),
        };

    private static ModifierKind MapModifierKind(string value) =>
        value switch
        {
            "set" => ModifierKind.Set,
            "increment" => ModifierKind.Increment,
            "decrement" => ModifierKind.Decrement,
            "append" => ModifierKind.Append,
            "add" => ModifierKind.Add,
            "remove" => ModifierKind.Remove,
            "set-primary" => ModifierKind.SetPrimary,
            "unset-primary" => ModifierKind.UnsetPrimary,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported modifier kind."),
        };

    private static ConditionKind MapConditionKind(string value) =>
        value switch
        {
            "lessThan" => ConditionKind.LessThan,
            "greaterThan" => ConditionKind.GreaterThan,
            "equalTo" => ConditionKind.EqualTo,
            "notEqualTo" => ConditionKind.NotEqualTo,
            "atLeast" => ConditionKind.AtLeast,
            "atMost" => ConditionKind.AtMost,
            "instanceOf" => ConditionKind.InstanceOf,
            "notInstanceOf" => ConditionKind.NotInstanceOf,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported condition kind."),
        };

    private static ConditionGroupKind MapConditionGroupKind(string value) =>
        value switch
        {
            "and" => ConditionGroupKind.And,
            "or" => ConditionGroupKind.Or,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported condition group kind."),
        };

    private static SelectionEntryKind MapSelectionEntryKind(string value) =>
        value switch
        {
            "upgrade" => SelectionEntryKind.Upgrade,
            "model" => SelectionEntryKind.Model,
            "unit" => SelectionEntryKind.Unit,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported selection entry kind."),
        };

    private static EntryLinkKind MapEntryLinkKind(string value) =>
        value switch
        {
            "selectionEntry" => EntryLinkKind.SelectionEntry,
            "selectionEntryGroup" => EntryLinkKind.SelectionEntryGroup,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported entry link kind."),
        };

    private static InfoLinkKind MapInfoLinkKind(string value) =>
        value switch
        {
            "profile" => InfoLinkKind.Profile,
            "rule" => InfoLinkKind.Rule,
            "infoGroup" => InfoLinkKind.InfoGroup,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported info link kind."),
        };

    private static string SerializeNode(SourceNode node)
    {
        using var sw = new StringWriter();
        node.Serialize(sw);
        return sw.ToString();
    }
}