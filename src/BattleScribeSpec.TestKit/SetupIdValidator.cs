using BattleScribeSpec.Protocol;

namespace BattleScribeSpec;

/// <summary>
/// Validates that all IDs within a spec setup tree are unique.
/// Only checks <c>Id</c> properties (not reference fields like targetId, typeId, childId).
/// </summary>
public static class SetupIdValidator
{
    /// <summary>
    /// Validates that all IDs in the setup are unique. Throws if duplicates are found.
    /// </summary>
    /// <param name="setup">The setup to validate.</param>
    /// <param name="specId">The spec ID, used in error messages.</param>
    /// <exception cref="InvalidOperationException">Thrown when duplicate IDs are found.</exception>
    public static void Validate(SetupDef setup, string specId)
    {
        var idLocations = new Dictionary<string, List<string>>();
        CollectGameSystem(setup.GameSystem, "gameSystem", idLocations);
        if (setup.Catalogues is not null)
        {
            for (var i = 0; i < setup.Catalogues.Count; i++)
            {
                CollectCatalogue(setup.Catalogues[i], $"catalogues[{i}]", idLocations);
            }
        }
        var duplicates = idLocations
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => $"  '{kv.Key}' at: {string.Join(", ", kv.Value)}")
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"Spec '{specId}' has duplicate IDs in setup:\n{string.Join("\n", duplicates)}");
        }
    }

    private static void AddId(string id, string path, Dictionary<string, List<string>> idLocations)
    {
        if (string.IsNullOrEmpty(id))
            return;
        if (!idLocations.TryGetValue(id, out var locations))
        {
            locations = [];
            idLocations[id] = locations;
        }
        locations.Add(path);
    }

    private static void CollectGameSystem(ProtocolGameSystem gs, string path, Dictionary<string, List<string>> ids)
    {
        AddId(gs.Id, path, ids);
        CollectList(gs.CostTypes, path, ids);
        CollectList(gs.ProfileTypes, path, ids);
        CollectList(gs.ForceEntries, path, ids);
        CollectList(gs.CategoryEntries, path, ids);
        CollectList(gs.Publications, path, ids);
        CollectList(gs.SelectionEntries, path, ids);
        CollectList(gs.EntryLinks, path, ids);
        CollectList(gs.Rules, path, ids);
        CollectList(gs.InfoLinks, path, ids);
        CollectList(gs.SharedSelectionEntries, path, ids);
        CollectList(gs.SharedSelectionEntryGroups, path, ids);
        CollectList(gs.SharedRules, path, ids);
        CollectList(gs.SharedProfiles, path, ids);
        CollectList(gs.SharedInfoGroups, path, ids);
    }

    private static void CollectCatalogue(ProtocolCatalogue cat, string path, Dictionary<string, List<string>> ids)
    {
        AddId(cat.Id, path, ids);
        CollectList(cat.SelectionEntries, path, ids);
        CollectList(cat.SelectionEntryGroups, path, ids);
        CollectList(cat.EntryLinks, path, ids);
        CollectList(cat.SharedSelectionEntries, path, ids);
        CollectList(cat.SharedSelectionEntryGroups, path, ids);
        CollectList(cat.SharedRules, path, ids);
        CollectList(cat.SharedProfiles, path, ids);
        CollectList(cat.SharedInfoGroups, path, ids);
        CollectList(cat.Rules, path, ids);
        CollectList(cat.InfoLinks, path, ids);
        CollectList(cat.CatalogueLinks, path, ids);
        CollectList(cat.Publications, path, ids);
        CollectList(cat.CostTypes, path, ids);
        CollectList(cat.ProfileTypes, path, ids);
        CollectList(cat.CategoryEntries, path, ids);
        CollectList(cat.ForceEntries, path, ids);
    }

    // Per-type collection methods

    private static void CollectList(List<ProtocolCostType>? items, string parent, Dictionary<string, List<string>> ids)
    {
        if (items is null) return;
        for (var i = 0; i < items.Count; i++)
            AddId(items[i].Id, $"{parent}/costTypes[{i}]", ids);
    }

    private static void CollectList(List<ProtocolProfileType>? items, string parent, Dictionary<string, List<string>> ids)
    {
        if (items is null) return;
        for (var i = 0; i < items.Count; i++)
        {
            var path = $"{parent}/profileTypes[{i}]";
            AddId(items[i].Id, path, ids);
            CollectList(items[i].CharacteristicTypes, path, ids);
        }
    }

    private static void CollectList(List<ProtocolCharacteristicType>? items, string parent, Dictionary<string, List<string>> ids)
    {
        if (items is null) return;
        for (var i = 0; i < items.Count; i++)
            AddId(items[i].Id, $"{parent}/characteristicTypes[{i}]", ids);
    }

    private static void CollectList(List<ProtocolForceEntry>? items, string parent, Dictionary<string, List<string>> ids)
    {
        if (items is null) return;
        for (var i = 0; i < items.Count; i++)
        {
            var path = $"{parent}/forceEntries[{i}]";
            AddId(items[i].Id, path, ids);
            CollectList(items[i].Constraints, path, ids);
            CollectList(items[i].CategoryLinks, path, ids);
            CollectList(items[i].ForceEntries, path, ids);
            CollectList(items[i].Profiles, path, ids);
            CollectList(items[i].Rules, path, ids);
            CollectList(items[i].InfoGroups, path, ids);
            CollectList(items[i].InfoLinks, path, ids);
        }
    }

    private static void CollectList(List<ProtocolCategoryEntry>? items, string parent, Dictionary<string, List<string>> ids)
    {
        if (items is null) return;
        for (var i = 0; i < items.Count; i++)
        {
            var path = $"{parent}/categoryEntries[{i}]";
            AddId(items[i].Id, path, ids);
            CollectList(items[i].Constraints, path, ids);
            CollectList(items[i].Profiles, path, ids);
            CollectList(items[i].Rules, path, ids);
            CollectList(items[i].InfoGroups, path, ids);
            CollectList(items[i].InfoLinks, path, ids);
        }
    }

    private static void CollectList(List<ProtocolSelectionEntry>? items, string parent, Dictionary<string, List<string>> ids)
    {
        if (items is null) return;
        for (var i = 0; i < items.Count; i++)
        {
            var path = $"{parent}/selectionEntries[{i}]";
            AddId(items[i].Id, path, ids);
            CollectList(items[i].Constraints, path, ids);
            CollectList(items[i].SelectionEntries, path, ids);
            CollectList(items[i].SelectionEntryGroups, path, ids);
            CollectList(items[i].EntryLinks, path, ids);
            CollectList(items[i].CategoryLinks, path, ids);
            CollectList(items[i].Profiles, path, ids);
            CollectList(items[i].Rules, path, ids);
            CollectList(items[i].InfoGroups, path, ids);
            CollectList(items[i].InfoLinks, path, ids);
        }
    }

    private static void CollectList(List<ProtocolSelectionEntryGroup>? items, string parent, Dictionary<string, List<string>> ids)
    {
        if (items is null) return;
        for (var i = 0; i < items.Count; i++)
        {
            var path = $"{parent}/selectionEntryGroups[{i}]";
            AddId(items[i].Id, path, ids);
            CollectList(items[i].Constraints, path, ids);
            CollectList(items[i].SelectionEntries, path, ids);
            CollectList(items[i].SelectionEntryGroups, path, ids);
            CollectList(items[i].EntryLinks, path, ids);
            CollectList(items[i].CategoryLinks, path, ids);
            CollectList(items[i].Profiles, path, ids);
            CollectList(items[i].Rules, path, ids);
            CollectList(items[i].InfoGroups, path, ids);
            CollectList(items[i].InfoLinks, path, ids);
        }
    }

    private static void CollectList(List<ProtocolEntryLink>? items, string parent, Dictionary<string, List<string>> ids)
    {
        if (items is null) return;
        for (var i = 0; i < items.Count; i++)
        {
            var path = $"{parent}/entryLinks[{i}]";
            AddId(items[i].Id, path, ids);
            CollectList(items[i].Constraints, path, ids);
            CollectList(items[i].CategoryLinks, path, ids);
            CollectList(items[i].SelectionEntries, path, ids);
            CollectList(items[i].SelectionEntryGroups, path, ids);
            CollectList(items[i].EntryLinks, path, ids);
            CollectList(items[i].Profiles, path, ids);
            CollectList(items[i].Rules, path, ids);
            CollectList(items[i].InfoGroups, path, ids);
            CollectList(items[i].InfoLinks, path, ids);
        }
    }

    private static void CollectList(List<ProtocolCategoryLink>? items, string parent, Dictionary<string, List<string>> ids)
    {
        if (items is null) return;
        for (var i = 0; i < items.Count; i++)
        {
            var path = $"{parent}/categoryLinks[{i}]";
            AddId(items[i].Id, path, ids);
            CollectList(items[i].Constraints, path, ids);
            CollectList(items[i].Profiles, path, ids);
            CollectList(items[i].Rules, path, ids);
            CollectList(items[i].InfoGroups, path, ids);
            CollectList(items[i].InfoLinks, path, ids);
        }
    }

    private static void CollectList(List<ProtocolConstraint>? items, string parent, Dictionary<string, List<string>> ids)
    {
        if (items is null) return;
        for (var i = 0; i < items.Count; i++)
            AddId(items[i].Id, $"{parent}/constraints[{i}]", ids);
    }

    private static void CollectList(List<ProtocolRule>? items, string parent, Dictionary<string, List<string>> ids)
    {
        if (items is null) return;
        for (var i = 0; i < items.Count; i++)
            AddId(items[i].Id, $"{parent}/rules[{i}]", ids);
    }

    private static void CollectList(List<ProtocolProfile>? items, string parent, Dictionary<string, List<string>> ids)
    {
        if (items is null) return;
        for (var i = 0; i < items.Count; i++)
            AddId(items[i].Id, $"{parent}/profiles[{i}]", ids);
    }

    private static void CollectList(List<ProtocolInfoGroup>? items, string parent, Dictionary<string, List<string>> ids)
    {
        if (items is null) return;
        for (var i = 0; i < items.Count; i++)
        {
            var path = $"{parent}/infoGroups[{i}]";
            AddId(items[i].Id, path, ids);
            CollectList(items[i].Profiles, path, ids);
            CollectList(items[i].Rules, path, ids);
            CollectList(items[i].InfoLinks, path, ids);
            CollectList(items[i].InfoGroups, path, ids);
        }
    }

    private static void CollectList(List<ProtocolInfoLink>? items, string parent, Dictionary<string, List<string>> ids)
    {
        if (items is null) return;
        for (var i = 0; i < items.Count; i++)
            AddId(items[i].Id, $"{parent}/infoLinks[{i}]", ids);
    }

    private static void CollectList(List<ProtocolCatalogueLink>? items, string parent, Dictionary<string, List<string>> ids)
    {
        if (items is null) return;
        for (var i = 0; i < items.Count; i++)
            AddId(items[i].Id, $"{parent}/catalogueLinks[{i}]", ids);
    }

    private static void CollectList(List<ProtocolPublication>? items, string parent, Dictionary<string, List<string>> ids)
    {
        if (items is null) return;
        for (var i = 0; i < items.Count; i++)
            AddId(items[i].Id, $"{parent}/publications[{i}]", ids);
    }
}
