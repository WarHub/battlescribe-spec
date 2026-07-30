using BattleScribeSpec.GameData;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <b><c>expectedFile</c> must not pass when the engine cannot export.</b>
/// <para>
/// The gamedata path never grew the swallow the roster path had —
/// <c>GameDataRunner.ExecuteFileAssertion</c> called <see cref="IGameDataEngine.ExportActiveFile"/>
/// unguarded, so the interface default's <see cref="NotSupportedException"/> reached the step loop
/// and was recorded as a failure. That behaviour is now load-bearing rather than incidental (the
/// roster runner was changed to match it), so it is pinned here: the failure must survive, and it
/// must name the opt-out so an author has something to do about it.
/// </para>
/// <para>
/// The opt-out is spec-level (<c>engines: {…: skip}</c>) — <c>GameDataStepDef</c> has no
/// <c>skipEngines</c>, and gamedata does not need one: all three export specs exist to byte-compare
/// an export, so skipping the assertion would leave an empty spec behind. Applying the spec-level
/// skip is the caller's job (<c>SpecFileBase.IsApplicableTo</c>), which is why there is no runner
/// test for it here.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class GameDataCapabilityRegressionTests
{
    [Fact]
    public void ExpectedFile_WhenTheEngineCannotExport_Fails()
    {
        var engine = new NonExportingGameDataEngine();
        var runner = new GameDataRunner(engine, "battlescribe");

        var result = runner.Run(FileAssertionSpec());

        Assert.False(result.Passed);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("expectedFile", failure, StringComparison.Ordinal);
        Assert.Contains("battlescribe", failure, StringComparison.Ordinal);
        Assert.Contains("skip", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedFile_WhenTheEngineExports_ActuallyCompares()
    {
        var engine = new NonExportingGameDataEngine { Xml = "<catalogue>actual</catalogue>" };
        var runner = new GameDataRunner(engine, "battlescribe");
        var spec = FileAssertionSpec();
        spec.Steps[^1].ExpectedFile!.Content = "<catalogue>expected</catalogue>";

        var result = runner.Run(spec);

        Assert.Equal(1, engine.ExportCalls);
        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Contains("does not match expected", StringComparison.Ordinal));
    }

    private static GameDataSpecFile FileAssertionSpec() => new()
    {
        Id = "gamedata-file-assertion",
        Category = "runner",
        Description = "expectedFile byte-compare",
        Setup = new GameDataSetupDef
        {
            GameSystem = new ProtocolGameSystem { Id = "gs-1", Name = "GS" },
            Catalogues = [new ProtocolCatalogue { Id = "cat-1", Name = "Cat", GameSystemId = "gs-1" }],
            Edit = "cat-1",
        },
        Steps =
        [
            new GameDataStepDef
            {
                Id = "exported",
                ExpectedFile = new ExpectedFileDef { Content = "<catalogue/>" },
            },
        ],
    };

    /// <summary>
    /// Minimal engine that leaves every optional member on the interface default. With
    /// <see cref="Xml"/> unset, <see cref="ExportActiveFile"/> is the inherited default that throws
    /// <see cref="NotSupportedException"/> — the "I cannot do this" signal under test.
    /// </summary>
    private sealed class NonExportingGameDataEngine : IGameDataEngine
    {
        public string? Xml { get; init; }

        public int ExportCalls { get; private set; }

        public IReadOnlyList<string> Setup(ProtocolGameSystem gameSystem, ProtocolCatalogue[] catalogues) => [];

        public GameDataActionOutputs AddEntry(string parentId, string entryType, string? name = null, string? id = null)
            => new() { EntryId = id ?? "entry-1" };

        public void RemoveEntry(string entryId) { }

        public void SetField(string entryId, string field, string? value) { }

        public GameDataActionOutputs AddLink(string parentId, string linkType, string targetId, string? id = null)
            => new() { EntryId = id ?? "link-1" };

        public string ExportActiveFile()
        {
            ExportCalls++;
            return Xml ?? throw new NotSupportedException("ExportActiveFile is not supported by this engine.");
        }

        public GameDataState GetState() => new();

        public void Dispose() { }
    }
}
