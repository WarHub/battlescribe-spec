using System.Text.RegularExpressions;

namespace BattleScribeSpec.Cli.Tests;

/// <summary>
/// <c>bs-spec run</c> reads a spec's <c>engines:</c> by the one rule every runner applies (#573): the engine's
/// own entry, else its family's. A UI driver inherits its family's <c>skip</c>, in every domain, and the roster
/// branch honours a skip at all — it used to run every roster spec whatever its <c>engines:</c> said, so
/// <c>bs-spec run --engine battlescribe --ui</c> failed a spec the BsRosterUi lane skips.
/// </summary>
/// <remarks>
/// Each run names the reference adapter after a built-in engine (<c>&lt;name&gt;=dotnet:…</c>): the name is what a
/// spec's <c>engines:</c> is read against, and a skip that failed would reach the reference adapter rather than
/// a desktop app or a live site.
/// </remarks>
public sealed class RunEngineExpectationTests
{
    private static string As(string engine) => $"{engine}=dotnet:{CliProcess.ReferenceAdapterDll}";

    [Theory]
    [Trait("Category", "Integration")]
    // engines: {battlescribe: skip} — the family's engine, and (no entry of its own) its UI driver.
    [InlineData("roster", "protocol", "protocol-duplicate-force", "battlescribe", "battlescribe")]
    [InlineData("roster", "protocol", "protocol-duplicate-force", "battlescribe-ui", "battlescribe")]
    [InlineData("gamedata", "nr", "nr-condition-types", "battlescribe-ui", "battlescribe")]
    // engines: {newrecruit: skip} — the NR UI driver inherits it.
    [InlineData("roster", "customization", "customization-category", "newrecruit-ui", "newrecruit")]
    public async Task Run_ASpecTheEnginesFamilyOptsOut_IsSkippedWithTheEntryThatDecided(
        string domain, string category, string id, string engine, string declaredBy)
    {
        var spec = CliProcess.SpecPath(domain, category, id + ".yaml");
        Assert.True(File.Exists(spec), $"Spec not found: {spec}");

        var (exitCode, stdOut, stdErr) = await CliProcess.RunAsync("run", spec, "--engine", As(engine));

        var combined = stdOut + stdErr;
        Assert.True(exitCode == 0, $"exit code {exitCode}; output:\n{combined}");

        // The warning is wrapped at the console width; the words are what is asserted, not where it broke.
        var unwrapped = Regex.Replace(combined, @"\s+", " ");
        Assert.Contains(
            $"Spec '{id}' is not applicable to engine '{engine}' (the spec says engines: {{{declaredBy}: skip}}; skipped).",
            unwrapped,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PASS", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("FAIL", combined, StringComparison.Ordinal);
    }

    /// <summary>
    /// A driver's own entry wins over its family's: <c>newrecruit-ui: pass</c> keeps a spec its family opts out
    /// of. The spec runs (on the reference adapter, which is not New Recruit, so its verdict is not the point).
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_ASpecTheDriverKeeps_RunsThoughItsFamilyIsOptedOut()
    {
        var spec = CliProcess.SpecPath("gamedata", "load", "load-malformed-catalogue.yaml");
        Assert.True(File.Exists(spec), $"Spec not found: {spec}");

        var (_, stdOut, stdErr) = await CliProcess.RunAsync("run", spec, "--engine", As("newrecruit-ui"));

        var combined = Regex.Replace(stdOut + stdErr, @"\s+", " ");
        Assert.DoesNotContain("not applicable", combined, StringComparison.Ordinal);
        Assert.Contains("Engine: newrecruit-ui", combined, StringComparison.Ordinal);
    }
}
