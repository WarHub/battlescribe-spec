using System.Text.Json;

namespace BattleScribeSpec;

/// <summary>
/// Tracks expected failures per engine. Specs listed as expected failures
/// are still run but their failure doesn't cause a test suite failure.
/// This enables tracking NR conformance progress over time.
///
/// Expected failures are stored in a JSON file per engine:
///   specs/expected-failures/{engine}.json
/// </summary>
public sealed class ExpectedFailures
{
    private readonly Dictionary<string, ExpectedFailureEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public string Engine { get; }
    public IReadOnlyDictionary<string, ExpectedFailureEntry> Entries => _entries;

    public ExpectedFailures(string engine)
    {
        Engine = engine;
    }

    /// <summary>
    /// Check if a spec is expected to fail for this engine.
    /// </summary>
    public bool IsExpectedFailure(string specId) => _entries.ContainsKey(specId);

    /// <summary>
    /// Get the expected failure entry for a spec, or null if not expected to fail.
    /// </summary>
    public ExpectedFailureEntry? GetEntry(string specId) =>
        _entries.TryGetValue(specId, out var entry) ? entry : null;

    /// <summary>
    /// Add or update an expected failure entry.
    /// </summary>
    public void Add(string specId, string reason, string? category = null)
    {
        _entries[specId] = new ExpectedFailureEntry(specId, reason, category);
    }

    /// <summary>
    /// Remove an expected failure (spec is now passing).
    /// </summary>
    public bool Remove(string specId) => _entries.Remove(specId);

    /// <summary>
    /// Load expected failures from a JSON file.
    /// Returns empty set if file doesn't exist.
    /// </summary>
    public static ExpectedFailures Load(string engine, string? specsDir = null)
    {
        var result = new ExpectedFailures(engine);
        specsDir ??= SpecLoader.FindSpecsDirectory();
        if (specsDir is null) return result;

        var path = Path.Combine(specsDir, "expected-failures", $"{engine}.json");
        if (!File.Exists(path)) return result;

        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<ExpectedFailureEntry>>(json, JsonOptions);
        if (entries is null) return result;

        foreach (var entry in entries)
            result._entries[entry.SpecId] = entry;

        return result;
    }

    /// <summary>
    /// Save expected failures to a JSON file.
    /// </summary>
    public void Save(string? specsDir = null)
    {
        specsDir ??= SpecLoader.FindSpecsDirectory()
            ?? throw new InvalidOperationException("Cannot find specs directory");

        var dir = Path.Combine(specsDir, "expected-failures");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"{Engine}.json");
        var entries = _entries.Values.OrderBy(e => e.SpecId).ToList();
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Classify a spec result considering expected failures.
    /// Checks both plain specId and category/specId formats.
    /// </summary>
    public SpecResultClassification Classify(SpecResult result)
    {
        var fullId = string.IsNullOrEmpty(result.Category) ? result.SpecId : $"{result.Category}/{result.SpecId}";
        var isExpected = IsExpectedFailure(result.SpecId) || IsExpectedFailure(fullId);

        if (result.Passed && !isExpected)
            return SpecResultClassification.Passed;

        if (result.Passed && isExpected)
            return SpecResultClassification.UnexpectedPass; // Spec improved! Remove from expected failures

        if (!result.Passed && isExpected)
            return SpecResultClassification.ExpectedFailure; // Known issue, don't fail the suite

        return SpecResultClassification.Failed; // Real failure
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

/// <summary>
/// An expected failure entry for a specific spec.
/// </summary>
public sealed record ExpectedFailureEntry(
    string SpecId,
    string Reason,
    string? Category = null);

/// <summary>
/// Classification of a spec result considering expected failures.
/// </summary>
public enum SpecResultClassification
{
    /// <summary>Spec passed as expected.</summary>
    Passed,

    /// <summary>Spec failed and was NOT in expected failures — real regression.</summary>
    Failed,

    /// <summary>Spec failed but was in expected failures — known issue.</summary>
    ExpectedFailure,

    /// <summary>Spec passed but was in expected failures — progress! Remove from list.</summary>
    UnexpectedPass
}
