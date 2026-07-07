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
}
