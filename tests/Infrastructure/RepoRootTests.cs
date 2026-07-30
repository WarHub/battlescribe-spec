namespace BattleScribeSpec.Tests;

/// <summary>
/// Pins the repo-root walk against the two ways a <c>.git</c>-based predicate got it wrong.
/// Everything is built in a temp directory: these must not pass or fail because of where the test
/// process happens to be checked out.
/// </summary>
public sealed class RepoRootTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("bsspec-reporoot-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void FindFrom_NestedWorktree_StopsAtTheWorktree_NotTheCheckoutItIsNestedIn()
    {
        // The bug this pins: production walked up for a `.git` DIRECTORY. In a git worktree `.git`
        // is a FILE holding a `gitdir:` pointer, so Directory.Exists was false, the walk sailed past
        // the worktree root, and — because this repo's worktrees live at .claude/worktrees/<name>
        // INSIDE the main checkout — it landed on the main checkout. Every artifact path then
        // resolved against the wrong tree: `bs-engine-host` was looked for in the main checkout's
        // stale artifacts/ and the CLI suite failed with "Could not locate bs-engine-host".
        var main = CreateDirectory("main");
        File.WriteAllText(Path.Combine(main, RepoRoot.MarkerFileName), "<Solution />");
        Directory.CreateDirectory(Path.Combine(main, ".git"));                  // normal checkout: a directory
        CreateDirectory("main", "artifacts", "bin", "BattleScribeSpec.EngineHost", "debug");

        var worktree = CreateDirectory("main", ".claude", "worktrees", "wt");
        File.WriteAllText(Path.Combine(worktree, RepoRoot.MarkerFileName), "<Solution />");
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: /somewhere/.git/worktrees/wt\n"); // worktree: a FILE
        var worktreeBin = CreateDirectory("main", ".claude", "worktrees", "wt", "artifacts", "bin", "BattleScribeSpec.EngineHost", "debug");

        var resolved = RepoRoot.FindFrom(worktreeBin);

        Assert.Equal(worktree, resolved);
        Assert.NotEqual(main, resolved);
    }

    [Fact]
    public void FindFrom_NestedClone_WalksPastIt_BecauseItIsNotThisRepository()
    {
        // The second reason `.git` is the wrong marker, independent of worktrees: submodules
        // (.deps/wham) and cloned test data (.testdata/wh40k-9e, lib/nr-editor) each carry their
        // own `.git`. A walk that stops at "any git checkout" stops there whenever the working
        // directory is inside one — and that directory has no artifacts/ or specs/ at all.
        var checkout = CreateDirectory("checkout");
        File.WriteAllText(Path.Combine(checkout, RepoRoot.MarkerFileName), "<Solution />");

        var clone = CreateDirectory("checkout", ".testdata", "wh40k-9e");
        Directory.CreateDirectory(Path.Combine(clone, ".git"));
        var inClone = CreateDirectory("checkout", ".testdata", "wh40k-9e", "data");

        Assert.Equal(checkout, RepoRoot.FindFrom(inClone));
    }

    [Fact]
    public void FindFrom_MarkerInTheStartDirectory_ReturnsThatDirectory()
    {
        var root = CreateDirectory("root");
        File.WriteAllText(Path.Combine(root, RepoRoot.MarkerFileName), "<Solution />");

        Assert.Equal(root, RepoRoot.FindFrom(root));
    }

    [Fact]
    public void FromBinaries_ContainsTheRunningAssembly()
    {
        // Not "is the repo root of this machine's main checkout" — that would be the very
        // assumption the bug made. The resolved root must contain the binaries that asked for it,
        // which is what makes artifacts/ paths resolve against the tree they were built in.
        var root = RepoRoot.FromBinaries;
        Assert.SkipWhen(root is null, "Test binaries are not inside a checkout; nothing to pin.");

        var baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        Assert.StartsWith(root + Path.DirectorySeparatorChar, baseDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private string CreateDirectory(params string[] segments)
        => Directory.CreateDirectory(Path.Combine([_temp, .. segments])).FullName;
}
