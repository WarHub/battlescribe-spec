using System.Text.Json;
using Json.Schema;
using Yaml2JsonNode;
using YamlDotNet.RepresentationModel;

namespace BattleScribeSpec.Tests;

/// <summary>
/// The spec JSON Schemas in <c>docs/</c>, one per spec domain plus the definitions they share, loaded the way an
/// editor loads them: from the files, with relative <c>$ref</c>s between them resolved against each file's own
/// location.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why four files and no <c>$id</c>.</b> A scenario spec is a GameData setup, GameData steps and roster steps,
/// so <c>scenario-spec-schema.json</c> reuses the other two schemas' definitions by <c>$ref</c> rather than copying
/// them; the setup data both domains share lives in <c>spec-common-schema.json</c>. The files carry no <c>$id</c>,
/// so a relative <c>$ref</c> resolves against the file it is written in — which is what the VS Code YAML extension
/// does with the files <c>.vscode/settings.json</c> maps. An <c>$id</c> would move that base to a URL nothing serves.
/// </para>
/// <para>
/// <b>Why the base is not the file's URI.</b> This loader reproduces that resolution with each file at the same
/// name under one base, <see cref="BaseUri"/>, rather than at its <c>file:</c> URI: resolving a relative reference
/// against a <c>file:</c> base, .NET's <see cref="Uri"/> escapes the <c>#</c> into the path
/// (<c>gamedata-spec-schema.json%23/$defs/setup</c>), and JsonSchema.Net then applies the referenced file's
/// <em>root</em> — silently. Every cross-file reference landed on a root that way when this was first written: the
/// scenario setup was checked as a whole GameData spec, and every setup type as <c>spec-common-schema.json</c>'s
/// root, which accepts anything. The broken samples in <see cref="SpecSchemaTests"/> each reach through a
/// cross-file reference, so a loader that loses the fragment again fails them.
/// </para>
/// <para>
/// JsonSchema.Net resolves references when a schema is built, so the files are built in dependency order, into a
/// registry of their own that fetches nothing.
/// </para>
/// </remarks>
internal static class SpecSchemas
{
    public const string CommonFile = "spec-common-schema.json";
    public const string RosterFile = "roster-spec-schema.json";
    public const string GameDataFile = "gamedata-spec-schema.json";
    public const string ScenarioFile = "scenario-spec-schema.json";

    /// <summary>Each file refers only to files before it.</summary>
    private static readonly string[] Files = [CommonFile, RosterFile, GameDataFile, ScenarioFile];

    /// <summary>
    /// Where the schemas sit for reference resolution: <c>docs/</c> under a host that cannot exist (RFC 2606
    /// reserves <c>.invalid</c>), so nothing is ever fetched from it. Only the relative layout matters.
    /// </summary>
    private static readonly Uri BaseUri = new("https://battlescribe-spec.invalid/docs/");

    private static readonly Lazy<IReadOnlyDictionary<string, JsonSchema>> Built = new(() =>
    {
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        var built = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        foreach (var file in Files)
        {
            built[file] = JsonSchema.FromFile(PathOf(file), options, new Uri(BaseUri, file));
        }

        return built;
    });

    public static JsonSchema Roster => Built.Value[RosterFile];

    public static JsonSchema GameData => Built.Value[GameDataFile];

    public static JsonSchema Scenario => Built.Value[ScenarioFile];

    /// <summary>The schema file's full path.</summary>
    public static string PathOf(string file) => Path.Combine(TestPaths.Root, "docs", file);

    /// <summary>The schema file as JSON, for reading its definitions rather than validating against it.</summary>
    public static JsonDocument Document(string file) => JsonDocument.Parse(File.ReadAllText(PathOf(file)));

    /// <summary>
    /// What <paramref name="schema"/> finds wrong with the first document of <paramref name="yaml"/>, one line
    /// per error: <c>{instance location}: {keyword}: {message}</c>. Empty when the document is valid.
    /// </summary>
    /// <remarks>
    /// Only the errors that made the document invalid. A flat list of every failing node also reports the
    /// <c>if</c> that did not match and the losing branches of an <c>anyOf</c> that another branch satisfied —
    /// noise at the very locations a broken sample names, which would let a sample "find" its error in a
    /// condition that never failed anything. So the walk descends only through failing nodes, and never into an
    /// <c>if</c>, whose failure is a branch taken rather than an error.
    /// </remarks>
    public static IReadOnlyList<string> Validate(JsonSchema schema, string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0)
        {
            return ["<root>: the YAML stream contains no documents"];
        }

        using var json = JsonDocument.Parse(stream.Documents[0].ToJsonNode()?.ToJsonString() ?? "null");
        var result = schema.Evaluate(json.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        if (result.IsValid)
        {
            return [];
        }

        var errors = new List<string>();
        Collect(result);
        return errors.Count > 0 ? errors : ["<root>: invalid, but the validator reported no detail"];

        void Collect(EvaluationResults node)
        {
            if (node.IsValid || node.EvaluationPath.ToString().EndsWith("/if", StringComparison.Ordinal))
            {
                return;
            }

            var at = node.InstanceLocation.ToString() is { Length: > 0 } location ? location : "<root>";
            errors.AddRange((node.Errors ?? Enumerable.Empty<KeyValuePair<string, string>>()).Select(e => $"{at}: {e.Key}: {e.Value}"));

            foreach (var child in node.Details ?? [])
            {
                Collect(child);
            }
        }
    }
}
