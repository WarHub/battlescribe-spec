using System.Text;

namespace BattleScribeSpec;

/// <summary>
/// UTF-8 without a byte-order mark on every redirected standard stream — the ONE place that
/// decides what encoding a pipe between two of this repo's processes speaks. The parent side is
/// <c>AdapterProcess.BuildStartInfo</c> (it decodes and encodes with <see cref="Encoding"/>); the
/// child side is <see cref="UseForRedirectedStreams"/>, called first thing by every entry point
/// that can sit on the other end of such a pipe: <c>bs-spec</c>, <c>bs-engine-host</c> and
/// <c>bs-reference-adapter</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it has to be stated at both ends.</b> Left alone, .NET on Windows encodes and decodes a
/// redirected stream with the <em>console's</em> code page — <c>GetConsoleCP</c> for stdin,
/// <c>GetConsoleOutputCP</c> for stdout and stderr — and the two ends of one pipe need not share a
/// console. A child started with <c>CreateNoWindow</c> (as every adapter is) gets a fresh hidden
/// console on the system's OEM code page; a child that inherits its parent's console gets whatever
/// <c>chcp</c> left there. Measured on Windows 11 (system code page 65001) from a console set to
/// 437: the parent's default stdin encoder wrote U+2014 as <c>-</c> (437 has no em dash, so it
/// best-fits, silently) and both kinds of child read <c>-</c>; from a console set to 1252 the
/// parent sent <c>0x97</c> and a <c>CreateNoWindow</c> child, on its own 65001 console, read U+FFFD.
/// Nothing failed; the text was just different. 210 specs carry non-ASCII text, and the stdin spec
/// path (<c>bs-spec run -</c>) hands it over raw.
/// </para>
/// <para>
/// <b>Why VSTest hid it and Microsoft.Testing.Platform does not.</b> Under VSTest the test process
/// runs on a hidden console of its own on the OEM code page — measured from a <c>chcp 437</c>
/// terminal: <c>GetConsoleCP</c> 65001 inside the test — so every child the suite spawns sees the
/// same code page as its parent and the two ends agree by accident. Microsoft.Testing.Platform runs
/// the test executable on the console it was started from and keeps that console's input code page
/// (measured by running the xunit v3 test executable directly from the same terminal:
/// <c>GetConsoleCP</c> 437), so the terminal's code page reaches every pipe. This landed before the
/// suites moved to the new runner, so that the move could not be what changed it.
/// </para>
/// <para>
/// <b>Why only redirected streams, and why never <see cref="Console.InputEncoding"/>.</b> A stream
/// that is a console is encoded in the console's own code page, which is what that console shows
/// and what typing into it produces — the two ends already agree. Re-encoding it as UTF-8 would
/// garble what a person types or reads on a non-UTF-8 console, so the interactive REPLs keep the
/// console's <see cref="Console.In"/>. Setting
/// <see cref="Console.InputEncoding"/>/<see cref="Console.OutputEncoding"/> instead would call
/// <c>SetConsoleCP</c> on the console itself, which is shared with the user's shell and outlives
/// this process. Re-opening the redirected handle touches nothing but this process's own reader or
/// writer.
/// </para>
/// <para>
/// <b>No byte-order mark, ever, on what we write.</b> A pipe cannot seek, so a
/// <see cref="StreamWriter"/> with a BOM-emitting encoding writes <c>EF BB BF</c> before its first
/// line — the first protocol response would then not start with <c>{</c>. Readers, conversely,
/// <em>accept</em> a leading BOM (and drop it): a spec piped from a file saved with one is still a
/// spec.
/// </para>
/// </remarks>
public static class Utf8Stdio
{
    /// <summary>UTF-8 that writes no byte-order mark. The encoding of every redirected standard stream.</summary>
    public static UTF8Encoding Encoding { get; } = new(encoderShouldEmitUTF8Identifier: false);

    private static int _applied;

    /// <summary>
    /// Re-open each redirected standard stream of this process — stdin, stdout, stderr, each
    /// independently — as UTF-8 without a BOM, via <see cref="Console.SetIn"/>,
    /// <see cref="Console.SetOut"/> and <see cref="Console.SetError"/>. A stream still attached to a
    /// console is left exactly as it was. Idempotent; call it before anything touches
    /// <see cref="Console"/> (Spectre's console in <c>Ui</c> captures <see cref="Console.Error"/> once).
    /// </summary>
    public static void UseForRedirectedStreams()
    {
        if (Interlocked.Exchange(ref _applied, 1) != 0)
        {
            return;
        }

        var streams = Open(
            new Redirection(Console.IsInputRedirected, Console.IsOutputRedirected, Console.IsErrorRedirected),
            Console.OpenStandardInput,
            Console.OpenStandardOutput,
            Console.OpenStandardError);

        if (streams.Input is { } input)
        {
            Console.SetIn(input);
        }

        if (streams.Output is { } output)
        {
            Console.SetOut(output);
        }

        if (streams.Error is { } error)
        {
            Console.SetError(error);
        }
    }

    /// <summary>
    /// The decision <see cref="UseForRedirectedStreams"/> applies, without applying it: a UTF-8
    /// reader or writer over each redirected stream, null for each stream that is a console. Split
    /// out so it can be checked against in-memory streams — the real one mutates process-wide state.
    /// </summary>
    internal static (TextReader? Input, TextWriter? Output, TextWriter? Error) Open(
        Redirection redirection,
        Func<Stream> openInput,
        Func<Stream> openOutput,
        Func<Stream> openError) =>
        (redirection.Input ? CreateReader(openInput()) : null,
         redirection.Output ? CreateWriter(openOutput()) : null,
         redirection.Error ? CreateWriter(openError()) : null);

    /// <summary>
    /// A UTF-8 reader over <paramref name="stream"/>. A leading byte-order mark is honoured and
    /// dropped rather than read as U+FEFF.
    /// </summary>
    internal static StreamReader CreateReader(Stream stream) =>
        new(stream, Encoding, detectEncodingFromByteOrderMarks: true);

    /// <summary>
    /// A UTF-8 writer over <paramref name="stream"/> that never writes a byte-order mark and flushes
    /// every write — the same <c>AutoFlush</c> the console's own writers have, so a line is on the
    /// pipe when <c>WriteLine</c> returns and output interleaves with the other stream as before.
    /// </summary>
    internal static StreamWriter CreateWriter(Stream stream) =>
        new(stream, Encoding) { AutoFlush = true };

    /// <summary>Which of the three standard streams are redirected (true) rather than a console.</summary>
    internal readonly record struct Redirection(bool Input, bool Output, bool Error);
}
