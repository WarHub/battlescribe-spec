using System.Text.Json;
using Json.Schema;
using Yaml2JsonNode;
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
        var stream = new YamlStream();
        stream.Load(new StringReader(yamlText));
        var jsonNode = stream.Documents[0].ToJsonNode();
        var jsonString = jsonNode?.ToJsonString() ?? "null";
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
}
