using System.Text;

namespace BattleScribeSpec.Cli.Tests;

/// <summary>Holds <see cref="CliProcess"/> to what its remarks promise about every child it starts.</summary>
[Trait("Category", "Unit")]
public sealed class CliProcessTests
{
    [Fact]
    public void Child_StartsInTheTestOutputFolder_OnAConsoleOfItsOwn_WithUtf8Pipes()
    {
        var psi = CliProcess.CreateStartInfo(["--help"]);

        Assert.Equal(AppContext.BaseDirectory, psi.WorkingDirectory);
        Assert.True(psi.CreateNoWindow, "the child must get a console of its own");
        Assert.Equal(new[] { CliProcess.CliDll, "--help" }, psi.ArgumentList);
        foreach (var (pipe, encoding) in new[]
        {
            ("stdin", psi.StandardInputEncoding),
            ("stdout", psi.StandardOutputEncoding),
            ("stderr", psi.StandardErrorEncoding),
        })
        {
            Assert.True(encoding is not null, $"{pipe} is not set, so it follows the console code page");
            Assert.True(encoding.CodePage == Encoding.UTF8.CodePage, $"{pipe} is {encoding.WebName}, not UTF-8");
            Assert.True(encoding.GetPreamble().Length == 0, $"{pipe} writes a byte-order mark");
        }
    }

    /// <summary>
    /// A spawned <c>bs-spec</c> is a test fixture, not a CI step, so it must not append a "Trace
    /// summary" section to the step summary of whichever CI step is running the tests — which every
    /// <c>run --all</c> and <c>compare</c> in this project did while it inherited those variables.
    /// </summary>
    /// <remarks>
    /// Falsifiable locally as well as in CI: the variables are set in this process for the duration
    /// (CI already has them), so removing either name from <see cref="CliProcess.WithheldEnvironment"/>
    /// lets it through. The names are spelled out here rather than read from that list, so emptying
    /// the list cannot make this pass vacuously.
    /// </remarks>
    [Fact]
    public void Child_InheritsNoGitHubActionsIdentity()
    {
        string[] names = ["GITHUB_STEP_SUMMARY", "GITHUB_ACTIONS"];
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", Path.Combine(Path.GetTempPath(), "bsspec-not-a-step-summary.md"));
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "true");

            var psi = CliProcess.CreateStartInfo([]);

            foreach (var name in names)
            {
                Assert.False(psi.Environment.ContainsKey(name), $"{name} reaches the spawned bs-spec");
            }
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
