namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// <see cref="SpecFormatter"/> on a scenario spec's shape: <c>data:</c> / <c>roster:</c> blocks are step
/// items, their own steps are separated by blank lines, and an <c>expectedState</c> inside a block is
/// zone-sorted like a top-level one. Without these the "formatted" lint held a scenario to the
/// top-level rules only.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SpecFormatterScenarioTests
{
    private const string Unformatted = """
        id: s
        category: handoff
        description: d
        setup:
          edit: cat-1
          gameSystem:
            id: gs-1
            name: GS
            forceEntries:
              - id: fe-1
                name: Detachment
        steps:
          - action: newRoster
          - roster:
              - action: addForce
                id: add-detachment
                forceEntryId: fe-1
              - expectedState:
                  forces:
                    - name: Detachment
                  costs:
                    - typeId: ct-pts
                      value: 15
          - data:
              - action: reload
              - expectedState:
                  catalogues:
                    - id: cat-1
          - action: saveData
        """;

    private const string Formatted = """
        id: s
        category: handoff
        description: d

        setup:
          edit: cat-1
          gameSystem:
            id: gs-1
            name: GS
            forceEntries:
              - id: fe-1
                name: Detachment
        steps:
          - action: newRoster

          - roster:
              - action: addForce
                id: add-detachment
                forceEntryId: fe-1

              - expectedState:
                  costs:
                    - typeId: ct-pts
                      value: 15
                  forces:
                    - name: Detachment

          - data:
              - action: reload

              - expectedState:
                  catalogues:
                    - id: cat-1

          - action: saveData

        """;

    [Fact]
    public void ScenarioShape_IsFormatted()
        => Assert.Equal(Normalize(Formatted), SpecFormatter.FormatText(Unformatted));

    [Fact]
    public void Formatting_IsIdempotent()
    {
        var once = SpecFormatter.FormatText(Unformatted);

        Assert.Equal(once, SpecFormatter.FormatText(once));
    }

    /// <summary>
    /// A six-space list item outside a block — a setup force entry — is not a block step, so nothing is
    /// inserted before it.
    /// </summary>
    [Fact]
    public void SixSpaceItemsOutsideABlock_AreLeftAlone()
    {
        var formatted = SpecFormatter.FormatText(Unformatted);

        Assert.Contains("    forceEntries:\n      - id: fe-1\n", formatted, StringComparison.Ordinal);
    }

    // The raw literals above end with an empty line so the expected text carries one trailing newline.
    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
}
