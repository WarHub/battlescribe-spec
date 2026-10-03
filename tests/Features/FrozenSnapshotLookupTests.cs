using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.NrGameDataUiDriver;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// <b>A checkout replays its own frozen snapshots, never the enclosing tree's.</b> The overloads the tests
/// call — <see cref="HarRecorder.FindFrozenHarFile(string)"/> and both <c>FindFrozenStaticDir(string)</c> —
/// look up exactly <c>&lt;root&gt;/.testdata/…</c> under the root they are given, with no walk. That is the
/// whole difference from the CLI's working-directory lookups, and the one a worktree depends on: it sits
/// at <c>.claude/worktrees/&lt;name&gt;</c> inside the main checkout, so a walk that starts in a worktree
/// without its own <c>.testdata</c> finds the main checkout's — a different NR client than the pin — and
/// every frozen lane goes green against it while <c>TestDataPinDriftTests</c> looks at the other file.
/// </summary>
/// <remarks>
/// Everything is built in a temp directory, an outer "checkout" holding both snapshots and an inner one
/// nested in it: these must not pass or fail because of where the test process is checked out. Nothing
/// else pins the no-walk rule — every lane and drift test stays green if it is lost, which is how the
/// borrowed snapshot went unnoticed. Mutation-checked when written: a walk up from the root put back into
/// each overload turns the "inner has none" rows red.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class FrozenSnapshotLookupTests : IDisposable
{
    private readonly string _outer = Directory.CreateTempSubdirectory("bsspec-frozen-lookup-").FullName;

    private string Inner => Path.Combine(_outer, ".claude", "worktrees", "wt");

    public FrozenSnapshotLookupTests()
    {
        ProvisionSnapshots(_outer);
        Directory.CreateDirectory(Inner);
    }

    public void Dispose() => Directory.Delete(_outer, recursive: true);

    /// <summary>The three lookups a test may call, by name.</summary>
    public static TheoryData<string> Lookups => [nameof(HarRecorder), nameof(NewRecruitGameDataEngine), nameof(NrGameDataUiEngine)];

    [Theory]
    [MemberData(nameof(Lookups))]
    public void AnInnerCheckoutWithoutItsOwnSnapshot_GetsNull_NotTheEnclosingCheckouts(string lookup)
    {
        Assert.NotNull(Find(lookup, _outer)); // the outer snapshot is real: a null below is the rule, not a broken fixture
        Assert.Null(Find(lookup, Inner));
    }

    [Theory]
    [MemberData(nameof(Lookups))]
    public void AnInnerCheckoutWithItsOwnSnapshot_GetsItsOwn(string lookup)
    {
        ProvisionSnapshots(Inner);

        Assert.Equal(Expected(lookup, Inner), Find(lookup, Inner));
    }

    [Theory]
    [MemberData(nameof(Lookups))]
    public void BinariesOutsideACheckout_GetNull(string lookup) =>
        Assert.Null(Find(lookup, null));

    [Fact]
    public void AnEditorSnapshotWithoutIndexHtml_IsNotOne()
    {
        // The static-dir lookups accept a directory only when it holds the deployment's entry page, so a
        // half-provisioned .testdata/nr-editor reads as absent rather than as a site that 404s at bring-up.
        Directory.CreateDirectory(Path.Combine(Inner, ".testdata", "nr-editor"));

        Assert.Null(NewRecruitGameDataEngine.FindFrozenStaticDir(Inner));
        Assert.Null(NrGameDataUiEngine.FindFrozenStaticDir(Inner));
    }

    private static string? Find(string lookup, string? root) => lookup switch
    {
        nameof(HarRecorder) => HarRecorder.FindFrozenHarFile(root),
        nameof(NewRecruitGameDataEngine) => NewRecruitGameDataEngine.FindFrozenStaticDir(root),
        nameof(NrGameDataUiEngine) => NrGameDataUiEngine.FindFrozenStaticDir(root),
        _ => throw new ArgumentOutOfRangeException(nameof(lookup), lookup, null),
    };

    private static string Expected(string lookup, string root) => lookup == nameof(HarRecorder)
        ? Path.Combine(root, ".testdata", "newrecruit-har", "newrecruit.har")
        : Path.Combine(root, ".testdata", "nr-editor");

    /// <summary>The two frozen snapshots as <c>setup.ps1</c> lays them out: the NR HAR and the NR Editor deployment.</summary>
    private static void ProvisionSnapshots(string root)
    {
        var har = Directory.CreateDirectory(Path.Combine(root, ".testdata", "newrecruit-har")).FullName;
        File.WriteAllText(Path.Combine(har, "newrecruit.har"), "{}");
        var editor = Directory.CreateDirectory(Path.Combine(root, ".testdata", "nr-editor")).FullName;
        File.WriteAllText(Path.Combine(editor, "index.html"), "<!doctype html>");
    }
}
