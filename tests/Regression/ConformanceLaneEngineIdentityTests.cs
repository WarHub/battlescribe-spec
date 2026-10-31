using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Guards that a UI conformance lane resolves spec expectations against the engine it DRIVES, not
/// only against the name it reports.
/// </summary>
/// <remarks>
/// <see cref="RunnerAndProtocolRegressionTests"/> already covers the resolution rule inside
/// <see cref="RosterRunner"/>. That rule was correct and unreachable: <see cref="ConformanceTestBase"/>
/// passed ONE name for both identities, so a lane running <c>battlescribe-ui</c> looked up every
/// per-engine <c>expectedState</c> under that name, found none, and fell through to the base
/// assertion — the one written for the engine whose behaviour differs. Twenty roster specs carry a
/// <c>battlescribe:</c> override precisely because BattleScribe diverges there.
/// <para>
/// A unit test at the <see cref="RosterRunner"/> level cannot catch that, because the defect is in
/// what the lane HANDS the runner. So this exercises the lane's own entry point.
/// </para>
/// <para>
/// <see cref="GameDataConformanceTestBase"/> then carried the same defect for GameData specs, because each base
/// stated its own rule: it handed <see cref="GameDataRunner"/> the exact name and no family, so a
/// <c>battlescribe:</c> override never reached the <c>battlescribe-ui</c> lane (#573). Both bases now derive
/// one <see cref="EngineIdentity"/> from the lane's name.
/// </para>
/// </remarks>
public sealed class ConformanceLaneEngineIdentityTests
{
    private const string SpecYaml = """
        id: lane-identity
        category: regression
        description: The base engine's override applies to its UI driver

        setup:
          gameSystem:
            forceEntries:
              - id: fe-1
                name: Patrol
          catalogues:
            - id: cat-1

        steps:
          - expectedState:
              forceCount: 99
              engines:
                battlescribe:
                  forceCount: 0
        """;

    [Fact]
    public void UiLane_InheritsItsBaseEngineExpectedState()
    {
        var specPath = WriteSpec();
        try
        {
            // Fails against the base assertion (99 forces) and passes against the `battlescribe`
            // override (0). The fake reports an empty roster, so the verdict is entirely a question
            // of which expectation the lane chose.
            var lane = new FakeUiLane();
            lane.Run(specPath);
        }
        finally
        {
            File.Delete(specPath);
        }
    }

    private const string GameDataSpecYaml = """
        id: lane-identity
        category: regression
        description: The family's override applies to its UI driver

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
                battlescribe:
                  catalogues:
                    - id: cat-1
                      name: from-battlescribe
        """;

    [Fact]
    public void UiGameDataLane_InheritsItsFamilysExpectedState()
    {
        var specPath = WriteSpec(GameDataSpecYaml);
        try
        {
            // The fake's catalogue is named after the `battlescribe` override and not the base assertion, so the
            // lane passes only if it read the family's entry — which it did not before #573.
            new FakeUiGameDataLane().Run(specPath);
        }
        finally
        {
            File.Delete(specPath);
        }
    }

    private static string WriteSpec(string yaml = SpecYaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lane-identity-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    /// <summary>A GameData lane shaped like the BS GameData UI one: a UI driver in the <c>battlescribe</c> family.</summary>
    private sealed class FakeUiGameDataLane : GameDataConformanceTestBase
    {
        public FakeUiGameDataLane()
            : base(TestContext.Current.TestOutputHelper!)
        {
        }

        protected override string EngineName => "battlescribe-ui";

        protected override IGameDataEngine? GetEngine() => new NamedCatalogueEngine("from-battlescribe");

        public void Run(string specPath) => RunSpec(specPath, "regression/lane-identity");
    }

    /// <summary>An editor whose one catalogue reports the given name, so the applied expectation is visible.</summary>
    private sealed class NamedCatalogueEngine(string catalogueName) : IGameDataEngine
    {
        public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues) => [];

        public GameDataActionOutputs AddEntry(string parentId, string entryType, string? name = null, string? id = null) => new();

        public void RemoveEntry(string entryId)
        {
        }

        public void SetField(string entryId, string field, string? value)
        {
        }

        public GameDataActionOutputs AddLink(string parentId, string linkType, string targetId, string? id = null) => new();

        public GameDataState GetState() => new()
        {
            Catalogues = [new CatalogueDataState { Id = "cat-1", Name = catalogueName }],
        };

        public void Dispose()
        {
        }
    }

    /// <summary>A conformance lane shaped like the BS Roster UI one: a UI driver over a base engine.</summary>
    private sealed class FakeUiLane : ConformanceTestBase
    {
        public FakeUiLane()
            : base(TestContext.Current.TestOutputHelper!)
        {
        }

        protected override string EngineName => "battlescribe-ui";

        protected override IRosterEngine? GetEngine() => new EmptyRosterEngine();

        public void Run(string specPath) => RunSpec(specPath, "regression/lane-identity");
    }

    private sealed class EmptyRosterEngine : IRosterEngine
    {
        public RosterState State { get; } = new("roster", "gs", [], [], []);

        public void SetTestContext(string specId)
        {
        }

        public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues) => [];

        public ActionOutputs AddForce(string forceEntryId, string catalogueId) => new();

        public ActionOutputs AddChildForce(string parentForceId, string forceEntryId, string catalogueId) => new();

        public void RemoveForce(string forceId)
        {
        }

        public ActionOutputs SelectEntry(string forceId, string entryId) => new();

        public ActionOutputs SelectChildEntry(string forceId, string parentSelectionId, string entryId) => new();

        public void DeselectSelection(string forceId, string selectionId)
        {
        }

        public void SetSelectionCount(string forceId, string selectionId, int count)
        {
        }

        public ActionOutputs DuplicateSelection(string forceId, string selectionId) => new();

        public ActionOutputs DuplicateForce(string forceId) => new();

        public void SetCostLimit(string costTypeId, decimal value)
        {
        }

        public RosterState GetRosterState() => State;

        public IReadOnlyList<ValidationErrorState> GetValidationErrors() => [];

        public void Cleanup()
        {
        }

        public void Dispose()
        {
        }
    }
}
