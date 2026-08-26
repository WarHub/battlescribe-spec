using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace BattleScribeSpec;

/// <summary>
/// A step's declaration that its action is expected to be <b>refused by the engine</b> — shared by
/// roster and gamedata steps.
/// <para>
/// This is not <c>expectedState.errors</c>, and keeping the two apart is the reason it exists.
/// <c>errors:</c> asserts the roster's <em>validation list</em>: the engine accepted the operation,
/// the roster exists, and it is merely not legal to field. <c>expectFailure</c> asserts that the
/// operation did not happen at all — the parser rejected the payload, the engine declined the edit.
/// A malformed <c>.ros</c> produces no validation list to assert against, which is exactly why
/// #23, #25 and #268 each rediscovered the gap.
/// </para>
/// <para>
/// Only <see cref="ActionFailureKind.Engine"/> satisfies it. A failure the adapter raised while
/// resolving ids the spec named, a harness fault, and a failure from an adapter that does not
/// classify are all left fatal — see <see cref="ActionFailureKind"/>.
/// </para>
/// </summary>
public sealed class ExpectFailureDef
{
    /// <summary>
    /// Whether this engine is expected to refuse. Null means the default, <c>true</c>.
    /// <para>
    /// The <c>false</c> form exists so a spec can record an engine that <em>accepts</em> a payload
    /// the others reject, as a per-engine override, instead of hiding the divergence behind
    /// <c>skipEngines</c>. A skip says "we did not look"; <c>false</c> says "we looked, and this
    /// engine takes it" — which for a conformance suite is a finding, not an exemption. Same reason
    /// <c>errors:</c> records BattleScribe and NewRecruit raising on different nodes rather than
    /// normalising one into the other.
    /// </para>
    /// </summary>
    public bool? Expected { get; set; }

    /// <summary>
    /// Optional case-insensitive substring the refusal message must contain. Matched against the
    /// <em>engine's</em> message, never the harness framing around it — see
    /// <see cref="ActionFailure.MessageOf"/>.
    /// </summary>
    public string? MessageContains { get; set; }

    /// <summary>
    /// Per-engine overrides. Each value is the same three shapes the field itself takes:
    /// <c>true</c>, <c>false</c>, or a mapping. Not valid inside an override — one level only.
    /// </summary>
    public Dictionary<string, ExpectFailureDef>? Engines { get; set; }

    /// <summary>True unless the spec explicitly said this engine succeeds.</summary>
    public bool IsExpected => Expected ?? true;

    /// <summary>
    /// The effective expectation for an engine: an override's non-null fields replace the base, the
    /// same merge every other <c>ForEngine</c> in the model performs. A consequence worth knowing:
    /// an override cannot <em>widen</em> the base's <see cref="MessageContains"/> back to
    /// unconstrained, because null means "inherit" and not "no constraint".
    /// </summary>
    public ExpectFailureDef ForEngine(string? engineName)
    {
        if (engineName is null || Engines is null || !Engines.TryGetValue(engineName, out var over))
        {
            return this;
        }

        return new ExpectFailureDef
        {
            Expected = over.Expected ?? Expected,
            MessageContains = over.MessageContains ?? MessageContains,
            // Engines is deliberately not propagated — an override is a leaf.
        };
    }
}

/// <summary>
/// Judges an action's outcome against a step's <c>expectFailure</c>. Shared verbatim by
/// <see cref="Roster.RosterRunner"/> and <see cref="GameData.GameDataRunner"/> — the two runners
/// disagree about plenty, but not about what a refusal is.
/// </summary>
public static class ExpectFailure
{
    /// <summary>
    /// Did this exception satisfy the declaration? True only for an engine refusal whose message
    /// carries what the spec said it would.
    /// </summary>
    public static bool IsSatisfiedBy(Exception exception, ExpectFailureDef expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        return ActionFailure.Classify(exception) == ActionFailureKind.Engine
            && (expected.MessageContains is not { Length: > 0 } needle
                || ActionFailure.MessageOf(exception).Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether the failure is one the harness itself caused, and so a genuine harness error.</summary>
    public static bool IsHarnessFault(Exception exception)
        => ActionFailure.Classify(exception) == ActionFailureKind.Harness;

    /// <summary>
    /// Why <paramref name="exception"/> did not satisfy <paramref name="expected"/>. Each branch
    /// names the next move, because the four ways to miss have four different fixes and only one of
    /// them is "the engine changed its message".
    /// </summary>
    public static string Explain(
        Exception exception,
        ExpectFailureDef expected,
        int stepIndex,
        string action,
        string engineLabel)
    {
        ArgumentNullException.ThrowIfNull(expected);

        var message = ActionFailure.MessageOf(exception);
        return ActionFailure.Classify(exception) switch
        {
            ActionFailureKind.Engine =>
                $"Step {stepIndex}: engine '{engineLabel}' refused '{action}' as expected, but the message does " +
                $"not contain \"{expected.MessageContains}\": \"{message}\"",

            ActionFailureKind.Address =>
                $"Step {stepIndex}: '{action}' failed because the adapter could not resolve an id this spec " +
                $"named — not because engine '{engineLabel}' refused it: \"{message}\". expectFailure asserts " +
                "engine refusals only: every engine resolves ids through its own adapter, so an unresolvable " +
                "id fails identically everywhere and asserting one would make a spec typo pass. Fix the id, or " +
                "drop expectFailure from this step.",

            ActionFailureKind.Harness =>
                $"Step {stepIndex}: '{action}' failed with a harness fault ({exception.GetType().Name}), not an " +
                $"engine refusal: \"{message}\". expectFailure never matches a fault.",

            ActionFailureKind.Unsupported =>
                $"Step {stepIndex}: engine '{engineLabel}' does not implement '{action}' at all, so it never " +
                $"reached the input this step expects it to refuse: \"{message}\". A capability gap is not a " +
                "refusal — an engine that cannot try must fail the spec, never pass it. Opt this engine out " +
                $"explicitly: 'skipEngines: [{engineLabel}]' on this step, or 'engines: {{{engineLabel}: skip}}' " +
                "on the spec.",

            _ =>
                $"Step {stepIndex}: engine '{engineLabel}' failed '{action}', but its adapter sent no 'kind' on " +
                $"the action result, so nothing classified the failure: \"{message}\". Send " +
                "\"kind\":\"engine\" for a refusal (see docs/adapter-protocol.md), or opt this engine out explicitly: " +
                $"'skipEngines: [{engineLabel}]' on this step, or 'engines: {{{engineLabel}: skip}}' on the spec.",
        };
    }

    /// <summary>The message for an action that succeeded where the spec said it would be refused.</summary>
    public static string ExplainUnexpectedSuccess(int stepIndex, string action, string engineLabel)
        => $"Step {stepIndex}: expected engine '{engineLabel}' to refuse '{action}', but it succeeded. If this " +
           $"engine legitimately accepts this input, record that rather than skipping it: " +
           $"'expectFailure: {{engines: {{{engineLabel}: false}}}}'.";
}

/// <summary>
/// Reads the three shapes <c>expectFailure</c> accepts — <c>true</c>, <c>false</c>, and a mapping —
/// into one <see cref="ExpectFailureDef"/>.
/// <para>
/// Hand-written rather than delegated back to the deserializer: the spec loader is a
/// <c>StaticDeserializerBuilder</c> over a source-generated context, and re-entering it for the
/// mapping branch of the very type this converter claims is how converters recurse forever. Parsing
/// three keys by hand costs less than the alternative and rejects an unknown key by name, which the
/// generated path would silently ignore.
/// </para>
/// </summary>
public sealed class ExpectFailureYamlConverter : IYamlTypeConverter
{
    /// <inheritdoc/>
    public bool Accepts(Type type) => type == typeof(ExpectFailureDef);

    /// <inheritdoc/>
    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        => Read(parser, isOverride: false);

    /// <summary>Not supported: nothing in this repo writes spec YAML, only reads it.</summary>
    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        => throw new NotSupportedException("expectFailure is read-only; specs are never serialized back to YAML.");

    private static ExpectFailureDef Read(IParser parser, bool isOverride)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            return new ExpectFailureDef
            {
                Expected = ParseBool(scalar, "expectFailure: expected 'true', 'false', or a mapping"),
            };
        }

        var start = parser.Consume<MappingStart>();
        var def = new ExpectFailureDef();

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>();
            switch (key.Value)
            {
                case "expected":
                    def.Expected = ParseBool(parser.Consume<Scalar>(), "expectFailure.expected: expected 'true' or 'false'");
                    break;

                case "messageContains":
                    def.MessageContains = parser.Consume<Scalar>().Value;
                    break;

                case "engines" when isOverride:
                    throw new YamlException(
                        key.Start,
                        key.End,
                        "expectFailure: 'engines' cannot nest inside a per-engine override — one level only.");

                case "engines":
                    def.Engines = ReadEngines(parser);
                    break;

                default:
                    throw new YamlException(
                        key.Start,
                        key.End,
                        $"expectFailure: unknown key '{key.Value}' (expected 'expected', 'messageContains' or 'engines').");
            }
        }

        if (def is { Expected: false, Engines: null, MessageContains: not null })
        {
            throw new YamlException(
                start.Start,
                start.End,
                "expectFailure: 'messageContains' with 'expected: false' asserts a message on an action " +
                "that must succeed. Drop one of the two.");
        }

        return def;
    }

    private static Dictionary<string, ExpectFailureDef> ReadEngines(IParser parser)
    {
        var engines = new Dictionary<string, ExpectFailureDef>(StringComparer.Ordinal);
        parser.Consume<MappingStart>();
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var engineName = parser.Consume<Scalar>();
            if (!engines.TryAdd(engineName.Value, Read(parser, isOverride: true)))
            {
                throw new YamlException(
                    engineName.Start,
                    engineName.End,
                    $"expectFailure.engines: duplicate engine '{engineName.Value}'.");
            }
        }

        return engines;
    }

    private static bool ParseBool(Scalar scalar, string what)
    {
        if (string.Equals(scalar.Value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(scalar.Value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new YamlException(scalar.Start, scalar.End, $"{what}, got '{scalar.Value}'.");
    }
}
