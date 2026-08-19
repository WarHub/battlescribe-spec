using System.Text.Json;
using Json.Schema;
using Yaml2JsonNode;
using YamlDotNet.RepresentationModel;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Validates every roster spec YAML against the JSON Schema in docs/spec-schema.json, and pins the
/// restrictive direction of the schema's own patterns — which the corpus cannot do on its own.
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
    /// then be refused by the linter — which names a catalogue entry, not the schema — for a shape the
    /// schema had just blessed.
    ///
    /// The pattern tolerates surrounding whitespace because the runtime does: <c>ErrorAddress.Parse</c>
    /// and <c>ExpressionResolver.Resolve</c> both trim before inspecting. It still rejects a value
    /// whose expression is not the whole token — <c>Resolve</c> returns those unchanged, so they
    /// resolve to a literal that matches nothing, silently.
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
    [InlineData("category", true)]
    // Whitespace the runtime trims away.
    [InlineData("selection  ${{ steps.a.selectionId }}", true)]
    [InlineData("selection ${{ steps.a.selectionId }} ", true)]
    // The expression must be the whole token, or it resolves to a literal that never matches.
    [InlineData("selection ${{ steps.a.selectionId }} junk", false)]
    [InlineData("selection sel-${{ steps.a.selectionId }}", false)]
    [InlineData("selection ${{", false)]
    // Giving an id-less kind an id is a lint error, and the pattern rejects it too.
    [InlineData("roster ros-1", false)]
    public void ErrorAddressPattern_AcceptsOnlyNodeAddressedForms(string on, bool expectedValid)
    {
        if (SpecsDir is null || !Directory.Exists(SpecsDir))
        {
            return;
        }

        // Evaluate through the real validator, not a .NET Regex re-run of the pattern: the pattern
        // is only meaningful as JSON Schema, and a proxy assertion can agree with the regex engine
        // while the validator disagrees.
        var specJson = $$"""
            {
              "id": "error-address-probe",
              "category": "probe",
              "description": "Minimal spec whose only variable is the error address under test.",
              "setup": { "gameSystem": {} },
              "steps": [
                {
                  "expectedState": {
                    "errors": [ { "on": {{JsonSerializer.Serialize(on)}}, "from": "entry/constraint" } ]
                  }
                }
              ]
            }
            """;

        using var doc = JsonDocument.Parse(specJson);
        var result = Schema.Value.Evaluate(doc.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        Assert.True(
            result.IsValid == expectedValid,
            $"on: '{on}' should be {(expectedValid ? "accepted" : "rejected")} by docs/spec-schema.json, " +
            $"but the validator {(result.IsValid ? "accepted" : "rejected")} it.");
    }
}
