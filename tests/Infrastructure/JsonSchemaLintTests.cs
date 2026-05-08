using System.Text.Json;
using Json.Schema;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Lint tests that validate JSON schema files against their declared metaschema.
/// Discovers all *.json files in the repository that declare a known JSON Schema
/// metaschema URI in their "$schema" property and validates each against it.
/// </summary>
[Trait("Category", "Unit")]
public sealed class JsonSchemaLintTests
{
    private static readonly string? RepoRoot = FindRepoRoot();

    private static readonly IReadOnlyDictionary<string, JsonSchema> MetaschemaByUri =
        new Dictionary<string, JsonSchema>(StringComparer.Ordinal)
        {
            ["https://json-schema.org/draft/2020-12/schema"] = MetaSchemas.Draft202012,
            ["https://json-schema.org/draft/2019-09/schema"] = MetaSchemas.Draft201909,
            ["http://json-schema.org/draft-07/schema#"] = MetaSchemas.Draft7,
            ["http://json-schema.org/draft-06/schema#"] = MetaSchemas.Draft6,
        };

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "BattleScribeSpec.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public static IEnumerable<object[]> AllJsonSchemaFiles()
    {
        if (RepoRoot is null)
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(RepoRoot, "*.json", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('/', '\\');
            if (normalized.Contains("\\artifacts\\")
                || normalized.Contains("\\node_modules\\")
                || normalized.Contains("\\.git\\")
                || normalized.Contains("\\.testdata\\"))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            if (!text.Contains("\"$schema\""))
            {
                continue;
            }

            string? schemaUri;
            try
            {
                using var doc = JsonDocument.Parse(text);
                schemaUri = doc.RootElement.TryGetProperty("$schema", out var prop)
                    ? prop.GetString()
                    : null;
            }
            catch
            {
                continue;
            }

            if (schemaUri is null || !MetaschemaByUri.ContainsKey(schemaUri))
            {
                continue;
            }

            var relPath = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
            yield return [file, relPath, schemaUri];
        }
    }

    [Theory]
    [MemberData(nameof(AllJsonSchemaFiles))]
    public void JsonSchemaIsValidAgainstMetaschema(string filePath, string relPath, string schemaUri)
    {
        var fileText = File.ReadAllText(filePath);
        using var jsonDoc = JsonDocument.Parse(fileText);

        var metaSchema = MetaschemaByUri[schemaUri];
        var result = metaSchema.Evaluate(jsonDoc.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        if (!result.IsValid)
        {
            var errors = (result.Details ?? [])
                .Where(d => !d.IsValid && d.Errors is not null && d.Errors.Count > 0)
                .SelectMany(d => d.Errors!.Select(e =>
                    $"  {d.InstanceLocation}: {e.Key} — {e.Value}"))
                .ToList();
            Assert.Fail($"{relPath} failed metaschema validation ({schemaUri}):\n{string.Join("\n", errors)}");
        }
    }
}
