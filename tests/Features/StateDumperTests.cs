using BattleScribeSpec;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests;

[Trait("Category", "Unit")]
public class StateDumperTests
{
    private static RosterState MinimalRoster(
        IReadOnlyList<ForceState>? forces = null,
        IReadOnlyList<CostState>? costs = null) =>
        new("Test Roster", "test-gs", forces ?? [], costs ?? [], []);

    private static ForceState SimpleForce(
        string name = "Detachment",
        string? catalogueId = "cat-1",
        IReadOnlyList<SelectionState>? selections = null) =>
        new(Id: null, name, catalogueId, selections ?? []);

    private static SelectionState SimpleSelection(
        string name = "Marine",
        string? entryId = "se-1",
        string type = "unit",
        int number = 1) =>
        new(Id: null, name, entryId, type, number, Hidden: false, Costs: [], Children: []);

    [Fact]
    public void DumpTree_MinimalRoster_WritesRosterHeader()
    {
        var state = MinimalRoster();
        var output = DumpToString(state);

        Assert.Contains("Roster: Test Roster", output);
        Assert.Contains("gameSystemId: test-gs", output);
        Assert.Contains("Forces: 0", output);
    }

    [Fact]
    public void DumpTree_WithForceAndSelection_WritesTree()
    {
        var sel = SimpleSelection();
        var force = SimpleForce(selections: [sel]);
        var state = MinimalRoster(forces: [force]);

        var output = DumpToString(state);

        Assert.Contains("Force[0]: \"Detachment\"", output);
        Assert.Contains("catalogueId=cat-1", output);
        Assert.Contains("Selections: 1", output);
        Assert.Contains("[0] \"Marine\" (unit) ×1", output);
        Assert.Contains("entryId=se-1", output);
    }

    [Fact]
    public void DumpTree_WithCosts_WritesCostLine()
    {
        var costs = new List<CostState> { new("pts", "ct-pts", 100), new("PL", "ct-pl", 5) };
        var state = MinimalRoster(costs: costs);

        var output = DumpToString(state);

        Assert.Contains("Costs: pts=100, PL=5", output);
    }

    [Fact]
    public void DumpTree_WithPublication_WritesPublicationFields()
    {
        var sel = new SelectionState(Id: null, "Marine", "se-1", "unit", 1, false, [], [],
            PublicationId: "pub-1", PublicationName: "Core Rulebook", Page: "42");
        var force = SimpleForce(selections: [sel]);
        var state = MinimalRoster(forces: [force]);

        var output = DumpToString(state);

        Assert.Contains("pub=\"Core Rulebook\"", output);
        Assert.Contains("pubId=pub-1", output);
        Assert.Contains("p.42", output);
    }

    [Fact]
    public void DumpTree_WithProfiles_WritesProfileAndCharacteristics()
    {
        var chars = new List<CharacteristicState> { new("WS", null, "3+"), new("BS", null, "4+") };
        var profile = new ProfileState("Power Armor", "pt-1", "Unit Stats", false, chars);
        var sel = new SelectionState(Id: null, "Marine", "se-1", "unit", 1, false, [], [],
            Profiles: [profile]);
        var force = SimpleForce(selections: [sel]);
        var state = MinimalRoster(forces: [force]);

        var output = DumpToString(state);

        Assert.Contains("\"Power Armor\" [Unit Stats]", output);
        Assert.Contains("WS=3+, BS=4+", output);
    }

    [Fact]
    public void DumpTree_WithRules_WritesRuleAndDescription()
    {
        var rule = new RuleState("Rapid Fire", "Double shots at half range", false);
        var sel = new SelectionState(Id: null, "Marine", "se-1", "unit", 1, false, [], [],
            Rules: [rule]);
        var force = SimpleForce(selections: [sel]);
        var state = MinimalRoster(forces: [force]);

        var output = DumpToString(state);

        Assert.Contains("\"Rapid Fire\"", output);
        Assert.Contains("Double shots at half range", output);
    }

    [Fact]
    public void DumpTree_WithCategories_WritesCategoryLine()
    {
        var cats = new List<CategoryState>
        {
            new("Troops", "ce-1", Primary: true),
            new("Infantry", "ce-2", Primary: false)
        };
        var sel = new SelectionState(Id: null, "Marine", "se-1", "unit", 1, false, [], [],
            Categories: cats);
        var force = SimpleForce(selections: [sel]);
        var state = MinimalRoster(forces: [force]);

        var output = DumpToString(state);

        Assert.Contains("Categories: [*Troops, Infantry]", output);
    }

    [Fact]
    public void DumpTree_NestedChildren_WritesRecursively()
    {
        var child = SimpleSelection(name: "Weapon", entryId: "se-weapon", type: "upgrade");
        var parent = new SelectionState(Id: null, "Marine", "se-1", "unit", 1, false, [], [child]);
        var force = SimpleForce(selections: [parent]);
        var state = MinimalRoster(forces: [force]);

        var output = DumpToString(state);

        Assert.Contains("Children: 1", output);
        Assert.Contains("\"Weapon\" (upgrade) ×1", output);
    }

    [Fact]
    public void DumpTree_HiddenSelection_ShowsHiddenTag()
    {
        var sel = new SelectionState(Id: null, "Secret", "se-1", "unit", 1, Hidden: true, Costs: [], Children: []);
        var force = SimpleForce(selections: [sel]);
        var state = MinimalRoster(forces: [force]);

        var output = DumpToString(state);

        Assert.Contains("[hidden]", output);
    }

    [Fact]
    public void DumpTree_ValidationErrors_WritesErrorList()
    {
        var state = MinimalRoster();
        var errors = new List<ValidationErrorState>
        {
            new("Over pts limit", OwnerType: "roster", EntryId: "costLimits")
        };

        var output = DumpToString(state, errors);

        Assert.Contains("Errors: 1", output);
        Assert.Contains("Over pts limit", output);
        Assert.Contains("owner=roster", output);
        Assert.Contains("entryId=costLimits", output);
    }

    [Fact]
    public void DumpTree_NoErrors_WritesNone()
    {
        var state = MinimalRoster();
        var output = DumpToString(state);

        Assert.Contains("Errors: (none)", output);
    }

    [Fact]
    public void DumpTree_ForcePublication_WritesForcePublicationFields()
    {
        var force = new ForceState(Id: null, "Battalion", "cat-1", [],
            PublicationId: "pub-1", Page: "10");
        var state = MinimalRoster(forces: [force]);

        var output = DumpToString(state);

        Assert.Contains("pub=pub-1", output);
        Assert.Contains("p.10", output);
    }

    [Fact]
    public void DumpTree_ChildForces_WritesRecursively()
    {
        var child = new ForceState(Id: null, "Patrol", "cat-1", []);
        var parent = new ForceState(Id: null, "Battalion", "cat-1", [], ChildForces: [child]);
        var state = MinimalRoster(forces: [parent]);

        var output = DumpToString(state);

        Assert.Contains("ChildForces: 1", output);
        Assert.Contains("\"Patrol\"", output);
    }

    [Fact]
    public void DumpJson_ReturnsValidJson()
    {
        var sel = SimpleSelection();
        var force = SimpleForce(selections: [sel]);
        var state = MinimalRoster(forces: [force]);

        var output = DumpToString(state, options: new DumpOptions(Json: true));

        Assert.Contains("\"roster\"", output);
        Assert.Contains("\"validationErrors\"", output);
        Assert.Contains("\"Marine\"", output);

        // Should be valid JSON
        var doc = System.Text.Json.JsonDocument.Parse(output);
        Assert.NotNull(doc.RootElement.GetProperty("roster"));
    }

    [Fact]
    public void DumpTree_Enricher_AppendsEngineSections()
    {
        var state = MinimalRoster();
        var enricher = new TestEnricher(new Dictionary<string, object?>
        {
            ["test-section"] = "custom data here"
        });

        var output = DumpToString(state, options: new DumpOptions(Enricher: enricher));

        Assert.Contains("Engine-specific:", output);
        Assert.Contains("[test-section]", output);
        Assert.Contains("custom data here", output);
    }

    [Fact]
    public void DumpJson_Enricher_IncludesExtraKeys()
    {
        var state = MinimalRoster();
        var enricher = new TestEnricher(new Dictionary<string, object?>
        {
            ["extra"] = "value"
        });

        var output = DumpToString(state, options: new DumpOptions(Json: true, Enricher: enricher));

        var doc = System.Text.Json.JsonDocument.Parse(output);
        Assert.True(doc.RootElement.TryGetProperty("extra", out var val));
        Assert.Equal("value", val.GetString());
    }

    private static string DumpToString(
        RosterState state,
        IReadOnlyList<ValidationErrorState>? errors = null,
        DumpOptions? options = null)
    {
        using var writer = new StringWriter();
        StateDumper.Dump(state, errors ?? [], writer, options);
        return writer.ToString();
    }

    private sealed class TestEnricher(Dictionary<string, object?> data) : IDumpEnricher
    {
        public Dictionary<string, object?> EnrichDump(DumpContext context) => data;
    }
}
