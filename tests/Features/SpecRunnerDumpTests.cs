using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests;

[Trait("Category", "Unit")]
public class SpecRunnerDumpTests
{
    private static ProtocolGameSystem MinimalGs() => new()
    {
        Id = "dump-gs",
        Name = "Dump Test",
        ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Force" }],
        CostTypes = [new ProtocolCostType { Id = "pts", Name = "pts" }]
    };

    private static ProtocolCatalogue MinimalCat() => new()
    {
        Id = "dump-cat",
        Name = "Dump Cat",
        GameSystemId = "dump-gs",
        SelectionEntries =
        [
            new ProtocolSelectionEntry { Id = "se-1", Name = "Unit", Type = "unit" }
        ]
    };

    [Fact]
    public void OnStepCompleted_CalledAfterAction()
    {
        using var engine = new BattleScribeRosterEngine();
        var runner = new SpecRunner(engine, engineName: "battlescribe");

        var callbackSteps = new List<(int Index, string? Action)>();
        runner.OnStepCompleted = (index, step, state, errors) => callbackSteps.Add((index, step.Action));

        var spec = new SpecFile
        {
            Id = "dump-test-action",
            Category = "test",
            Description = "test",
            Setup = new SetupDef { GameSystem = MinimalGs(), Catalogues = [MinimalCat()] },
            Steps =
            [
                new StepDef { Action = "addForce", ForceEntryId = "fe-1" }
            ]
        };

        runner.Run(spec);

        Assert.Single(callbackSteps);
        Assert.Equal(0, callbackSteps[0].Index);
        Assert.Equal("addForce", callbackSteps[0].Action);
    }

    [Fact]
    public void OnStepCompleted_CalledAfterAssertion()
    {
        using var engine = new BattleScribeRosterEngine();
        var runner = new SpecRunner(engine, engineName: "battlescribe");

        var states = new List<RosterState>();
        runner.OnStepCompleted = (_, _, state, _) => states.Add(state);

        var spec = new SpecFile
        {
            Id = "dump-test-assertion",
            Category = "test",
            Description = "test",
            Setup = new SetupDef { GameSystem = MinimalGs(), Catalogues = [MinimalCat()] },
            Steps =
            [
                new StepDef { Action = "addForce", ForceEntryId = "fe-1" },
                new StepDef
                {
                    ExpectedState = new ExpectedStateDef { ForceCount = 1 }
                }
            ]
        };

        runner.Run(spec);

        Assert.Equal(2, states.Count);
        // After addForce, should have 1 force
        Assert.Single(states[0].Forces);
        // After assertion, state should be the same
        Assert.Single(states[1].Forces);
    }

    [Fact]
    public void DumpAction_IsNoOp_ButTriggersCallback()
    {
        using var engine = new BattleScribeRosterEngine();
        var runner = new SpecRunner(engine, engineName: "battlescribe");

        var dumpCalls = new List<int>();
        runner.OnStepCompleted = (index, step, _, _) =>
        {
            if (step.Action == "dump")
            {
                dumpCalls.Add(index);
            }
        };

        var spec = new SpecFile
        {
            Id = "dump-test-dump-action",
            Category = "test",
            Description = "test",
            Setup = new SetupDef { GameSystem = MinimalGs(), Catalogues = [MinimalCat()] },
            Steps =
            [
                new StepDef { Action = "addForce", ForceEntryId = "fe-1", Id = "af" },
                new StepDef { Action = "dump" },
                new StepDef { Action = "selectEntry", ForceId = "${{ steps.af.forceId }}", EntryId = "se-1" }
            ]
        };

        var result = runner.Run(spec);

        Assert.Single(dumpCalls);
        Assert.Equal(1, dumpCalls[0]);
        // The spec should complete without errors from the dump action
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void DumpAction_DoesNotBreakSpecExecution()
    {
        using var engine = new BattleScribeRosterEngine();
        var runner = new SpecRunner(engine, engineName: "battlescribe");

        var spec = new SpecFile
        {
            Id = "dump-test-no-break",
            Category = "test",
            Description = "test",
            Setup = new SetupDef { GameSystem = MinimalGs(), Catalogues = [MinimalCat()] },
            Steps =
            [
                new StepDef { Action = "addForce", ForceEntryId = "fe-1", Id = "af2" },
                new StepDef { Action = "dump" },
                new StepDef { Action = "selectEntry", ForceId = "${{ steps.af2.forceId }}", EntryId = "se-1" },
                new StepDef
                {
                    ExpectedState = new ExpectedStateDef
                    {
                        Forces = [new ExpectedForceDef { SelectionCount = 1 }]
                    }
                }
            ]
        };

        // No callback set — dump should be silently ignored
        var result = runner.Run(spec);

        Assert.Empty(result.Failures);
    }

    [Fact]
    public void OnStepCompleted_ReceivesValidState()
    {
        using var engine = new BattleScribeRosterEngine();
        var runner = new SpecRunner(engine, engineName: "battlescribe");

        RosterState? capturedState = null;
        IReadOnlyList<ValidationErrorState>? capturedErrors = null;
        runner.OnStepCompleted = (_, _, state, errors) =>
        {
            capturedState = state;
            capturedErrors = errors;
        };

        var spec = new SpecFile
        {
            Id = "dump-test-valid-state",
            Category = "test",
            Description = "test",
            Setup = new SetupDef { GameSystem = MinimalGs(), Catalogues = [MinimalCat()] },
            Steps =
            [
                new StepDef { Action = "addForce", ForceEntryId = "fe-1", Id = "af3" },
                new StepDef { Action = "selectEntry", ForceId = "${{ steps.af3.forceId }}", EntryId = "se-1" }
            ]
        };

        runner.Run(spec);

        Assert.NotNull(capturedState);
        Assert.Equal("dump-gs", capturedState!.GameSystemId);
        Assert.Single(capturedState.Forces);
        Assert.Single(capturedState.Forces[0].Selections);
        Assert.Equal("Unit", capturedState.Forces[0].Selections[0].Name);
        Assert.NotNull(capturedErrors);
        Assert.Empty(capturedErrors!);
    }
}
