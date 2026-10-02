namespace BattleScribeSpec.Tests.Features;

/// <summary>THROWAWAY probe (P10), never merged: a deliberate failure in a `dotnet run` lane.</summary>
[Trait("Category", "Unit")]
public sealed class P10ProbeTests
{
    [Fact]
    public void P10_DeliberateFailure_InADotnetRunLane() => Assert.Fail("P10 probe: deliberate failure (dotnet run lane)");
}
