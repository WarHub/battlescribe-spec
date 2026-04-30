using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec;

/// <summary>
/// Validates that all IDs within a spec setup tree are unique.
/// Uses reflection to automatically walk all protocol types — no manual updates
/// needed when types are added/changed. Only collects properties named exactly
/// "Id" (not reference fields like TargetId, TypeId, ChildId, GameSystemId).
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
        if (setup.GameSystem is not null)
        {
            CollectIds(setup.GameSystem, "gameSystem", idLocations);
        }

        if (setup.Catalogues is not null)
        {
            for (var i = 0; i < setup.Catalogues.Count; i++)
            {
                CollectIds(setup.Catalogues[i], $"catalogues[{i}]", idLocations);
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

    /// <summary>
    /// Cache of reflected type metadata to avoid repeated reflection per type.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, TypeInfo> TypeInfoCache = [];

    private sealed record TypeInfo(PropertyInfo? IdProperty, (PropertyInfo Property, string Name)[] ListProperties);

    private static TypeInfo GetTypeInfo([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type)
    {
        if (TypeInfoCache.TryGetValue(type, out var cached))
        {
            return cached;
        }

        var idProp = type.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        // Only collect "Id" properties that are string type
        if (idProp is not null && idProp.PropertyType != typeof(string))
        {
            idProp = null;
        }

        var listProps = new List<(PropertyInfo, string)>();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var propType = prop.PropertyType;
            if (!propType.IsGenericType || propType.GetGenericTypeDefinition() != typeof(List<>))
            {
                continue;
            }

            var elementType = propType.GetGenericArguments()[0];
            // Recurse into List<T> where T is a protocol/setup type (has at least one property with Id or List<>)
            if (IsWalkableType(elementType))
            {
                listProps.Add((prop, ToCamelCase(prop.Name)));
            }
        }

        var info = new TypeInfo(idProp, [.. listProps]);
        return TypeInfoCache.GetOrAdd(type, info);
    }

    private static bool IsWalkableType(Type type)
    {
        // Walk into types in the Protocol namespace, or SetupDef-related types
        return type.Namespace == typeof(ProtocolGameSystem).Namespace
            || type == typeof(SetupDef);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Protocol types are preserved — they're used directly in strongly-typed code throughout the codebase.")]
    private static void CollectIds(object obj, string path, Dictionary<string, List<string>> idLocations)
    {
        var typeInfo = GetTypeInfo(obj.GetType());

        // Collect this object's Id
        if (typeInfo.IdProperty is not null)
        {
            var id = (string?)typeInfo.IdProperty.GetValue(obj);
            if (!string.IsNullOrEmpty(id))
            {
                if (!idLocations.TryGetValue(id, out var locations))
                {
                    locations = [];
                    idLocations[id] = locations;
                }
                locations.Add(path);
            }
        }

        // Recurse into child lists
        foreach (var (prop, name) in typeInfo.ListProperties)
        {
            if (prop.GetValue(obj) is not IList list)
            {
                continue;
            }

            for (var i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (item is not null)
                {
                    CollectIds(item, $"{path}/{name}[{i}]", idLocations);
                }
            }
        }
    }

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
