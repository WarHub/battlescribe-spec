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

        foreach (var costType in gameSystem.CostTypes ?? [])
            node = node.AddCostTypes(MapCostType(costType));

        foreach (var forceEntry in gameSystem.ForceEntries ?? [])
            node = node.AddForceEntries(MapForceEntry(forceEntry));

        foreach (var categoryEntry in gameSystem.CategoryEntries ?? [])
            node = node.AddCategoryEntries(MapCategoryEntry(categoryEntry));

        foreach (var profileType in gameSystem.ProfileTypes ?? [])
            node = node.AddProfileTypes(MapProfileType(profileType));

        foreach (var pub in gameSystem.Publications ?? [])
            node = node.AddPublications(MapPublication(pub));

        foreach (var se in gameSystem.SelectionEntries ?? [])
            node = node.AddSelectionEntries(MapSelectionEntry(se));

        foreach (var el in gameSystem.EntryLinks ?? [])
            node = node.AddEntryLinks(MapEntryLink(el));

        foreach (var rule in gameSystem.Rules ?? [])
            node = node.AddRules(MapRule(rule));

        foreach (var il in gameSystem.InfoLinks ?? [])
            node = node.AddInfoLinks(MapInfoLink(il));

        foreach (var se in gameSystem.SharedSelectionEntries ?? [])
            node = node.AddSharedSelectionEntries(MapSelectionEntry(se));

        foreach (var seg in gameSystem.SharedSelectionEntryGroups ?? [])
            node = node.AddSharedSelectionEntryGroups(MapSelectionEntryGroup(seg));

        foreach (var rule in gameSystem.SharedRules ?? [])
            node = node.AddSharedRules(MapRule(rule));

        foreach (var profile in gameSystem.SharedProfiles ?? [])
            node = node.AddSharedProfiles(MapProfile(profile));

        foreach (var ig in gameSystem.SharedInfoGroups ?? [])
            node = node.AddSharedInfoGroups(MapInfoGroup(ig));

        return node;
    }

    private static CatalogueNode MapCatalogue(GamesystemNode gamesystem, ProtocolCatalogue catalogue)
    {
        var node = Catalogue(gamesystem: gamesystem, name: catalogue.Name, id: catalogue.Id);

        foreach (var selectionEntry in catalogue.SelectionEntries ?? [])
        {
            node = node.AddSelectionEntries(MapSelectionEntry(selectionEntry));
        }

        foreach (var entryLink in catalogue.EntryLinks ?? [])
        {
            node = node.AddEntryLinks(MapEntryLink(entryLink));
        }

        foreach (var sharedSelectionEntry in catalogue.SharedSelectionEntries ?? [])
        {
            node = node.AddSharedSelectionEntries(MapSelectionEntry(sharedSelectionEntry));
        }

        foreach (var sharedSelectionEntryGroup in catalogue.SharedSelectionEntryGroups ?? [])
        {
            node = node.AddSharedSelectionEntryGroups(MapSelectionEntryGroup(sharedSelectionEntryGroup));
        }

        foreach (var sharedRule in catalogue.SharedRules ?? [])
        {
            node = node.AddSharedRules(MapRule(sharedRule));
        }

        foreach (var sharedProfile in catalogue.SharedProfiles ?? [])
        {
            node = node.AddSharedProfiles(MapProfile(sharedProfile));
        }

        foreach (var sharedInfoGroup in catalogue.SharedInfoGroups ?? [])
            node = node.AddSharedInfoGroups(MapInfoGroup(sharedInfoGroup));

        foreach (var rule in catalogue.Rules ?? [])
            node = node.AddRules(MapRule(rule));

        foreach (var infoLink in catalogue.InfoLinks ?? [])
        {
            node = node.AddInfoLinks(MapInfoLink(infoLink));
        }

        foreach (var catalogueLink in catalogue.CatalogueLinks ?? [])
        {
            node = node.AddCatalogueLinks(MapCatalogueLink(catalogueLink));
        }

        foreach (var publication in catalogue.Publications ?? [])
        {
            node = node.AddPublications(MapPublication(publication));
        }

        foreach (var costType in catalogue.CostTypes ?? [])
            node = node.AddCostTypes(MapCostType(costType));

        foreach (var profileType in catalogue.ProfileTypes ?? [])
            node = node.AddProfileTypes(MapProfileType(profileType));

        foreach (var categoryEntry in catalogue.CategoryEntries ?? [])
            node = node.AddCategoryEntries(MapCategoryEntry(categoryEntry));

        foreach (var forceEntry in catalogue.ForceEntries ?? [])
            node = node.AddForceEntries(MapForceEntry(forceEntry));

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

        foreach (var constraint in spec.Constraints ?? [])
            node = node.AddConstraints(MapConstraint(constraint));

        foreach (var modifier in spec.Modifiers ?? [])
            node = node.AddModifiers(MapModifier(modifier));

        foreach (var modifierGroup in spec.ModifierGroups ?? [])
            node = node.AddModifierGroups(MapModifierGroup(modifierGroup));

        foreach (var categoryLink in spec.CategoryLinks ?? [])
            node = node.AddCategoryLinks(MapCategoryLink(categoryLink));

        foreach (var forceEntry in spec.ForceEntries ?? [])
            node = node.AddForceEntries(MapForceEntry(forceEntry));

        foreach (var rule in spec.Rules ?? [])
            node = node.AddRules(MapRule(rule));

        foreach (var profile in spec.Profiles ?? [])
            node = node.AddProfiles(MapProfile(profile));

        foreach (var infoGroup in spec.InfoGroups ?? [])
            node = node.AddInfoGroups(MapInfoGroup(infoGroup));

        foreach (var infoLink in spec.InfoLinks ?? [])
            node = node.AddInfoLinks(MapInfoLink(infoLink));

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

        foreach (var constraint in spec.Constraints ?? [])
            node = node.AddConstraints(MapConstraint(constraint));

        foreach (var modifier in spec.Modifiers ?? [])
            node = node.AddModifiers(MapModifier(modifier));

        foreach (var modifierGroup in spec.ModifierGroups ?? [])
            node = node.AddModifierGroups(MapModifierGroup(modifierGroup));

        foreach (var rule in spec.Rules ?? [])
            node = node.AddRules(MapRule(rule));

        foreach (var profile in spec.Profiles ?? [])
            node = node.AddProfiles(MapProfile(profile));

        foreach (var infoGroup in spec.InfoGroups ?? [])
            node = node.AddInfoGroups(MapInfoGroup(infoGroup));

        foreach (var infoLink in spec.InfoLinks ?? [])
            node = node.AddInfoLinks(MapInfoLink(infoLink));

        return node;
    }

    private static ProfileTypeNode MapProfileType(ProtocolProfileType spec)
    {
        var node = ProfileType(comment: null, id: spec.Id, name: spec.Name);
        foreach (var characteristicType in spec.CharacteristicTypes ?? [])
        {
            node = node.AddCharacteristicTypes(MapCharacteristicType(characteristicType));
        }

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

        foreach (var cost in spec.Costs ?? [])
        {
            node = node.AddCosts(MapCost(cost));
        }

        foreach (var constraint in spec.Constraints ?? [])
        {
            node = node.AddConstraints(MapConstraint(constraint));
        }

        foreach (var modifier in spec.Modifiers ?? [])
        {
            node = node.AddModifiers(MapModifier(modifier));
        }

        foreach (var modifierGroup in spec.ModifierGroups ?? [])
        {
            node = node.AddModifierGroups(MapModifierGroup(modifierGroup));
        }

        foreach (var childEntry in spec.SelectionEntries ?? [])
        {
            node = node.AddSelectionEntries(MapSelectionEntry(childEntry));
        }

        foreach (var group in spec.SelectionEntryGroups ?? [])
        {
            node = node.AddSelectionEntryGroups(MapSelectionEntryGroup(group));
        }

        foreach (var categoryLink in spec.CategoryLinks ?? [])
        {
            node = node.AddCategoryLinks(MapCategoryLink(categoryLink));
        }

        foreach (var rule in spec.Rules ?? [])
        {
            node = node.AddRules(MapRule(rule));
        }

        foreach (var profile in spec.Profiles ?? [])
        {
            node = node.AddProfiles(MapProfile(profile));
        }

        foreach (var infoGroup in spec.InfoGroups ?? [])
        {
            node = node.AddInfoGroups(MapInfoGroup(infoGroup));
        }

        foreach (var entryLink in spec.EntryLinks ?? [])
        {
            node = node.AddEntryLinks(MapEntryLink(entryLink));
        }

        foreach (var infoLink in spec.InfoLinks ?? [])
        {
            node = node.AddInfoLinks(MapInfoLink(infoLink));
        }

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

        foreach (var constraint in spec.Constraints ?? [])
            node = node.AddConstraints(MapConstraint(constraint));

        foreach (var modifier in spec.Modifiers ?? [])
            node = node.AddModifiers(MapModifier(modifier));

        foreach (var modifierGroup in spec.ModifierGroups ?? [])
            node = node.AddModifierGroups(MapModifierGroup(modifierGroup));

        foreach (var selectionEntry in spec.SelectionEntries ?? [])
            node = node.AddSelectionEntries(MapSelectionEntry(selectionEntry));

        foreach (var childGroup in spec.SelectionEntryGroups ?? [])
            node = node.AddSelectionEntryGroups(MapSelectionEntryGroup(childGroup));

        foreach (var entryLink in spec.EntryLinks ?? [])
            node = node.AddEntryLinks(MapEntryLink(entryLink));

        foreach (var categoryLink in spec.CategoryLinks ?? [])
            node = node.AddCategoryLinks(MapCategoryLink(categoryLink));

        foreach (var profile in spec.Profiles ?? [])
            node = node.AddProfiles(MapProfile(profile));

        foreach (var rule in spec.Rules ?? [])
            node = node.AddRules(MapRule(rule));

        foreach (var infoGroup in spec.InfoGroups ?? [])
            node = node.AddInfoGroups(MapInfoGroup(infoGroup));

        foreach (var infoLink in spec.InfoLinks ?? [])
            node = node.AddInfoLinks(MapInfoLink(infoLink));

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

        foreach (var cost in spec.Costs ?? [])
            node = node.AddCosts(MapCost(cost));

        foreach (var constraint in spec.Constraints ?? [])
            node = node.AddConstraints(MapConstraint(constraint));

        foreach (var modifier in spec.Modifiers ?? [])
            node = node.AddModifiers(MapModifier(modifier));

        foreach (var modifierGroup in spec.ModifierGroups ?? [])
            node = node.AddModifierGroups(MapModifierGroup(modifierGroup));

        foreach (var categoryLink in spec.CategoryLinks ?? [])
            node = node.AddCategoryLinks(MapCategoryLink(categoryLink));

        foreach (var se in spec.SelectionEntries ?? [])
            node = node.AddSelectionEntries(MapSelectionEntry(se));

        foreach (var seg in spec.SelectionEntryGroups ?? [])
            node = node.AddSelectionEntryGroups(MapSelectionEntryGroup(seg));

        foreach (var el in spec.EntryLinks ?? [])
            node = node.AddEntryLinks(MapEntryLink(el));

        foreach (var profile in spec.Profiles ?? [])
            node = node.AddProfiles(MapProfile(profile));

        foreach (var rule in spec.Rules ?? [])
            node = node.AddRules(MapRule(rule));

        foreach (var ig in spec.InfoGroups ?? [])
            node = node.AddInfoGroups(MapInfoGroup(ig));

        foreach (var il in spec.InfoLinks ?? [])
            node = node.AddInfoLinks(MapInfoLink(il));

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

        foreach (var condition in spec.Conditions ?? [])
        {
            node = node.AddConditions(MapCondition(condition));
        }

        foreach (var conditionGroup in spec.ConditionGroups ?? [])
        {
            node = node.AddConditionGroups(MapConditionGroup(conditionGroup));
        }

        foreach (var repeat in spec.Repeats ?? [])
        {
            node = node.AddRepeats(MapRepeat(repeat));
        }

        return node;
    }

    private static ModifierGroupNode MapModifierGroup(ProtocolModifierGroup spec)
    {
        var node = ModifierGroup();

        foreach (var condition in spec.Conditions ?? [])
        {
            node = node.AddConditions(MapCondition(condition));
        }

        foreach (var conditionGroup in spec.ConditionGroups ?? [])
        {
            node = node.AddConditionGroups(MapConditionGroup(conditionGroup));
        }

        foreach (var repeat in spec.Repeats ?? [])
        {
            node = node.AddRepeats(MapRepeat(repeat));
        }

        foreach (var modifier in spec.Modifiers ?? [])
        {
            node = node.AddModifiers(MapModifier(modifier));
        }

        foreach (var modifierGroup in spec.ModifierGroups ?? [])
        {
            node = node.AddModifierGroups(MapModifierGroup(modifierGroup));
        }

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

        foreach (var condition in spec.Conditions ?? [])
        {
            node = node.AddConditions(MapCondition(condition));
        }

        foreach (var conditionGroup in spec.ConditionGroups ?? [])
        {
            node = node.AddConditionGroups(MapConditionGroup(conditionGroup));
        }

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

        foreach (var constraint in spec.Constraints ?? [])
            node = node.AddConstraints(MapConstraint(constraint));

        foreach (var modifier in spec.Modifiers ?? [])
            node = node.AddModifiers(MapModifier(modifier));

        foreach (var modifierGroup in spec.ModifierGroups ?? [])
            node = node.AddModifierGroups(MapModifierGroup(modifierGroup));

        foreach (var rule in spec.Rules ?? [])
            node = node.AddRules(MapRule(rule));

        foreach (var profile in spec.Profiles ?? [])
            node = node.AddProfiles(MapProfile(profile));

        foreach (var infoGroup in spec.InfoGroups ?? [])
            node = node.AddInfoGroups(MapInfoGroup(infoGroup));

        foreach (var infoLink in spec.InfoLinks ?? [])
            node = node.AddInfoLinks(MapInfoLink(infoLink));

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

        foreach (var modifier in spec.Modifiers ?? [])
            node = node.AddModifiers(MapModifier(modifier));

        foreach (var modifierGroup in spec.ModifierGroups ?? [])
            node = node.AddModifierGroups(MapModifierGroup(modifierGroup));

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

        foreach (var characteristic in spec.Characteristics ?? [])
            node = node.AddCharacteristics(MapCharacteristic(characteristic));

        foreach (var modifier in spec.Modifiers ?? [])
            node = node.AddModifiers(MapModifier(modifier));

        foreach (var modifierGroup in spec.ModifierGroups ?? [])
            node = node.AddModifierGroups(MapModifierGroup(modifierGroup));

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

        foreach (var profile in spec.Profiles ?? [])
            node = node.AddProfiles(MapProfile(profile));

        foreach (var rule in spec.Rules ?? [])
            node = node.AddRules(MapRule(rule));

        foreach (var modifier in spec.Modifiers ?? [])
            node = node.AddModifiers(MapModifier(modifier));

        foreach (var modifierGroup in spec.ModifierGroups ?? [])
            node = node.AddModifierGroups(MapModifierGroup(modifierGroup));

        foreach (var infoLink in spec.InfoLinks ?? [])
            node = node.AddInfoLinks(MapInfoLink(infoLink));

        foreach (var infoGroup in spec.InfoGroups ?? [])
            node = node.AddInfoGroups(MapInfoGroup(infoGroup));

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

        foreach (var modifier in spec.Modifiers ?? [])
            node = node.AddModifiers(MapModifier(modifier));

        foreach (var modifierGroup in spec.ModifierGroups ?? [])
            node = node.AddModifierGroups(MapModifierGroup(modifierGroup));

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
