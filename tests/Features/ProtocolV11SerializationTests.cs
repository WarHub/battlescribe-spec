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
}
