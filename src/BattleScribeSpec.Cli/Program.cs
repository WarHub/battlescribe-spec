namespace BattleScribeSpec.Cli;

/// <summary>
/// Entry point for <c>bs-spec</c> — the BattleScribe conformance-spec developer CLI
/// (run specs, probe UIs, format specs, export XML).
/// </summary>
public static class Program
{
    public static Task<int> Main(string[] args)
    {
        // First, before anything touches Console: a redirected stdin/stdout/stderr speaks UTF-8
        // whatever the console's code page is (see Utf8Stdio). Here and not in RunAsync, which tests
        // call in-process and must not re-point the test host's own Console.
        Utf8Stdio.UseForRedirectedStreams();
        return RunAsync(args);
    }

    /// <summary>
    /// Programmatic entry point (used by tests). Parses <paramref name="args"/> and
    /// returns the process exit code.
    /// </summary>
    public static Task<int> RunAsync(params string[] args) =>
        CommandFactory.CreateRootCommand().Parse(args).InvokeAsync();
}
