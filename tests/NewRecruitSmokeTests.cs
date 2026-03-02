using BattleScribeSpec;
using BattleScribeSpec.NewRecruit;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

/// <summary>
/// End-to-end smoke test for the New Recruit adapter.
/// Exercises the full pipeline: CatXmlGenerator → NR data loading → actions → state reading.
/// Skipped when NR_ENGINE_URL is not set.
/// </summary>
[Collection("NewRecruit")]
public sealed class NewRecruitSmokeTests
{
    private readonly ITestOutputHelper _output;
    private readonly NewRecruitFixture _fixture;

    public NewRecruitSmokeTests(ITestOutputHelper output, NewRecruitFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    [SkippableFact]
    public void Smoke_Setup_CreatesRosterWithNoErrors()
    {
        Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new GameSystemSpec(
            Id: "smoke-gs",
            Name: "Smoke Test System",
            CostTypes: [new CostTypeSpec(Id: "pts", Name: "Points")],
            CategoryEntries: [new CategoryEntrySpec(Id: "cat-troops", Name: "Troops")],
            ForceEntries:
            [
                new ForceEntrySpec(
                    Id: "fe-main",
                    Name: "Main Force",
                    CategoryLinks:
                    [
                        new CategoryLinkSpec(Id: "cl-1", TargetId: "cat-troops", Name: "Troops")
                    ])
            ]);

        var cat = new CatalogueSpec(
            Id: "smoke-cat",
            Name: "Smoke Catalogue",
            GameSystemId: "smoke-gs",
            SelectionEntries:
            [
                new SelectionEntrySpec(
                    Id: "se-warrior",
                    Name: "Warrior",
                    Type: "unit",
                    Costs: [new CostSpec(Name: "Points", TypeId: "pts", Value: 50)],
                    CategoryLinks:
                    [
                        new CategoryLinkSpec(Id: "cl-w", TargetId: "cat-troops", Name: "Troops", Primary: true)
                    ])
            ]);

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

        var gs = new GameSystemSpec(
            Id: "smoke-gs2",
            Name: "Smoke System 2",
            CostTypes: [new CostTypeSpec(Id: "pts", Name: "pts")],
            CategoryEntries: [new CategoryEntrySpec(Id: "cat-1", Name: "Troops")],
            ForceEntries:
            [
                new ForceEntrySpec(
                    Id: "fe-1",
                    Name: "Battalion",
                    CategoryLinks:
                    [
                        new CategoryLinkSpec(Id: "cl-1", TargetId: "cat-1", Name: "Troops")
                    ])
            ]);

        var cat = new CatalogueSpec(
            Id: "smoke-cat2",
            Name: "Cat",
            GameSystemId: "smoke-gs2",
            SelectionEntries:
            [
                new SelectionEntrySpec(
                    Id: "se-1",
                    Name: "Unit A",
                    Type: "unit",
                    Costs: [new CostSpec(Name: "pts", TypeId: "pts", Value: 100)])
            ]);

        var errors = _fixture.Engine!.Setup(gs, [cat]);
        Assert.Empty(errors);

        _output.WriteLine("Adding force...");
        _fixture.Engine.AddForce(0);

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
        var gs = new GameSystemSpec(
            Id: "xml-test",
            Name: "XML Test",
            CostTypes:
            [
                new CostTypeSpec(Id: "pts", Name: "Points"),
                new CostTypeSpec(Id: "pl", Name: "Power Level")
            ],
            CategoryEntries:
            [
                new CategoryEntrySpec(Id: "cat-hq", Name: "HQ"),
                new CategoryEntrySpec(Id: "cat-tr", Name: "Troops")
            ],
            ForceEntries:
            [
                new ForceEntrySpec(
                    Id: "fe-1",
                    Name: "Patrol",
                    CategoryLinks:
                    [
                        new CategoryLinkSpec(Id: "cl-1", TargetId: "cat-hq", Name: "HQ"),
                        new CategoryLinkSpec(Id: "cl-2", TargetId: "cat-tr", Name: "Troops")
                    ])
            ]);

        var cat = new CatalogueSpec(
            Id: "cat-sm",
            Name: "Space Marines",
            GameSystemId: "xml-test",
            SelectionEntries:
            [
                new SelectionEntrySpec(
                    Id: "se-cap",
                    Name: "Captain",
                    Type: "model",
                    Costs:
                    [
                        new CostSpec(Name: "Points", TypeId: "pts", Value: 75),
                        new CostSpec(Name: "Power Level", TypeId: "pl", Value: 5)
                    ],
                    CategoryLinks:
                    [
                        new CategoryLinkSpec(Id: "cl-cap", TargetId: "cat-hq", Name: "HQ", Primary: true)
                    ],
                    ChildEntries:
                    [
                        new SelectionEntrySpec(
                            Id: "se-sword",
                            Name: "Power Sword",
                            Type: "upgrade",
                            Modifiers:
                            [
                                new ModifierSpec(
                                    Type: "set",
                                    Field: "hidden",
                                    Value: "true",
                                    Conditions:
                                    [
                                        new ConditionSpec(
                                            Type: "atLeast",
                                            Value: 1,
                                            Field: "selections",
                                            Scope: "parent",
                                            ChildId: "se-other")
                                    ])
                            ])
                    ])
            ]);

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
