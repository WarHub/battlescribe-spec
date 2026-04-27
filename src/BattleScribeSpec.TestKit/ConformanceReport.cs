namespace BattleScribeSpec;

public record ConformanceReport(
    string Engine,
    DateTime GeneratedAt,
    int TotalSpecs,
    int Passed,
    int Failed,
    int Skipped,
    double PassRate,
    List<SpecResultSummary> Results,
    string? AssertionEngine = null);

public record SpecResultSummary(
    string SpecId,
    string Category,
    string Description,
    string Status,
    List<string> Failures,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    List<string>? Tags = null);
