using BattleScribeSpec.NewRecruit;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// Pins the two ends of the <c>--trace-store</c> switch together.
/// </summary>
/// <remarks>
/// <para>
/// The CLI deliberately does not reference driver types — <c>EngineOptions.ApplyDiagnosticSwitches</c>
/// inlines the variable name, the same way <c>RunCommand.ReportDiagnosticDumps</c> inlines the BS-UI
/// diagnostics path. Inlining is the right call for the dependency graph and the wrong one for
/// refactoring safety: rename <c>NrStoreTraceJs.EnableVariable</c> and the flag silently stops
/// working, with no compiler error and no failing test, because "diagnostics were not captured"
/// looks exactly like "nothing went wrong".
/// </para>
/// <para>
/// That is the same failure shape as the bug this whole tool exists because of: an optional call to
/// an action that did not exist, reporting success. So the coupling gets a test.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class CliDiagnosticSwitchTests
{
    /// <summary>The literal that <c>EngineOptions.ApplyDiagnosticSwitches</c> exports.</summary>
    private const string InlinedInCli = "NR_TRACE_STORE";

    [Fact]
    public void TraceStoreVariable_MatchesTheNameTheCliExports()
    {
        Assert.Equal(InlinedInCli, NrStoreTraceJs.EnableVariable);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Enabled_ReadsTheVariable(string? value, bool expected)
    {
        var previous = Environment.GetEnvironmentVariable(NrStoreTraceJs.EnableVariable);
        try
        {
            Environment.SetEnvironmentVariable(NrStoreTraceJs.EnableVariable, value);
            Assert.Equal(expected, NrStoreTraceJs.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NrStoreTraceJs.EnableVariable, previous);
        }
    }

    /// <summary>
    /// Tracing must stay off unless asked for. It replaces the store's function identities, so a
    /// default-on tracer would perturb every <c>bs-spec compare</c> — a harness whose entire purpose
    /// is to vary one thing at a time.
    /// </summary>
    [Fact]
    public void Enabled_IsOffByDefault()
    {
        var previous = Environment.GetEnvironmentVariable(NrStoreTraceJs.EnableVariable);
        try
        {
            Environment.SetEnvironmentVariable(NrStoreTraceJs.EnableVariable, null);
            Assert.False(NrStoreTraceJs.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NrStoreTraceJs.EnableVariable, previous);
        }
    }
}
