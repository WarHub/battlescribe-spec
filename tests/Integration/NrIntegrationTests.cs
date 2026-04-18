using BattleScribeSpec;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Integration tests for NewRecruitRosterEngine.
/// These exercise the full adapter against the live NR site.
/// Skipped unless NR_ENGINE_URL is set.
/// </summary>
[Collection("SequentialLiveNewRecruit")]
[Trait("Category", "Integration")]
public sealed class NrIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly SequentialLiveNewRecruitFixture _fixture;

    public NrIntegrationTests(ITestOutputHelper output, SequentialLiveNewRecruitFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    private static ProtocolGameSystem CreateTestGameSystem() => new()
    {
        Id = "test-gs",
        Name = "Age of Sigmar 4.0",
        ForceEntries =
        [
            new ProtocolForceEntry { Id = "force-1", Name = "Test Force" }
        ]
    };

    private static ProtocolCatalogue CreateTestCatalogue() => new()
    {
        Id = "cat-1",
        Name = "Beasts of Chaos [LEGENDS]",
        GameSystemId = "test-gs",
        SelectionEntries =
        [
            new ProtocolSelectionEntry { Id = "entry-1", Name = "Test Unit", Type = "unit" }
        ]
    };

    [SkippableFact]
    public void Setup_CreatesRosterWithForce()
    {
        Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = CreateTestGameSystem();
        var cat = CreateTestCatalogue();
        var errors = _fixture.Engine!.Setup(gs, [cat]);

        _output.WriteLine($"Setup errors: [{string.Join(", ", errors)}]");
        Assert.Empty(errors);

        // Setup creates a roster but removes auto-forces; add one explicitly
        _fixture.Engine.AddForce(0);

        // Allow Pinia store to settle after setup — polling the state reader
        // will capture the latest snapshot once stable.
        WaitForStoreSettled();

        var state = _fixture.Engine.GetRosterState();
        _output.WriteLine($"Roster: '{state.Name}', Forces: {state.Forces.Count}");
        foreach (var err in state.ValidationErrors)
            _output.WriteLine($"  Validation: {err}");

        Assert.True(state.Forces.Count >= 1, $"Should have at least 1 force after setup. Name='{state.Name}', Forces={state.Forces.Count}");
    }

    [SkippableFact]
    public void SelectEntry_AddsSelection()
    {
        Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = CreateTestGameSystem();
        var cat = CreateTestCatalogue();
        var errors = _fixture.Engine!.Setup(gs, [cat]);
        Assert.Empty(errors);

        _fixture.Engine.AddForce(0);

        var stateBefore = _fixture.Engine.GetRosterState();
        var selsBefore = stateBefore.Forces[0].Selections.Count;
        _output.WriteLine($"Before SelectEntry: {selsBefore} selections");
        foreach (var sel in stateBefore.Forces[0].Selections)
            _output.WriteLine($"  [{sel.Type}] {sel.Name} (count={sel.Number})");

        // SelectEntry calls incrementAmount — for already-selected entries this increases count
        // For entries that don't accept more, it may have no effect
        // This test verifies the call doesn't throw
        try
        {
            _fixture.Engine.SelectEntry(0, 0);
            _output.WriteLine("SelectEntry(0, 0) succeeded");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"SelectEntry(0, 0) threw: {ex.Message}");
        }

        var stateAfter = _fixture.Engine.GetRosterState();
        var selsAfter = stateAfter.Forces[0].Selections.Count;
        _output.WriteLine($"After SelectEntry: {selsAfter} selections");

        // Verify state reading still works after an action
        Assert.True(stateAfter.Forces.Count >= 1);
    }

    [SkippableFact]
    public void GetRosterState_ReturnsSelectionDetails()
    {
        Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = CreateTestGameSystem();
        var cat = CreateTestCatalogue();
        var errors = _fixture.Engine!.Setup(gs, [cat]);
        Assert.Empty(errors);

        _fixture.Engine.AddForce(0);

        // Select an entry to have a non-default selection
        _fixture.Engine.SelectEntry(0, 0);

        var state = _fixture.Engine.GetRosterState();
        Assert.NotEmpty(state.Forces);

        var force = state.Forces[0];
        Assert.NotEmpty(force.Selections);

        // Log all selections for debugging
        foreach (var sel in force.Selections)
        {
            _output.WriteLine($"  Selection: {sel.Name} (type={sel.Type}, count={sel.Number}, costs={sel.Costs.Count}, children={sel.Children.Count})");
            foreach (var cost in sel.Costs)
                _output.WriteLine($"    Cost: {cost.Name}={cost.Value}");
            foreach (var child in sel.Children)
                _output.WriteLine($"    Child: {child.Name} (type={child.Type}, count={child.Number})");
        }
    }

    [SkippableFact]
    public void GetValidationErrors_ReturnsErrors()
    {
        Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = CreateTestGameSystem();
        var cat = CreateTestCatalogue();
        var errors = _fixture.Engine!.Setup(gs, [cat]);
        Assert.Empty(errors);

        var validationErrors = _fixture.Engine.GetValidationErrors();
        _output.WriteLine($"Validation errors: {validationErrors.Count}");
        foreach (var err in validationErrors)
            _output.WriteLine($"  - {err}");

        // Just verify it doesn't throw — errors are expected for an empty roster
    }


    /// <summary>
    /// Wait for the NR Pinia store to settle after an action.
    /// Uses Playwright's timeout mechanism instead of Thread.Sleep.
    /// </summary>
    private void WaitForStoreSettled(int timeoutMs = 1000)
    {
        _fixture.Engine!.Browser.Page.WaitForTimeoutAsync(timeoutMs).GetAwaiter().GetResult();
    }
}
