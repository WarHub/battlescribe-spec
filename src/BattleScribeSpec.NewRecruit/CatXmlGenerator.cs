using BattleScribeSpec;
using WarHub.ArmouryModel.Source;
using WarHub.ArmouryModel.Source.BattleScribe;
using static WarHub.ArmouryModel.Source.NodeFactory;

namespace BattleScribeSpec.NewRecruit;

public static class CatXmlGenerator
{
    public static string GenerateGameSystemXml(GameSystemSpec gameSystem) =>
        SerializeNode(MapGameSystem(gameSystem));

    public static string GenerateCatalogueXml(GameSystemSpec gameSystem, CatalogueSpec catalogue)
    {
        var gamesystem = MapGameSystem(gameSystem);
        return SerializeNode(MapCatalogue(gamesystem, catalogue));
    }

    public static string GenerateCatalogueXml(GameSystemSpec gameSystem, CatalogueSpec[] catalogues)
    {
        if (catalogues.Length == 0)
        {
            throw new ArgumentException("At least one catalogue is required.", nameof(catalogues));
        }

        return GenerateCatalogueXml(gameSystem, catalogues[0]);
    }

    private static GamesystemNode MapGameSystem(GameSystemSpec gameSystem)
    {
        var node = Gamesystem(name: gameSystem.Name, id: gameSystem.Id);

        foreach (var costType in gameSystem.CostTypes ?? [])
        {
            node = node.AddCostTypes(MapCostType(costType));
        }

        foreach (var forceEntry in gameSystem.ForceEntries ?? [])
        {
            node = node.AddForceEntries(MapForceEntry(forceEntry));
        }

        foreach (var categoryEntry in gameSystem.CategoryEntries ?? [])
        {
            node = node.AddCategoryEntries(MapCategoryEntry(categoryEntry));
        }

        foreach (var profileType in gameSystem.ProfileTypes ?? [])
        {
            node = node.AddProfileTypes(MapProfileType(profileType));
        }

        return node;
    }

    private static CatalogueNode MapCatalogue(GamesystemNode gamesystem, CatalogueSpec catalogue)
    {
        var node = Catalogue(gamesystem: gamesystem, name: catalogue.Name, id: catalogue.Id);

        foreach (var selectionEntry in catalogue.SelectionEntries ?? [])
        {
            node = node.AddSelectionEntries(MapSelectionEntry(selectionEntry));
        }

        foreach (var selectionEntryGroup in catalogue.SelectionEntryGroups ?? [])
        {
            node = node.AddSharedSelectionEntryGroups(MapSelectionEntryGroup(selectionEntryGroup));
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
        {
            node = node.AddSharedInfoGroups(MapInfoGroup(sharedInfoGroup));
        }

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

        return node;
    }

    private static CostTypeNode MapCostType(CostTypeSpec spec) =>
        CostType(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            defaultCostLimit: (decimal)spec.DefaultCostLimit,
            hidden: spec.Hidden);

    private static ForceEntryNode MapForceEntry(ForceEntrySpec spec)
    {
        var node = ForceEntry(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: null,
            page: null,
            hidden: false);

        foreach (var categoryLink in spec.CategoryLinks ?? [])
        {
            node = node.AddCategoryLinks(MapCategoryLink(categoryLink));
        }

        foreach (var forceEntry in spec.ForceEntries ?? [])
        {
            node = node.AddForceEntries(MapForceEntry(forceEntry));
        }

        return node;
    }

    private static CategoryEntryNode MapCategoryEntry(CategoryEntrySpec spec) =>
        CategoryEntry(name: spec.Name, id: spec.Id);

    private static ProfileTypeNode MapProfileType(ProfileTypeSpec spec)
    {
        var node = ProfileType(comment: null, id: spec.Id, name: spec.Name);
        foreach (var characteristicType in spec.CharacteristicTypes ?? [])
        {
            node = node.AddCharacteristicTypes(MapCharacteristicType(characteristicType));
        }

        return node;
    }

    private static CharacteristicTypeNode MapCharacteristicType(CharacteristicTypeSpec spec) =>
        CharacteristicType(comment: null, id: spec.Id, name: spec.Name);

    private static SelectionEntryNode MapSelectionEntry(SelectionEntrySpec spec)
    {
        var page = string.IsNullOrWhiteSpace(spec.Page) ? null : spec.Page;
        var node = SelectionEntry(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: null,
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

        foreach (var childEntry in spec.ChildEntries ?? [])
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

    private static SelectionEntryGroupNode MapSelectionEntryGroup(SelectionEntryGroupSpec spec)
    {
        var defaultSelectionEntryId = string.IsNullOrWhiteSpace(spec.DefaultSelectionEntryId)
            ? null
            : spec.DefaultSelectionEntryId;
        var node = SelectionEntryGroup(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: null,
            page: null,
            hidden: spec.Hidden,
            collective: false,
            exported: spec.Import,
            defaultSelectionEntryId: defaultSelectionEntryId);

        foreach (var constraint in spec.Constraints ?? [])
        {
            node = node.AddConstraints(MapConstraint(constraint));
        }

        foreach (var modifier in spec.Modifiers ?? [])
        {
            node = node.AddModifiers(MapModifier(modifier));
        }

        foreach (var selectionEntry in spec.SelectionEntries ?? [])
        {
            node = node.AddSelectionEntries(MapSelectionEntry(selectionEntry));
        }

        return node;
    }

    private static EntryLinkNode MapEntryLink(EntryLinkSpec spec)
    {
        var node = EntryLink(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: null,
            page: null,
            hidden: spec.Hidden,
            collective: false,
            exported: spec.Import,
            targetId: spec.TargetId,
            type: MapEntryLinkKind(spec.Type));

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

        foreach (var categoryLink in spec.CategoryLinks ?? [])
        {
            node = node.AddCategoryLinks(MapCategoryLink(categoryLink));
        }

        return node;
    }

    private static ConstraintNode MapConstraint(ConstraintSpec spec) =>
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

    private static ModifierNode MapModifier(ModifierSpec spec)
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

    private static ModifierGroupNode MapModifierGroup(ModifierGroupSpec spec)
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

    private static ConditionNode MapCondition(ConditionSpec spec)
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

    private static ConditionGroupNode MapConditionGroup(ConditionGroupSpec spec)
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

    private static RepeatNode MapRepeat(RepeatSpec spec)
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

    private static CategoryLinkNode MapCategoryLink(CategoryLinkSpec spec) =>
        CategoryLink(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: null,
            page: null,
            hidden: false,
            targetId: spec.TargetId,
            primary: spec.Primary);

    private static RuleNode MapRule(RuleSpec spec)
    {
        var page = string.IsNullOrWhiteSpace(spec.Page) ? null : spec.Page;
        var node = Rule(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: null,
            page: page,
            hidden: spec.Hidden,
            description: spec.Description);

        foreach (var modifier in spec.Modifiers ?? [])
        {
            node = node.AddModifiers(MapModifier(modifier));
        }

        return node;
    }

    private static ProfileNode MapProfile(ProfileSpec spec)
    {
        var node = Profile(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: null,
            page: null,
            hidden: spec.Hidden,
            typeId: spec.TypeId,
            typeName: spec.TypeName);

        foreach (var characteristic in spec.Characteristics ?? [])
        {
            node = node.AddCharacteristics(MapCharacteristic(characteristic));
        }

        foreach (var modifier in spec.Modifiers ?? [])
        {
            node = node.AddModifiers(MapModifier(modifier));
        }

        return node;
    }

    private static CharacteristicNode MapCharacteristic(CharacteristicSpec spec) =>
        Characteristic(name: spec.Name, typeId: spec.TypeId, value: spec.Value);

    private static InfoGroupNode MapInfoGroup(InfoGroupSpec spec)
    {
        var node = InfoGroup(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: null,
            page: null,
            hidden: spec.Hidden);

        foreach (var profile in spec.Profiles ?? [])
        {
            node = node.AddProfiles(MapProfile(profile));
        }

        foreach (var rule in spec.Rules ?? [])
        {
            node = node.AddRules(MapRule(rule));
        }

        foreach (var modifier in spec.Modifiers ?? [])
        {
            node = node.AddModifiers(MapModifier(modifier));
        }

        foreach (var infoLink in spec.InfoLinks ?? [])
        {
            node = node.AddInfoLinks(MapInfoLink(infoLink));
        }

        return node;
    }

    private static CatalogueLinkNode MapCatalogueLink(CatalogueLinkSpec spec) =>
        CatalogueLink(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            targetId: spec.TargetId,
            type: CatalogueLinkKind.Catalogue,
            importRootEntries: spec.ImportRootEntries);

    private static PublicationNode MapPublication(PublicationSpec spec)
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

    private static InfoLinkNode MapInfoLink(InfoLinkSpec spec)
    {
        var node = InfoLink(
            comment: null,
            id: spec.Id,
            name: spec.Name,
            publicationId: null,
            page: null,
            hidden: spec.Hidden,
            targetId: spec.TargetId,
            type: MapInfoLinkKind(spec.Type));

        foreach (var modifier in spec.Modifiers ?? [])
        {
            node = node.AddModifiers(MapModifier(modifier));
        }

        return node;
    }

    private static CostNode MapCost(CostSpec spec) =>
        Cost(name: spec.Name, typeId: spec.TypeId, value: (decimal)spec.Value);

    private static ConstraintKind MapConstraintKind(string value) =>
        value switch
        {
            "min" => ConstraintKind.Minimum,
            "max" => ConstraintKind.Maximum,
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
