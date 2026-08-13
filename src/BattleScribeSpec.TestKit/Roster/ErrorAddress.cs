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

    /// <summary>The token that marks an <c>on:</c> value as node-addressed rather than legacy.</summary>
    public const string ExpressionMarker = "${{";

    private ErrorAddress(string type, string? nodeId, string? legacyEntryId, string raw)
    {
        Type = type;
        NodeId = nodeId;
        LegacyEntryId = legacyEntryId;
        Raw = raw;
    }

    /// <summary>The node kind: <c>roster</c>, <c>force</c>, <c>category</c>, <c>selection</c>, <c>group</c>.</summary>
    public string Type { get; }

    /// <summary>The resolved runtime node id, or null for a bare kind-only address.</summary>
    public string? NodeId { get; }

    /// <summary>
    /// The CATALOGUE entry id of a not-yet-migrated assertion. Non-null only on the transient legacy
    /// branch — see <see cref="IsLegacyEntryAddressed"/>.
    /// </summary>
    public string? LegacyEntryId { get; }

    /// <summary>The <c>on:</c> value as the spec wrote it, before resolution.</summary>
    public string Raw { get; }

    /// <summary>
    /// TRANSIENT — true when this <c>on:</c> still names a catalogue entry instead of a node.
    /// <para>
    /// The corpus migrates per category in #424, and until it is finished both forms have to be
    /// accepted at once or no batch is bisectable. The discriminator is the presence of a
    /// <c>${{</c> in the id token, which is exact rather than heuristic: a node id can only ever be
    /// written as a step reference, and a catalogue entry id never is. <b>Delete this property, the
    /// branch in <see cref="Matches"/>, and the branch in <see cref="Parse"/> together when #424
    /// closes.</b>
    /// </para>
    /// </summary>
    public bool IsLegacyEntryAddressed => LegacyEntryId is not null;

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
            return new ErrorAddress(raw, nodeId: null, legacyEntryId: null, raw);
        }

        var type = raw[..spaceIdx];
        var id = raw[(spaceIdx + 1)..].Trim();

        // TRANSIENT (#424): no expression means the id is a catalogue entry id, not a node id.
        if (!id.Contains(ExpressionMarker, StringComparison.Ordinal))
        {
            return new ErrorAddress(type, nodeId: null, legacyEntryId: id, raw);
        }

        return new ErrorAddress(type, resolve?.Invoke(id) ?? id, legacyEntryId: null, raw);
    }

    /// <summary>Does <paramref name="error"/> sit on the node this address names?</summary>
    public bool Matches(ValidationErrorState error)
    {
        // TRANSIENT (#424): the pre-#423 comparison, unchanged — the normalized post-placement
        // attribution, by catalogue entry id.
        if (LegacyEntryId is { } entryId)
        {
            return error.OwnerType == Type && error.OwnerEntryId == entryId;
        }

        return error.RaisedOnType == Type && (NodeId is null || error.RaisedOnId == NodeId);
    }

    /// <summary>
    /// The address as a failure message shows it: what the spec wrote, plus what it resolved to when
    /// those differ — a bare <c>${{ steps.… }}</c> in the message would say nothing about which node
    /// was looked for.
    /// </summary>
    public override string ToString() => NodeId is { } id && !Raw.EndsWith(id, StringComparison.Ordinal)
        ? $"{Raw} → {Type} {id}"
        : Raw;
}
