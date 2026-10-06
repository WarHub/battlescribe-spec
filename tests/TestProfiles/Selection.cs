namespace BattleScribeSpec.Tests.Profiles;

/// <summary>
/// Which tests of an assembly a profile runs. Each selection renders one canonical test-case filter
/// (VSTest's <c>TestCaseFilter</c> syntax, which MTP's <c>--filter</c> also takes) and states which
/// engine lanes it <see cref="Claims"/>.
/// </summary>
/// <remarks>
/// <para>
/// A filter is a conjunction of groups, and a group is one clause or a parenthesised disjunction of
/// clauses: <c>(Engine=BsRoster|Engine=BsGameData)&amp;DisplayName~kitchen-sink</c>. Every clause is
/// <c>Property Operator Value</c> with an explicit operator. That matters because a bare word is also a
/// valid filter — VSTest reads it as <c>FullyQualifiedName~word</c> — so a typo in a hand-written
/// filter does not fail, it silently selects something else. The registry lint checks each clause
/// with <see cref="ClausesOf"/>, and the values of <c>Engine</c> and <c>Category</c>
/// against the traits the assembly actually carries.
/// </para>
/// <para>
/// <b>Claims.</b> A selection claims the engine lanes it exists to run, per assembly — every engine
/// lane lives in <see cref="EngineLanes.Assembly"/>. <see cref="Engines"/> claims those engines;
/// <see cref="AllExcept"/> and <see cref="PrePush"/> claim the complement of what they exclude;
/// <see cref="Raw"/> claims exactly what it declares; <see cref="Whole"/> claims nothing. A
/// <see cref="Raw"/> filter that also reaches some lane's own tests without existing to run them says
/// so in <see cref="Incidental"/>, with the reason; the registry lint evaluates every filter against
/// the traits of each lane's classes and fails on a lane that is reached but neither claimed nor
/// declared incidental, and on a claimed lane the filter cannot reach.
/// </para>
/// </remarks>
internal abstract record Selection
{
    /// <summary>The canonical filter, or <see langword="null"/> when the whole assembly runs.</summary>
    public abstract string? Filter { get; }

    /// <summary>The engine lanes this selection exists to run, in registry order.</summary>
    public abstract IReadOnlyList<string> Claims { get; }

    /// <summary>
    /// Engine lanes whose own tests the filter also selects without existing to run them, each with
    /// the reason. Only <see cref="Raw"/> sets it: every other selection is built from the
    /// <c>Engine</c> clauses its claims come from.
    /// </summary>
    public IReadOnlyList<(string Engine, string Why)> Incidental { get; private init; } = [];

    /// <summary>The extra clauses ANDed onto the selection.</summary>
    public IReadOnlyList<string> Clauses { get; init; } = [];

    /// <summary>This selection, narrowed by one more clause.</summary>
    /// <param name="clause">
    /// One <c>Property Operator Value</c>. A compound one is refused: the clauses are appended
    /// unparenthesised, so <c>Where("Category=X|Category=Y")</c> would render
    /// <c>…&amp;Category=X|Category=Y</c>, which VSTest reads as <c>(…&amp;Category=X)|Category=Y</c> — a
    /// broader selection than the one written, and one the clause lint cannot see, because each piece
    /// it splits out is well-formed.
    /// </param>
    public Selection Where(string clause)
    {
        if (clause.IndexOfAny(['&', '|', '(', ')']) >= 0)
        {
            throw new ArgumentException(
                $"Where takes one Property Operator Value clause, not '{clause}': clauses are ANDed on unparenthesised, so a "
                + "'|' would split the whole filter. Write the disjunction as its own selection (Engines(…) or Raw(…)).",
                nameof(clause));
        }

        return this with { Clauses = [.. Clauses, clause] };
    }

    /// <summary>The tests of these engine lanes.</summary>
    public static Selection Engines(params string[] engines) => new EnginesSelection(engines);

    /// <summary>Every test except those of these engine lanes — a deny-list, stated verbatim.</summary>
    public static Selection AllExcept(params string[] engines) => new AllExceptSelection(engines);

    /// <summary>
    /// The pre-push gate's selection: every engine lane as much as its <see cref="EngineLane.InPrePush"/>
    /// says — a <see cref="PrePushPart.None"/> lane excluded, a <see cref="PrePushPart.KitchenSink"/> lane
    /// without its <see cref="EngineLane.OtherSpecs"/> test. Derived from <see cref="EngineLanes.All"/>, so
    /// the filter and the decisions recorded there are one record.
    /// </summary>
    public static Selection PrePush() =>
        EngineLanes.All.Where(static l => l.InPrePush == PrePushPart.KitchenSink).Aggregate(
            AllExcept([.. EngineLanes.All.Where(static l => l.InPrePush == PrePushPart.None).Select(static l => l.Trait)]),
            static (selection, lane) => selection.Where(WithoutOtherSpecs(lane)));

    /// <summary>
    /// What a smoke profile runs of a split aggregate lane: the lane's tests without its
    /// <see cref="EngineLane.OtherSpecs"/> test — its kitchen-sink half, and its contract checks with it.
    /// </summary>
    public static Selection KitchenSink(string engine) =>
        Engines(engine).Where(WithoutOtherSpecs(EngineLanes.Find(engine)
            ?? throw new ArgumentException($"'{engine}' is not an engine lane.", nameof(engine))));

    private static string WithoutOtherSpecs(EngineLane lane) =>
        $"FullyQualifiedName!={lane.OtherSpecs ?? throw new ArgumentException($"{lane.Trait} is not split into KitchenSink and OtherSpecs (EngineLane.OtherSpecs).", nameof(lane))}";

    /// <summary>A hand-written filter, claiming exactly the engines it lists.</summary>
    /// <param name="expression">The filter, in the clause grammar described on <see cref="Selection"/>.</param>
    /// <param name="claims">
    /// The engine lanes the filter exists to run. Required, so a filter that selects an engine says
    /// so; a filter over categories claims nothing.
    /// </param>
    /// <param name="incidental">
    /// Engine lanes whose own tests the filter reaches anyway, each with the reason (see
    /// <see cref="Incidental"/>).
    /// </param>
    public static Selection Raw(string expression, string[] claims, (string Engine, string Why)[]? incidental = null) =>
        new RawSelection(expression, claims) { Incidental = incidental ?? [] };

    /// <summary>The whole assembly, unfiltered.</summary>
    public static Selection Whole { get; } = new WholeSelection();

    /// <summary>
    /// The clauses of a filter: split at <c>&amp;</c> and <c>|</c>, parentheses dropped. Each one should
    /// be <c>Property Operator Value</c>.
    /// </summary>
    public static IReadOnlyList<string> ClausesOf(string filter) =>
        [.. filter.Split(['&', '|'], StringSplitOptions.TrimEntries).Select(static c => c.Trim('(', ')', ' '))];

    /// <summary>The filter body before <see cref="Clauses"/> are appended.</summary>
    protected abstract string? Body { get; }

    /// <summary>The body with every clause ANDed on; a body containing <c>|</c> is parenthesised first.</summary>
    protected string? Render()
    {
        var parts = new List<string>();
        if (Body is { Length: > 0 } body)
        {
            parts.Add(Clauses.Count > 0 && body.Contains('|', StringComparison.Ordinal) && !IsParenthesised(body) ? $"({body})" : body);
        }

        parts.AddRange(Clauses);
        return parts.Count == 0 ? null : string.Join("&", parts);
    }

    private static bool IsParenthesised(string s) => s.StartsWith('(') && s.EndsWith(')') && s.IndexOf(')', StringComparison.Ordinal) == s.Length - 1;

    private sealed record EnginesSelection(IReadOnlyList<string> Lanes) : Selection
    {
        protected override string? Body => Lanes.Count == 1
            ? $"Engine={Lanes[0]}"
            : $"({string.Join("|", Lanes.Select(static e => $"Engine={e}"))})";

        public override string? Filter => Render();

        public override IReadOnlyList<string> Claims => Lanes;
    }

    private sealed record AllExceptSelection(IReadOnlyList<string> Excluded) : Selection
    {
        protected override string? Body => string.Join("&", Excluded.Select(static e => $"Engine!={e}"));

        public override string? Filter => Render();

        public override IReadOnlyList<string> Claims =>
            [.. EngineLanes.All.Select(static l => l.Trait).Where(t => !Excluded.Contains(t, StringComparer.Ordinal))];
    }

    private sealed record RawSelection(string Expression, IReadOnlyList<string> Declared) : Selection
    {
        protected override string? Body => Expression;

        public override string? Filter => Render();

        public override IReadOnlyList<string> Claims => Declared;
    }

    private sealed record WholeSelection : Selection
    {
        protected override string? Body => null;

        public override string? Filter => Render();

        public override IReadOnlyList<string> Claims => [];
    }
}
