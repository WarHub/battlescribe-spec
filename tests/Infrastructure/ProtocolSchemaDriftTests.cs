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
    /// Maps each C# protocol type to its corresponding JSON Schema $defs key.
    /// This mapping itself acts as a drift guard: adding a new type to
    /// ProtocolJsonContext requires adding it here too.
    /// </summary>
    private static readonly Dictionary<Type, string> TypeToSchemaDef = new()
    {
        // Commands
        [typeof(SetupCommand)] = "setupCommand",
        [typeof(SetupFromFilesCommand)] = "setupFromFilesCommand",
        [typeof(ActionCommand)] = "actionCommand",
        [typeof(GetStateCommand)] = "getStateCommand",
        [typeof(GetErrorsCommand)] = "getErrorsCommand",
        [typeof(TeardownCommand)] = "teardownCommand",
        // Responses
        [typeof(SetupResult)] = "setupResult",
        [typeof(ActionResult)] = "actionResult",
        [typeof(StateResponse)] = "stateResponse",
        [typeof(ErrorsResponse)] = "errorsResponse",
        [typeof(TeardownResult)] = "teardownResult",
        [typeof(ProtocolError)] = "protocolError",
        // Shared protocol setup types
        [typeof(ProtocolGameSystem)] = "gameSystem",
        [typeof(ProtocolCatalogue)] = "catalogue",
        [typeof(ProtocolCostType)] = "costType",
        [typeof(ProtocolProfileType)] = "profileType",
        [typeof(ProtocolCharacteristicType)] = "characteristicType",
        [typeof(ProtocolForceEntry)] = "forceEntry",
        [typeof(ProtocolCategoryEntry)] = "categoryEntry",
        [typeof(ProtocolSelectionEntry)] = "selectionEntry",
        [typeof(ProtocolSelectionEntryGroup)] = "selectionEntryGroup",
        [typeof(ProtocolEntryLink)] = "entryLink",
        [typeof(ProtocolCategoryLink)] = "categoryLink",
        [typeof(ProtocolCostValue)] = "costValue",
        [typeof(ProtocolConstraint)] = "constraint",
        [typeof(ProtocolModifier)] = "modifier",
        [typeof(ProtocolModifierGroup)] = "modifierGroup",
        [typeof(ProtocolCondition)] = "condition",
        [typeof(ProtocolConditionGroup)] = "conditionGroup",
        [typeof(ProtocolRepeat)] = "repeat",
        [typeof(ProtocolRule)] = "rule",
        [typeof(ProtocolProfile)] = "profile",
        [typeof(ProtocolCharacteristic)] = "characteristic",
        [typeof(ProtocolInfoGroup)] = "infoGroup",
        [typeof(ProtocolInfoLink)] = "infoLink",
        [typeof(ProtocolCatalogueLink)] = "catalogueLink",
        [typeof(ProtocolPublication)] = "publication",
        [typeof(ProtocolDataFile)] = "dataFile",
        // State types
        [typeof(ActionOutputs)] = "actionOutputs",
        [typeof(RosterState)] = "stateResponse", // RosterState fields are inlined into stateResponse
        [typeof(ForceState)] = "forceState",
        [typeof(SelectionState)] = "selectionState",
        [typeof(CostState)] = "costState",
        [typeof(ProfileState)] = "profileState",
        [typeof(CharacteristicState)] = "characteristicState",
        [typeof(RuleState)] = "ruleState",
        [typeof(CategoryState)] = "categoryState",
        [typeof(PublicationState)] = "publicationState",
        [typeof(ValidationErrorState)] = "validationErrorState",
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
        foreach (var (type, schemaKey) in TypeToSchemaDef)
        {
            if (SkipBidirectionalCheck.Contains(type))
            {
                continue;
            }

            data.Add(type, schemaKey);
        }
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
    public void AllProtocolJsonContextTypesAreMapped()
    {
        // Get all types registered in ProtocolJsonContext (excluding base/abstract types and primitives)
        var registeredTypes = GetProtocolJsonContextTypes();
        var mappedTypes = TypeToSchemaDef.Keys.ToHashSet();

        var unmapped = registeredTypes
            .Where(t => !mappedTypes.Contains(t))
            .Where(t => !t.IsAbstract) // Skip ProtocolCommand, ProtocolResponse base classes
            .Where(t => t != typeof(ProtocolCommand) && t != typeof(ProtocolResponse))
            .ToList();

        if (unmapped.Count > 0)
        {
            Assert.Fail(
                $"Types registered in ProtocolJsonContext but not mapped in ProtocolSchemaDriftTests:\n" +
                $"  {string.Join(", ", unmapped.Select(t => t.Name))}\n" +
                $"  Action: Add these types to TypeToSchemaDef dictionary and create corresponding " +
                $"$defs entries in docs/protocol-schema.json");
        }
    }

    [Fact]
    public void AllCommandDiscriminatorsAreInSchema()
    {
        var commandDef = GetSchemaDefOneOfConsts("command");
        var csharpDiscriminators = GetPolymorphicDiscriminators<ProtocolCommand>();

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
        var csharpDiscriminators = GetPolymorphicDiscriminators<ProtocolResponse>();

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
        var attributes = typeof(ProtocolJsonContext)
            .GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonSerializableAttribute), false)
            .Cast<System.Text.Json.Serialization.JsonSerializableAttribute>()
            .ToList();

        return attributes.Select(a => a.TypeInfoPropertyName)
            .Where(name => name is not null)
            .Select(name => typeof(ProtocolJsonContext).GetProperty(name!)?.PropertyType)
            .Where(t => t is not null)
            .Select(t => t!.IsGenericType && t.GetGenericTypeDefinition() == typeof(JsonTypeInfo<>)
                ? t.GetGenericArguments()[0]
                : t)
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
