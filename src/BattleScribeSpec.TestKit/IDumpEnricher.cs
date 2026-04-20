namespace BattleScribeSpec;

/// <summary>
/// Optional interface for engines to provide extra data during state dumps.
/// Implementations return key-value pairs that are appended to the dump output.
/// </summary>
public interface IDumpEnricher
{
    /// <summary>
    /// Return engine-specific enrichment data for the current state.
    /// Keys are section names, values are the content to display.
    /// Called after <see cref="IRosterEngine.GetRosterState"/> during a dump.
    /// </summary>
    Dictionary<string, object?> EnrichDump(DumpContext context);
}

/// <summary>
/// Context passed to <see cref="IDumpEnricher.EnrichDump"/>.
/// </summary>
public record DumpContext(
    RosterState State,
    IReadOnlyList<ValidationErrorState> Errors);
