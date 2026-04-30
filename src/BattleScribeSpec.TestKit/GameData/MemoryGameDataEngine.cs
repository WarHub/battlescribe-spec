using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.GameData;

/// <summary>
/// In-memory implementation of <see cref="IGameDataEngine"/> for testing the runner
/// and spec pipeline without any external engine dependency.
/// Maintains a mutable tree that can be queried for state snapshots.
/// </summary>
public sealed class MemoryGameDataEngine : IGameDataEngine
{
    private string _gameSystemId = "";
    private string _gameSystemName = "";
    private readonly List<MutableCatalogue> _catalogues = [];
    private readonly Dictionary<string, MutableEntry> _entriesById = [];
    private readonly Dictionary<string, string> _parentMap = []; // entryId → parentId
    private int _nextId = 1;

    public void SetTestContext(string specId) { }

    public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
    {
        _gameSystemId = gameSystem.Id;
        _gameSystemName = gameSystem.Name;
        _catalogues.Clear();
        _entriesById.Clear();
        _parentMap.Clear();

        foreach (var cat in catalogues)
        {
            var mc = new MutableCatalogue(cat.Id, cat.Name, cat.GameSystemId);
            _catalogues.Add(mc);

            // Import existing selection entries
            if (cat.SelectionEntries is not null)
            {
                foreach (var se in cat.SelectionEntries)
                {
                    var entry = ImportSelectionEntry(se);
                    mc.SelectionEntries.Add(entry);
                    _parentMap[entry.Id] = cat.Id;
                }
            }

            if (cat.SharedSelectionEntries is not null)
            {
                foreach (var se in cat.SharedSelectionEntries)
                {
                    var entry = ImportSelectionEntry(se);
                    mc.SharedSelectionEntries.Add(entry);
                    _parentMap[entry.Id] = cat.Id;
                }
            }
        }

        return [];
    }

    private MutableEntry ImportSelectionEntry(ProtocolSelectionEntry se)
    {
        var entry = new MutableEntry(se.Id, se.Name, "selectionEntry", se.Hidden);
        _entriesById[entry.Id] = entry;

        if (se.SelectionEntries is not null)
        {
            foreach (var child in se.SelectionEntries)
            {
                var childEntry = ImportSelectionEntry(child);
                entry.Children.Add(childEntry);
                _parentMap[childEntry.Id] = entry.Id;
            }
        }

        return entry;
    }

    public GameDataActionOutputs AddEntry(string parentId, string entryType, string? name = null)
    {
        var newId = $"generated-{_nextId++}";
        var entry = new MutableEntry(newId, name ?? $"New {entryType}", entryType, false);
        _entriesById[newId] = entry;
        _parentMap[newId] = parentId;

        // Find parent and add
        if (TryGetCatalogue(parentId, out var cat))
        {
            GetContainerForType(cat, entryType).Add(entry);
        }
        else if (_entriesById.TryGetValue(parentId, out var parentEntry))
        {
            parentEntry.Children.Add(entry);
        }
        else
        {
            throw new InvalidOperationException($"Parent '{parentId}' not found");
        }

        return new GameDataActionOutputs { EntryId = newId };
    }

    public void RemoveEntry(string entryId)
    {
        if (!_entriesById.TryGetValue(entryId, out var entry))
        {
            throw new InvalidOperationException($"Entry '{entryId}' not found");
        }

        // Remove from parent
        if (_parentMap.TryGetValue(entryId, out var parentId))
        {
            if (TryGetCatalogue(parentId, out var cat))
            {
                RemoveFromCatalogue(cat, entry);
            }
            else if (_entriesById.TryGetValue(parentId, out var parentEntry))
            {
                parentEntry.Children.Remove(entry);
            }
            _parentMap.Remove(entryId);
        }

        // Remove recursively from registry
        RemoveFromRegistry(entry);
    }

    public void MoveEntry(string entryId, string newParentId, int? index = null)
    {
        if (!_entriesById.TryGetValue(entryId, out var entry))
        {
            throw new InvalidOperationException($"Entry '{entryId}' not found");
        }

        // Remove from current parent
        if (_parentMap.TryGetValue(entryId, out var oldParentId))
        {
            if (TryGetCatalogue(oldParentId, out var oldCat))
            {
                RemoveFromCatalogue(oldCat, entry);
            }
            else if (_entriesById.TryGetValue(oldParentId, out var oldParent))
            {
                oldParent.Children.Remove(entry);
            }
        }

        // Add to new parent
        _parentMap[entryId] = newParentId;
        if (TryGetCatalogue(newParentId, out var newCat))
        {
            var container = GetContainerForType(newCat, entry.EntryType);
            if (index is { } idx && idx >= 0 && idx <= container.Count)
            {
                container.Insert(idx, entry);
            }
            else
            {
                container.Add(entry);
            }
        }
        else if (_entriesById.TryGetValue(newParentId, out var newParent))
        {
            if (index is { } idx && idx >= 0 && idx <= newParent.Children.Count)
            {
                newParent.Children.Insert(idx, entry);
            }
            else
            {
                newParent.Children.Add(entry);
            }
        }
        else
        {
            throw new InvalidOperationException($"New parent '{newParentId}' not found");
        }
    }

    public void SetField(string entryId, string field, string? value)
    {
        if (!_entriesById.TryGetValue(entryId, out var entry))
        {
            throw new InvalidOperationException($"Entry '{entryId}' not found");
        }

        switch (field)
        {
            case "name":
                entry.Name = value ?? "";
                break;
            case "hidden":
                entry.Hidden = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                break;
            default:
                entry.Fields[field] = value;
                break;
        }
    }

    public GameDataActionOutputs AddLink(string parentId, string linkType, string targetId)
    {
        var newId = $"generated-{_nextId++}";
        var entry = new MutableEntry(newId, $"Link to {targetId}", linkType, false);
        entry.Fields["targetId"] = targetId;
        _entriesById[newId] = entry;
        _parentMap[newId] = parentId;

        if (TryGetCatalogue(parentId, out var cat))
        {
            GetContainerForType(cat, linkType).Add(entry);
        }
        else if (_entriesById.TryGetValue(parentId, out var parentEntry))
        {
            parentEntry.Children.Add(entry);
        }
        else
        {
            throw new InvalidOperationException($"Parent '{parentId}' not found");
        }

        return new GameDataActionOutputs { EntryId = newId };
    }

    public GameDataState GetState()
    {
        return new GameDataState
        {
            GameSystem = new GameSystemDataState
            {
                Id = _gameSystemId,
                Name = _gameSystemName,
            },
            Catalogues = [.. _catalogues.Select(SnapshotCatalogue)],
        };
    }

    public IReadOnlyList<ValidationErrorState> GetValidationErrors() => [];

    public void Cleanup()
    {
        _catalogues.Clear();
        _entriesById.Clear();
        _parentMap.Clear();
    }

    public void Dispose() => Cleanup();

    // ===== Helpers =====

    private bool TryGetCatalogue(string id, out MutableCatalogue catalogue)
    {
        catalogue = _catalogues.FirstOrDefault(c => c.Id == id)!;
        return catalogue is not null;
    }

    private static List<MutableEntry> GetContainerForType(MutableCatalogue cat, string entryType)
    {
        return entryType switch
        {
            "selectionEntry" => cat.SelectionEntries,
            "selectionEntryGroup" => cat.SharedSelectionEntryGroups,
            "entryLink" => cat.EntryLinks,
            "rule" => cat.Rules,
            "profile" => cat.SharedProfiles,
            "infoLink" or "categoryLink" => cat.EntryLinks,
            "forceEntry" => cat.ForceEntries,
            "categoryEntry" => cat.CategoryEntries,
            _ => cat.SelectionEntries,
        };
    }

    private static void RemoveFromCatalogue(MutableCatalogue cat, MutableEntry entry)
    {
        cat.SelectionEntries.Remove(entry);
        cat.SharedSelectionEntries.Remove(entry);
        cat.SharedSelectionEntryGroups.Remove(entry);
        cat.EntryLinks.Remove(entry);
        cat.Rules.Remove(entry);
        cat.SharedRules.Remove(entry);
        cat.SharedProfiles.Remove(entry);
        cat.ForceEntries.Remove(entry);
        cat.CategoryEntries.Remove(entry);
    }

    private void RemoveFromRegistry(MutableEntry entry)
    {
        _entriesById.Remove(entry.Id);
        foreach (var child in entry.Children)
        {
            RemoveFromRegistry(child);
        }
    }

    private static CatalogueDataState SnapshotCatalogue(MutableCatalogue cat)
    {
        return new CatalogueDataState
        {
            Id = cat.Id,
            Name = cat.Name,
            GameSystemId = cat.GameSystemId,
            SelectionEntries = [.. cat.SelectionEntries.Select(SnapshotEntry)],
            SharedSelectionEntries = [.. cat.SharedSelectionEntries.Select(SnapshotEntry)],
            SharedSelectionEntryGroups = [.. cat.SharedSelectionEntryGroups.Select(SnapshotEntry)],
            EntryLinks = [.. cat.EntryLinks.Select(SnapshotEntry)],
            Rules = [.. cat.Rules.Select(SnapshotEntry)],
            SharedRules = [.. cat.SharedRules.Select(SnapshotEntry)],
            SharedProfiles = [.. cat.SharedProfiles.Select(SnapshotEntry)],
            ForceEntries = [.. cat.ForceEntries.Select(SnapshotEntry)],
            CategoryEntries = [.. cat.CategoryEntries.Select(SnapshotEntry)],
            Publications = [],
            CostTypes = [],
            ProfileTypes = [],
        };
    }

    private static DataEntryState SnapshotEntry(MutableEntry entry)
    {
        return new DataEntryState
        {
            Id = entry.Id,
            Name = entry.Name,
            EntryType = entry.EntryType,
            Hidden = entry.Hidden,
            Children = [.. entry.Children.Select(SnapshotEntry)],
            Fields = entry.Fields.Count > 0
                ? new Dictionary<string, string?>(entry.Fields)
                : null,
        };
    }

    // ===== Mutable internal types =====

    private sealed class MutableCatalogue(string id, string name, string gameSystemId)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string GameSystemId { get; } = gameSystemId;
        public List<MutableEntry> SelectionEntries { get; } = [];
        public List<MutableEntry> SharedSelectionEntries { get; } = [];
        public List<MutableEntry> SharedSelectionEntryGroups { get; } = [];
        public List<MutableEntry> EntryLinks { get; } = [];
        public List<MutableEntry> Rules { get; } = [];
        public List<MutableEntry> SharedRules { get; } = [];
        public List<MutableEntry> SharedProfiles { get; } = [];
        public List<MutableEntry> ForceEntries { get; } = [];
        public List<MutableEntry> CategoryEntries { get; } = [];
    }

    private sealed class MutableEntry(string id, string name, string entryType, bool hidden)
    {
        public string Id { get; } = id;
        public string Name { get; set; } = name;
        public string EntryType { get; } = entryType;
        public bool Hidden { get; set; } = hidden;
        public List<MutableEntry> Children { get; } = [];
        public Dictionary<string, string?> Fields { get; } = [];
    }
}
