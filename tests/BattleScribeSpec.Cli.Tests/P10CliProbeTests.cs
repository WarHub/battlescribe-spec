namespace BattleScribeSpec.Cli.Tests;

/// <summary>THROWAWAY probe (P10), never merged: a deliberate failure in the `dotnet test` lane.</summary>
[Trait("Category", "Unit")]
public sealed class P10CliProbeTests
{
    [Fact]
    public void P10_DeliberateFailure_InTheDotnetTestLane() => Assert.Fail("P10 probe: deliberate failure (dotnet test lane)");
}
