using BattleScribeSpec.Engines;

namespace BattleScribeSpec.Tests.Features;

public sealed class EngineConnectableTests
{
    [Theory]
    [InlineData("battlescribe", "battlescribe")]
    [InlineData("newrecruit-ui", "newrecruit-ui")]
    [InlineData("wham", "wham")]
    public void PlainName_IsRegistryLookup(string input, string expectedName)
    {
        var connectable = EngineConnectable.Parse(input);
        Assert.Equal(expectedName, connectable.Name);
        Assert.False(connectable.IsLaunchable);
    }

    [Fact]
    public void Exec_SplitsExecutableAndArguments()
    {
        var connectable = EngineConnectable.Parse("exec:node adapters/wham.js --fast");
        Assert.Null(connectable.Name);
        Assert.Equal("node", connectable.Executable);
        Assert.Equal("adapters/wham.js --fast", connectable.Arguments);
    }

    [Fact]
    public void Exec_WithoutArguments_HasNullArguments()
    {
        var connectable = EngineConnectable.Parse("exec:./adapter");
        Assert.Equal("./adapter", connectable.Executable);
        Assert.Null(connectable.Arguments);
    }

    [Fact]
    public void Dotnet_IsSugarForDotnetExec()
    {
        var connectable = EngineConnectable.Parse("dotnet:artifacts/bin/adapter.dll");
        Assert.Null(connectable.Name);
        Assert.Equal("dotnet", connectable.Executable);
        Assert.Equal("artifacts/bin/adapter.dll", connectable.Arguments);
    }

    [Fact]
    public void NameEqualsConnectable_CarriesIdentityAndLaunch()
    {
        var connectable = EngineConnectable.Parse("battlescribe=dotnet:bs-reference-adapter.dll");
        Assert.Equal("battlescribe", connectable.Name);
        Assert.Equal("dotnet", connectable.Executable);
        Assert.Equal("bs-reference-adapter.dll", connectable.Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("exec:")]
    [InlineData("wham=")]
    [InlineData("wham=notascheme")]
    [InlineData("not a name")]
    public void Invalid_Throws(string input)
        => Assert.Throws<FormatException>(() => EngineConnectable.Parse(input));

    [Fact]
    public void ExecArguments_MayContainEquals()
    {
        var connectable = EngineConnectable.Parse("exec:node app.js --mode=fast");
        Assert.Equal("node", connectable.Executable);
        Assert.Equal("app.js --mode=fast", connectable.Arguments);
    }
}
