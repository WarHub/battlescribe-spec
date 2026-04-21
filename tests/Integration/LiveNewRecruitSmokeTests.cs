using BattleScribeSpec;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// End-to-end smoke test for the New Recruit adapter.
/// Exercises the full pipeline: CatXmlGenerator → NR data loading → actions → state reading.
/// Skipped when NR_ENGINE_URL is not set.
/// </summary>
[Collection("SequentialLiveNewRecruit")]
[Trait("Category", "Smoke")]
public sealed class LiveNewRecruitSmokeTests
{
    private readonly ITestOutputHelper _output;
    private readonly SequentialLiveNewRecruitFixture _fixture;

    public LiveNewRecruitSmokeTests(ITestOutputHelper output, SequentialLiveNewRecruitFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    [SkippableFact]
    public void Smoke_Setup_CreatesRosterWithNoErrors()
    {
        Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new ProtocolGameSystem
        {
            Id = "smoke-gs",
            Name = "Smoke Test System",
            CostTypes = [new ProtocolCostType { Id = "pts", Name = "Points" }],
            CategoryEntries = [new ProtocolCategoryEntry { Id = "cat-troops", Name = "Troops" }],
            ForceEntries =
            [
                new ProtocolForceEntry
                {
                    Id = "fe-main",
                    Name = "Main Force",
                    CategoryLinks =
                    [
                        new ProtocolCategoryLink { Id = "cl-1", TargetId = "cat-troops", Name = "Troops" }
                    ],
                },
            ],
        };

        var cat = new ProtocolCatalogue
        {
            Id = "smoke-cat",
            Name = "Smoke Catalogue",
            GameSystemId = "smoke-gs",
            SelectionEntries =
            [
                new ProtocolSelectionEntry
                {
                    Id = "se-warrior",
                    Name = "Warrior",
                    Type = "unit",
                    Costs = [new ProtocolCostValue { Name = "Points", TypeId = "pts", Value = 50 }],
                    CategoryLinks =
                    [
                        new ProtocolCategoryLink { Id = "cl-w", TargetId = "cat-troops", Name = "Troops", Primary = true }
                    ],
                },
            ],
        };

        _output.WriteLine("Calling Setup...");
        var errors = _fixture.Engine!.Setup(gs, [cat]);

        foreach (var err in errors)
            _output.WriteLine($"  Setup error: {err}");

        Assert.Empty(errors);
        _output.WriteLine("Setup succeeded with no errors.");
    }

    [SkippableFact]
    public void Smoke_AddForceAndReadState()
    {
        Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new ProtocolGameSystem
        {
            Id = "smoke-gs2",
            Name = "Smoke System 2",
            CostTypes = [new ProtocolCostType { Id = "pts", Name = "pts" }],
            CategoryEntries = [new ProtocolCategoryEntry { Id = "cat-1", Name = "Troops" }],
            ForceEntries =
            [
                new ProtocolForceEntry
                {
                    Id = "fe-1",
                    Name = "Battalion",
                    CategoryLinks =
                    [
                        new ProtocolCategoryLink { Id = "cl-1", TargetId = "cat-1", Name = "Troops" }
                    ],
                },
            ],
        };

        var cat = new ProtocolCatalogue
        {
            Id = "smoke-cat2",
            Name = "Cat",
            GameSystemId = "smoke-gs2",
            SelectionEntries =
            [
                new ProtocolSelectionEntry
                {
                    Id = "se-1",
                    Name = "Unit A",
                    Type = "unit",
                    Costs = [new ProtocolCostValue { Name = "pts", TypeId = "pts", Value = 100 }],
                },
            ],
        };

        var errors = _fixture.Engine!.Setup(gs, [cat]);
        Assert.Empty(errors);

        _output.WriteLine("Adding force...");
        _fixture.Engine.AddForce("fe-1");

        _output.WriteLine("Reading roster state...");
        var state = _fixture.Engine.GetRosterState();

        _output.WriteLine($"Roster: name='{state.Name}', forces={state.Forces.Count}, costs={state.Costs.Count}");
        foreach (var force in state.Forces)
        {
            _output.WriteLine($"  Force: '{force.Name}', selections={force.Selections.Count}");
            foreach (var sel in force.Selections)
                _output.WriteLine($"    Selection: '{sel.Name}', type={sel.Type}, costs={sel.Costs.Count}");
        }

        Assert.True(state.Forces.Count >= 1, $"Expected at least 1 force, got {state.Forces.Count}");
    }

    [SkippableFact]
    public void Smoke_RunSimpleSpec()
    {
        Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

        // Run the simplest existing spec against NR to validate the full pipeline
        var specsDir = SpecLoader.FindSpecsDirectory();
        Skip.If(specsDir is null, "Specs directory not found");

        // Find a basic spec to run
        var specFiles = SpecLoader.DiscoverSpecs(specsDir!)
            .Where(s => s.Id.Contains("basic") || s.Id.Contains("simple"))
            .Take(3)
            .ToList();

        Skip.If(specFiles.Count == 0, "No basic/simple specs found");

        foreach (var (path, id, category) in specFiles)
        {
            _output.WriteLine($"Running spec: {category}/{id}");
            var spec = SpecLoader.Load(path);

            if (!spec.IsApplicableTo("newrecruit"))
            {
                _output.WriteLine($"  Skipped (not applicable to newrecruit)");
                continue;
            }

            var runner = new SpecRunner(_fixture.Engine!);
            var result = runner.Run(spec);

            _output.WriteLine($"  Result: {(result.Passed ? "PASS" : "FAIL")}");
            foreach (var failure in result.Failures)
                _output.WriteLine($"    {failure}");
        }
    }

    [SkippableFact]
    public void Smoke_CatXmlGeneratorProducesValidXml()
    {
        // This test doesn't need NR, but validates the XML generation pipeline
        // that feeds into the NR engine
        var gs = new ProtocolGameSystem
        {
            Id = "xml-test",
            Name = "XML Test",
            CostTypes =
            [
                new ProtocolCostType { Id = "pts", Name = "Points" },
                new ProtocolCostType { Id = "pl", Name = "Power Level" },
            ],
            CategoryEntries =
            [
                new ProtocolCategoryEntry { Id = "cat-hq", Name = "HQ" },
                new ProtocolCategoryEntry { Id = "cat-tr", Name = "Troops" },
            ],
            ForceEntries =
            [
                new ProtocolForceEntry
                {
                    Id = "fe-1",
                    Name = "Patrol",
                    CategoryLinks =
                    [
                        new ProtocolCategoryLink { Id = "cl-1", TargetId = "cat-hq", Name = "HQ" },
                        new ProtocolCategoryLink { Id = "cl-2", TargetId = "cat-tr", Name = "Troops" },
                    ],
                },
            ],
        };

        var cat = new ProtocolCatalogue
        {
            Id = "cat-sm",
            Name = "Space Marines",
            GameSystemId = "xml-test",
            SelectionEntries =
            [
                new ProtocolSelectionEntry
                {
                    Id = "se-cap",
                    Name = "Captain",
                    Type = "model",
                    Costs =
                    [
                        new ProtocolCostValue { Name = "Points", TypeId = "pts", Value = 75 },
                        new ProtocolCostValue { Name = "Power Level", TypeId = "pl", Value = 5 },
                    ],
                    CategoryLinks =
                    [
                        new ProtocolCategoryLink { Id = "cl-cap", TargetId = "cat-hq", Name = "HQ", Primary = true }
                    ],
                    SelectionEntries =
                    [
                        new ProtocolSelectionEntry
                        {
                            Id = "se-sword",
                            Name = "Power Sword",
                            Type = "upgrade",
                            Modifiers =
                            [
                                new ProtocolModifier
                                {
                                    Type = "set",
                                    Field = "hidden",
                                    Value = "true",
                                    Conditions =
                                    [
                                        new ProtocolCondition
                                        {
                                            Type = "atLeast",
                                            Value = 1,
                                            Field = "selections",
                                            Scope = "parent",
                                            ChildId = "se-other",
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        var gstXml = CatXmlGenerator.GenerateGameSystemXml(gs);
        var catXml = CatXmlGenerator.GenerateCatalogueXml(gs, cat);

        Assert.Contains("xml-test", gstXml);
        Assert.Contains("XML Test", gstXml);
        Assert.Contains("Points", gstXml);
        Assert.Contains("Patrol", gstXml);

        Assert.Contains("cat-sm", catXml);
        Assert.Contains("Space Marines", catXml);
        Assert.Contains("Captain", catXml);
        Assert.Contains("Power Sword", catXml);
        Assert.Contains("hidden", catXml);

        _output.WriteLine($"GST XML size: {gstXml.Length} chars");
        _output.WriteLine($"CAT XML size: {catXml.Length} chars");
    }
}
