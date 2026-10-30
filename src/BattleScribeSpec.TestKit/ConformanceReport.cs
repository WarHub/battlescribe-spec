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
/// <para>
/// <paramref name="Domain"/> is the spec's tree under <c>specs/</c> — <c>roster</c>, <c>gamedata</c> or
/// <c>scenario</c> — set by the spec-suite runner for every entry it reports. <c>category/id</c> alone does not
/// name a spec across domains: the trees share category names (<c>cost</c>, <c>constraint</c>, …), and
/// nothing keeps two of them from holding the same id. See <see cref="QualifiedId"/>.
/// </para>
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
    int AdapterDeaths = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Domain = null)
{
    /// <summary>
    /// The spec's name across every domain of a run: <c>domain/category/id</c> (<c>category/id</c> when the
    /// entry has no domain). What a whole-run map is keyed by — <c>bs-spec compare</c>'s verdicts, the JSON
    /// output's adapter-death lookup — because keyed by <c>category/id</c>, a roster and a scenario spec of one
    /// name are one key, and building the map throws.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string QualifiedId => QualifiedName(Domain, Category, SpecId);

    /// <summary>See <see cref="QualifiedId"/>.</summary>
    public static string QualifiedName(string? domain, string category, string specId) =>
        domain is null ? $"{category}/{specId}" : $"{domain}/{category}/{specId}";
}
