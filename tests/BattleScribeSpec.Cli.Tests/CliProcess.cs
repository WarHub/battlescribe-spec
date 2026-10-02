using System.Diagnostics;
using BattleScribeSpec.TestSupport;

namespace BattleScribeSpec.Cli.Tests;

/// <summary>What a spawned <c>bs-spec</c> did: its exit code and everything it wrote.</summary>
internal sealed record CliResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// The one way this project starts the real <c>bs-spec</c> out of process, and the one place it
/// finds the binaries it needs. It replaced five copies of a <c>RunCliAsync</c> helper and five
/// walks up from the binaries to the repository root, one per test class.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why out of process at all.</b> <c>Program.RunAsync</c> in-process cannot tell a clean
/// <c>error:</c> line from an unhandled crash — System.CommandLine turns both into exit 1 — and
/// redirecting <see cref="Console"/> in-process races every other test class xUnit runs alongside.
/// </para>
/// <para>
/// <b>What the copies left implicit, stated here:</b>
/// <list type="bullet">
/// <item><b>Working directory</b> is <see cref="AppContext.BaseDirectory"/>, this test assembly's
/// output folder — what the child inherited from both test runners anyway, now written down so it
/// stops depending on the runner. It matters: <c>run --all</c> and <c>compare</c> write
/// <c>artifacts/telemetry/</c> relative to it, and a repo-root working directory would put test
/// runs into the folder CI uploads.</item>
/// <item><b>No GitHub Actions identity.</b> <c>GITHUB_STEP_SUMMARY</c> and <c>GITHUB_ACTIONS</c> are
/// removed from the child's environment. The child is a test fixture, not a CI step: inherited, they
/// made every spawned <c>run --all</c>/<c>compare</c> append its own "Trace summary" section to the
/// summary of whichever CI step happened to be running the tests.</item>
/// <item><b>UTF-8 on all three pipes</b>, no byte-order mark — the parent's half of what
/// <c>Utf8Stdio</c> makes <c>bs-spec</c> do on its side. Unset, each pipe would follow this process's
/// console code page.</item>
/// <item><b>A console of its own</b> (<see cref="ProcessStartInfo.CreateNoWindow"/>), as
/// <c>AdapterProcess</c> gives every adapter: the child needs no console — all three streams are
/// pipes — and must never be able to change this one.</item>
/// <item><b>Paths</b> resolve through <see cref="RepoRoot.FromBinaries"/>, the repository's one
/// root resolution, not another hand-rolled walk.</item>
/// </list>
/// </para>
/// </remarks>
internal static class CliProcess
{
    /// <summary>The checkout these test binaries were built from.</summary>
    public static string RepoRootDirectory { get; } = RepoRoot.FromBinaries
        ?? throw new InvalidOperationException(
            $"No {RepoRoot.MarkerFileName} above {AppContext.BaseDirectory}: Cli.Tests must run from a checkout's artifacts/bin.");

    /// <summary>The built <c>bs-spec.dll</c>.</summary>
    public static string CliDll => FindBuiltDll("BattleScribeSpec.Cli", "bs-spec.dll");

    /// <summary>The built <c>bs-reference-adapter.dll</c> (not referenced by this project, so built separately).</summary>
    public static string ReferenceAdapterDll => FindBuiltDll("BattleScribeSpec.ReferenceAdapter", "bs-reference-adapter.dll");

    /// <summary>An absolute path under the repository root, e.g. <c>SpecPath("roster", "protocol", "x.yaml")</c> under <c>specs/</c>.</summary>
    public static string SpecPath(params string[] segments) =>
        Path.Combine([RepoRootDirectory, "specs", .. segments]);

    /// <summary>Run <c>bs-spec</c> with <paramref name="args"/> and an empty, closed stdin.</summary>
    public static Task<CliResult> RunAsync(params string[] args) => RunAsync(args, stdin: "");

    /// <summary>
    /// Run <c>bs-spec</c> with <paramref name="args"/>, write <paramref name="stdin"/> to it and close
    /// its stdin, and capture its exit code and output.
    /// </summary>
    /// <param name="args">The arguments after <c>bs-spec.dll</c>.</param>
    /// <param name="stdin">Everything the child reads on stdin, sent as UTF-8.</param>
    /// <param name="legacyCodePageConsole">
    /// Start the child on a console of its own set to <see cref="LegacyCodePageConsole.CodePage"/>
    /// (Windows only; see <see cref="LegacyCodePageConsole"/>), so the test sees what a user on such
    /// a console sees.
    /// </param>
    public static async Task<CliResult> RunAsync(
        IReadOnlyList<string> args,
        string stdin,
        bool legacyCodePageConsole = false)
    {
        var psi = CreateStartInfo(args);
        if (legacyCodePageConsole)
        {
            LegacyCodePageConsole.Apply(psi);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start bs-spec.dll.");
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, await stdOutTask, await stdErrTask);
    }

    /// <summary>
    /// The last non-blank line of <paramref name="output"/> — what a CI log tail or a last-line match
    /// sees, so the line a test asserts there is the line a reader gets.
    /// </summary>
    public static string LastLine(string output) =>
        output.Split('\n').Select(line => line.TrimEnd('\r')).LastOrDefault(line => line.Trim().Length > 0) ?? "";

    /// <summary>The variables a spawned <c>bs-spec</c> must not inherit (see the remarks on <see cref="CliProcess"/>).</summary>
    internal static readonly string[] WithheldEnvironment = ["GITHUB_STEP_SUMMARY", "GITHUB_ACTIONS"];

    /// <summary>
    /// How <see cref="RunAsync(IReadOnlyList{string}, string, bool)"/> starts the child — everything the
    /// remarks on <see cref="CliProcess"/> promise, before anything is started. Split out so
    /// <c>CliProcessTests</c> can hold it to those promises.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8Stdio.Encoding,
            StandardOutputEncoding = Utf8Stdio.Encoding,
            StandardErrorEncoding = Utf8Stdio.Encoding,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(CliDll);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var name in WithheldEnvironment)
        {
            psi.Environment.Remove(name);
        }

        return psi;
    }

    /// <summary>
    /// <c>artifacts/bin/&lt;project&gt;/&lt;pivot&gt;/&lt;file&gt;</c>, trying this test assembly's own
    /// pivot first (debug/release) and then <c>debug</c>, the pivot CI builds under. Fails the test
    /// naming the path it expected when neither exists.
    /// </summary>
    private static string FindBuiltDll(string project, string file)
    {
        var binRoot = Path.Combine(RepoRootDirectory, "artifacts", "bin");
        var ownPivot = Path.GetRelativePath(binRoot, AppContext.BaseDirectory)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .FirstOrDefault();

        foreach (var pivot in new[] { ownPivot, "debug" }.OfType<string>().Distinct())
        {
            var dll = Path.Combine(binRoot, project, pivot, file);
            if (File.Exists(dll))
            {
                return dll;
            }
        }

        var expected = Path.Combine(binRoot, project, ownPivot ?? "debug", file);
        Assert.Fail($"{file} not built: {expected}");
        return expected;
    }
}
