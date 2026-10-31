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
    /// Per-engine expectations: engine name to <c>pass</c>, <c>fail</c> or <c>skip</c>. Engine names are
    /// open-ended strings (<c>battlescribe</c>, <c>newrecruit-ui</c>, an adapter's own). An engine the map does
    /// not cover, by its own name or its family's, is expected to pass. Read it through
    /// <see cref="ExpectationFor"/>, never by key: an entry for a family covers the family's UI driver.
    /// </summary>
    public Dictionary<string, string>? Engines { get; set; }

    /// <summary>
    /// What the spec expects of <paramref name="engine"/>: its own entry when the spec declares one, else its
    /// family's (<see cref="EngineIdentity.KeyIn{T}"/>), else <see cref="EngineExpectation.Pass"/>. A UI
    /// driver inherits <c>battlescribe: skip</c> and can still say <c>battlescribe-ui: pass</c>.
    /// </summary>
    public EngineExpectation ExpectationFor(EngineIdentity engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return Engines is not null && Engines.TryGetValue(DeclaringKey(engine), out var expectation)
            ? Parse(expectation)
            : EngineExpectation.Pass;
    }

    /// <summary>
    /// The <c>engines:</c> key <see cref="ExpectationFor"/> reads for <paramref name="engine"/> — the name a
    /// message cites when it says why a spec was skipped (<c>the spec says engines: {battlescribe: skip}</c>).
    /// </summary>
    public string DeclaringKey(EngineIdentity engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return engine.KeyIn(Engines);
    }

    /// <summary>Whether the spec runs on <paramref name="engine"/> at all: anything but <see cref="EngineExpectation.Skip"/>.</summary>
    public bool IsApplicableTo(EngineIdentity engine) => ExpectationFor(engine) != EngineExpectation.Skip;

    /// <summary>Whether the spec is expected to fail on <paramref name="engine"/>.</summary>
    public bool IsExpectedToFail(EngineIdentity engine) => ExpectationFor(engine) == EngineExpectation.Fail;

    /// <summary>
    /// An <c>engines:</c> value as an expectation. Case-insensitive, as the runners always compared it; a value
    /// that is none of the three reads as <c>pass</c>, and the spec lint refuses it before any runner sees it.
    /// </summary>
    private static EngineExpectation Parse(string expectation)
        => string.Equals(expectation, "skip", StringComparison.OrdinalIgnoreCase) ? EngineExpectation.Skip
            : string.Equals(expectation, "fail", StringComparison.OrdinalIgnoreCase) ? EngineExpectation.Fail
            : EngineExpectation.Pass;
}
