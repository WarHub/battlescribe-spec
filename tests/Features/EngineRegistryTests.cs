using System.Globalization;
using BattleScribeSpec.Engines;

namespace BattleScribeSpec.Tests.Features;

public sealed class EngineRegistryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bsspec-registry-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_dir, "engines.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Builtins_AreAlwaysKnown()
    {
        var registry = EngineRegistry.Load(null);
        var entry = registry.Resolve(EngineConnectable.Parse("battlescribe-ui"));
        Assert.True(entry.Builtin);
        Assert.Equal(1, entry.Profile.MaxParallel);
        Assert.Contains("gamedata", entry.Domains);
    }

    [Fact]
    public void ConfigEngine_ResolvesWithLaunchAndMetadata()
    {
        var path = WriteConfig("""
            {"engines":{"wham":{"exec":"node adapters/wham.js","domains":["roster"],"maxParallel":8}}}
            """);
        var registry = EngineRegistry.Load(path);

        var entry = registry.Resolve(EngineConnectable.Parse("wham"));
        Assert.Equal("wham", entry.Name);
        Assert.Equal("node", entry.Executable);
        Assert.Equal("adapters/wham.js", entry.Arguments);
        Assert.Equal(["roster"], entry.Domains);
        Assert.Equal(8, entry.Profile.MaxParallel);
        Assert.False(entry.Builtin);
    }

    [Fact]
    public void UnknownName_ThrowsWithKnownNames()
    {
        var registry = EngineRegistry.Load(null);
        var ex = Assert.Throws<KeyNotFoundException>(
            () => registry.Resolve(EngineConnectable.Parse("phalanx")));
        Assert.Contains("battlescribe", ex.Message);
    }

    [Fact]
    public void AdHocLaunchable_ResolvesWithoutRegistry()
    {
        var registry = EngineRegistry.Load(null);
        var entry = registry.Resolve(EngineConnectable.Parse("exec:./my-adapter"));
        Assert.Null(entry.Name);
        Assert.Equal("./my-adapter", entry.Executable);
        Assert.Equal(["roster", "gamedata"], entry.Domains); // optimistic; describe narrows at runtime
    }

    [Fact]
    public void NameEqualsConnectable_OverridesConfigLaunch_KeepsMetadata()
    {
        var path = WriteConfig("""
            {"engines":{"wham":{"exec":"node old.js","domains":["roster"],"maxParallel":2}}}
            """);
        var registry = EngineRegistry.Load(path);

        var entry = registry.Resolve(EngineConnectable.Parse("wham=exec:node new.js"));
        Assert.Equal("wham", entry.Name);
        Assert.Equal("new.js", entry.Arguments);
        Assert.Equal(2, entry.Profile.MaxParallel); // metadata merged from config
    }

    [Fact]
    public void LoadDefault_FindsConfigInAncestorDirectory()
    {
        WriteConfig("""{"engines":{"wham":{"exec":"node w.js"}}}""");
        var nested = Directory.CreateDirectory(Path.Combine(_dir, "a", "b")).FullName;

        var registry = EngineRegistry.LoadDefault(nested);
        Assert.Equal("node", registry.Resolve(EngineConnectable.Parse("wham")).Executable);
    }

    [Fact]
    public void MalformedExec_NamesConfigFileAndEntry()
    {
        var path = WriteConfig("""{"engines":{"wham":{"exec":"   "}}}""");
        var ex = Assert.Throws<InvalidDataException>(() => EngineRegistry.Load(path));
        Assert.Contains("wham", ex.Message);
        Assert.Contains(path, ex.Message);
    }

    /// <summary>
    /// A NEGATIVE memPerInstanceBytes used to escape BOTH of ConcurrencyPolicy's guards — the memory
    /// bound (gated on <c>&gt; 0</c>) and the undeclared-engine worker cap (gated on <c>== 0</c>) —
    /// handing an unmeasured third-party engine the machine's full width. One minus sign, safety cap
    /// gone. It is rejected at load now, before any policy can be asked to interpret it.
    /// </summary>
    [Fact]
    public void NegativeMemPerInstanceBytes_IsRejectedAtLoad()
    {
        var path = WriteConfig("""{"engines":{"wham":{"exec":"node w.js","memPerInstanceBytes":-1}}}""");

        var ex = Assert.Throws<InvalidDataException>(() => EngineRegistry.Load(path));

        Assert.Contains("memPerInstanceBytes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("wham", ex.Message, StringComparison.Ordinal);
        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    public void NonPositiveOversubscriptionFactor_IsRejectedAtLoad(double factor)
    {
        // k <= 0 makes ceil(cpu × k) zero workers, silently floored back to 1 — a config that says
        // one thing and means another.
        var value = factor.ToString(CultureInfo.InvariantCulture);
        var path = WriteConfig(
            "{\"engines\":{\"wham\":{\"exec\":\"node w.js\",\"oversubscriptionFactor\":" + value + "}}}");

        var ex = Assert.Throws<InvalidDataException>(() => EngineRegistry.Load(path));

        Assert.Contains("oversubscriptionFactor", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeMaxParallel_IsRejectedAtLoad()
    {
        var path = WriteConfig("""{"engines":{"wham":{"exec":"node w.js","maxParallel":-1}}}""");

        var ex = Assert.Throws<InvalidDataException>(() => EngineRegistry.Load(path));

        Assert.Contains("maxParallel", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Omitting the optional numbers stays legal — the conservative defaults apply.</summary>
    [Fact]
    public void OmittedProfileNumbers_LoadWithConservativeDefaults()
    {
        var path = WriteConfig("""{"engines":{"wham":{"exec":"node w.js"}}}""");

        var profile = EngineRegistry.Load(path).Resolve(EngineConnectable.Parse("wham")).Profile;

        Assert.Equal(0, profile.MemPerInstanceBytes); // "undeclared" → the policy's cap binds
        Assert.Equal(1.0, profile.OversubscriptionFactor);
        Assert.Equal(0, profile.MaxParallel); // 0 = unlimited (the cap is what actually bounds it)
    }
}
