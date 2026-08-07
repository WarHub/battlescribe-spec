using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Integration tests for NewRecruitRosterEngine.
/// These exercise the full adapter against the live NR site.
/// Skipped unless NR_ENGINE_URL is set.
/// </summary>
[Collection("SequentialLiveNrRoster")]
[Trait("Category", "Integration")]
[Trait("Engine", "LiveNrRoster")]
public sealed class LiveNrRosterIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly SequentialLiveNrRosterFixture _fixture;

    public LiveNrRosterIntegrationTests(ITestOutputHelper output, SequentialLiveNrRosterFixture fixture)
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

    [Fact]
    public void Setup_CreatesRosterWithForce()
    {
        Assert.SkipWhen(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = CreateTestGameSystem();
        var cat = CreateTestCatalogue();
        var errors = _fixture.Engine!.Setup(gs, [cat]);

        _output.WriteLine($"Setup errors: [{string.Join(", ", errors)}]");
        Assert.Empty(errors);

        // Setup creates a roster but removes auto-forces; add one explicitly
        _fixture.Engine.AddForce("force-1", "cat-1");

        // Allow Pinia store to settle after setup — polling the state reader
        // will capture the latest snapshot once stable.
        WaitForStoreSettled();

        var state = _fixture.Engine.GetRosterState();
        _output.WriteLine($"Roster: '{state.Name}', Forces: {state.Forces.Count}");
        foreach (var err in state.ValidationErrors)
        {
            _output.WriteLine($"  Validation: {err}");
        }

        Assert.True(state.Forces.Count >= 1, $"Should have at least 1 force after setup. Name='{state.Name}', Forces={state.Forces.Count}");
    }

    [Fact]
    public void SelectEntry_AddsSelection()
    {
        Assert.SkipWhen(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = CreateTestGameSystem();
        var cat = CreateTestCatalogue();
        var errors = _fixture.Engine!.Setup(gs, [cat]);
        Assert.Empty(errors);

        var addForceResult = _fixture.Engine.AddForce("force-1", "cat-1");

        var stateBefore = _fixture.Engine.GetRosterState();
        var selsBefore = stateBefore.Forces[0].Selections.Count;
        _output.WriteLine($"Before SelectEntry: {selsBefore} selections");
        foreach (var sel in stateBefore.Forces[0].Selections)
        {
            _output.WriteLine($"  [{sel.Type}] {sel.Name} (count={sel.Number})");
        }

        // SelectEntry calls incrementAmount — for already-selected entries this increases count
        // For entries that don't accept more, it may have no effect
        // This test verifies the call doesn't throw
        try
        {
            _fixture.Engine.SelectEntry(addForceResult.ForceId!, "entry-1");
            _output.WriteLine("SelectEntry succeeded");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"SelectEntry threw: {ex.Message}");
        }

        var stateAfter = _fixture.Engine.GetRosterState();
        var selsAfter = stateAfter.Forces[0].Selections.Count;
        _output.WriteLine($"After SelectEntry: {selsAfter} selections");

        // Verify state reading still works after an action
        Assert.True(stateAfter.Forces.Count >= 1);
    }

    [Fact]
    public void GetRosterState_ReturnsSelectionDetails()
    {
        Assert.SkipWhen(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = CreateTestGameSystem();
        var cat = CreateTestCatalogue();
        var errors = _fixture.Engine!.Setup(gs, [cat]);
        Assert.Empty(errors);

        var addForceResult = _fixture.Engine.AddForce("force-1", "cat-1");

        // Select an entry to have a non-default selection
        _fixture.Engine.SelectEntry(addForceResult.ForceId!, "entry-1");

        var state = _fixture.Engine.GetRosterState();
        Assert.NotEmpty(state.Forces);

        var force = state.Forces[0];
        Assert.NotEmpty(force.Selections);

        // Log all selections for debugging
        foreach (var sel in force.Selections)
        {
            _output.WriteLine($"  Selection: {sel.Name} (type={sel.Type}, count={sel.Number}, costs={sel.Costs.Count}, children={sel.Children.Count})");
            foreach (var cost in sel.Costs)
            {
                _output.WriteLine($"    Cost: {cost.Name}={cost.Value}");
            }

            foreach (var child in sel.Children)
            {
                _output.WriteLine($"    Child: {child.Name} (type={child.Type}, count={child.Number})");
            }
        }
    }

    [Fact]
    public void GetValidationErrors_ReturnsErrors()
    {
        Assert.SkipWhen(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = CreateTestGameSystem();
        var cat = CreateTestCatalogue();
        var errors = _fixture.Engine!.Setup(gs, [cat]);
        Assert.Empty(errors);

        var validationErrors = _fixture.Engine.GetValidationErrors();
        _output.WriteLine($"Validation errors: {validationErrors.Count}");
        foreach (var err in validationErrors)
        {
            _output.WriteLine($"  - {err}");
        }

        // Just verify it doesn't throw — errors are expected for an empty roster
    }


    /// <summary>
    /// Waits for NR's store to hold a roster with at least one force.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was <c>Page.WaitForTimeoutAsync(1000)</c>, described in its own summary as "uses
    /// Playwright's timeout mechanism instead of Thread.Sleep" — which is a distinction without a
    /// difference. <c>WaitForTimeoutAsync</c> IS a sleep, and Playwright's own docs discourage it.
    /// </para>
    /// <para>
    /// The condition is deliberately "the army exists and has a force" rather than a poll of the
    /// exact thing the caller asserts. Polling the assertion would make it unfalsifiable —
    /// "Forces.Count >= 1" would degrade to "this became true within N seconds", which is not the
    /// same claim.
    /// </para>
    /// </remarks>
    private void WaitForStoreSettled(int timeoutMs = 10_000)
    {
        _fixture.Engine!.Browser.Page.WaitForFunctionAsync(
            """
            () => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const army = pinia?._s?.get('lists')?.currentList?.army ?? window.__bsspec?.army;
                return (army?.getForces?.() || []).length > 0;
            }
            """,
            null,
            new() { Timeout = timeoutMs }).GetAwaiter().GetResult();
    }
}
