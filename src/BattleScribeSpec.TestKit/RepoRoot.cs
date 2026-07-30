namespace BattleScribeSpec;

/// <summary>
/// Locates the repository root by walking up the directory chain looking for
/// <see cref="MarkerFileName"/>. This is the ONE implementation — every production site that needs
/// a path anchored at the checkout (<c>artifacts/</c>, <c>specs/</c>, <c>lib/</c>, <c>.deps/</c>)
/// calls it rather than hand-rolling another walk.
///
/// <para><b>Why the solution file and not <c>.git</c>.</b> Four sites used to walk up for a
/// <c>.git</c> <em>directory</em>. In a git worktree <c>.git</c> is a <em>file</em> holding a
/// <c>gitdir:</c> pointer, so <c>Directory.Exists</c> was false and the walk sailed straight past
/// the worktree root — and because this repo's worktrees live at <c>.claude/worktrees/&lt;name&gt;</c>
/// <em>inside</em> the main checkout, it landed on the main checkout instead, resolving every
/// artifact path against the wrong tree. Handling <c>.git</c>-as-file would have fixed that one
/// case, but <c>.git</c> is the wrong marker regardless: it marks "some git checkout", not "this
/// source tree". A walk up from inside a submodule (<c>.deps/wham</c>) or a cloned data directory
/// (<c>.testdata/wh40k-9e</c>, <c>lib/nr-editor</c> — reachable whenever the user's working
/// directory is in one) stops at that nested repository, which has no <c>artifacts/</c> or
/// <c>specs/</c> at all. <c>BattleScribeSpec.slnx</c> names <em>this</em> repository, so it cannot
/// false-positive that way, and it is present in a source archive that carries no git metadata.
/// The repo's test helpers already used this marker; production now agrees with them.</para>
///
/// <para><b>Published / installed layouts.</b> Neither marker exists next to an installed
/// <c>bs-spec</c>, so both would return null there — the marker choice costs nothing outside a
/// checkout, and callers keep their existing fallbacks (env override, sibling assembly, PATH,
/// current directory) for that case.</para>
/// </summary>
public static class RepoRoot
{
    /// <summary>The file whose presence marks the repository root.</summary>
    public const string MarkerFileName = "BattleScribeSpec.slnx";

    /// <summary>
    /// The repository root containing the running assembly, or null when the binaries live outside
    /// a checkout (published/installed layout). Computed once — <see cref="AppContext.BaseDirectory"/>
    /// cannot change for the lifetime of the process.
    /// </summary>
    public static string? FromBinaries { get; } = FindFrom(AppContext.BaseDirectory);

    /// <summary>
    /// The repository root containing the current working directory, falling back to
    /// <see cref="FromBinaries"/>. Prefers the tree the user is standing in — a hand-run CLI should
    /// act on the checkout the user is in — and only then the tree the binaries came from. Not
    /// cached: the working directory can change.
    /// </summary>
    public static string? FromWorkingDirectory => FindFrom(Directory.GetCurrentDirectory()) ?? FromBinaries;

    /// <summary>
    /// Walk up from <paramref name="startDirectory"/> (inclusive) to the nearest ancestor holding
    /// <see cref="MarkerFileName"/>. Returns the absolute path without a trailing separator, or null
    /// if no ancestor has the marker. Nearest wins, so a checkout nested inside another checkout —
    /// a worktree under <c>.claude/worktrees/</c> — resolves to itself, not to the outer tree.
    /// </summary>
    /// <param name="startDirectory">Where to start looking; relative paths resolve against the current directory.</param>
    public static string? FindFrom(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(startDirectory);

        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(startDirectory));
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, MarkerFileName)))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
