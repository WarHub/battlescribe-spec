using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Protocol;
using WarHub.ArmouryModel.Source;
using WarHub.ArmouryModel.Source.BattleScribe;

namespace BattleScribeSpec.Tests;

[Trait("Category", "Unit")]
public class RunnerAndProtocolRegressionTests
{
    [Fact]
    public void SpecRunner_PropagatesSetupErrors_AndSkipsSteps()
    {
        var engine = new FakeEngine { SetupErrors = ["missing catalogue"] };
        var runner = new SpecRunner(engine);
        var spec = new SpecFile
        {
            Id = "setup-errors",
            Category = "runner",
            Description = "setup errors should stop execution",
            Setup = new SetupDef { GameSystem = new GameSystemDef(), Catalogues = [new CatalogueDef()] },
            Steps = [new StepDef { Action = "addForce", ForceEntryId = "fe-1" }]
        };

        var result = runner.Run(spec);

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Contains("Setup error: missing catalogue"));
        Assert.Equal(0, engine.ActionCalls);
    }

    [Fact]
    public void SpecRunner_StopsAfterActionException()
    {
        var engine = new FakeEngine { ThrowOnAddForce = true };
        var runner = new SpecRunner(engine);
        var spec = new SpecFile
        {
            Id = "action-stop",
            Category = "runner",
            Description = "action failure should stop later steps",
            Setup = new SetupDef { GameSystem = new GameSystemDef(), Catalogues = [new CatalogueDef()] },
            Steps =
            [
                new StepDef { Action = "addForce", ForceEntryId = "fe-1" },
                new StepDef { ExpectedState = new ExpectedStateDef { ForceCount = 1 } }
            ]
        };

        var result = runner.Run(spec);

        Assert.False(result.Passed);
        Assert.Single(result.Failures);
        Assert.Contains("Step 0", result.Failures[0]);
        Assert.Equal(0, engine.GetStateCalls);
    }

    [Fact]
    public void SpecRunner_AssertsSelectionHidden()
    {
        var engine = new FakeEngine
        {
            State = new RosterState(
                "roster",
                "gs",
                [new ForceState(Id: null, "force", "cat", [new SelectionState(Id: null, "Hidden Squad", "se-1", "unit", 1, Hidden: false, Costs: [], Children: [])])],
                [],
                [])
        };
        var runner = new SpecRunner(engine);
        var spec = new SpecFile
        {
            Id = "hidden-assert",
            Category = "runner",
            Description = "hidden field should be asserted",
            Setup = new SetupDef { GameSystem = new GameSystemDef(), Catalogues = [new CatalogueDef()] },
            Steps =
            [
                new StepDef
                {
                    ExpectedState = new ExpectedStateDef
                    {
                        Forces =
                        [
                            new ExpectedForceDef
                            {
                                Selections =
                                [
                                    new ExpectedSelectionDef { Name = "Hidden Squad", Hidden = true }
                                ]
                            }
                        ]
                    }
                }
            ]
        };

        var result = runner.Run(spec);

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Contains(".hidden"));
    }

    [Fact]
    public void SpecRunner_CallsCleanup_AfterSuccessfulRun()
    {
        var engine = new FakeEngine();
        var runner = new SpecRunner(engine);
        var spec = new SpecFile
        {
            Id = "cleanup-success",
            Category = "runner",
            Description = "cleanup should be called after successful spec",
            Setup = new SetupDef { GameSystem = new GameSystemDef(), Catalogues = [new CatalogueDef()] },
            Steps = [new StepDef { ExpectedState = new ExpectedStateDef { ForceCount = 0 } }]
        };

        runner.Run(spec);

        Assert.Equal(1, engine.CleanupCalls);
    }

    [Fact]
    public void SpecRunner_CallsCleanup_AfterFailedAction()
    {
        var engine = new FakeEngine { ThrowOnAddForce = true };
        var runner = new SpecRunner(engine);
        var spec = new SpecFile
        {
            Id = "cleanup-failure",
            Category = "runner",
            Description = "cleanup should be called even after action failure",
            Setup = new SetupDef { GameSystem = new GameSystemDef(), Catalogues = [new CatalogueDef()] },
            Steps = [new StepDef { Action = "addForce", ForceEntryId = "fe-1" }]
        };

        runner.Run(spec);

        Assert.Equal(1, engine.CleanupCalls);
    }

    [Fact]
    public void SpecRunner_CallsCleanup_AfterSetupErrors()
    {
        var engine = new FakeEngine { SetupErrors = ["bad setup"] };
        var runner = new SpecRunner(engine);
        var spec = new SpecFile
        {
            Id = "cleanup-setup-error",
            Category = "runner",
            Description = "cleanup should be called even after setup errors",
            Setup = new SetupDef { GameSystem = new GameSystemDef(), Catalogues = [new CatalogueDef()] },
            Steps = [new StepDef { Action = "addForce", ForceEntryId = "fe-1" }]
        };

        runner.Run(spec);

        Assert.Equal(1, engine.CleanupCalls);
    }

    [Fact]
    public void SpecRunner_CallsCleanup_AfterSetupException()
    {
        var engine = new FakeEngine { ThrowOnSetup = true };
        var runner = new SpecRunner(engine);
        var spec = new SpecFile
        {
            Id = "cleanup-setup-exception",
            Category = "runner",
            Description = "cleanup should be called even after setup throws",
            Setup = new SetupDef { GameSystem = new GameSystemDef(), Catalogues = [new CatalogueDef()] },
            Steps = [new StepDef { ExpectedState = new ExpectedStateDef { ForceCount = 0 } }]
        };

        var result = runner.Run(spec);

        Assert.Equal(1, engine.CleanupCalls);
        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Contains("Setup failed"));
    }

    [Fact]
    public void CatXmlGenerator_ThrowsOnEmptyCatalogueArray()
    {
        var gameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" };
        Assert.Throws<ArgumentException>(() => CatXmlGenerator.GenerateCatalogueXml(gameSystem, Array.Empty<ProtocolCatalogue>()));
    }

    [Fact]
    public void DataLoader_LoadDirectory_Throws_WhenMultipleGamesystemsFound()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bs-spec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            WriteNodeToFile(TestDataFactory.CreateMinimalGamesystem(), Path.Combine(tempDir, "one.gst"));
            WriteNodeToFile(TestDataFactory.CreateMinimalGamesystem(), Path.Combine(tempDir, "two.gst"));

            var ex = Assert.Throws<InvalidOperationException>(() => DataLoader.LoadDirectory(tempDir));
            Assert.Contains("at most one .gst file", ex.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void WriteNodeToFile(SourceNode node, string filePath)
    {
        using var stream = File.Create(filePath);
        using var writer = new StreamWriter(stream, leaveOpen: true);
        BattleScribeXmlSerializer.Instance.Serialize(node, writer);
    }

    private sealed class FakeEngine : IRosterEngine
    {
        public IReadOnlyList<string> SetupErrors { get; init; } = [];
        public bool ThrowOnSetup { get; init; }
        public bool ThrowOnAddForce { get; init; }
        public int ActionCalls { get; private set; }
        public int GetStateCalls { get; private set; }
        public int CleanupCalls { get; private set; }

        public RosterState State { get; init; } = new("roster", "gs", [], [], []);

        public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
        {
            if (ThrowOnSetup)
                throw new InvalidOperationException("setup boom");
            return SetupErrors;
        }

        public ActionOutputs AddForce(string forceEntryId, string? catalogueId = null)
        {
            ActionCalls++;
            if (ThrowOnAddForce)
                throw new InvalidOperationException("boom");
            return new ActionOutputs { ForceId = "force-1" };
        }

        public ActionOutputs AddChildForce(string parentForceId, string forceEntryId)
        {
            ActionCalls++;
            return new ActionOutputs { ForceId = "child-force-1" };
        }

        public void RemoveForce(string forceId) => ActionCalls++;

        public ActionOutputs SelectEntry(string forceId, string entryId)
        {
            ActionCalls++;
            return new ActionOutputs { SelectionId = "sel-1" };
        }

        public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId)
        {
            ActionCalls++;
            return new ActionOutputs { SelectionId = "child-sel-1" };
        }

        public void DeselectSelection(string forceId, string selectionId) => ActionCalls++;

        public void SetSelectionCount(string forceId, string selectionId, int count) => ActionCalls++;

        public ActionOutputs DuplicateSelection(string forceId, string selectionId)
        {
            ActionCalls++;
            return new ActionOutputs { SelectionId = "dup-sel-1" };
        }

        public void SetCostLimit(string costTypeId, double value) => ActionCalls++;

        public RosterState GetRosterState()
        {
            GetStateCalls++;
            return State;
        }

        public IReadOnlyList<ValidationErrorState> GetValidationErrors() => State.ValidationErrors;

        public void Cleanup() => CleanupCalls++;

        public void Dispose()
        {
        }
    }
}

