namespace BattleScribeSpec;

/// <summary>
/// Why an action failed — the distinction that decides whether a spec is allowed to assert the
/// failure via <c>expectFailure</c>.
/// <para>
/// Three different layers can make an action throw, and until this type existed they all arrived at
/// the runner as the same thing: an exception message string. That is fine while every action
/// failure is fatal, and not fine the moment a spec can assert one — a primitive satisfied by
/// <em>any</em> failure is satisfied by a typo in the spec and by a dead adapter, which is the
/// vacuous-pass defect class removed in #309 and again in <c>ExecuteFileAssertion</c>.
/// </para>
/// </summary>
public enum ActionFailureKind
{
    /// <summary>
    /// The engine had everything it was given and declined — a parser rejecting malformed XML, an
    /// engine refusing an operation its own rules disallow. This is engine behaviour, it is what the
    /// conformance suite exists to compare across engines, and it is the <b>only</b> kind
    /// <c>expectFailure</c> accepts.
    /// </summary>
    Engine,

    /// <summary>
    /// The spec named something that does not exist, and the adapter's own lookup said so before the
    /// engine was asked anything (<see cref="SpecAddressingException"/>). Every engine fails these
    /// identically because every engine resolves ids the same way — through its adapter — so they
    /// measure the harness, not the engine. A spec that could assert one would turn its own typo
    /// into a passing test.
    /// </summary>
    Address,

    /// <summary>
    /// The harness or the transport broke: a timeout, a disposed engine, a null dereference in our
    /// own code. Never engine behaviour, never assertable.
    /// </summary>
    Harness,

    /// <summary>
    /// The engine does not implement this action at all — the <see cref="NotSupportedException"/>
    /// the <see cref="Roster.IRosterEngine"/> defaults throw. A capability gap, not a refusal, and
    /// the difference is the whole of #309: three of the four engines cannot load a roster (#450),
    /// so if "did not support it" satisfied <c>expectFailure</c> they would every one of them pass
    /// #23's malformed-input specs without ever parsing a byte. Opting an engine out is the spec's
    /// job — <c>skipEngines</c>, or <c>engines: {…: skip}</c> — never the harness's, via a kind that
    /// happens to match.
    /// </summary>
    Unsupported,

    /// <summary>
    /// The failure crossed the protocol from an adapter that does not send <c>kind</c>, so nothing
    /// classified it. Treated as not-assertable: an adapter that has not adopted the discriminator
    /// makes a spec's <c>expectFailure</c> <b>fail</b>, with a message naming what to implement,
    /// rather than pass on an unexamined failure. Same direction as the export capability gap in
    /// <c>ExecuteFileAssertion</c> — an undeclared gap is loud, never silent.
    /// </summary>
    Unclassified,
}

/// <summary>
/// Thrown by an adapter's own id resolution when a spec names a force, selection, entry, catalogue
/// or file that is not there. Declaring this — rather than the <see cref="InvalidOperationException"/>
/// these sites used to throw — is what lets the harness tell a spec bug apart from an engine
/// refusal. See <see cref="ActionFailureKind.Address"/>.
/// <para>
/// Engine authors: throw this from lookup, and only from lookup. Anything the engine itself refuses
/// must surface as its own exception, or it will be misreported as a spec error and can never be
/// asserted.
/// </para>
/// </summary>
public sealed class SpecAddressingException : Exception
{
    /// <summary>Creates an exception with no message. Present for the standard exception shape.</summary>
    public SpecAddressingException() { }

    /// <inheritdoc cref="SpecAddressingException"/>
    public SpecAddressingException(string message) : base(message) { }

    /// <inheritdoc cref="SpecAddressingException"/>
    public SpecAddressingException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when the adapter itself is broken rather than the engine or the spec — an IKVM member the
/// obfuscated jar no longer exposes, a driver invariant that does not hold. Declaring it keeps such
/// a break out of <see cref="ActionFailureKind.Engine"/>, where the classifier's "everything else"
/// rule would otherwise put it and where a spec's <c>expectFailure</c> could be satisfied by our own
/// bug.
/// <para>
/// The exception types in <see cref="ActionFailure.Classify"/>'s fault list cover the faults the
/// framework raises. This covers the ones only the adapter can recognise.
/// </para>
/// </summary>
public sealed class HarnessFaultException : Exception
{
    /// <summary>Creates an exception with no message. Present for the standard exception shape.</summary>
    public HarnessFaultException() { }

    /// <inheritdoc cref="HarnessFaultException"/>
    public HarnessFaultException(string message) : base(message) { }

    /// <inheritdoc cref="HarnessFaultException"/>
    public HarnessFaultException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// An action that came back <c>ok:false</c> over the adapter protocol, carrying the classification
/// the adapter made (<see cref="Kind"/>) and the engine's own message unwrapped from the transport
/// framing (<see cref="EngineMessage"/>).
/// <para>
/// <see cref="Exception.Message"/> keeps the framed form the runner has always logged
/// (<c>Action 'X' failed: …</c>); <see cref="EngineMessage"/> is what <c>expectFailure</c>'s
/// <c>messageContains</c> matches, so a spec's expectation is written against what the engine said
/// and not against harness wording that may change.
/// </para>
/// </summary>
public sealed class ActionFailedException : Exception
{
    /// <summary>Creates an exception with no message. Present for the standard exception shape.</summary>
    public ActionFailedException() { }

    /// <inheritdoc cref="ActionFailedException"/>
    public ActionFailedException(string message) : base(message) { }

    /// <inheritdoc cref="ActionFailedException"/>
    public ActionFailedException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a failure carrying the adapter's classification and the engine's own message.</summary>
    public ActionFailedException(string message, ActionFailureKind kind, string? engineMessage)
        : base(message)
    {
        Kind = kind;
        EngineMessage = engineMessage;
    }

    /// <summary>What the adapter said the failure was. <see cref="ActionFailureKind.Unclassified"/> when it said nothing.</summary>
    public ActionFailureKind Kind { get; } = ActionFailureKind.Unclassified;

    /// <summary>The engine's own message, without the <c>Action 'X' failed:</c> framing.</summary>
    public string? EngineMessage { get; }
}

/// <summary>
/// The single classification rule, applied in both places a failure is classified: adapter-side,
/// where the verdict is written to the wire as <c>kind</c>, and in-process, where the runner applies
/// it to the raw exception because there is no adapter in between. Keeping it one function is the
/// point — the protocol path is defined as "the in-process rule, executed on the adapter side".
/// </summary>
public static class ActionFailure
{
    /// <summary>
    /// Classify an exception thrown by (or on behalf of) an engine action.
    /// <para>
    /// <b>The remainder is <see cref="ActionFailureKind.Engine"/></b>, which is the honest reading:
    /// an engine cannot be asked to declare its own refusals — the BattleScribe Java engine throws
    /// what it throws, through IKVM, and wrapping all sixteen adapter methods to relabel it would
    /// be a lie about where the knowledge lives. So the two kinds that are <em>not</em> engine
    /// behaviour declare themselves instead: <see cref="SpecAddressingException"/> for a spec that
    /// named nothing, and the fault list below for the ways the harness itself breaks.
    /// </para>
    /// </summary>
    public static ActionFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            // Already classified upstream — an adapter's verdict is never re-derived from the
            // framed message it travelled in.
            ActionFailedException failed => failed.Kind,

            SpecAddressingException => ActionFailureKind.Address,

            // A capability gap wearing an exception. Checked before the fault list and before the
            // catch-all so it can never read as a refusal — see ActionFailureKind.Unsupported.
            NotSupportedException => ActionFailureKind.Unsupported,

            // Never engine behaviour: the transport gave up, the engine was already gone, or our
            // own code dereferenced nothing. A spec must not be able to assert any of these.
            HarnessFaultException
                or TimeoutException
                or OperationCanceledException
                or ObjectDisposedException
                or NullReferenceException
                or IndexOutOfRangeException
                or OutOfMemoryException => ActionFailureKind.Harness,

            _ => ActionFailureKind.Engine,
        };
    }

    /// <summary>The wire spelling of a kind, for the protocol's <c>kind</c> field.</summary>
    public static string? ToWire(ActionFailureKind kind) => kind switch
    {
        ActionFailureKind.Engine => "engine",
        ActionFailureKind.Address => "address",
        ActionFailureKind.Harness => "harness",
        ActionFailureKind.Unsupported => "unsupported",
        _ => null,
    };

    /// <summary>
    /// Read a wire <c>kind</c>. An absent or unrecognised value is
    /// <see cref="ActionFailureKind.Unclassified"/> — never silently promoted to
    /// <see cref="ActionFailureKind.Engine"/>, which is the whole safety property of the field.
    /// </summary>
    public static ActionFailureKind FromWire(string? wire) => wire switch
    {
        "engine" => ActionFailureKind.Engine,
        "address" => ActionFailureKind.Address,
        "harness" => ActionFailureKind.Harness,
        "unsupported" => ActionFailureKind.Unsupported,
        _ => ActionFailureKind.Unclassified,
    };

    /// <summary>
    /// The message a spec's <c>messageContains</c> is matched against: the engine's own words when
    /// the failure crossed the protocol, the exception message otherwise.
    /// </summary>
    public static string MessageOf(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is ActionFailedException { EngineMessage: { } engineMessage }
            ? engineMessage
            : exception.Message;
    }
}
