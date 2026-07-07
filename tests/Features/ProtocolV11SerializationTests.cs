using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests.Features;

public sealed class ProtocolV11SerializationTests
{
    [Fact]
    public void DescribeCommand_RoundTrips()
    {
        var json = ProtocolSerializer.SerializeCommand(new DescribeCommand());
        Assert.Contains("\"type\":\"describe\"", json);

        var command = ProtocolSerializer.DeserializeCommand(json);
        Assert.IsType<DescribeCommand>(command);
    }

    [Fact]
    public void DescribeResult_RoundTrips_WithCapabilities()
    {
        var result = new DescribeResult
        {
            Name = "battlescribe",
            Version = "2.03.29",
            Domains = ["roster", "gamedata"],
            Capabilities = new AdapterCapabilities { Screenshot = true, MaxParallel = 4 },
        };

        var json = ProtocolSerializer.SerializeResponse(result);
        Assert.Contains("\"type\":\"describeResult\"", json);

        var parsed = Assert.IsType<DescribeResult>(ProtocolSerializer.DeserializeResponse(json));
        Assert.Equal("battlescribe", parsed.Name);
        Assert.Equal("1.1", parsed.ProtocolVersion);
        Assert.Equal(["roster", "gamedata"], parsed.Domains);
        Assert.True(parsed.Capabilities.Screenshot);
        Assert.False(parsed.Capabilities.Record);
        Assert.Equal(4, parsed.Capabilities.MaxParallel);
    }

    [Fact]
    public void DescribeResult_Defaults_AreRosterOnlyNoCapabilities()
    {
        var parsed = Assert.IsType<DescribeResult>(
            ProtocolSerializer.DeserializeResponse("""{"type":"describeResult","name":"x"}"""));
        Assert.Equal(["roster"], parsed.Domains);
        Assert.False(parsed.Capabilities.Screenshot);
        Assert.Equal(0, parsed.Capabilities.MaxParallel);
    }

    [Theory]
    [InlineData("""{"type":"screenshot"}""", typeof(ScreenshotCommand))]
    [InlineData("""{"type":"exportRosterXml"}""", typeof(ExportRosterXmlCommand))]
    [InlineData("""{"type":"recordStart"}""", typeof(RecordStartCommand))]
    [InlineData("""{"type":"recordStop"}""", typeof(RecordStopCommand))]
    public void ParityCommands_Deserialize(string json, Type expected)
        => Assert.IsType(expected, ProtocolSerializer.DeserializeCommand(json), exactMatch: true);

    [Fact]
    public void ParityResponses_RoundTrip()
    {
        Assert.Contains("\"pngBase64\":\"QUJD\"", ProtocolSerializer.SerializeResponse(
            new ScreenshotResult { PngBase64 = "QUJD" }));

        // STJ's default encoder escapes '<'/'>' as </> for HTML safety, so the wire
        // form isn't the literal string — assert round-trip fidelity instead of a raw substring.
        var xmlJson = ProtocolSerializer.SerializeResponse(new RosterXmlResult { Xml = "<roster/>" });
        var xmlResult = Assert.IsType<RosterXmlResult>(ProtocolSerializer.DeserializeResponse(xmlJson));
        Assert.Equal("<roster/>", xmlResult.Xml);

        Assert.IsType<RecordResult>(ProtocolSerializer.DeserializeResponse(
            """{"type":"recordResult","actionsJson":"[]"}"""));
    }

    [Fact]
    public void GameDataSetup_RoundTrips()
    {
        var json = ProtocolSerializer.SerializeCommand(new GameDataSetupCommand
        {
            SpecId = "spec-1",
            GameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" },
            Catalogues = [new ProtocolCatalogue { Id = "cat-1", Name = "Cat", GameSystemId = "gs" }],
        });
        Assert.Contains("\"type\":\"gamedataSetup\"", json);

        var parsed = Assert.IsType<GameDataSetupCommand>(ProtocolSerializer.DeserializeCommand(json));
        Assert.Equal("cat-1", Assert.Single(parsed.Catalogues).Id);
    }

    [Fact]
    public void GameDataAction_RoundTrips()
    {
        var json = ProtocolSerializer.SerializeCommand(new GameDataActionCommand
        {
            Action = "addEntry",
            ParentId = "cat-1",
            EntryType = "selectionEntry",
            Name = "Unit",
            Id = "declared-id",
        });
        var parsed = Assert.IsType<GameDataActionCommand>(ProtocolSerializer.DeserializeCommand(json));
        Assert.Equal("addEntry", parsed.Action);
        Assert.Equal("declared-id", parsed.Id);
    }

    [Fact]
    public void GameDataState_RoundTrips()
    {
        var response = new GameDataStateResponse
        {
            State = new BattleScribeSpec.GameData.GameDataState
            {
                GameSystem = new BattleScribeSpec.GameData.GameSystemDataState { Id = "gs", Name = "GS" },
            },
        };
        var json = ProtocolSerializer.SerializeResponse(response);
        Assert.Contains("\"type\":\"gamedataState\"", json);

        var parsed = Assert.IsType<GameDataStateResponse>(ProtocolSerializer.DeserializeResponse(json));
        Assert.Equal("gs", parsed.State.GameSystem!.Id);
    }
}
