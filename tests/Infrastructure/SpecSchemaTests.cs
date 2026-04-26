using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using YamlDotNet.RepresentationModel;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Validates all spec YAML files against the JSON Schema in docs/spec-schema.json.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SpecSchemaTests
{
    private static readonly string? SpecsDir = SpecLoader.FindSpecsDirectory();

    private static readonly Lazy<JsonSchema> Schema = new(() =>
    {
        var schemaPath = Path.Combine(SpecsDir!, "..", "docs", "spec-schema.json");
        var schemaText = File.ReadAllText(schemaPath);
        return JsonSchema.FromText(schemaText);
    });

    public static IEnumerable<object[]> AllSpecs()
    {
        if (SpecsDir is null || !Directory.Exists(SpecsDir))
            yield break;
        foreach (var (path, id, category) in SpecLoader.DiscoverSpecs(SpecsDir))
        {
            var relPath = Path.GetRelativePath(SpecsDir, path).Replace('\\', '/');
            yield return [path, relPath];
        }
    }

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void SpecValidatesAgainstSchema(string specPath, string specName)
    {
        var yamlText = File.ReadAllText(specPath);
        var jsonNode = YamlToJsonNode(yamlText);
        var jsonString = jsonNode?.ToJsonString(new JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        }) ?? "null";
        using var jsonDoc = JsonDocument.Parse(jsonString);
        var result = Schema.Value.Evaluate(jsonDoc.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });
        if (!result.IsValid)
        {
            var errors = (result.Details ?? [])
                .Where(d => !d.IsValid && d.Errors is not null && d.Errors.Count > 0)
                .SelectMany(d => d.Errors!.Select(e =>
                    $"  {d.InstanceLocation}: {e.Key} — {e.Value}"))
                .ToList();
            Assert.Fail($"{specName} failed schema validation:\n{string.Join("\n", errors)}");
        }
    }

    private static JsonNode? YamlToJsonNode(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0)
            return null;
        return ConvertNode(stream.Documents[0].RootNode);
    }

    private static JsonNode? ConvertNode(YamlNode node)
    {
        return node switch
        {
            YamlMappingNode mapping => new JsonObject(
                mapping.Children.Select(kv =>
                    KeyValuePair.Create(
                        ((YamlScalarNode)kv.Key).Value!,
                        ConvertNode(kv.Value)))),
            YamlSequenceNode sequence =>
                new JsonArray(sequence.Children.Select(ConvertNode).ToArray()),
            YamlScalarNode scalar => ConvertScalar(scalar),
            _ => null
        };
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (value is null)
            return null;

        // Quoted strings stay as strings
        if (scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted
            or YamlDotNet.Core.ScalarStyle.DoubleQuoted
            or YamlDotNet.Core.ScalarStyle.Literal
            or YamlDotNet.Core.ScalarStyle.Folded)
            return JsonValue.Create(value);

        // Null
        if (value is "" or "~" or "null" or "Null" or "NULL")
            return null;

        // Boolean (YAML 1.1)
        if (value is "true" or "True" or "TRUE" or "yes" or "Yes" or "YES"
            or "on" or "On" or "ON")
            return JsonValue.Create(true);
        if (value is "false" or "False" or "FALSE" or "no" or "No" or "NO"
            or "off" or "Off" or "OFF")
            return JsonValue.Create(false);

        // Integer
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return JsonValue.Create(l);

        // Float
        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);

        // Special floats
        if (value is ".inf" or ".Inf" or ".INF")
            return JsonValue.Create(double.PositiveInfinity);
        if (value is "-.inf" or "-.Inf" or "-.INF")
            return JsonValue.Create(double.NegativeInfinity);
        if (value is ".nan" or ".NaN" or ".NAN")
            return JsonValue.Create(double.NaN);

        return JsonValue.Create(value);
    }
}
