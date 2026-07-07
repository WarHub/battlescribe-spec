using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Tests.Infrastructure;

namespace BattleScribeSpec.Tests.Features;

public sealed class GameDataProtocolTests
{
    private static InMemoryAdapterConnection Connect(bool gamedata = true) => new(
        (input, output, ct) => AdapterHandler.RunAsync(
            new AdapterOptions
            {
                RosterEngineFactory = () => new BattleScribeSpec.BattleScribeRosterEngine(),
                GameDataEngineFactory = gamedata ? () => new BattleScribeSpec.BattleScribeGameDataEngine() : null,
                Name = "battlescribe",
            },
            input, output, ct));

    private static readonly ProtocolGameSystem GameSystem = new() { Id = "gs", Name = "GS" };

    private static readonly ProtocolCatalogue Catalogue = new()
    {
        Id = "cat-1",
        Name = "Cat",
        GameSystemId = "gs",
    };

    [Fact]
    public async Task AddEntry_SetField_GetState_OverTheWire()
    {
        await using var connection = Connect();
        using IGameDataEngine engine = new JsonProtocolGameDataEngine(connection);

        Assert.Empty(engine.Setup(GameSystem, [Catalogue]));
        engine.OpenFile("cat-1");

        var outputs = engine.AddEntry("cat-1", "selectionEntry", name: "Unit", id: "se-new");
        Assert.Equal("se-new", outputs.EntryId);

        engine.SetField("se-new", "name", "Renamed Unit");

        var state = engine.GetState();
        var catalogue = Assert.Single(state.Catalogues);
        Assert.Contains(catalogue.SelectionEntries, e => e.Name == "Renamed Unit");
    }

    [Fact]
    public async Task Describe_AdvertisesGamedataDomain()
    {
        await using var connection = Connect();
        var described = await AdapterDescriber.DescribeAsync(connection);
        Assert.Equal(["roster", "gamedata"], described.Domains);
    }

    [Fact]
    public async Task GamedataCommands_WithoutFactory_AnswerNotSupported()
    {
        await using var connection = Connect(gamedata: false);
        var response = await connection.SendCommandAsync(
            new GameDataSetupCommand { GameSystem = GameSystem },
            TestContext.Current.CancellationToken);
        var error = Assert.IsType<ProtocolError>(response);
        Assert.Contains("gamedata", error.Message);
    }
}
