using System.Text.Json;
using Json.Schema;
using Yaml2JsonNode;
using YamlDotNet.RepresentationModel;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Validates all spec YAML files against the JSON Schema in docs/spec-schema.json.
/// </summary>
[Trait("Category", "Lint")]
public sealed class SpecSchemaTests
{
    private static readonly string? SpecsDir = SpecLoader.FindRosterSpecsDirectory();

    private static string SchemaPath => Path.Combine(SpecsDir!, "..", "..", "docs", "spec-schema.json");

    private static readonly Lazy<JsonSchema> Schema = new(() =>
    {
        var schemaText = File.ReadAllText(SchemaPath);
        return JsonSchema.FromText(schemaText);
    });

    public static IEnumerable<object[]> AllSpecs()
    {
        if (SpecsDir is null || !Directory.Exists(SpecsDir))
        {
            yield break;
        }

        foreach (var (path, _, _) in SpecLoader.DiscoverSpecs(SpecsDir))
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
        if (stream.Documents.Count == 0)
        {
            Assert.Fail($"{specName} failed schema validation: YAML stream contains no documents.");
        }
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

    /// <summary>
    /// The corpus is the regression test for the permissive direction — every spec is evaluated
    /// against this schema above. Nothing guards the restrictive direction: no spec is written in
    /// the entry-addressed <c>on:</c> dialect #419 retired, so loosening the pattern back to
    /// <c>( \S.*)?</c> would reintroduce the legacy form without failing a single test.
    ///
    /// That mattered once already: the schema accepted the legacy form long after the code stopped
    /// honouring it (<see cref="Roster.ErrorAddress.Matches"/> never matches a literal id, and
    /// <c>SpecLintTests</c> rejects the spec outright), so a spec could pass schema validation and
    /// then fail lint with a message saying the schema should not have let it through.
    /// </summary>
    [Theory]
    // Retired entry-addressed form: a literal second token names a catalogue ENTRY, which is a set
    // of nodes rather than one. Rejected at every layer.
    [InlineData("selection se-unit-a", false)]
    [InlineData("force fe-detachment", false)]
    [InlineData("category cat-troops", false)]
    // Node-addressed: the only way to name a per-run node id.
    [InlineData("selection ${{ steps.select-first.selectionId }}", true)]
    [InlineData("force ${{ steps.add-force.forceId }}", true)]
    [InlineData("category ${{ steps.select-first.categoryId }}", true)]
    // Bare kinds that have no id a spec can name.
    [InlineData("roster", true)]
    [InlineData("group", true)]
    // Bare addressable kinds match on kind alone — unused by the corpus, but the runner supports it,
    // so the schema must not forbid it.
    [InlineData("selection", true)]
    [InlineData("force", true)]
    // Giving an id-less kind an id is a lint error, and the pattern rejects it too.
    [InlineData("roster ros-1", false)]
    public void ErrorAddressPattern_AcceptsOnlyNodeAddressedForms(string on, bool expectedValid)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SchemaPath));
        var pattern = doc.RootElement
            .GetProperty("$defs").GetProperty("errorAssertion")
            .GetProperty("properties").GetProperty("on")
            .GetProperty("pattern").GetString();

        Assert.NotNull(pattern);
        var actual = System.Text.RegularExpressions.Regex.IsMatch(on, pattern);

        Assert.True(
            actual == expectedValid,
            $"on: '{on}' should be {(expectedValid ? "accepted" : "rejected")} by the schema pattern " +
            $"'{pattern}', but was {(actual ? "accepted" : "rejected")}.");
    }
}
