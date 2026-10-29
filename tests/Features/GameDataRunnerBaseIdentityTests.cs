using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// <see cref="GameDataRunner"/>'s optional base identity: per-engine overrides resolve to the running
/// engine's own entry when the spec has one, else to the base engine's — the rule
/// <see cref="Roster.RosterRunner"/> already follows. Scenario runs pass a base so one rule covers both
/// kinds of block; without one the gamedata lanes' exact-name rule is unchanged.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GameDataRunnerBaseIdentityTests
{
    private const string Spec = """
        id: base-identity
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

    [Fact]
    public void Override_ForConcreteName_Wins()
    {
        var result = Run(
            actualName: "from-ui",
            overrides: "battlescribe-ui: {catalogues: [{id: cat-1, name: from-ui}]}\n        battlescribe: {catalogues: [{id: cat-1, name: from-bs}]}",
            engineName: "battlescribe-ui",
            baseEngineName: "battlescribe");

        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Override_FallsBackToBase()
    {
        var result = Run(
            actualName: "from-bs",
            overrides: "battlescribe: {catalogues: [{id: cat-1, name: from-bs}]}",
            engineName: "battlescribe-ui",
            baseEngineName: "battlescribe");

        Assert.Empty(result.Failures);
    }

    /// <summary>Without a base the gamedata lanes keep their rule: only the exact name's entry applies.</summary>
    [Fact]
    public void NoBase_KeepsTodaysRule()
    {
        var result = Run(
            actualName: "base",
            overrides: "battlescribe: {catalogues: [{id: cat-1, name: from-bs}]}",
            engineName: "battlescribe-ui",
            baseEngineName: null);

        Assert.Empty(result.Failures);
    }

    private static SpecResult Run(string actualName, string overrides, string engineName, string? baseEngineName)
    {
        var spec = SpecLoader.LoadGameDataFromYaml(Spec.Replace("{OVERRIDES}", overrides, StringComparison.Ordinal));
        return new GameDataRunner(new NamedCatalogueEngine(actualName), engineName, baseEngineName).Run(spec);
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
