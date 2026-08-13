namespace BattleScribeSpec.Roster;

/// <summary>
/// A parsed <c>on:</c> value — the roster NODE an error assertion names.
/// </summary>
/// <remarks>
/// <para>
/// <b>The contract (#419/#423).</b> <c>on:</c> names the node the engine raised the error on:
/// <see cref="ValidationErrorState.RaisedOnType"/> plus <see cref="ValidationErrorState.RaisedOnId"/>,
/// captured at the moment of capture and never rewritten. Node ids are minted per run on every lane,
/// so the id is written as a <c>${{ steps.… }}</c> reference and resolved before the match; a literal
/// node id is never valid and is not accepted as one.
/// </para>
/// <para>
/// <b>Two kinds have no id to name</b> — <c>roster</c> and <c>group</c>. Neither is a node the state
/// model gives a spec any way to reference (<c>RosterState</c> carries no id at all; a
/// selectionEntryGroup node exists in NewRecruit and nowhere in the state model). Both are therefore
/// written bare, matching on kind alone, which measurement says is never ambiguous: at most one
/// roster-raised and one group-raised error per step across the whole corpus on both lanes.
/// </para>
/// </remarks>
public readonly record struct ErrorAddress
{
    /// <summary>Every node kind an engine reports as a raising node.</summary>
    public static readonly IReadOnlySet<string> KnownTypes =
        new HashSet<string>(StringComparer.Ordinal) { "roster", "force", "category", "selection", "group" };

    /// <summary>
    /// The kinds that carry no addressable id, so an <c>on:</c> naming one is written bare.
    /// </summary>
    public static readonly IReadOnlySet<string> IdLessTypes =
        new HashSet<string>(StringComparer.Ordinal) { "roster", "group" };

    /// <summary>
    /// The token an id-carrying <c>on:</c> must contain. A node id is minted per run, so the only
    /// way to write one is a step reference; a literal second token names a catalogue entry, which
    /// is the pre-#423 form and no longer an address at all. <see cref="IsLiteralId"/> is what
    /// rejects it, in the linter and here, from one definition.
    /// </summary>
    public const string ExpressionMarker = "${{";

    private ErrorAddress(string type, string? nodeId, bool literalId, string raw)
    {
        Type = type;
        NodeId = nodeId;
        IsLiteralId = literalId;
        Raw = raw;
    }

    /// <summary>The node kind: <c>roster</c>, <c>force</c>, <c>category</c>, <c>selection</c>, <c>group</c>.</summary>
    public string Type { get; }

    /// <summary>The resolved runtime node id, or null for a bare kind-only address.</summary>
    public string? NodeId { get; }

    /// <summary>The <c>on:</c> value as the spec wrote it, before resolution.</summary>
    public string Raw { get; }

    /// <summary>
    /// True when the second token is a literal rather than a <c>${{ … }}</c> reference — the
    /// entry-addressed form #419 removed. It is not a node address and cannot become one: a
    /// catalogue entry id names a SET of nodes, and two selections of one entry are indistinguishable
    /// by it. <see cref="Matches"/> never matches such an address, and the linter rejects the spec
    /// before it runs so the failure names the mistake instead of reporting a missing error.
    /// </summary>
    public bool IsLiteralId { get; }

    /// <summary>
    /// Parse an <c>on:</c> value. <paramref name="resolve"/> expands a <c>${{ steps.… }}</c>
    /// reference to this run's minted id; it is only called for a node-addressed value, so a caller
    /// with no step outputs (the linter) can pass null and still see the shape.
    /// </summary>
    public static ErrorAddress Parse(string on, Func<string, string?>? resolve = null)
    {
        var raw = on.Trim();
        var spaceIdx = raw.IndexOf(' ');
        if (spaceIdx < 0)
        {
            // Bare: the kinds with nothing to name. Matches on the raising node's kind.
            return new ErrorAddress(raw, nodeId: null, literalId: false, raw);
        }

        var type = raw[..spaceIdx];
        var id = raw[(spaceIdx + 1)..].Trim();

        if (!id.Contains(ExpressionMarker, StringComparison.Ordinal))
        {
            return new ErrorAddress(type, nodeId: null, literalId: true, raw);
        }

        return new ErrorAddress(type, resolve?.Invoke(id) ?? id, literalId: false, raw);
    }

    /// <summary>Does <paramref name="error"/> sit on the node this address names?</summary>
    public bool Matches(ValidationErrorState error)
        => !IsLiteralId && error.RaisedOnType == Type && (NodeId is null || error.RaisedOnId == NodeId);

    /// <summary>
    /// The address as a failure message shows it: what the spec wrote, plus what it resolved to when
    /// those differ — a bare <c>${{ steps.… }}</c> in the message would say nothing about which node
    /// was looked for.
    /// </summary>
    public override string ToString() => NodeId is { } id && !Raw.EndsWith(id, StringComparison.Ordinal)
        ? $"{Raw} → {Type} {id}"
        : Raw;
}
