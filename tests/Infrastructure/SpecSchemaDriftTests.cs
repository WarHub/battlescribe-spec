using System.Reflection;
using System.Text.Json;
using BattleScribeSpec.GameData;
using BattleScribeSpec.Roster;
using BattleScribeSpec.Scenario;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Holds the spec schemas in <c>docs/</c> (<see cref="SpecSchemas"/>) to the C# they describe, in both directions,
/// so neither can move without the other:
/// <list type="bullet">
///   <item>each action enum is exactly its domain's vocabulary — <see cref="SpecValidator.KnownActions"/>,
///   <see cref="GameDataActions.All"/>, <see cref="ScenarioHandoffs.All"/>;</item>
///   <item>each spec model type's definition names exactly the fields the loader reads into it. The loader refuses
///   a key the model lacks, so a schema property with no field blesses a spec the loader rejects, and a field with
///   no schema property flags a spec the loader accepts;</item>
///   <item>every model type a spec can reach has a definition.</item>
/// </list>
/// </summary>
/// <remarks>
/// The corpus validating (<see cref="SpecSchemaTests"/>) catches neither direction on its own: a field no spec uses
/// yet, or an action only a new spec will name, is invisible to it until someone writes that spec in an editor that
/// is now telling them it is wrong. The same idea as <c>ProtocolSchemaDriftTests</c> for the protocol schema.
/// </remarks>
[Trait("Category", "Lint")]
public sealed class SpecSchemaDriftTests
{
    // ── Vocabularies ─────────────────────────────────────────────────

    [Fact]
    public void RosterActionEnum_IsTheLoadersVocabulary() =>
        AssertSameSet(
            SchemaEnum(SpecSchemas.RosterFile, "actionName"), SpecValidator.KnownActions,
            $"docs/{SpecSchemas.RosterFile} $defs/actionName", "SpecValidator.KnownActions (its ActionParams table)");

    [Fact]
    public void GameDataActionEnum_IsGameDataActionsAll() =>
        AssertSameSet(
            SchemaEnum(SpecSchemas.GameDataFile, "actionName"), GameDataActions.All,
            $"docs/{SpecSchemas.GameDataFile} $defs/actionName", "GameDataActions.All");

    [Fact]
    public void HandoffEnums_AreScenarioHandoffs()
    {
        AssertSameSet(
            SchemaEnum(SpecSchemas.ScenarioFile, "handoffAction"), ScenarioHandoffs.All,
            $"docs/{SpecSchemas.ScenarioFile} $defs/handoffAction", "ScenarioHandoffs.All");
        AssertSameSet(
            SchemaEnum(SpecSchemas.ScenarioFile, "fileHandoffAction"), ScenarioHandoffs.All.Where(ScenarioHandoffs.TakesFile),
            $"docs/{SpecSchemas.ScenarioFile} $defs/fileHandoffAction", "the hand-offs ScenarioHandoffs.TakesFile names");
    }

    private static void AssertSameSet(IEnumerable<string> schema, IEnumerable<string> code, string schemaWhere, string codeWhere)
    {
        var inSchema = schema.ToHashSet(StringComparer.Ordinal);
        var inCode = code.ToHashSet(StringComparer.Ordinal);
        var missing = inCode.Except(inSchema).Order(StringComparer.Ordinal).ToList();
        var extra = inSchema.Except(inCode).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"{schemaWhere} is not {codeWhere}.\n" +
            (missing.Count > 0 ? $"  In the code, not the schema (an editor flags every spec that uses it): {string.Join(", ", missing)}\n" : "") +
            (extra.Count > 0 ? $"  In the schema, not the code (the schema blesses a spec the suite refuses): {string.Join(", ", extra)}\n" : "") +
            "  Make the two lists the same.");
    }

    private static List<string> SchemaEnum(string file, string def)
    {
        using var doc = SpecSchemas.Document(file);
        return [.. Def(doc, file, def).GetProperty("enum").EnumerateArray().Select(e => e.GetString()!)];
    }

    // ── Model fields ─────────────────────────────────────────────────

    private static readonly Type[] SpecRoots = [typeof(SpecFile), typeof(GameDataSpecFile), typeof(ScenarioSpecFile)];

    /// <summary>
    /// The definition each model type is read from, by file and <c>$defs</c> key (null: the file's root). The
    /// protocol's setup types are not listed: each is <c>spec-common-schema.json</c>'s definition named for the
    /// type without its <c>Protocol</c> prefix (<c>ProtocolCostType</c> → <c>costType</c>), the rule
    /// <c>ProtocolSchemaDriftTests</c> applies to the protocol schema.
    /// </summary>
    private static readonly Dictionary<Type, (string File, string? Def)> Definitions = new()
    {
        [typeof(SpecFile)] = (SpecSchemas.RosterFile, null),
        [typeof(SetupDef)] = (SpecSchemas.RosterFile, "setup"),
        [typeof(StepDef)] = (SpecSchemas.RosterFile, "step"),
        [typeof(ExpectedStateDef)] = (SpecSchemas.RosterFile, "expectedState"),
        [typeof(ErrorAssertionDef)] = (SpecSchemas.RosterFile, "errorAssertion"),
        [typeof(ExpectedForceDef)] = (SpecSchemas.RosterFile, "expectedForce"),
        [typeof(ExpectedSelectionDef)] = (SpecSchemas.RosterFile, "expectedSelection"),
        [typeof(ExpectedCostDef)] = (SpecSchemas.RosterFile, "expectedCost"),
        [typeof(ExpectedProfileDef)] = (SpecSchemas.RosterFile, "expectedProfile"),
        [typeof(ExpectedCharacteristicDef)] = (SpecSchemas.RosterFile, "expectedCharacteristic"),
        [typeof(ExpectedRuleDef)] = (SpecSchemas.RosterFile, "expectedRule"),
        [typeof(ExpectedCategoryDef)] = (SpecSchemas.RosterFile, "expectedCategory"),
        [typeof(ExpectedPublicationDef)] = (SpecSchemas.RosterFile, "expectedPublication"),

        [typeof(GameDataSpecFile)] = (SpecSchemas.GameDataFile, null),
        [typeof(GameDataSetupDef)] = (SpecSchemas.GameDataFile, "setup"),
        [typeof(GameDataStepDef)] = (SpecSchemas.GameDataFile, "step"),
        [typeof(GameDataExpectedStateDef)] = (SpecSchemas.GameDataFile, "expectedState"),
        [typeof(ExpectedErrorDef)] = (SpecSchemas.GameDataFile, "expectedError"),
        [typeof(ExpectedCatalogueDataDef)] = (SpecSchemas.GameDataFile, "expectedCatalogue"),
        [typeof(ExpectedGameSystemDataDef)] = (SpecSchemas.GameDataFile, "expectedGameSystem"),
        [typeof(ExpectedDataEntryDef)] = (SpecSchemas.GameDataFile, "expectedEntry"),

        [typeof(ScenarioSpecFile)] = (SpecSchemas.ScenarioFile, null),
        [typeof(ScenarioStepDef)] = (SpecSchemas.ScenarioFile, "item"),

        [typeof(ExpectFailureDef)] = (SpecSchemas.CommonFile, "expectFailure"),
        [typeof(ExpectedFileDef)] = (SpecSchemas.CommonFile, "expectedFile"),
    };

    private static (string File, string? Def) DefinitionOf(Type type)
    {
        if (Definitions.TryGetValue(type, out var definition))
        {
            return definition;
        }

        if (type.Namespace == "BattleScribeSpec.Protocol" && type.Name.StartsWith("Protocol", StringComparison.Ordinal))
        {
            var name = type.Name["Protocol".Length..];
            return (SpecSchemas.CommonFile, char.ToLowerInvariant(name[0]) + name[1..]);
        }

        throw new KeyNotFoundException(type.Name);
    }

    /// <summary>
    /// One row per model type a spec can reach from its root, labelled with the type's name: found by walking the
    /// models, so a type added to them is a row here before anyone remembers to map it.
    /// </summary>
    public static IEnumerable<TheoryDataRow<string>> ModelTypes() =>
        ReachableModelTypes().Values.Select(t => new TheoryDataRow<string>(t.Name) { Label = t.Name });

    [Theory]
    [MemberData(nameof(ModelTypes))]
    public void ModelFields_AreTheSchemaDefinitionsProperties(string typeName)
    {
        var type = ReachableModelTypes()[typeName];
        (string File, string? Def) definition;
        try
        {
            definition = DefinitionOf(type);
        }
        catch (KeyNotFoundException)
        {
            Assert.Fail(
                $"{type.FullName} is read from spec YAML, but no schema definition is mapped to it. Add its definition " +
                $"to the schema of the spec domain it belongs to (docs/*-spec-schema.json, or {SpecSchemas.CommonFile} " +
                $"when more than one domain reads it), and map it in {nameof(SpecSchemaDriftTests)}.{nameof(Definitions)}.");
            return;
        }

        using var doc = SpecSchemas.Document(definition.File);
        var where = $"docs/{definition.File} {(definition.Def is null ? "(root)" : $"$defs/{definition.Def}")}";
        var schemaNode = definition.Def is null ? doc.RootElement : Def(doc, definition.File, definition.Def);
        var inSchema = SchemaProperties(doc, schemaNode);
        var inModel = YamlProperties(type).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var missing = inModel.Except(inSchema).Order(StringComparer.Ordinal).ToList();
        var extra = inSchema.Except(inModel).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"{where} does not name the fields {type.Name} reads.\n" +
            (missing.Count > 0 ? $"  Fields the loader reads that the schema refuses: {string.Join(", ", missing)}\n" : "") +
            (extra.Count > 0 ? $"  Properties the schema accepts that the loader refuses: {string.Join(", ", extra)}\n" : "") +
            $"  Make the definition's properties {type.Name}'s fields.");
    }

    [Fact]
    public void EveryMappedDefinition_IsAModelTypeASpecReaches()
    {
        var reachable = ReachableModelTypes().Values.ToHashSet();
        var stale = Definitions.Keys.Where(t => !reachable.Contains(t)).Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            $"{nameof(Definitions)} maps types no spec root reaches: {string.Join(", ", stale)}. Remove them, or the " +
            "schema definitions they name, which now describe nothing the loader reads.");
    }

    /// <summary>
    /// The properties a definition accepts: its own, and those of the branches of a top-level <c>oneOf</c>,
    /// <c>anyOf</c> or <c>allOf</c>, following local <c>$ref</c>s — so a scenario item, three closed shapes
    /// under one <c>oneOf</c>, accepts the union of their fields, as its one model type does.
    /// </summary>
    private static HashSet<string> SchemaProperties(JsonDocument doc, JsonElement node)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(node);
        return names;

        void Collect(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (schema.TryGetProperty("$ref", out var reference) && reference.GetString() is { } r && r.StartsWith("#/$defs/", StringComparison.Ordinal))
            {
                Collect(doc.RootElement.GetProperty("$defs").GetProperty(r["#/$defs/".Length..]));
            }

            if (schema.TryGetProperty("properties", out var properties))
            {
                names.UnionWith(properties.EnumerateObject().Select(p => p.Name));
            }

            foreach (var combinator in (string[])["oneOf", "anyOf", "allOf"])
            {
                if (schema.TryGetProperty(combinator, out var branches))
                {
                    foreach (var branch in branches.EnumerateArray())
                    {
                        Collect(branch);
                    }
                }
            }
        }
    }

    private static JsonElement Def(JsonDocument doc, string file, string def)
    {
        if (doc.RootElement.TryGetProperty("$defs", out var defs) && defs.TryGetProperty(def, out var node))
        {
            return node;
        }

        Assert.Fail($"docs/{file} has no $defs/{def}.");
        return default; // unreachable
    }

    // ── What the loader reads ────────────────────────────────────────

    /// <summary>
    /// The keys the spec loader reads into <paramref name="type"/>: its public settable properties, named as
    /// <see cref="SpecLoader"/>'s deserializer names them, minus <see cref="YamlIgnoreAttribute"/>.
    /// </summary>
    private static IEnumerable<(string Name, PropertyInfo Property)> YamlProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod?.IsPublic == true && p.SetMethod?.IsPublic == true && !p.IsDefined(typeof(YamlIgnoreAttribute)))
            .Select(p => (CamelCaseNamingConvention.Instance.Apply(p.Name), p));

    private static readonly Lazy<SortedDictionary<string, Type>> Reachable = new(() =>
    {
        var found = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        var queue = new Queue<Type>(SpecRoots);
        while (queue.TryDequeue(out var type))
        {
            if (found.TryGetValue(type.Name, out var known))
            {
                Assert.True(known == type, $"Two spec model types are named {type.Name}: {known.FullName} and {type.FullName}.");
                continue;
            }

            found[type.Name] = type;
            foreach (var (_, property) in YamlProperties(type))
            {
                if (ModelTypeOf(property.PropertyType) is { } next)
                {
                    queue.Enqueue(next);
                }
            }
        }

        return found;
    });

    private static SortedDictionary<string, Type> ReachableModelTypes() => Reachable.Value;

    /// <summary>The spec model type a property holds — through a nullable, list or dictionary — or null for a scalar.</summary>
    private static Type? ModelTypeOf(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsGenericType)
        {
            var arguments = type.GetGenericArguments();
            return ModelTypeOf(arguments[^1]);
        }

        return type.IsClass && type != typeof(string) && type.Namespace?.StartsWith("BattleScribeSpec", StringComparison.Ordinal) == true
            ? type
            : null;
    }
}
