using System.Diagnostics;
using System.Text;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.TestSupport;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// Every pipe between two of this repo's processes is UTF-8 without a byte-order mark, at both ends:
/// the parent's (<see cref="AdapterProcess.BuildStartInfo"/>) and the child's
/// (<see cref="Utf8Stdio.UseForRedirectedStreams"/>, called by each entry point). The structural
/// facts are checked here directly; the adapters' half is also checked by driving them, from a
/// console whose code page is not UTF-8. The CLI's half is <c>StdinSpecEncodingTests</c> in Cli.Tests.
/// </summary>
public sealed class RedirectedStdioEncodingTests
{
    private const string Name = "GS \u2014 Alpha"; // an em dash, spelled as an escape so this file's own encoding cannot matter

    // ===== structural =====

    [Fact]
    [Trait("Category", "Unit")]
    public void TheEncoding_IsUtf8_WithAnEmptyPreamble()
    {
        AssertUtf8WithoutPreamble(Utf8Stdio.Encoding, "Utf8Stdio.Encoding");
    }

    /// <summary>
    /// The parent pins all three pipes. Falsifiable: drop any of the three
    /// <c>Standard*Encoding</c> assignments and that pipe follows this process's console code page.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void AdapterProcess_PinsAllThreePipes_ToUtf8WithoutAPreamble()
    {
        var psi = AdapterProcess.BuildStartInfo("dotnet", "adapter.dll", environment: null);

        AssertUtf8WithoutPreamble(psi.StandardInputEncoding, nameof(psi.StandardInputEncoding));
        AssertUtf8WithoutPreamble(psi.StandardOutputEncoding, nameof(psi.StandardOutputEncoding));
        AssertUtf8WithoutPreamble(psi.StandardErrorEncoding, nameof(psi.StandardErrorEncoding));
    }

    /// <summary>
    /// What a child writes: exactly the UTF-8 bytes of the line, with no byte-order mark in front
    /// (a pipe cannot seek, so a BOM-emitting writer would put <c>EF BB BF</c> before the first
    /// protocol response) — and already on the stream when <c>WriteLine</c> returns, with no
    /// <c>Flush</c>, because the reader on the other end waits for a whole line.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Writer_WritesNoByteOrderMark_AndFlushesEveryLine()
    {
        using var stream = new MemoryStream();
        using var writer = Utf8Stdio.CreateWriter(stream);

        writer.WriteLine(Name);

        Assert.Equal(Encoding.UTF8.GetBytes(Name + writer.NewLine), stream.ToArray());
    }

    /// <summary>What a child reads: UTF-8, and a leading byte-order mark is a marker, not a character.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Reader_DecodesUtf8_AndDropsALeadingByteOrderMark()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Name + "\n")];
        using var reader = Utf8Stdio.CreateReader(new MemoryStream(bytes));

        Assert.Equal(Name, reader.ReadLine());
    }

    /// <summary>
    /// Only a redirected stream is re-opened; one that is a console is never even opened (the
    /// delegate would throw). Falsifiable: re-open stdout unconditionally and this throws.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Open_ReopensOnlyTheRedirectedStreams()
    {
        static Stream NeverOpened() => throw new InvalidOperationException("a console stream must be left alone");

        var (input, output, error) = Utf8Stdio.Open(
            new Utf8Stdio.Redirection(Input: true, Output: false, Error: true),
            () => new MemoryStream(),
            NeverOpened,
            () => new MemoryStream());

        Assert.Null(output);
        var reader = Assert.IsType<StreamReader>(input);
        AssertUtf8WithoutPreamble(reader.CurrentEncoding, "stdin reader");
        var writer = Assert.IsType<StreamWriter>(error);
        AssertUtf8WithoutPreamble(writer.Encoding, "stderr writer");
        Assert.True(writer.AutoFlush, "stderr writer must flush every write");
        reader.Dispose();
        writer.Dispose();
    }

    // ===== behavioural: the adapters' half =====

    public static TheoryData<string, string, string> Adapters => new()
    {
        { "BattleScribeSpec.EngineHost", "bs-engine-host.dll", " serve --engine battlescribe" },
        { "BattleScribeSpec.ReferenceAdapter", "bs-reference-adapter.dll", "" },
    };

    /// <summary>
    /// A client that sends raw UTF-8 — legal JSON, and what Node's <c>JSON.stringify</c> or Python's
    /// <c>json.dumps(ensure_ascii=False)</c> produce — reaches the adapter intact even when the
    /// adapter's console is on code page 437.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why raw bytes and not <see cref="AdapterProcess.SendCommandAsync"/>.</b> Our own client
    /// serializes with System.Text.Json, which escapes every non-ASCII character, so a round trip
    /// through it is ASCII on the wire and passes whatever either end's encoding is — it cannot
    /// detect this defect. The command below is written as raw text instead, and the adapter's
    /// answer (escaped by the adapter's System.Text.Json, so ASCII) says exactly what it decoded.
    /// </para>
    /// <para>
    /// Falsifiable on Windows: remove <c>Utf8Stdio.UseForRedirectedStreams()</c> from the adapter's
    /// entry point and it decodes <c>E2 80 94</c> as 437's <c>ΓÇö</c>. Only there — on Linux, where
    /// every CI job runs, it passes trivially (see <see cref="LegacyCodePageConsole"/>).
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Adapters))]
    [Trait("Category", "Integration")]
    public async Task Adapter_DecodesRawUtf8_FromALegacyCodePageConsole(string project, string dll, string arguments)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = BuiltBinaries.Find(TestPaths.Root, project, dll);

        var psi = AdapterProcess.BuildStartInfo("dotnet", $"\"{path}\"{arguments}", environment: null);
        LegacyCodePageConsole.Apply(psi);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {dll}.");
        var stdErr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.StandardInput.WriteLineAsync(
                "{\"type\":\"setup\",\"corrId\":1,\"gameSystem\":{\"id\":\"gs\",\"name\":\"" + Name + "\"}}");
            Assert.IsType<SetupResult>(await ReadResponseAsync(process, ct));

            await process.StandardInput.WriteLineAsync("{\"type\":\"getState\",\"corrId\":2}");
            var state = Assert.IsType<StateResponse>(await ReadResponseAsync(process, ct));
            Assert.Equal(Name, state.GameSystemName);
        }
        finally
        {
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
        }

        Assert.True(process.ExitCode == 0, $"{dll} exited {process.ExitCode}; stderr:\n{await stdErr}");
    }

    private static async Task<ProtocolResponse?> ReadResponseAsync(Process process, CancellationToken ct)
    {
        var line = await process.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromMinutes(2), ct);
        Assert.NotNull(line);
        return ProtocolSerializer.DeserializeResponse(line);
    }

    private static void AssertUtf8WithoutPreamble(Encoding? encoding, string what)
    {
        Assert.True(encoding is not null, $"{what} is not set, so it follows the console code page");
        Assert.True(encoding.CodePage == Encoding.UTF8.CodePage, $"{what} is {encoding.WebName}, not UTF-8");
        Assert.True(encoding.GetPreamble().Length == 0, $"{what} writes a byte-order mark");
    }
}
