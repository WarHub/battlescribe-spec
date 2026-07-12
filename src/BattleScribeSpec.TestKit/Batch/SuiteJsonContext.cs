using System.Text.Json.Serialization;

namespace BattleScribeSpec.Batch;

/// <summary>
/// Source-generated JSON context for spec-suite report output.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(JsonRunReport))]
[JsonSerializable(typeof(ConformanceReport))]
public partial class SuiteJsonContext : JsonSerializerContext;

/// <summary>
/// Typed model for the JSON output format (replaces anonymous type for AOT compatibility).
/// </summary>
public sealed class JsonRunReport
{
    [JsonPropertyName("engine")]
    public string? Engine { get; init; }

    [JsonPropertyName("passed")]
    public int Passed { get; init; }

    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("elapsedSeconds")]
    public double ElapsedSeconds { get; init; }

    [JsonPropertyName("specs")]
    public List<JsonSpecEntry> Specs { get; init; } = [];
}

public sealed class JsonSpecEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("category")]
    public string Category { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("passed")]
    public bool Passed { get; init; }

    [JsonPropertyName("failures")]
    public IReadOnlyList<string> Failures { get; init; } = [];

    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Wall-clock time spent running this spec, in milliseconds.</summary>
    [JsonPropertyName("durationMs")]
    public double DurationMs { get; init; }
}
