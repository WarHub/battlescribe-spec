namespace BattleScribeSpec.Cli.Tests;

/// <summary>
/// Covers the rewired <c>verify</c> gamedata matrix (#271 PR 2): each <c>--engines</c> entry is
/// resolved via <c>EngineConnectable.Parse</c> + <c>EngineRegistry.Resolve</c>, spawned as a child
/// adapter process, and driven entirely over the JSON-line protocol. Drives the BattleScribe
/// reference adapter as the <c>battlescribe</c> identity (<c>battlescribe=dotnet:bs-reference-adapter.dll</c>),
/// same locator as <see cref="RunProtocolTests"/>.
/// </summary>
public sealed class VerifyProtocolTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Verify_GameDataSpec_OverReferenceAdapter_PrintsMatrixAndExitsZero()
    {
        // Spawned out-of-process (like RunProtocolTests' break-and-quit case): asserting on
        // captured stdout means redirecting Console.Out, and xUnit runs test classes
        // concurrently by default, so redirecting the shared static Console in-process risks
        // cross-test interference. A separate process gives this run its own stdout pipe.
        var spec = CliProcess.SpecPath("gamedata", "entry", "add-entry-basic.yaml");
        Assert.True(File.Exists(spec), $"Spec not found: {spec}");

        var adapterDll = CliProcess.ReferenceAdapterDll;

        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync(
            "verify", spec, "--engines", $"battlescribe=dotnet:{adapterDll}");

        Assert.True(exitCode == 0, $"exit code {exitCode}; stdout: {stdOut}; stderr: {stdErr}");
        Assert.Contains("battlescribe", stdOut);
        Assert.Contains("spec", stdOut);
        // Verify the matrix row shows a PASS cell for the spec, not just that N/A appears.
        Assert.Matches(@"add-entry-basic\s+PASS", stdOut);
    }
}
