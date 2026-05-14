using System.Runtime.CompilerServices;
using System.Text.Json;
using Json.Schema;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Lint tests that validate JSON schema files in docs/ against the JSON Schema draft 2020-12 metaschema.
/// </summary>
[Trait("Category", "Lint")]
public sealed class JsonSchemaLintTests
{
    private static readonly JsonSchema SupportedMetaschema = MetaSchemas.Draft202012;
    private static readonly Uri SupportedMetaschemaUri = MetaSchemas.Draft202012.BaseUri
        ?? throw new InvalidOperationException("MetaSchemas.Draft202012 has no BaseUri.");
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string DocsDirectory = FindDocsDirectory();

    private static string FindRepoRoot([CallerFilePath] string callerFilePath = "")
    {
        if (!Path.IsPathRooted(callerFilePath))
        {
            throw new InvalidOperationException(
                $"[CallerFilePath] returned a non-rooted path '{callerFilePath}'. " +
                "Ensure the project is not built with a PathMap that strips the absolute path.");
        }

        var dir = Path.GetDirectoryName(callerFilePath);
        while (dir is not null)
        {
            if (Directory.EnumerateFiles(dir, "*.slnx").Any())
            {
                return dir.Replace('\\', '/');
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Could not find repository root (no *.slnx marker found) while traversing parents of '{callerFilePath}'.");
    }

    private static string FindDocsDirectory()
    {
        var docsDirectory = Path.Combine(RepoRoot, "docs").Replace('\\', '/');
        if (!Directory.Exists(docsDirectory))
        {
            throw new DirectoryNotFoundException($"JSON schema lint could not find docs directory '{docsDirectory}'.");
        }

        return docsDirectory;
    }

    public static TheoryData<string, string> AllJsonSchemaFiles()
    {
        var files = new TheoryData<string, string>();

        foreach (var file in Directory.EnumerateFiles(DocsDirectory, "*.json", SearchOption.AllDirectories))
        {
            using var doc = LoadJsonDocument(file);
            if (!doc.RootElement.TryGetProperty("$schema", out var schemaProperty))
            {
                continue;
            }

            var schemaUri = schemaProperty.GetString();
            if (!string.Equals(schemaUri, SupportedMetaschemaUri.OriginalString, StringComparison.Ordinal))
            {
                continue;
            }

            files.Add(file.Replace('\\', '/'), Path.GetRelativePath(RepoRoot, file).Replace('\\', '/'));
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"No JSON Schema files declaring supported metaschema '{SupportedMetaschemaUri.OriginalString}' were found under '{DocsDirectory}'.");
        }

        return files;
    }

    [Theory]
    [MemberData(nameof(AllJsonSchemaFiles))]
    public void JsonSchemaIsValidAgainstMetaschema(string filePath, string relPath)
    {
        using var jsonDoc = LoadJsonDocument(filePath);

        var result = SupportedMetaschema.Evaluate(jsonDoc.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        if (!result.IsValid)
        {
            var errors = CollectErrorMessages(result);
            Assert.Fail($"{relPath} failed metaschema validation ({SupportedMetaschemaUri.OriginalString}):\n{string.Join("\n", errors)}");
        }
    }

    private static JsonDocument LoadJsonDocument(string filePath)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(filePath));
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Failed to read JSON file '{Path.GetRelativePath(RepoRoot, filePath).Replace('\\', '/')}'.", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Failed to read JSON file '{Path.GetRelativePath(RepoRoot, filePath).Replace('\\', '/')}'.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse JSON file '{Path.GetRelativePath(RepoRoot, filePath).Replace('\\', '/')}'.", ex);
        }
    }

    private static IReadOnlyList<string> CollectErrorMessages(EvaluationResults result)
    {
        var messages = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        CollectErrorMessages(result, messages, seen);

        if (messages.Count == 0)
        {
            messages.Add("  <root>: metaschema validation failed but no detailed errors were reported.");
        }

        return messages;
    }

    private static void CollectErrorMessages(EvaluationResults result, List<string> messages, HashSet<string> seen)
    {
        if (result.Errors is not null)
        {
            var instanceLocation = string.IsNullOrEmpty(result.InstanceLocation.ToString())
                ? "<root>"
                : result.InstanceLocation.ToString();

            foreach (var error in result.Errors)
            {
                var message = $"  {instanceLocation}: {error.Key} — {error.Value}";
                if (seen.Add(message))
                {
                    messages.Add(message);
                }
            }
        }

        if (result.Details is null)
        {
            return;
        }

        foreach (var detail in result.Details)
        {
            CollectErrorMessages(detail, messages, seen);
        }
    }
}
