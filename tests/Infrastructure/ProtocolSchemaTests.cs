using System.Runtime.CompilerServices;
using System.Text.Json;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;
using Json.Schema;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Validates that serialized protocol messages conform to docs/protocol-schema.json.
/// This catches schema drift when ProtocolMessages.cs changes.
/// </summary>
[Trait("Category", "Lint")]
public sealed class ProtocolSchemaTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static readonly Lazy<JsonSchema> Schema = new(() =>
    {
        var schemaPath = Path.Combine(RepoRoot, "docs", "protocol-schema.json");
        var schemaText = File.ReadAllText(schemaPath);
        return JsonSchema.FromText(schemaText);
    });

    private static readonly JsonSerializerOptions SerializerOptions =
        ProtocolJsonContext.Default.Options;

    private static string FindRepoRoot([CallerFilePath] string callerFilePath = "")
    {
        var dir = Path.GetDirectoryName(callerFilePath);
        while (dir is not null)
        {
            if (Directory.EnumerateFiles(dir, "*.slnx").Any())
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Could not find repository root (no *.slnx marker found) while traversing parents of '{callerFilePath}'.");
    }

    public static TheoryData<string, string> AllProtocolMessages()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, json) in GetSampleMessages())
        {
            data.Add(name, json);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllProtocolMessages))]
    public void ProtocolMessageValidatesAgainstSchema(string messageName, string json)
    {
        using var jsonDoc = JsonDocument.Parse(json);
        var result = Schema.Value.Evaluate(jsonDoc.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        if (!result.IsValid)
        {
            var errors = (result.Details ?? [])
                .Where(d => !d.IsValid && d.Errors is not null && d.Errors.Count > 0)
                .SelectMany(d => d.Errors!.Select(e =>
                    $"  {d.InstanceLocation}: {e.Key} — {e.Value}"))
                .ToList();
            Assert.Fail($"Protocol message '{messageName}' failed schema validation:\n{string.Join("\n", errors)}");
        }
    }

    private static IEnumerable<(string Name, string Json)> GetSampleMessages()
    {
        // Commands
        yield return ("setup", SerializeCommand(new SetupCommand
        {
            SpecId = "test-spec",
            GameSystem = new ProtocolGameSystem
            {
                Id = "gs1",
                Name = "Test System",
                CostTypes = [new ProtocolCostType { Id = "pts", Name = "Points" }],
                ForceEntries = [new ProtocolForceEntry { Id = "fe1", Name = "Detachment" }],
                CategoryEntries = [new ProtocolCategoryEntry { Id = "cat1", Name = "HQ" }],
            },
            Catalogues = [new ProtocolCatalogue
            {
                Id = "cat-1",
                Name = "Test Catalogue",
                GameSystemId = "gs1",
            }],
        }));

        yield return ("setupFromFiles", SerializeCommand(new SetupFromFilesCommand
        {
            SpecId = "file-spec",
            Files = [new ProtocolDataFile { FileName = "test.gst", Content = "<xml/>" }],
        }));

        yield return ("action", SerializeCommand(new ActionCommand
        {
            Action = "addForce",
            ForceEntryId = "fe1",
            CatalogueId = "cat-1",
        }));

        yield return ("action-selectEntry", SerializeCommand(new ActionCommand
        {
            Action = "selectEntry",
            ForceId = "f1",
            EntryId = "e1",
        }));

        yield return ("action-setSelectionCount", SerializeCommand(new ActionCommand
        {
            Action = "setSelectionCount",
            ForceId = "f1",
            SelectionId = "s1",
            Count = 3,
        }));

        yield return ("action-setCostLimit", SerializeCommand(new ActionCommand
        {
            Action = "setCostLimit",
            CostTypeId = "pts",
            Value = 2000m,
        }));

        yield return ("action-setCustomization", SerializeCommand(new ActionCommand
        {
            Action = "setCustomization",
            ForceId = "f1",
            SelectionId = "s1",
            CustomName = "My Unit",
            CustomNotes = "Some notes",
        }));

        yield return ("action-loadRoster", SerializeCommand(new ActionCommand
        {
            Action = "loadRoster",
            Xml = "<?xml version=\"1.0\"?><roster id=\"r1\" name=\"Imported\"/>",
        }));

        yield return ("action-reload", SerializeCommand(new ActionCommand
        {
            Action = "reload",
        }));

        yield return ("getState", SerializeCommand(new GetStateCommand()));

        yield return ("getErrors", SerializeCommand(new GetErrorsCommand()));

        yield return ("teardown", SerializeCommand(new TeardownCommand()));

        // Responses
        yield return ("setupResult", SerializeResponse(new SetupResult
        {
            Errors = ["warning1"],
        }));

        yield return ("setupResult-empty", SerializeResponse(new SetupResult()));

        yield return ("actionResult-ok", SerializeResponse(new ActionResult
        {
            Ok = true,
            Outputs = new ActionOutputs
            {
                ForceId = "f1",
                SelectionId = "s1",
                Selections = new Dictionary<string, string> { ["e1"] = "s2" },
            },
        }));

        yield return ("actionResult-error", SerializeResponse(new ActionResult
        {
            Ok = false,
            Error = "Something went wrong",
        }));

        yield return ("state", SerializeResponse(new StateResponse
        {
            Name = "Test Roster",
            GameSystemId = "gs1",
            GameSystemName = "Test System",
            Forces =
            [
                new ForceState(
                    Id: "f1",
                    Name: "Battalion",
                    CatalogueId: "cat-1",
                    Selections:
                    [
                        new SelectionState(
                            Id: "s1",
                            Name: "Commander",
                            EntryId: "e1",
                            Type: "unit",
                            Number: 1,
                            Hidden: false,
                            Costs: [new CostState("Points", "pts", 100m)],
                            Children: [],
                            Profiles:
                            [
                                new ProfileState(
                                    "Stats",
                                    TypeId: "profile-type-1",
                                    TypeName: "Unit",
                                    Hidden: false,
                                    Characteristics: [new CharacteristicState("WS", "char1", "3+")])
                            ],
                            Rules: [new RuleState("Rule1", "A rule", Hidden: false)],
                            Categories: [new CategoryState(Name: "HQ", EntryId: "cat1", Primary: true)])
                    ],
                    Profiles: [new ProfileState("Force Profile", "fpt1", "ForceType", false, [])],
                    Rules: [new RuleState("ForceRule", "desc", false)],
                    Categories: [new CategoryState(Name: "HQ", EntryId: "cat1", Primary: true)],
                    Publications: [new PublicationState("pub1", "Core Book")])
            ],
            Costs = [new CostState("Points", "pts", 100m)],
            CostLimits = [new CostState("Points", "pts", 2000m)],
            ValidationErrors =
            [
                new ValidationErrorState(
                    "Too few selections",
                    OwnerType: "selection",
                    OwnerEntryId: "e1",
                    EntryId: "e1",
                    ConstraintId: "c1",
                    ConstraintType: "min",
                    ConstraintField: "selections",
                    RaisedOnType: "category",
                    RaisedOnId: "cat-node-1")
            ],
        }));

        yield return ("errors", SerializeResponse(new ErrorsResponse
        {
            Errors =
            [
                new ValidationErrorState(
                    "Minimum not met", OwnerType: "force", RaisedOnType: "force", RaisedOnId: "f1")
            ],
        }));

        yield return ("teardownResult", SerializeResponse(new TeardownResult()));

        yield return ("protocolError", SerializeResponse(new ProtocolError
        {
            Message = "Adapter crashed",
        }));
    }

    private static string SerializeCommand(ProtocolCommand message)
    {
        return JsonSerializer.Serialize(message, ProtocolJsonContext.Default.ProtocolCommand);
    }

    private static string SerializeResponse(ProtocolResponse message)
    {
        return JsonSerializer.Serialize(message, ProtocolJsonContext.Default.ProtocolResponse);
    }
}
