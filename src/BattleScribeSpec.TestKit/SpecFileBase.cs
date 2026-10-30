using YamlDotNet.Serialization;

namespace BattleScribeSpec;

/// <summary>
/// Base class for all spec file types (roster, gamedata).
/// Contains shared metadata fields: id, category, description, tags, engines.
/// </summary>
public abstract class SpecFileBase
{
    public required string Id { get; set; }

    /// <summary>
    /// Absolute path the spec was loaded from. Set by <see cref="SpecLoader"/>; not part of the YAML.
    /// Used to resolve side-files (e.g. export snapshots) next to the spec. Null when loaded from a
    /// string (inline-only — side-file references then error clearly).
    /// </summary>
    [YamlIgnore]
    public string? SourcePath { get; set; }

    /// <summary>Directory containing the spec file, or null when loaded from a string.</summary>
    [YamlIgnore]
    public string? SourceDirectory => SourcePath is null ? null : System.IO.Path.GetDirectoryName(SourcePath);

    public required string Category { get; set; }

    public required string Description { get; set; }

    public List<string>? Tags { get; set; }

    /// <summary>
    /// Per-engine expectations. Null means all engines expected to pass.
    /// Map of engine name to expectation: "pass" (default), "fail", or "skip".
    /// Engine names are open-ended strings (e.g. "battlescribe", "newrecruit").
    /// Unlisted engines are expected to pass.
    /// </summary>
    public Dictionary<string, string>? Engines { get; set; }

    /// <summary>
    /// Check if this spec should run on the given engine (not "skip").
    /// Null/empty engines means applicable to all engines.
    /// </summary>
    public bool IsApplicableTo(string engineName)
        => !ShouldSkip(engineName);

    /// <summary>
    /// Check if this spec should be skipped entirely for the given engine.
    /// </summary>
    public bool ShouldSkip(string engineName)
    {
        if (Engines is null || Engines.Count == 0)
        {
            return false;
        }

        return Engines.TryGetValue(engineName, out var expectation)
            && string.Equals(expectation, "skip", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if this spec is expected to fail on the given engine.
    /// </summary>
    public bool IsExpectedToFail(string engineName)
    {
        if (Engines is null || Engines.Count == 0)
        {
            return false;
        }

        return Engines.TryGetValue(engineName, out var expectation)
            && string.Equals(expectation, "fail", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get the expectation for a given engine: "pass", "fail", or "skip".
    /// Defaults to "pass" if engine is not listed or engines is null.
    /// </summary>
    public string GetExpectation(string engineName)
    {
        if (Engines is null || Engines.Count == 0)
        {
            return "pass";
        }

        return Engines.TryGetValue(engineName, out var expectation) ? expectation : "pass";
    }

    /// <summary>
    /// The expectation for <paramref name="engineName"/>, resolved most-specific-first: its own entry when
    /// the spec declares one, else <paramref name="baseEngineName"/>'s (<c>battlescribe-ui</c> →
    /// <c>battlescribe</c>). A UI driver inherits its base engine's declarations by default and can still
    /// state its own — the rule the roster lanes and every scenario run apply.
    /// </summary>
    public string GetExpectation(string engineName, string? baseEngineName)
        => baseEngineName is not null && Engines?.ContainsKey(engineName) != true
            ? GetExpectation(baseEngineName)
            : GetExpectation(engineName);
}
