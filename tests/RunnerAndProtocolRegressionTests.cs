using BattleScribeSpec.Protocol;
using WarHub.ArmouryModel.Source;
using WarHub.ArmouryModel.Source.BattleScribe;

namespace BattleScribeSpec.Tests;

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
            Steps = [new StepDef { Action = "addForce", ForceEntryIndex = 0 }]
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
                new StepDef { Action = "addForce", ForceEntryIndex = 0 },
                new StepDef { Assert = "forceCount", Expected = 1 }
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
                [new ForceState("force", "cat", [new SelectionState("Hidden Squad", "se-1", "unit", 1, Hidden: false, Costs: [], Children: [])])],
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
    public void ProtocolConverter_ThrowsOnEmptyCatalogueArray()
    {
        var gameSystem = new GameSystemSpec("gs", "GS");
        Assert.Throws<ArgumentException>(() => ProtocolConverter.ToSetupCommand(gameSystem, []));
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
        public bool ThrowOnAddForce { get; init; }
        public int ActionCalls { get; private set; }
        public int GetStateCalls { get; private set; }

        public RosterState State { get; init; } = new("roster", "gs", [], [], []);

        public IReadOnlyList<string> Setup(GameSystemSpec gameSystem, CatalogueSpec[] catalogues) => SetupErrors;

        public void AddForce(int forceEntryIndex, int catalogueIndex = 0)
        {
            ActionCalls++;
            if (ThrowOnAddForce)
                throw new InvalidOperationException("boom");
        }

        public void RemoveForce(int forceIndex) => ActionCalls++;

        public void SelectEntry(int forceIndex, int entryIndex) => ActionCalls++;

        public void SelectChildEntry(int forceIndex, int selectionIndex, int childEntryIndex) => ActionCalls++;

        public void DeselectSelection(int forceIndex, int selectionIndex) => ActionCalls++;

        public void SetSelectionCount(int forceIndex, int entryIndex, int count) => ActionCalls++;

        public void DuplicateSelection(int forceIndex, int selectionIndex) => ActionCalls++;

        public void SetCostLimit(string costTypeId, double value) => ActionCalls++;

        public RosterState GetRosterState()
        {
            GetStateCalls++;
            return State;
        }

        public IReadOnlyList<ValidationErrorState> GetValidationErrors() => State.ValidationErrors;

        public void Dispose()
        {
        }
    }
}

