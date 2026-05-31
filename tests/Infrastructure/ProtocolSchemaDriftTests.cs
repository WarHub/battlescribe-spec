using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Ensures the protocol JSON Schema stays in sync with C# types.
/// Detects drift in both directions:
/// - C# property added but missing from schema → test fails
/// - Schema property exists but C# type lacks it → test fails
/// - New command/response type added but not in schema → test fails
/// </summary>
[Trait("Category", "Lint")]
public sealed class ProtocolSchemaDriftTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static readonly Lazy<JsonDocument> SchemaDoc = new(() =>
    {
        var schemaPath = Path.Combine(RepoRoot, "docs", "protocol-schema.json");
        return JsonDocument.Parse(File.ReadAllText(schemaPath));
    });

    private static string FindRepoRoot([CallerFilePath] string callerFilePath = "")
    {
        var dir = Path.GetDirectoryName(callerFilePath);
        while (dir is not null)
        {
            if (Directory.EnumerateFiles(dir, "*.slnx").Any())
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Could not find repository root (no *.slnx marker found) while traversing parents of '{callerFilePath}'.");
    }

    /// <summary>
    /// Derives the JSON Schema $defs key from a C# type name using naming conventions:
    /// 1. Strip "Protocol" prefix if present
    /// 2. camelCase the result
    /// Exceptions are listed in <see cref="SchemaDefKeyOverrides"/>.
    /// </summary>
    private static string GetSchemaDefKey(Type type)
    {
        if (SchemaDefKeyOverrides.TryGetValue(type, out var overrideKey))
        {
            return overrideKey;
        }

        var name = type.Name;
        if (name.StartsWith("Protocol", StringComparison.Ordinal))
        {
            name = name["Protocol".Length..];
        }

        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Types whose schema $defs key cannot be derived from the generic naming rule.
    /// </summary>
    private static readonly Dictionary<Type, string> SchemaDefKeyOverrides = new()
    {
        [typeof(ProtocolError)] = "protocolError", // "Protocol" is part of the concept name, not a prefix
        [typeof(RosterState)] = "stateResponse", // RosterState fields are inlined into stateResponse
    };

    /// <summary>
    /// Properties that exist in the schema but not in the C# type,
    /// because they are discriminator fields injected by the polymorphic serializer.
    /// </summary>
    private static readonly HashSet<string> DiscriminatorProperties = ["type"];

    /// <summary>
    /// Properties that exist in the C# type but are marked [JsonIgnore] and
    /// won't appear in the schema (e.g. the abstract Type property on base classes).
    /// These are excluded by the JsonTypeInfo enumeration, so typically not needed,
    /// but kept for documentation.
    /// </summary>
    private static readonly Dictionary<Type, HashSet<string>> IgnoredCSharpProperties = new()
    {
        // RosterState is mapped to stateResponse which has extra "type" discriminator
        // and the field names differ (RosterState record param names vs StateResponse properties)
        [typeof(RosterState)] = ["type"],
    };

    /// <summary>
    /// Types where the schema $def has additional properties beyond what the C# type has,
    /// because the schema def combines the type discriminator with the type's own properties.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> ExtraSchemaProperties = [];

    /// <summary>
    /// Types to skip bidirectional property checking because they map to
    /// a schema def that represents a different structural concept (e.g.,
    /// RosterState maps to stateResponse which is a wrapper).
    /// </summary>
    private static readonly HashSet<Type> SkipBidirectionalCheck =
    [
        typeof(RosterState), // Properties are on StateResponse, which maps to stateResponse
    ];

    public static TheoryData<Type, string> AllMappedTypes()
    {
        var data = new TheoryData<Type, string>();
        foreach (var type in GetProtocolJsonContextTypes())
        {
            if (type.IsAbstract || type == typeof(ProtocolCommand) || type == typeof(ProtocolResponse))
            {
                continue;
            }

            if (SkipBidirectionalCheck.Contains(type))
            {
                continue;
            }

            data.Add(type, GetSchemaDefKey(type));
        }
        Assert.NotEmpty(data);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllMappedTypes))]
    public void CSharpPropertiesExistInSchema(Type csharpType, string schemaDefKey)
    {
        var schemaProperties = GetSchemaDefProperties(schemaDefKey);
        var csharpProperties = GetJsonPropertyNames(csharpType);

        var missingInSchema = csharpProperties
            .Where(p => !schemaProperties.Contains(p))
            .Where(p => !DiscriminatorProperties.Contains(p))
            .ToList();

        if (missingInSchema.Count > 0)
        {
            Assert.Fail(
                $"C# type '{csharpType.Name}' has JSON properties not in schema def '{schemaDefKey}':\n" +
                $"  Missing: {string.Join(", ", missingInSchema)}\n" +
                $"  Action: Add these properties to $defs/{schemaDefKey} in docs/protocol-schema.json");
        }
    }

    [Theory]
    [MemberData(nameof(AllMappedTypes))]
    public void SchemaPropertiesExistInCSharp(Type csharpType, string schemaDefKey)
    {
        var schemaProperties = GetSchemaDefProperties(schemaDefKey);
        var csharpProperties = GetJsonPropertyNames(csharpType);

        var extraInSchema = schemaProperties
            .Where(p => !csharpProperties.Contains(p))
            .Where(p => !DiscriminatorProperties.Contains(p))
            .Where(p => !(ExtraSchemaProperties.TryGetValue(schemaDefKey, out var extras) && extras.Contains(p)))
            .ToList();

        if (extraInSchema.Count > 0)
        {
            Assert.Fail(
                $"Schema def '{schemaDefKey}' has properties not in C# type '{csharpType.Name}':\n" +
                $"  Extra: {string.Join(", ", extraInSchema)}\n" +
                $"  Action: Remove these from $defs/{schemaDefKey} in docs/protocol-schema.json, " +
                $"or add corresponding properties to {csharpType.Name}");
        }
    }

    [Fact]
    public void AllProtocolJsonContextTypesHaveSchemaDefinitions()
    {
        // Every concrete type registered in ProtocolJsonContext must have a $defs entry
        // with a key matching the derived schema name.
        var registeredTypes = GetProtocolJsonContextTypes()
            .Where(t => !t.IsAbstract && t != typeof(ProtocolCommand) && t != typeof(ProtocolResponse))
            .ToList();
        Assert.NotEmpty(registeredTypes);

        var root = SchemaDoc.Value.RootElement;
        var defs = root.GetProperty("$defs");

        var missing = registeredTypes
            .Select(t => (Type: t, Key: GetSchemaDefKey(t)))
            .Where(pair => !defs.TryGetProperty(pair.Key, out _))
            .ToList();

        if (missing.Count > 0)
        {
            Assert.Fail(
                $"Types registered in ProtocolJsonContext have no matching $defs entry in schema:\n" +
                $"  {string.Join(", ", missing.Select(m => $"{m.Type.Name} → \"{m.Key}\""))}\n" +
                $"  Action: Add $defs entries or update SchemaDefKeyOverrides if the naming convention doesn't apply.");
        }
    }

    [Fact]
    public void AllCommandDiscriminatorsAreInSchema()
    {
        var commandDef = GetSchemaDefOneOfConsts("command");
        Assert.NotEmpty(commandDef);
        var csharpDiscriminators = GetPolymorphicDiscriminators<ProtocolCommand>();
        Assert.NotEmpty(csharpDiscriminators);

        var missingInSchema = csharpDiscriminators
            .Where(d => !commandDef.Contains(d))
            .ToList();

        if (missingInSchema.Count > 0)
        {
            Assert.Fail(
                $"Command discriminators in C# but not in schema 'command' oneOf:\n" +
                $"  Missing: {string.Join(", ", missingInSchema)}\n" +
                $"  Action: Add $ref entries for these in $defs/command/oneOf in docs/protocol-schema.json");
        }
    }

    [Fact]
    public void AllResponseDiscriminatorsAreInSchema()
    {
        var responseDef = GetSchemaDefOneOfConsts("response");
        Assert.NotEmpty(responseDef);
        var csharpDiscriminators = GetPolymorphicDiscriminators<ProtocolResponse>();
        Assert.NotEmpty(csharpDiscriminators);

        var missingInSchema = csharpDiscriminators
            .Where(d => !responseDef.Contains(d))
            .ToList();

        if (missingInSchema.Count > 0)
        {
            Assert.Fail(
                $"Response discriminators in C# but not in schema 'response' oneOf:\n" +
                $"  Missing: {string.Join(", ", missingInSchema)}\n" +
                $"  Action: Add $ref entries for these in $defs/response/oneOf in docs/protocol-schema.json");
        }
    }

    private static HashSet<string> GetSchemaDefProperties(string defKey)
    {
        var root = SchemaDoc.Value.RootElement;
        if (!root.TryGetProperty("$defs", out var defs) ||
            !defs.TryGetProperty(defKey, out var def) ||
            !def.TryGetProperty("properties", out var props))
        {
            Assert.Fail($"Schema $defs/{defKey}/properties not found in protocol-schema.json");
            return []; // unreachable
        }

        return [.. props.EnumerateObject().Select(p => p.Name)];
    }

    private static HashSet<string> GetJsonPropertyNames(Type type)
    {
        var context = ProtocolJsonContext.Default;
        var typeInfo = context.GetTypeInfo(type);
        if (typeInfo is not JsonTypeInfo objectInfo || objectInfo.Properties is null)
        {
            Assert.Fail($"Could not get JsonTypeInfo for {type.Name} from ProtocolJsonContext");
            return []; // unreachable
        }

        return [.. objectInfo.Properties.Select(p => p.Name)];
    }

    private static IReadOnlyList<Type> GetProtocolJsonContextTypes()
    {
        return CustomAttributeData
            .GetCustomAttributes(typeof(ProtocolJsonContext))
            .Where(a => a.AttributeType == typeof(System.Text.Json.Serialization.JsonSerializableAttribute))
            .Select(a => a.ConstructorArguments[0].Value as Type)
            .Where(t => t is not null)
            .ToList()!;
    }

    private static HashSet<string> GetPolymorphicDiscriminators<T>()
    {
        var attributes = typeof(T)
            .GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonDerivedTypeAttribute), false)
            .Cast<System.Text.Json.Serialization.JsonDerivedTypeAttribute>();

        return attributes
            .Select(a => a.TypeDiscriminator as string)
            .Where(d => d is not null)
            .ToHashSet()!;
    }

    private static HashSet<string> GetSchemaDefOneOfConsts(string defKey)
    {
        var root = SchemaDoc.Value.RootElement;
        if (!root.TryGetProperty("$defs", out var defs) ||
            !defs.TryGetProperty(defKey, out var def) ||
            !def.TryGetProperty("oneOf", out var oneOf))
        {
            return [];
        }

        // Each oneOf entry is a $ref like "#/$defs/setupCommand"
        // We need to resolve each ref to get the "type" const from its properties
        var consts = new HashSet<string>();
        foreach (var entry in oneOf.EnumerateArray())
        {
            if (!entry.TryGetProperty("$ref", out var refProp))
            {
                continue;
            }

            var refStr = refProp.GetString();
            if (refStr is null || !refStr.StartsWith("#/$defs/", StringComparison.Ordinal))
            {
                continue;
            }

            var referencedDefKey = refStr["#/$defs/".Length..];
            if (!defs.TryGetProperty(referencedDefKey, out var referencedDef) ||
                !referencedDef.TryGetProperty("properties", out var props) ||
                !props.TryGetProperty("type", out var typeProp) ||
                !typeProp.TryGetProperty("const", out var constVal))
            {
                continue;
            }

            var constStr = constVal.GetString();
            if (constStr is not null)
            {
                consts.Add(constStr);
            }
        }

        return consts;
    }
}
