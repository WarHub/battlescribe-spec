using System.Globalization;
using BattleScribeSpec.Concurrency;
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

    /// <summary>
    /// The context-axis declarations are validated exactly like the process-axis ones, and for the
    /// same reason: the policy gates both on <c>&gt; 0</c>, so a negative silently falls through to
    /// the undeclared default while looking, in the author's file, like a declaration.
    /// </summary>
    /// <remarks>
    /// Falsifiable: delete either check from <c>EngineRegistry.Validate</c> and the corresponding row
    /// loads without throwing. The error message must also name the field, so an author who wrote
    /// <c>-1</c> is told which of the four numbers they broke.
    /// </remarks>
    [Theory]
    [InlineData("contextPoolSize", -1)]
    [InlineData("memPerContextBytes", -1)]
    [InlineData("memPoolBaselineBytes", -1)]
    public void NegativeContextAxisDeclarations_AreRejectedAtLoad(string field, int value)
    {
        var path = WriteConfig(
            "{\"engines\":{\"wham\":{\"exec\":\"node w.js\",\"" + field + "\":" +
            value.ToString(CultureInfo.InvariantCulture) + "}}}");

        var ex = Assert.Throws<InvalidDataException>(() => EngineRegistry.Load(path));

        Assert.Contains(field, ex.Message, StringComparison.Ordinal);
        Assert.Contains("wham", ex.Message, StringComparison.Ordinal);
        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A slope without an intercept is not a memory model.</b> <c>memPerContextBytes</c> is the
    /// MARGINAL cost of one more browser context; <c>memPoolBaselineBytes</c> is the FIXED cost of the
    /// browser, driver and test host they all share. Declare one and the pool's memory bound charges the
    /// other to nobody — which is precisely the bug that let a 7.8 GiB runner authorise a pool costing
    /// 7.4 GiB. Both, or neither.
    /// </summary>
    /// <remarks>
    /// Falsifiable: delete the paired check from <c>EngineRegistry.Validate</c> and either row loads —
    /// the first with a memory bound that is optimistic by the whole baseline, the second with an
    /// intercept nothing will ever read (the bound is gated on the slope).
    /// </remarks>
    [Theory]
    [InlineData("\"memPerContextBytes\":209715200")]    // slope, no intercept — the optimistic bound
    [InlineData("\"memPoolBaselineBytes\":1073741824")] // intercept, no slope — a number nothing reads
    public void HalfAMemoryModel_IsRejectedAtLoad(string declaration)
    {
        var path = WriteConfig("{\"engines\":{\"wham\":{\"exec\":\"node w.js\"," + declaration + "}}}");

        var ex = Assert.Throws<InvalidDataException>(() => EngineRegistry.Load(path));

        Assert.Contains("memPerContextBytes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("memPoolBaselineBytes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("wham", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A third-party engine can declare the context axis, and the policy reads it.</summary>
    [Fact]
    public void DeclaredContextAxis_IsLoadedAsTheEnginesOwnAbsolutePoolSize()
    {
        var path = WriteConfig(
            """
            {"engines":{"wham":{"exec":"node w.js","contextPoolSize":12,
             "memPerContextBytes":209715200,"memPoolBaselineBytes":1073741824}}}
            """);

        var profile = EngineRegistry.Load(path).Resolve(EngineConnectable.Parse("wham")).Profile;

        Assert.Equal(12, profile.ContextPoolSize);
        Assert.Equal(209_715_200L, profile.MemPerContextBytes);
        Assert.Equal(1_073_741_824L, profile.MemPoolBaselineBytes);

        // And it is an ABSOLUTE count: the same 12 on a 4-CPU box and on a 64-core box. (1 GiB of shared
        // browser + driver + test host, plus 200 MiB per context × 12 = 2.4 GiB, so the 16 GiB box's
        // memory bound — 60 — does not bind either.)
        Assert.Equal(12, ConcurrencyPolicy.For(new MachineProfile(4, 16L << 30), profile).PoolSize);
        Assert.Equal(12, ConcurrencyPolicy.For(new MachineProfile(64, 256L << 30), profile).PoolSize);
    }

    /// <summary>
    /// <b>An entry with no <c>exec</c> cannot replace a built-in, so it may not shadow one.</b> Reaching
    /// for <c>{"battlescribe": {"endpoint": "local"}}</c> to <em>annotate</em> a built-in instead
    /// <em>replaced</em> it with an engine that has no executable — and <c>bs-spec run --engine
    /// battlescribe</c>, the primary documented usage, died with "no executable configured".
    /// </summary>
    /// <remarks>
    /// Reproduced on the CLI before this check existed. Falsifiable: delete the guard from
    /// <c>EngineRegistry.Load</c> and this loads, handing back an entry with <c>Executable == null</c> and
    /// <c>Builtin == false</c> for a name whose engine is <c>bs-engine-host</c>. A built-in's endpoint and
    /// profile are measured and declared in code; the way to declare an <em>ad-hoc</em> adapter launched
    /// under a built-in's name is <c>--engine-endpoint</c>, which the message says.
    /// </remarks>
    [Fact]
    public void ExeclessEntry_NamingABuiltin_IsRejectedAtLoad_RatherThanBrickingIt()
    {
        var path = WriteConfig("""{"engines":{"battlescribe":{"endpoint":"local"}}}""");

        var ex = Assert.Throws<InvalidDataException>(() => EngineRegistry.Load(path));

        Assert.Contains("battlescribe", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exec", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--engine-endpoint", ex.Message, StringComparison.Ordinal);

        // ...and a genuine replacement — one that actually brings an executable — is still allowed.
        var replaced = EngineRegistry
            .Load(WriteConfig("""{"engines":{"battlescribe":{"exec":"node bs.js","endpoint":"local"}}}"""))
            .Resolve(EngineConnectable.Parse("battlescribe"));

        Assert.Equal("node", replaced.Executable);
        Assert.False(replaced.Builtin);
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

        // Context axis: undeclared too → ConcurrencyPolicy.UndeclaredContextPoolSize, and no memory
        // bound on the pool. An omitted field must not mean "take the machine".
        Assert.Equal(0, profile.ContextPoolSize);
        Assert.Equal(0L, profile.MemPerContextBytes);
        Assert.Equal(4, ConcurrencyPolicy.For(new MachineProfile(64, 256L << 30), profile).PoolSize);
    }
}
