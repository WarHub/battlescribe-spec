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

/// <summary>
/// One spec's report entry. <paramref name="AdapterDeaths"/> counts how many times an adapter
/// process died while the harness's spec-suite runner was attempting this spec: 0 (no death), 1
/// (died once — either rescued by a retry on a fresh process, whose verdict then became
/// <paramref name="Status"/>, or failed immediately because the run's adapter-death cap was already
/// spent), or 2 (the retry also died, so this spec was failed with the adapter-death reason). Never
/// silently 0 when a crash occurred — this is the distinct signal a normal assertion failure doesn't
/// carry, and what <c>bs-spec compare</c> reads to explain a verdict divergence as a flake rather
/// than a conformance regression.
/// </summary>
public record SpecResultSummary(
    string SpecId,
    string Category,
    string Description,
    string Status,
    List<string> Failures,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    List<string>? Tags = null,
    double DurationMs = 0,
    int AdapterDeaths = 0);
