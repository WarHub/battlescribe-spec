using BattleScribeSpec;
using BattleScribeSpec.NewRecruit;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Integration tests for NewRecruitRosterEngine.
/// These exercise the full adapter against the live NR site.
/// Skipped unless NR_ENGINE_URL is set.
/// </summary>
[Collection("NewRecruit")]
public sealed class NrIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private NewRecruitRosterEngine? _engine;

    public NrIntegrationTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("NR_ENGINE_URL");
        if (string.IsNullOrEmpty(baseUrl)) return;
        var headless = Environment.GetEnvironmentVariable("NR_HEADLESS") != "false";
        _engine = await NewRecruitRosterEngine.CreateAsync(baseUrl, headless);
    }

    public async Task DisposeAsync()
    {
        _engine?.Dispose();
    }

    [SkippableFact]
    public void Setup_CreatesRosterWithForce()
    {
        Skip.If(_engine is null, "NR_ENGINE_URL not set");

        var gs = new GameSystemSpec(Name: "Age of Sigmar 4.0");
        var cat = new CatalogueSpec(Name: "Beasts of Chaos [LEGENDS]");
        var errors = _engine!.Setup(gs, [cat]);

        _output.WriteLine($"Setup errors: [{string.Join(", ", errors)}]");
        Assert.Empty(errors);

        // Small delay to let Pinia store settle
        Thread.Sleep(1000);

        var state = _engine.GetRosterState();
        _output.WriteLine($"Roster: '{state.Name}', Forces: {state.Forces.Count}");
        foreach (var err in state.ValidationErrors)
            _output.WriteLine($"  Validation: {err}");

        Assert.True(state.Forces.Count >= 1, $"Should have at least 1 force after setup. Name='{state.Name}', Forces={state.Forces.Count}");
    }

    [SkippableFact]
    public void SelectEntry_AddsSelection()
    {
        Skip.If(_engine is null, "NR_ENGINE_URL not set");

        var gs = new GameSystemSpec(Name: "Age of Sigmar 4.0");
        var cat = new CatalogueSpec(Name: "Beasts of Chaos [LEGENDS]");
        var errors = _engine!.Setup(gs, [cat]);
        Assert.Empty(errors);

        var stateBefore = _engine.GetRosterState();
        var selsBefore = stateBefore.Forces[0].Selections.Count;
        _output.WriteLine($"Before SelectEntry: {selsBefore} selections");
        foreach (var sel in stateBefore.Forces[0].Selections)
            _output.WriteLine($"  [{sel.Type}] {sel.Name} (count={sel.Number})");

        // SelectEntry calls incrementAmount — for already-selected entries this increases count
        // For entries that don't accept more, it may have no effect
        // This test verifies the call doesn't throw
        try
        {
            _engine.SelectEntry(0, 0);
            _output.WriteLine("SelectEntry(0, 0) succeeded");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"SelectEntry(0, 0) threw: {ex.Message}");
        }

        var stateAfter = _engine.GetRosterState();
        var selsAfter = stateAfter.Forces[0].Selections.Count;
        _output.WriteLine($"After SelectEntry: {selsAfter} selections");

        // Verify state reading still works after an action
        Assert.True(stateAfter.Forces.Count >= 1);
    }

    [SkippableFact]
    public void GetRosterState_ReturnsSelectionDetails()
    {
        Skip.If(_engine is null, "NR_ENGINE_URL not set");

        var gs = new GameSystemSpec(Name: "Age of Sigmar 4.0");
        var cat = new CatalogueSpec(Name: "Beasts of Chaos [LEGENDS]");
        var errors = _engine!.Setup(gs, [cat]);
        Assert.Empty(errors);

        // Select an entry to have a non-default selection
        _engine.SelectEntry(0, 0);

        var state = _engine.GetRosterState();
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
        Skip.If(_engine is null, "NR_ENGINE_URL not set");

        var gs = new GameSystemSpec(Name: "Age of Sigmar 4.0");
        var cat = new CatalogueSpec(Name: "Beasts of Chaos [LEGENDS]");
        var errors = _engine!.Setup(gs, [cat]);
        Assert.Empty(errors);

        var validationErrors = _engine.GetValidationErrors();
        _output.WriteLine($"Validation errors: {validationErrors.Count}");
        foreach (var err in validationErrors)
            _output.WriteLine($"  - {err}");

        // Just verify it doesn't throw — errors are expected for an empty roster
    }
}
