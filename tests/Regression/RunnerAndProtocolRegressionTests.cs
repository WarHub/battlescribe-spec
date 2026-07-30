using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using BattleScribeSpec.XmlGen;
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
        var runner = new RosterRunner(engine);
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

    // ── Engine identity: base vs concrete ────────────────────────────────
    // A UI driver asserts under its BASE engine's name (so it inherits every `engines: battlescribe:`
    // expectation) but must be gateable by its CONCRETE name, because it does not necessarily support
    // what the base engine supports. Collapsing the two made `skipEngines: [battlescribe-ui]` and
    // `engines: {battlescribe-ui: …}` silently inert under `bs-spec run --engine battlescribe --ui`.

    [Theory]
    // Concrete identity named → skip fires.
    [InlineData("battlescribe", "battlescribe-ui", "battlescribe-ui", 0)]
    // Base identity named → still fires, as it always did.
    [InlineData("battlescribe", "battlescribe-ui", "battlescribe", 0)]
    // Neither identity named → the step runs. An engine that genuinely cannot do this must FAIL
    // loudly, not be skipped by accident.
    [InlineData("battlescribe", "battlescribe-ui", "newrecruit", 1)]
    // No -ui in play: the single identity behaves exactly as before.
    [InlineData("battlescribe", null, "battlescribe", 0)]
    [InlineData("battlescribe", null, "newrecruit", 1)]
    public void SpecRunner_SkipEngines_MatchesBaseOrConcreteEngineIdentity(
        string engineName, string? engineIdentity, string skipName, int expectedActionCalls)
    {
        var engine = new FakeEngine();
        var runner = new RosterRunner(engine, null, engineName, engineIdentity);
        var spec = new SpecFile
        {
            Id = "skip-identity",
            Category = "runner",
            Description = "skipEngines matches either engine identity",
            Setup = new SetupDef { GameSystem = new GameSystemDef(), Catalogues = [new CatalogueDef()] },
            Steps = [new StepDef { Action = "addForce", ForceEntryId = "fe-1", SkipEngines = [skipName] }]
        };

        runner.Run(spec);

        Assert.Equal(expectedActionCalls, engine.ActionCalls);
    }

    [Theory]
    // The concrete engine has its own entry → most specific wins.
    [InlineData("battlescribe-ui", 7)]
    // It does not → it inherits the base engine's entry, which is what nearly every spec relies on.
    [InlineData("newrecruit-ui", 3)]
    public void SpecRunner_ExpectedStateOverride_PrefersConcreteIdentityThenBase(
        string engineIdentity, int expectedForceCount)
    {
        var engine = new FakeEngine
        {
            State = new RosterState("roster", "gs", [], [], []),
        };
        var runner = new RosterRunner(engine, null, "battlescribe", engineIdentity);
        var spec = new SpecFile
        {
            Id = "override-identity",
            Category = "runner",
            Description = "per-engine overrides resolve concrete-then-base",
            Setup = new SetupDef { GameSystem = new GameSystemDef(), Catalogues = [new CatalogueDef()] },
            Steps =
            [
                new StepDef
                {
                    ExpectedState = new ExpectedStateDef
                    {
                        ForceCount = 99,
                        Engines = new Dictionary<string, ExpectedStateDef>
                        {
                            ["battlescribe"] = new() { ForceCount = 3 },
                            ["battlescribe-ui"] = new() { ForceCount = 7 },
                        },
                    },
                }
            ]
        };

        var result = runner.Run(spec);

        // The fake reports zero forces, so the failure text tells us which expectation was applied.
        Assert.Contains(result.Failures, f => f.Contains($"expected {expectedForceCount} but got 0"));
    }

    // ── expectedFile must never pass vacuously ───────────────────────────
    // `RosterRunner.ExecuteFileAssertion` used to `catch (NotSupportedException) { return; }`, so an
    // engine that could not export made every byte-compare pass while comparing nothing. #326 removed
    // the trigger (the host wired the exporter for battlescribe-ui only, so three of four engines
    // reported "unsupported" over the protocol); these gates remove the swallow, so the next engine,
    // adapter, or regression that cannot export fails loudly instead of going green.

    [Fact]
    public void ExpectedFile_WhenTheEngineCannotExport_Fails()
    {
        // RosterXml unset → ExportRosterXml throws NotSupportedException, exactly as the interface
        // default does for an engine that never implemented it.
        var engine = new FakeEngine();
        var runner = new RosterRunner(engine, null, "battlescribe");
        var result = runner.Run(FileAssertionSpec());

        Assert.False(result.Passed);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("expectedFile", failure, StringComparison.Ordinal);
        // The message must name the engine and both opt-outs — a failure the reader cannot act on
        // invites the swallow back.
        Assert.Contains("battlescribe", failure, StringComparison.Ordinal);
        Assert.Contains("skipEngines", failure, StringComparison.Ordinal);
        Assert.Contains("skip", failure, StringComparison.Ordinal);
        // Not a harness crash: the engine answered honestly, the spec just failed to declare it.
        Assert.Null(result.HarnessError);
        Assert.Empty(result.SkippedSteps);
    }

    [Fact]
    public void ExpectedFile_WhenTheSpecOptsTheEngineOut_PassesAndSaysSo()
    {
        var engine = new FakeEngine();
        var runner = new RosterRunner(engine, null, "battlescribe");
        var spec = FileAssertionSpec();
        spec.Steps[^1].SkipEngines = ["battlescribe"];

        var result = runner.Run(spec);

        Assert.True(result.Passed);
        // The declared opt-out is honoured — and reported, so a pass that verified less than the
        // spec describes is visible rather than indistinguishable from a full run.
        var skipped = Assert.Single(result.SkippedSteps);
        Assert.Contains("expectedFile", skipped, StringComparison.Ordinal);
        Assert.Contains("battlescribe", skipped, StringComparison.Ordinal);
        Assert.Equal(0, engine.ExportCalls);
    }

    /// <summary>
    /// <c>skipEngines</c> on a non-action step used to be silently inert: the check lived inside
    /// <c>ExecuteAction</c>, which assertion steps never reach. Harmless while assertions could not
    /// trip over a capability gap; a trap the moment they can, since it is the very declaration the
    /// new failure message tells authors to write.
    /// </summary>
    [Fact]
    public void SkipEngines_IsHonoredOnExpectedStateSteps()
    {
        var engine = new FakeEngine();
        var runner = new RosterRunner(engine, null, "battlescribe");
        var spec = new SpecFile
        {
            Id = "skip-assertion",
            Category = "runner",
            Description = "skipEngines applies to assertion steps, not just actions",
            Setup = new SetupDef { GameSystem = new GameSystemDef(), Catalogues = [new CatalogueDef()] },
            Steps =
            [
                new StepDef
                {
                    // Unsatisfiable: the fake reports zero forces. Only the skip can keep this green.
                    ExpectedState = new ExpectedStateDef { ForceCount = 42 },
                    SkipEngines = ["battlescribe"],
                },
            ],
        };

        var result = runner.Run(spec);

        Assert.True(result.Passed);
        Assert.Single(result.SkippedSteps);
        Assert.Equal(0, engine.GetStateCalls);
    }

    /// <summary>
    /// The export is genuinely called and compared when the engine can export — otherwise "no
    /// failures" would prove nothing about whether the assertion ran at all, which is the exact
    /// ambiguity this whole area is about.
    /// </summary>
    [Fact]
    public void ExpectedFile_WhenTheEngineExports_ActuallyCompares()
    {
        var engine = new FakeEngine { RosterXml = "<roster>actual</roster>" };
        var runner = new RosterRunner(engine, null, "battlescribe");
        var spec = FileAssertionSpec();
        spec.Steps[^1].ExpectedFile!.Content = "<roster>expected</roster>";

        var result = runner.Run(spec);

        Assert.Equal(1, engine.ExportCalls);
        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Contains("does not match expected", StringComparison.Ordinal));
    }

    /// <summary>
    /// Moving the skip check out of <c>ExecuteAction</c> must not drop the empty-outputs store that
    /// keeps a downstream <c>${{ steps.&lt;id&gt; }}</c> reporting a missing <em>field</em> rather than
    /// a missing step.
    /// </summary>
    [Fact]
    public void SkippedAction_StillRegistersItsStepId()
    {
        var engine = new FakeEngine();
        var runner = new RosterRunner(engine, null, "battlescribe");
        var spec = new SpecFile
        {
            Id = "skip-outputs",
            Category = "runner",
            Description = "a skipped action still registers its step id",
            Setup = new SetupDef { GameSystem = new GameSystemDef(), Catalogues = [new CatalogueDef()] },
            Steps =
            [
                new StepDef { Action = "addForce", Id = "add", ForceEntryId = "fe-1", SkipEngines = ["battlescribe"] },
                new StepDef { Action = "removeForce", ForceId = "${{ steps.add.forceId }}" },
            ],
        };

        var result = runner.Run(spec);

        // The step resolves (not "step 'add' not found"); the field on it does not.
        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Contains("no forceId", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Failures, f => f.Contains("not found. Available steps", StringComparison.Ordinal));
    }

    /// <summary>A one-step spec whose only step is a side-file-free inline expectedFile byte-compare.</summary>
    private static SpecFile FileAssertionSpec() => new()
    {
        Id = "file-assertion",
        Category = "runner",
        Description = "expectedFile byte-compare",
        Setup = new SetupDef { GameSystem = new GameSystemDef(), Catalogues = [new CatalogueDef()] },
        Steps =
        [
            new StepDef
            {
                Id = "exported",
                ExpectedFile = new GameData.ExpectedFileDef { Content = "<roster/>" },
            },
        ],
    };

    [Fact]
    public void SpecRunner_StopsAfterActionException()
    {
        var engine = new FakeEngine { ThrowOnAddForce = true };
        var runner = new RosterRunner(engine);
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
        var runner = new RosterRunner(engine);
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
        var runner = new RosterRunner(engine);
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
        var runner = new RosterRunner(engine);
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
        var runner = new RosterRunner(engine);
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
        var runner = new RosterRunner(engine);
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
    public void SpecRunner_AssertsCosts_ExactSet_RosterLevel()
    {
        var engine = new FakeEngine
        {
            State = new RosterState(
                "roster", "gs", [],
                [new CostState("pts", "pts", 50), new CostState("PL", "pl", 3)],
                [])
        };
        var runner = new RosterRunner(engine);
        var spec = new SpecFile
        {
            Id = "costs-exact",
            Category = "runner",
            Description = "asserting costs should be exact-set — extra costs are failures",
            Setup = new SetupDef { GameSystem = new GameSystemDef(), Catalogues = [new CatalogueDef()] },
            Steps =
            [
                new StepDef
                {
                    ExpectedState = new ExpectedStateDef
                    {
                        Costs = [new ExpectedCostDef { TypeId = "pts", Value = 50 }]
                    }
                }
            ]
        };

        var result = runner.Run(spec);

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Contains("expected 1 cost(s) but got 2"));
    }

    [Fact]
    public void SpecRunner_AssertsCostLimits_ExactSet_RosterLevel()
    {
        var engine = new FakeEngine
        {
            State = new RosterState(
                "roster", "gs", [], [],
                [],
                CostLimits: [new CostState("pts", "pts", 1000), new CostState("PL", "pl", 50)])
        };
        var runner = new RosterRunner(engine);
        var spec = new SpecFile
        {
            Id = "costlimits-exact",
            Category = "runner",
            Description = "asserting costLimits should be exact-set — extra limits are failures",
            Setup = new SetupDef { GameSystem = new GameSystemDef(), Catalogues = [new CatalogueDef()] },
            Steps =
            [
                new StepDef
                {
                    ExpectedState = new ExpectedStateDef
                    {
                        CostLimits = [new ExpectedCostDef { TypeId = "pts", Value = 1000 }]
                    }
                }
            ]
        };

        var result = runner.Run(spec);

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Contains("expected 1 costLimit(s) but got 2"));
    }

    [Fact]
    public void SpecRunner_AssertsCosts_ExactSet_SelectionLevel()
    {
        var engine = new FakeEngine
        {
            State = new RosterState(
                "roster", "gs",
                [new ForceState(Id: null, "Force", "cat",
                    [new SelectionState(Id: null, "Unit", "se-1", "unit", 1, Hidden: false,
                        Costs: [new CostState("pts", "pts", 10), new CostState("PL", "pl", 1)],
                        Children: [])])],
                [],
                [])
        };
        var runner = new RosterRunner(engine);
        var spec = new SpecFile
        {
            Id = "sel-costs-exact",
            Category = "runner",
            Description = "asserting selection costs should be exact-set",
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
                                    new ExpectedSelectionDef
                                    {
                                        Name = "Unit",
                                        Costs = [new ExpectedCostDef { TypeId = "pts", Value = 10 }]
                                    }
                                ]
                            }
                        ]
                    }
                }
            ]
        };

        var result = runner.Run(spec);

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Contains("expected 1 cost(s) but got 2"));
    }

    [Fact]
    public void CatXmlGenerator_ThrowsOnEmptyCatalogueArray()
    {
        var gameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" };
        Assert.Throws<ArgumentException>(() => CatXmlGenerator.GenerateCatalogueXml(gameSystem, []));
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

        /// <summary>
        /// What <see cref="ExportRosterXml"/> returns. Null models an engine that cannot export at
        /// all — the interface default's <see cref="NotSupportedException"/>, which is the signal
        /// <c>RosterRunner</c> used to swallow.
        /// </summary>
        public string? RosterXml { get; init; }

        public int ExportCalls { get; private set; }
        public int ActionCalls { get; private set; }
        public int GetStateCalls { get; private set; }
        public int CleanupCalls { get; private set; }

        public RosterState State { get; init; } = new("roster", "gs", [], [], []);

        public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues)
        {
            if (ThrowOnSetup)
            {
                throw new InvalidOperationException("setup boom");
            }

            return SetupErrors;
        }

        public ActionOutputs AddForce(string forceEntryId, string catalogueId)
        {
            ActionCalls++;
            if (ThrowOnAddForce)
            {
                throw new InvalidOperationException("boom");
            }

            return new ActionOutputs { ForceId = "force-1" };
        }

        public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId)
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

        public ActionOutputs DuplicateForce(string forceId)
        {
            ActionCalls++;
            return new ActionOutputs { ForceId = "dup-force-1" };
        }

        public void SetCostLimit(string costTypeId, decimal value) => ActionCalls++;

        public RosterState GetRosterState()
        {
            GetStateCalls++;
            return State;
        }

        public IReadOnlyList<ValidationErrorState> GetValidationErrors() => State.ValidationErrors;

        public string ExportRosterXml()
        {
            ExportCalls++;
            return RosterXml
                ?? throw new NotSupportedException("This engine does not support roster XML export.");
        }

        public void Cleanup() => CleanupCalls++;

        public void Dispose()
        {
        }
    }
}

