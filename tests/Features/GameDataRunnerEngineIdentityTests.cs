using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// <see cref="GameDataRunner"/> resolves per-engine overrides through its <see cref="EngineIdentity"/>: the
/// running engine's own entry when the spec has one, else its family's — the rule
/// <see cref="Roster.RosterRunner"/> and every scenario run follow. The gamedata lanes used to hand the runner
/// the exact name alone, so a <c>battlescribe:</c> override never reached <c>battlescribe-ui</c> (#573).
/// </summary>
[Trait("Category", "Unit")]
public sealed class GameDataRunnerEngineIdentityTests
{
    private const string Spec = """
        id: engine-identity
        category: runner
        description: Which per-engine override applies.
        setup:
          edit: cat-1
          gameSystem:
            id: gs-1
            name: GS
          catalogues:
            - id: cat-1
              name: Cat
              gameSystemId: gs-1
        steps:
          - expectedState:
              catalogues:
                - id: cat-1
                  name: base
              engines:
                {OVERRIDES}
        """;

    private const string BothOverrides =
        "battlescribe-ui: {catalogues: [{id: cat-1, name: from-ui}]}\n        battlescribe: {catalogues: [{id: cat-1, name: from-bs}]}";

    private const string FamilyOverride = "battlescribe: {catalogues: [{id: cat-1, name: from-bs}]}";

    [Fact]
    public void TheDriversOwnOverride_Wins()
        => Assert.Empty(Run(actualName: "from-ui", BothOverrides, EngineIdentity.Of("battlescribe-ui")).Failures);

    /// <summary>The lane defect #573 fixed: the family's override reaches its UI driver.</summary>
    [Fact]
    public void TheFamilysOverride_ReachesItsDriver()
        => Assert.Empty(Run(actualName: "from-bs", FamilyOverride, EngineIdentity.Of("battlescribe-ui")).Failures);

    /// <summary>A driver's own entry is the driver's alone: the family's engine still reads its own.</summary>
    [Fact]
    public void TheDriversOverride_DoesNotReachTheFamilysEngine()
        => Assert.Empty(Run(actualName: "from-bs", BothOverrides, EngineIdentity.Of("battlescribe")).Failures);

    [Fact]
    public void AnotherFamilysOverride_DoesNotApply()
        => Assert.Empty(Run(actualName: "base", FamilyOverride, EngineIdentity.Of("newrecruit-ui")).Failures);

    /// <summary>An anonymous adapter has no identity, and no engine's override applies to it.</summary>
    [Fact]
    public void NoIdentity_AppliesNoOverride()
        => Assert.Empty(Run(actualName: "base", BothOverrides, identity: null).Failures);

    private static SpecResult Run(string actualName, string overrides, EngineIdentity? identity)
    {
        var spec = SpecLoader.LoadGameDataFromYaml(Spec.Replace("{OVERRIDES}", overrides, StringComparison.Ordinal));
        return new GameDataRunner(new NamedCatalogueEngine(actualName), identity).Run(spec);
    }

    /// <summary>An engine whose one catalogue reports the given name, so the applied override is visible.</summary>
    private sealed class NamedCatalogueEngine(string catalogueName) : IGameDataEngine
    {
        public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues) => [];

        public GameDataActionOutputs AddEntry(string parentId, string entryType, string? name = null, string? id = null) => new();

        public void RemoveEntry(string entryId) { }

        public void SetField(string entryId, string field, string? value) { }

        public GameDataActionOutputs AddLink(string parentId, string linkType, string targetId, string? id = null) => new();

        public GameDataState GetState() => new()
        {
            Catalogues = [new CatalogueDataState { Id = "cat-1", Name = catalogueName }],
        };

        public void Dispose() { }
    }
}
