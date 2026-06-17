namespace BattleScribeSpec.Cli;

/// <summary>
/// Entry point for <c>bs-spec</c> — the BattleScribe conformance-spec developer CLI
/// (run specs, probe UIs, format specs, export XML).
/// </summary>
public static class Program
{
    public static Task<int> Main(string[] args) => RunAsync(args);

    /// <summary>
    /// Programmatic entry point (used by tests). Parses <paramref name="args"/> and
    /// returns the process exit code.
    /// </summary>
    public static Task<int> RunAsync(params string[] args) =>
        CommandFactory.CreateRootCommand().Parse(args).InvokeAsync();
}
