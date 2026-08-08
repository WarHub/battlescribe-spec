using System.Reflection;
using System.Xml.Serialization;
using BattleScribeSpec.Protocol;
using WarHub.ArmouryModel.Source;
using WarHub.ArmouryModel.Source.BattleScribe;
using static WarHub.ArmouryModel.Source.NodeFactory;

namespace BattleScribeSpec.XmlGen;

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
        {
            throw new ArgumentException("At least one catalogue is required.", nameof(catalogues));
        }

        var gamesystem = MapGameSystem(gameSystem);
        var result = new List<(string, string)>();
        for (var i = 0; i < catalogues.Length; i++)
        {
            var xml = SerializeNode(MapCatalogue(gamesystem, catalogues[i]));
            result.Add(($"{catalogues[i].Id}.cat", xml));
        }
        return result;
    }

    private static GamesystemNode MapGameSystem(ProtocolGameSystem gameSystem)
    {
        var node = Gamesystem(name: gameSystem.Name, id: gameSystem.Id);

        if (gameSystem.CostTypes is { } costTypes)
        {
            node = node.AddCostTypes(costTypes.Select(MapCostType));
        }

        if (gameSystem.ForceEntries is { } forceEntries)
        {
            node = node.AddForceEntries(forceEntries.Select(MapForceEntry));
        }

        if (gameSystem.CategoryEntries is { } categoryEntries)
        {
            node = node.AddCategoryEntries(categoryEntries.Select(MapCategoryEntry));
        }

        if (gameSystem.ProfileTypes is { } profileTypes)
        {
            node = node.AddProfileTypes(profileTypes.Select(MapProfileType));
        }

        if (gameSystem.Publications is { } publications)
        {
            node = node.AddPublications(publications.Select(MapPublication));
        }

        if (gameSystem.SelectionEntries is { } selectionEntries)
        {
            node = node.AddSelectionEntries(selectionEntries.Select(MapSelectionEntry));
        }

        if (gameSystem.EntryLinks is { } entryLinks)
        {
            node = node.AddEntryLinks(entryLinks.Select(MapEntryLink));
        }

        if (gameSystem.Rules is { } rules)
        {
            node = node.AddRules(rules.Select(MapRule));
        }

        if (gameSystem.InfoLinks is { } infoLinks)
        {
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));
        }

        if (gameSystem.SharedSelectionEntries is { } sharedSelectionEntries)
        {
            node = node.AddSharedSelectionEntries(sharedSelectionEntries.Select(MapSelectionEntry));
        }

        if (gameSystem.SharedSelectionEntryGroups is { } sharedSelectionEntryGroups)
        {
            node = node.AddSharedSelectionEntryGroups(sharedSelectionEntryGroups.Select(MapSelectionEntryGroup));
        }

        if (gameSystem.SharedRules is { } sharedRules)
        {
            node = node.AddSharedRules(sharedRules.Select(MapRule));
        }

        if (gameSystem.SharedProfiles is { } sharedProfiles)
        {
            node = node.AddSharedProfiles(sharedProfiles.Select(MapProfile));
        }

        if (gameSystem.SharedInfoGroups is { } sharedInfoGroups)
        {
            node = node.AddSharedInfoGroups(sharedInfoGroups.Select(MapInfoGroup));
        }

        return node;
    }

    private static CatalogueNode MapCatalogue(GamesystemNode gamesystem, ProtocolCatalogue catalogue)
    {
        var node = Catalogue(gamesystem: gamesystem, name: catalogue.Name, id: catalogue.Id);

        if (catalogue.Library)
        {
            node = node.WithIsLibrary(true);
        }

        if (catalogue.SelectionEntries is { } selectionEntries)
        {
            node = node.AddSelectionEntries(selectionEntries.Select(MapSelectionEntry));
        }

        if (catalogue.EntryLinks is { } entryLinks)
        {
            node = node.AddEntryLinks(entryLinks.Select(MapEntryLink));
        }

        if (catalogue.SharedSelectionEntries is { } sharedSelectionEntries)
        {
            node = node.AddSharedSelectionEntries(sharedSelectionEntries.Select(MapSelectionEntry));
        }

        if (catalogue.SharedSelectionEntryGroups is { } sharedSelectionEntryGroups)
        {
            node = node.AddSharedSelectionEntryGroups(sharedSelectionEntryGroups.Select(MapSelectionEntryGroup));
        }

        if (catalogue.SharedRules is { } sharedRules)
        {
            node = node.AddSharedRules(sharedRules.Select(MapRule));
        }

        if (catalogue.SharedProfiles is { } sharedProfiles)
        {
            node = node.AddSharedProfiles(sharedProfiles.Select(MapProfile));
        }

        if (catalogue.SharedInfoGroups is { } sharedInfoGroups)
        {
            node = node.AddSharedInfoGroups(sharedInfoGroups.Select(MapInfoGroup));
        }

        if (catalogue.SharedForceEntries is { } sharedForceEntries)
        {
            node = node.AddSharedForceEntries(sharedForceEntries.Select(MapForceEntry));
        }

        if (catalogue.SharedAssociations is { } sharedAssociations)
        {
            node = node.AddSharedAssociations(sharedAssociations.Select(MapAssociation));
        }

        if (catalogue.Rules is { } rules)
        {
            node = node.AddRules(rules.Select(MapRule));
        }

        if (catalogue.InfoLinks is { } infoLinks)
        {
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));
        }

        if (catalogue.CatalogueLinks is { } catalogueLinks)
        {
            node = node.AddCatalogueLinks(catalogueLinks.Select(MapCatalogueLink));
        }

        if (catalogue.Publications is { } publications)
        {
            node = node.AddPublications(publications.Select(MapPublication));
        }

        if (catalogue.CostTypes is { } costTypes)
        {
            node = node.AddCostTypes(costTypes.Select(MapCostType));
        }

        if (catalogue.ProfileTypes is { } profileTypes)
        {
            node = node.AddProfileTypes(profileTypes.Select(MapProfileType));
        }

        if (catalogue.CategoryEntries is { } categoryEntries)
        {
            node = node.AddCategoryEntries(categoryEntries.Select(MapCategoryEntry));
        }

        if (catalogue.ForceEntries is { } forceEntries)
        {
            node = node.AddForceEntries(forceEntries.Select(MapForceEntry));
        }

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
        {
            node = node.AddConstraints(constraints.Select(MapConstraint));
        }

        if (spec.Modifiers is { } modifiers)
        {
            node = node.AddModifiers(modifiers.Select(MapModifier));
        }

        if (spec.ModifierGroups is { } modifierGroups)
        {
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));
        }

        if (spec.CategoryLinks is { } categoryLinks)
        {
            node = node.AddCategoryLinks(categoryLinks.Select((cl, i) => MapCategoryLink(cl, spec.Id, i)));
        }

        if (spec.ForceEntries is { } forceEntries)
        {
            node = node.AddForceEntries(forceEntries.Select(MapForceEntry));
        }

        if (spec.Rules is { } rules)
        {
            node = node.AddRules(rules.Select(MapRule));
        }

        if (spec.Profiles is { } profiles)
        {
            node = node.AddProfiles(profiles.Select(MapProfile));
        }

        if (spec.InfoGroups is { } infoGroups)
        {
            node = node.AddInfoGroups(infoGroups.Select(MapInfoGroup));
        }

        if (spec.InfoLinks is { } infoLinks)
        {
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));
        }

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
        {
            node = node.AddConstraints(constraints.Select(MapConstraint));
        }

        if (spec.Modifiers is { } modifiers)
        {
            node = node.AddModifiers(modifiers.Select(MapModifier));
        }

        if (spec.ModifierGroups is { } modifierGroups)
        {
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));
        }

        if (spec.Rules is { } rules)
        {
            node = node.AddRules(rules.Select(MapRule));
        }

        if (spec.Profiles is { } profiles)
        {
            node = node.AddProfiles(profiles.Select(MapProfile));
        }

        if (spec.InfoGroups is { } infoGroups)
        {
            node = node.AddInfoGroups(infoGroups.Select(MapInfoGroup));
        }

        if (spec.InfoLinks is { } infoLinks)
        {
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));
        }

        return node;
    }

    private static ProfileTypeNode MapProfileType(ProtocolProfileType spec)
    {
        var node = ProfileType(comment: null, id: spec.Id, name: spec.Name, kindValue: spec.Kind);
        if (spec.CharacteristicTypes is { } characteristicTypes)
        {
            node = node.AddCharacteristicTypes(characteristicTypes.Select(MapCharacteristicType));
        }
        if (spec.AttributeTypes is { } attributeTypes)
        {
            node = node.AddAttributeTypes(attributeTypes.Select(MapAttributeType));
        }

        return node;
    }

    private static CharacteristicTypeNode MapCharacteristicType(ProtocolCharacteristicType spec) =>
        CharacteristicType(comment: null, id: spec.Id, name: spec.Name,
            kindValue: spec.Kind, defaultValue: spec.DefaultValue);

    // NewRecruit addition.
    private static AttributeTypeNode MapAttributeType(ProtocolAttributeType spec) =>
        AttributeType(comment: null, id: spec.Id, name: spec.Name);

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
        {
            node = node.AddCosts(costs.Select(MapCost));
        }

        if (spec.Constraints is { } constraints)
        {
            node = node.AddConstraints(constraints.Select(MapConstraint));
        }

        if (spec.Modifiers is { } modifiers)
        {
            node = node.AddModifiers(modifiers.Select(MapModifier));
        }

        if (spec.ModifierGroups is { } modifierGroups)
        {
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));
        }

        if (spec.SelectionEntries is { } selectionEntries)
        {
            node = node.AddSelectionEntries(selectionEntries.Select(MapSelectionEntry));
        }

        if (spec.SelectionEntryGroups is { } selectionEntryGroups)
        {
            node = node.AddSelectionEntryGroups(selectionEntryGroups.Select(MapSelectionEntryGroup));
        }

        if (spec.CategoryLinks is { } categoryLinks)
        {
            node = node.AddCategoryLinks(categoryLinks.Select((cl, i) => MapCategoryLink(cl, spec.Id, i)));
        }

        if (spec.Rules is { } rules)
        {
            node = node.AddRules(rules.Select(MapRule));
        }

        if (spec.Profiles is { } profiles)
        {
            node = node.AddProfiles(profiles.Select(MapProfile));
        }

        if (spec.InfoGroups is { } infoGroups)
        {
            node = node.AddInfoGroups(infoGroups.Select(MapInfoGroup));
        }

        if (spec.EntryLinks is { } entryLinks)
        {
            node = node.AddEntryLinks(entryLinks.Select(MapEntryLink));
        }

        if (spec.InfoLinks is { } infoLinks)
        {
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));
        }

        if (spec.Associations is { } associations)
        {
            node = node.AddAssociations(associations.Select(MapAssociation));
        }

        return node;
    }

    // NewRecruit addition: associations relate a selection to a query-resolved set of selections.
    private static AssociationNode MapAssociation(ProtocolAssociation spec) =>
        Association(
            comment: null,
            field: spec.Field,
            scope: spec.Scope,
            value: 0m,
            isValuePercentage: false,
            shared: false,
            includeChildSelections: false,
            includeChildForces: false,
            id: spec.Id,
            name: spec.Name,
            min: spec.Min,
            max: spec.Max,
            childId: spec.ChildId);

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
        {
            node = node.AddConstraints(constraints.Select(MapConstraint));
        }

        if (spec.Modifiers is { } modifiers)
        {
            node = node.AddModifiers(modifiers.Select(MapModifier));
        }

        if (spec.ModifierGroups is { } modifierGroups)
        {
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));
        }

        if (spec.SelectionEntries is { } selectionEntries)
        {
            node = node.AddSelectionEntries(selectionEntries.Select(MapSelectionEntry));
        }

        if (spec.SelectionEntryGroups is { } selectionEntryGroups)
        {
            node = node.AddSelectionEntryGroups(selectionEntryGroups.Select(MapSelectionEntryGroup));
        }

        if (spec.EntryLinks is { } entryLinks)
        {
            node = node.AddEntryLinks(entryLinks.Select(MapEntryLink));
        }

        if (spec.CategoryLinks is { } categoryLinks)
        {
            node = node.AddCategoryLinks(categoryLinks.Select((cl, i) => MapCategoryLink(cl, spec.Id, i)));
        }

        if (spec.Profiles is { } profiles)
        {
            node = node.AddProfiles(profiles.Select(MapProfile));
        }

        if (spec.Rules is { } rules)
        {
            node = node.AddRules(rules.Select(MapRule));
        }

        if (spec.InfoGroups is { } infoGroups)
        {
            node = node.AddInfoGroups(infoGroups.Select(MapInfoGroup));
        }

        if (spec.InfoLinks is { } infoLinks)
        {
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));
        }

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
        {
            node = node.AddCosts(costs.Select(MapCost));
        }

        if (spec.Constraints is { } constraints)
        {
            node = node.AddConstraints(constraints.Select(MapConstraint));
        }

        if (spec.Modifiers is { } modifiers)
        {
            node = node.AddModifiers(modifiers.Select(MapModifier));
        }

        if (spec.ModifierGroups is { } modifierGroups)
        {
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));
        }

        if (spec.CategoryLinks is { } categoryLinks)
        {
            node = node.AddCategoryLinks(categoryLinks.Select((cl, i) => MapCategoryLink(cl, spec.Id, i)));
        }

        if (spec.SelectionEntries is { } selectionEntries)
        {
            node = node.AddSelectionEntries(selectionEntries.Select(MapSelectionEntry));
        }

        if (spec.SelectionEntryGroups is { } selectionEntryGroups)
        {
            node = node.AddSelectionEntryGroups(selectionEntryGroups.Select(MapSelectionEntryGroup));
        }

        if (spec.EntryLinks is { } entryLinks)
        {
            node = node.AddEntryLinks(entryLinks.Select(MapEntryLink));
        }

        if (spec.Profiles is { } profiles)
        {
            node = node.AddProfiles(profiles.Select(MapProfile));
        }

        if (spec.Rules is { } rules)
        {
            node = node.AddRules(rules.Select(MapRule));
        }

        if (spec.InfoGroups is { } infoGroups)
        {
            node = node.AddInfoGroups(infoGroups.Select(MapInfoGroup));
        }

        if (spec.InfoLinks is { } infoLinks)
        {
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));
        }

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
            type: MapConstraintKind(spec.Type),
            negative: spec.Negative,
            automatic: spec.Automatic,
            message: string.IsNullOrWhiteSpace(spec.Message) ? null : spec.Message);

    private static ModifierNode MapModifier(ProtocolModifier spec)
    {
        var node = Modifier(comment: null, type: MapModifierKind(spec.Type), field: spec.Field, value: spec.Value);

        if (spec.Conditions is { } conditions)
        {
            node = node.AddConditions(conditions.Select(MapCondition));
        }

        if (spec.ConditionGroups is { } conditionGroups)
        {
            node = node.AddConditionGroups(conditionGroups.Select(MapConditionGroup));
        }

        if (spec.Repeats is { } repeats)
        {
            node = node.AddRepeats(repeats.Select(MapRepeat));
        }

        if (spec.LocalConditionGroups is { } localConditionGroups)
        {
            node = node.AddLocalConditionGroups(localConditionGroups.Select(MapLocalConditionGroup));
        }

        return node;
    }

    // NewRecruit addition.
    private static LocalConditionGroupNode MapLocalConditionGroup(ProtocolLocalConditionGroup spec) =>
        LocalConditionGroup(
            comment: null,
            field: spec.Field,
            scope: spec.Scope,
            value: spec.Value,
            isValuePercentage: false,
            shared: false,
            includeChildSelections: spec.IncludeChildSelections,
            includeChildForces: spec.IncludeChildForces,
            childId: string.IsNullOrWhiteSpace(spec.ChildId) ? null : spec.ChildId,
            type: MapConditionKind(spec.Type),
            repeatCount: spec.Repeats);

    private static ModifierGroupNode MapModifierGroup(ProtocolModifierGroup spec)
    {
        var node = ModifierGroup();

        if (spec.Conditions is { } conditions)
        {
            node = node.AddConditions(conditions.Select(MapCondition));
        }

        if (spec.ConditionGroups is { } conditionGroups)
        {
            node = node.AddConditionGroups(conditionGroups.Select(MapConditionGroup));
        }

        if (spec.Repeats is { } repeats)
        {
            node = node.AddRepeats(repeats.Select(MapRepeat));
        }

        if (spec.Modifiers is { } modifiers)
        {
            node = node.AddModifiers(modifiers.Select(MapModifier));
        }

        if (spec.ModifierGroups is { } modifierGroups)
        {
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));
        }

        return node;
    }

    private static ConditionNode MapCondition(ProtocolCondition spec)
    {
        // Empty, never null: `childId` is `use="required"` on QueryFilteredBase in Catalogue.xsd,
        // so omitting it produces a file BattleScribe rejects outright — "File was corrupted and
        // has been deleted" — taking the whole catalogue with it. Empty is how the format spells
        // "no child filter"; absent is not expressible.
        var childId = string.IsNullOrWhiteSpace(spec.ChildId) ? string.Empty : spec.ChildId;
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
        {
            node = node.AddConditions(conditions.Select(MapCondition));
        }

        if (spec.ConditionGroups is { } conditionGroups)
        {
            node = node.AddConditionGroups(conditionGroups.Select(MapConditionGroup));
        }

        return node;
    }

    private static RepeatNode MapRepeat(ProtocolRepeat spec)
    {
        // Empty, never null: `childId` is `use="required"` on QueryFilteredBase in Catalogue.xsd,
        // so omitting it produces a file BattleScribe rejects outright — "File was corrupted and
        // has been deleted" — taking the whole catalogue with it. Empty is how the format spells
        // "no child filter"; absent is not expressible.
        var childId = string.IsNullOrWhiteSpace(spec.ChildId) ? string.Empty : spec.ChildId;
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

    /// <summary>
    /// An id for a category link, synthesised from its owner and position when the spec omits one.
    /// </summary>
    /// <remarks>
    /// BattleScribe's own data validator rejects a <c>categoryLink</c> with no id — "CategoryLink
    /// must have an ID" — and refuses the whole catalogue with it. That is its RUNTIME rule and is
    /// stricter than <c>Catalogue.xsd</c>, which requires only <c>targetId</c>, so a schema-valid
    /// file is not necessarily one it will load.
    /// <para>
    /// Owner id plus index, rather than the target id: two entries linking the same category would
    /// otherwise collide, and ids must be unique across the file. Deterministic, so regenerating
    /// the same spec produces the same file.
    /// </para>
    /// </remarks>
    private static string CategoryLinkId(ProtocolCategoryLink spec, string? ownerId, int index)
        => string.IsNullOrWhiteSpace(spec.Id)
            ? $"{ownerId}-catlink-{index}"
            : spec.Id;

    private static CategoryLinkNode MapCategoryLink(ProtocolCategoryLink spec, string? ownerId, int index)
    {
        var page = string.IsNullOrWhiteSpace(spec.Page) ? null : spec.Page;
        var pubId = string.IsNullOrWhiteSpace(spec.PublicationId) ? null : spec.PublicationId;
        var node = CategoryLink(
            comment: null,
            id: CategoryLinkId(spec, ownerId, index),
            name: spec.Name,
            publicationId: pubId,
            page: page,
            hidden: spec.Hidden,
            targetId: spec.TargetId,
            primary: spec.Primary);

        if (spec.Constraints is { } constraints)
        {
            node = node.AddConstraints(constraints.Select(MapConstraint));
        }

        if (spec.Modifiers is { } modifiers)
        {
            node = node.AddModifiers(modifiers.Select(MapModifier));
        }

        if (spec.ModifierGroups is { } modifierGroups)
        {
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));
        }

        if (spec.Rules is { } rules)
        {
            node = node.AddRules(rules.Select(MapRule));
        }

        if (spec.Profiles is { } profiles)
        {
            node = node.AddProfiles(profiles.Select(MapProfile));
        }

        if (spec.InfoGroups is { } infoGroups)
        {
            node = node.AddInfoGroups(infoGroups.Select(MapInfoGroup));
        }

        if (spec.InfoLinks is { } infoLinks)
        {
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));
        }

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
        {
            node = node.AddModifiers(modifiers.Select(MapModifier));
        }

        if (spec.ModifierGroups is { } modifierGroups)
        {
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));
        }

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
        {
            node = node.AddCharacteristics(characteristics.Select(MapCharacteristic));
        }

        if (spec.Modifiers is { } modifiers)
        {
            node = node.AddModifiers(modifiers.Select(MapModifier));
        }

        if (spec.ModifierGroups is { } modifierGroups)
        {
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));
        }

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
        {
            node = node.AddProfiles(profiles.Select(MapProfile));
        }

        if (spec.Rules is { } rules)
        {
            node = node.AddRules(rules.Select(MapRule));
        }

        if (spec.Modifiers is { } modifiers)
        {
            node = node.AddModifiers(modifiers.Select(MapModifier));
        }

        if (spec.ModifierGroups is { } modifierGroups)
        {
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));
        }

        if (spec.InfoLinks is { } infoLinks)
        {
            node = node.AddInfoLinks(infoLinks.Select(MapInfoLink));
        }

        if (spec.InfoGroups is { } infoGroups)
        {
            node = node.AddInfoGroups(infoGroups.Select(MapInfoGroup));
        }

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
        {
            node = node.AddModifiers(modifiers.Select(MapModifier));
        }

        if (spec.ModifierGroups is { } modifierGroups)
        {
            node = node.AddModifierGroups(modifierGroups.Select(MapModifierGroup));
        }

        return node;
    }

    private static CostNode MapCost(ProtocolCostValue spec) =>
        Cost(name: spec.Name, typeId: spec.TypeId, value: (decimal)spec.Value);

    private static ConstraintKind MapConstraintKind(string value) =>
        value switch
        {
            "min" or "atLeast" => ConstraintKind.Minimum,
            "max" or "atMost" => ConstraintKind.Maximum,
            "exactly" => ConstraintKind.Exactly, // NewRecruit addition.
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported constraint kind."),
        };

    private static ModifierKind MapModifierKind(string value) => MapXmlEnum<ModifierKind>(value);

    private static ConditionKind MapConditionKind(string value) => MapXmlEnum<ConditionKind>(value);

    private static ConditionGroupKind MapConditionGroupKind(string value) => MapXmlEnum<ConditionGroupKind>(value);

    private static SelectionEntryKind MapSelectionEntryKind(string value) => MapXmlEnum<SelectionEntryKind>(value);

    /// <summary>
    /// Maps a BattleScribe/NewRecruit XML attribute string to its wham enum member by matching the
    /// member's <see cref="XmlEnumAttribute"/> name. This auto-tracks the wham enums (incl. NR
    /// additions) without a hand-maintained per-value switch.
    /// </summary>
    private static TEnum MapXmlEnum<TEnum>(string value) where TEnum : struct, Enum
    {
        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var xmlName = field.GetCustomAttribute<XmlEnumAttribute>()?.Name;
            if (xmlName == value)
            {
                return (TEnum)field.GetValue(null)!;
            }
        }
        throw new ArgumentOutOfRangeException(
            nameof(value), value, $"Unsupported {typeof(TEnum).Name} value.");
    }

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
