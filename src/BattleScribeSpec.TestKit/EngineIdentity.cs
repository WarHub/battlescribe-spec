namespace BattleScribeSpec;

/// <summary>
/// The engine a spec is read for: the name specs address it by (<see cref="Name"/>) and the engine family
/// it belongs to (<see cref="Family"/>), whose declarations it inherits.
/// </summary>
/// <remarks>
/// <para>
/// <b>One rule for every per-engine lookup, in every runner and every domain: the engine's own entry, else
/// its family's.</b> A spec's <c>engines:</c> map (<c>pass</c>/<c>fail</c>/<c>skip</c>), the per-engine
/// overrides on <c>expectedState</c>, <c>expectFailure</c>, <c>expectedFile</c> and action inputs, a step's
/// <c>skipEngines</c>, and the <c>expectedFile</c> side-file tiers all resolve through this type. An entry for
/// <c>battlescribe</c> covers <c>battlescribe-ui</c> unless the spec names <c>battlescribe-ui</c> itself, and
/// then the driver's own entry wins.
/// </para>
/// <para>
/// <b>Why it is a type and not two strings.</b> Every runner used to take a name — some the driver's, some
/// the family's — and apply its own rule to it. The gamedata lanes, <c>bs-spec run</c>, <c>run --all</c>,
/// <c>verify</c> and several NR lanes looked up the exact name alone; the roster lanes and every scenario
/// run fell back to the family; <c>run --all</c> handed the gamedata runner the family name where the lanes
/// handed it the driver's. So whether <c>battlescribe: skip</c> covered <c>battlescribe-ui</c> depended on
/// which runner read the spec, and specs coped by restating the family's entry under the driver's name
/// (#573). A lookup now takes an <see cref="EngineIdentity"/>, so there is no exact-name form to call by
/// accident, and the one place the rule is written is <see cref="KeyIn{T}"/>.
/// </para>
/// </remarks>
public sealed record EngineIdentity
{
    /// <param name="name">The name specs address the engine by — <c>battlescribe-ui</c>, not <c>battlescribe</c>.</param>
    /// <param name="family">
    /// The engine whose declarations apply where the spec names none for <paramref name="name"/>.
    /// <see cref="Of"/> derives it; state it only when the family is not the name's own
    /// (<c>bs-spec run --all --assertion-engine</c>).
    /// </param>
    public EngineIdentity(string name, string family)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        Name = name;
        Family = family;
    }

    /// <summary>The name specs address the engine by: <c>battlescribe-ui</c>.</summary>
    public string Name { get; }

    /// <summary>
    /// The engine family, named after its base engine: <c>battlescribe</c> for both <c>battlescribe</c> and
    /// <c>battlescribe-ui</c>. Its entries apply wherever the spec has none for <see cref="Name"/>.
    /// </summary>
    public string Family { get; }

    /// <summary>The identity of the engine named <paramref name="name"/>, in the family <see cref="FamilyOf"/> names.</summary>
    public static EngineIdentity Of(string name) => new(name, FamilyOf(name));

    /// <summary>
    /// The family an engine name belongs to: the name with a trailing <c>-ui</c> removed. A UI driver drives
    /// its family's engine through the application's own interface, so it is held to what the family's
    /// engine is held to unless a spec says otherwise. The one definition of an engine family.
    /// </summary>
    public static string FamilyOf(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.EndsWith(UiSuffix, StringComparison.Ordinal) && name.Length > UiSuffix.Length
            ? name[..^UiSuffix.Length]
            : name;
    }

    private const string UiSuffix = "-ui";

    /// <summary>
    /// What a message calls an engine that has no identity — an anonymous <c>exec:</c>/<c>dotnet:</c>
    /// adapter. Never a key: a runner given no identity applies no engine's declarations.
    /// </summary>
    public const string Unnamed = "(unnamed engine)";

    /// <summary>Whether this engine is a driver of another engine's family — whether it has a family entry to inherit.</summary>
    public bool InheritsFromFamily => !string.Equals(Name, Family, StringComparison.Ordinal);

    /// <summary>
    /// The key whose entry in a per-engine map applies to this engine: <see cref="Name"/> when the map has an
    /// entry for it, else <see cref="Family"/>. Most specific first. Whether the family has an entry is the
    /// caller's lookup: the key is returned either way, and a key the map lacks means "no entry applies".
    /// </summary>
    public string KeyIn<T>(IReadOnlyDictionary<string, T>? entries)
        => entries is not null && entries.ContainsKey(Name) ? Name : Family;

    /// <summary>The names this engine answers to, own first: what a list such as <c>skipEngines</c> is matched against.</summary>
    public IReadOnlyList<string> Names => InheritsFromFamily ? [Name, Family] : [Name];

    /// <summary><see cref="Name"/>: what a message calls the engine, and what a spec would write to address it.</summary>
    public override string ToString() => Name;
}

/// <summary>What a spec's <c>engines:</c> map says about one engine (<see cref="SpecFileBase.ExpectationFor"/>).</summary>
public enum EngineExpectation
{
    /// <summary>Expected to pass — the default for an engine the map does not cover.</summary>
    Pass,

    /// <summary>Expected to fail: the spec runs, and passing is reported as an unexpected pass.</summary>
    Fail,

    /// <summary>Not run on this engine at all.</summary>
    Skip,
}
