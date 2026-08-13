using System.Reflection;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Guards that the IKVM-compiled BattleScribe engine assembly carries the <c>bsspecErrorId</c> field
/// the engine-jar patch adds (src/bs-engine-patch, the <c>PatchBattleScribeEngineJar</c> build
/// target). If the patch stops running — a repinned jar, a broken build target, a stale artifact —
/// the in-process adapter would otherwise fail only deep in a validation run; this fails immediately
/// and says why.
/// </summary>
[Trait("Category", "Unit")]
public sealed class EngineErrorFieldTests
{
    [Fact]
    public void EngineErrorType_CarriesBsspecErrorIdField()
    {
        // The engine types are IKVM-generated in the BattleScribeEngine assembly, which the
        // BattleScribe adapter references; load it by name rather than referencing IKVM types here.
        var engineAssembly = Assembly.Load("BattleScribeEngine");
        var errorType = engineAssembly.GetType("net.battlescribe.engine.b.a");
        Assert.NotNull(errorType);

        var field = errorType!.GetField("bsspecErrorId");
        Assert.NotNull(field);
        Assert.Equal(typeof(string), field!.FieldType);
    }

    [Fact]
    public void EngineErrorType_PinsSerialVersionUid_SoTheAddedFieldIsUidNeutral()
    {
        // The transform pins the pre-transform default serialVersionUID as an explicit field, so
        // adding bsspecErrorId does not change the class's serialization identity.
        var engineAssembly = Assembly.Load("BattleScribeEngine");
        var errorType = engineAssembly.GetType("net.battlescribe.engine.b.a");
        Assert.NotNull(errorType);

        var uid = errorType!.GetField("serialVersionUID",
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
        Assert.NotNull(uid);
    }
}
