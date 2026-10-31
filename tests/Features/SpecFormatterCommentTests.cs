namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// A comment heading an <c>expectedState</c> — between its key and its first property — survives the
/// zone sort. The sort used to collect only lines that belonged to a property, so a heading comment was
/// dropped whenever the properties were out of order, and the "formatted" lint then held the spec to the
/// comment-less text.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SpecFormatterCommentTests
{
    [Fact]
    public void HeadingComment_OfATopLevelExpectedState_Survives()
    {
        var formatted = SpecFormatter.FormatText("""
            id: s
            category: c
            description: d

            setup:
              gameSystem:
                id: gs-1
            steps:
              - expectedState:
                  # Why these two, in this order.
                  forces:
                    - name: Detachment
                  errors: []
            """);

        Assert.Contains(
            "  - expectedState:\n      # Why these two, in this order.\n      errors: []\n      forces:\n",
            formatted,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HeadingComment_OfAnExpectedStateInAScenarioBlock_Survives()
    {
        var formatted = SpecFormatter.FormatText("""
            id: s
            category: c
            description: d

            setup:
              edit: cat-1
              gameSystem:
                id: gs-1
            steps:
              - roster:
                  - expectedState:
                      # One violation, the hidden one.
                      forces:
                        - name: Detachment
                      errorCount: 1
            """);

        Assert.Contains(
            "      - expectedState:\n          # One violation, the hidden one.\n          errorCount: 1\n          forces:\n",
            formatted,
            StringComparison.Ordinal);
    }
}
