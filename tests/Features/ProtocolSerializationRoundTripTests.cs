using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Verifies that all protocol command and response types survive JSON round-trip
/// via ProtocolSerializer (and thus ProtocolJsonContext). This catches polymorphic
/// discriminator conflicts that only surface when the source-generated context is initialized.
/// </summary>
[Trait("Category", "Unit")]
public class ProtocolSerializationRoundTripTests
{
    [Fact]
    public void AllCommandTypes_RoundTrip()
    {
        ProtocolCommand[] commands =
        [
            new SetupCommand
            {
                SpecId = "test-spec",
                GameSystem = new ProtocolGameSystem { Id = "gs-1", Name = "Test GS" },
                Catalogues = [new ProtocolCatalogue { Id = "cat-1", Name = "Test Cat" }],
            },
            new SetupFromFilesCommand
            {
                SpecId = "test-spec",
                Files = [new ProtocolDataFile { FileName = "system.gst", Content = "<xml/>" }],
            },
            new ActionCommand
            {
                Action = "addForce",
                ForceEntryId = "fe-1",
                CatalogueId = "cat-1",
            },
            new GetStateCommand(),
            new GetErrorsCommand(),
            new TeardownCommand(),
        ];

        foreach (var command in commands)
        {
            var json = ProtocolSerializer.SerializeCommand(command);
            var deserialized = ProtocolSerializer.DeserializeCommand(json);

            Assert.NotNull(deserialized);
            Assert.IsType(command.GetType(), deserialized);
            Assert.Contains($"\"type\":\"{command.Type}\"", json);
        }
    }

    [Fact]
    public void AllResponseTypes_RoundTrip()
    {
        ProtocolResponse[] responses =
        [
            new SetupResult { Errors = ["some error"] },
            new ActionResult { Ok = true, Outputs = new ActionOutputs { ForceId = "f-1" } },
            new StateResponse
            {
                Name = "Test Roster",
                GameSystemId = "gs-1",
                GameSystemName = "Test GS",
                Forces = [new ForceState("f-1", "Patrol", "cat-1", [])],
                Costs = [new CostState("pts", "pts", 100)],
            },
            new ErrorsResponse { Errors = [new ValidationErrorState("Over limit")] },
            new TeardownResult(),
            new ProtocolError { Message = "Something went wrong" },
        ];

        foreach (var response in responses)
        {
            var json = ProtocolSerializer.SerializeResponse(response);
            var deserialized = ProtocolSerializer.DeserializeResponse(json);

            Assert.NotNull(deserialized);
            Assert.IsType(response.GetType(), deserialized);
            Assert.Contains($"\"type\":\"{response.Type}\"", json);
        }
    }

    /// <summary>
    /// Uses the kitchen-sink spec to exercise a realistic SetupCommand with all protocol
    /// data types populated (cost types, profile types, force entries, categories, etc.).
    /// </summary>
    [Fact]
    public void KitchenSinkSpec_SetupCommand_RoundTrips()
    {
        var specPath = FindSpec("protocol/protocol-kitchen-sink");
        var spec = SpecLoader.Load(specPath);
        var (gameSystem, catalogues) = SpecLoader.GetSetupData(spec.Setup, spec.Id);

        var command = new SetupCommand
        {
            SpecId = spec.Id,
            GameSystem = gameSystem,
            Catalogues = [.. catalogues],
        };

        var json = ProtocolSerializer.SerializeCommand(command);
        var deserialized = ProtocolSerializer.DeserializeCommand(json);

        Assert.NotNull(deserialized);
        var setup = Assert.IsType<SetupCommand>(deserialized);
        Assert.Equal(spec.Id, setup.SpecId);
        Assert.Equal(gameSystem.Id, setup.GameSystem.Id);
        Assert.Equal(catalogues.Length, setup.Catalogues.Count);
    }

    private static string FindSpec(string specId)
    {
        var specsDir = SpecLoader.FindRosterSpecsDirectory()
            ?? throw new InvalidOperationException("Could not find specs directory");
        // specId can be "category/id" or just "id"
        string? category = null;
        var id = specId;
        if (specId.Contains('/'))
        {
            var parts = specId.Split('/', 2);
            category = parts[0];
            id = parts[1];
        }
        var match = SpecLoader.DiscoverSpecs(specsDir)
            .FirstOrDefault(s => s.Id == id && (category is null || s.Category == category));
        if (match.Path is null)
        {
            throw new FileNotFoundException($"Spec not found: {specId}");
        }

        return match.Path;
    }
}
