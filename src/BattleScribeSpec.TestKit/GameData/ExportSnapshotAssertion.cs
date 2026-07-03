namespace BattleScribeSpec.GameData;

/// <summary>
/// Shared byte-compare / snapshot-update logic for <c>expectedFile</c> assertions, used by both the
/// gamedata and roster runners. Compares an engine's exported XML against a per-engine snapshot
/// side-file (or inline content), or — in update mode — (re)writes the snapshot.
///
/// <para>Reading is engine-agnostic (see <see cref="GameDataSnapshotResolver"/>: exact override →
/// family override → base). Writing is base-aware: the base (no-infix) file holds the
/// <see cref="GameDataSnapshotResolver.BaseEngineName"/> output; any other engine gets an override
/// only where it diverges. When a non-base engine diverges and has no override yet, the write is
/// resolved by a <see cref="SnapshotWriteTarget"/> policy (interactive prompt, else override).</para>
/// </summary>
public static class ExportSnapshotAssertion
{
    /// <summary>Where a diverging snapshot should be written when the engine has no override yet.</summary>
    public enum SnapshotWriteTarget
    {
        /// <summary>Update the shared base file (the reference the base engine defines).</summary>
        Base,

        /// <summary>Create a per-engine override, leaving the base untouched.</summary>
        Override,
    }

    /// <summary>Context handed to the divergence policy when a non-base engine has no override yet.</summary>
    public readonly record struct SnapshotDivergence(
        string Engine, string SpecId, string Key, string BasePath, string OverridePath);

    /// <summary>
    /// Matches an actual export against an expected template that may carry embedded
    /// <c>${{ steps.… }}</c> / <c>${{ match("…") }}</c> tokens. Returns true on match; otherwise false
    /// with a human-readable <paramref name="failDetail"/>. (Roster snapshots use this; gamedata
    /// leaves it null and compares verbatim.)
    /// </summary>
    public delegate bool TemplateMatcher(string expectedTemplate, string actual, out string? failDetail);

    /// <summary>
    /// Normalize CRLF -&gt; LF and canonicalize to exactly one trailing newline. Engines differ on
    /// whether the exported document ends with a newline (NewRecruit does, BattleScribe does not); that
    /// EOF newline carries no meaning, so we compare — and write snapshots — with a single trailing
    /// "\n". Keeps snapshot files POSIX-clean and leaves only meaningful bytes as overrides.
    /// </summary>
    public static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n").TrimEnd('\n') + "\n";

    /// <summary>Root element -&gt; file extension: gameSystem-&gt;gst, roster-&gt;ros, catalogue (default)-&gt;cat.</summary>
    public static string FileExtFromRoot(string xml)
    {
        var m = System.Text.RegularExpressions.Regex.Match(xml, @"<\s*(gameSystem|catalogue|roster)\b");
        return m.Success
            ? m.Groups[1].Value switch { "gameSystem" => "gst", "roster" => "ros", _ => "cat" }
            : "cat";
    }

    /// <summary>
    /// Default divergence policy: honor <c>BSSPEC_SNAPSHOT_ON_DIVERGE=base|override</c>; otherwise
    /// prompt on an interactive console, or fall back to creating an override (with a warning) when
    /// input is redirected (CI / the xUnit harness). Never silently rewrites the base for a non-base
    /// engine.
    /// </summary>
    public static SnapshotWriteTarget DefaultOnDiverge(SnapshotDivergence d)
    {
        var env = Environment.GetEnvironmentVariable("BSSPEC_SNAPSHOT_ON_DIVERGE");
        if (string.Equals(env, "base", StringComparison.OrdinalIgnoreCase))
        {
            return SnapshotWriteTarget.Base;
        }
        if (string.Equals(env, "override", StringComparison.OrdinalIgnoreCase))
        {
            return SnapshotWriteTarget.Override;
        }

        var baseName = Path.GetFileName(d.BasePath);
        var overrideName = Path.GetFileName(d.OverridePath);
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine(
                $"[snapshot] '{d.Engine}' export for '{d.Key}' diverges from base '{baseName}' and has no " +
                $"override; writing override '{overrideName}'. Set BSSPEC_SNAPSHOT_ON_DIVERGE=base to move the base instead.");
            return SnapshotWriteTarget.Override;
        }

        Console.Error.Write(
            $"[snapshot] '{d.Engine}' export for '{d.Key}' diverges from base '{baseName}'. " +
            $"Update [b]ase or create [o]verride '{overrideName}'? [b/O] ");
        var line = Console.ReadLine();
        return line?.Trim().StartsWith("b", StringComparison.OrdinalIgnoreCase) == true
            ? SnapshotWriteTarget.Base
            : SnapshotWriteTarget.Override;
    }

    /// <summary>
    /// Assert the exported <paramref name="actualRaw"/> against the engine's snapshot, or (re)write it
    /// in <paramref name="updateSnapshots"/> mode. Returns null on success, or an error message
    /// describing the mismatch / missing snapshot / misconfiguration. Pass <paramref name="def"/>
    /// already merged via <see cref="ExpectedFileDef.ForEngine"/>; <paramref name="exprResolve"/>
    /// resolves <c>${{ ... }}</c> step expressions in expected content. <paramref name="onDiverge"/>
    /// decides base-vs-override for a diverging non-base engine (defaults to
    /// <see cref="DefaultOnDiverge"/>).
    /// </summary>
    public static string? AssertOrUpdate(
        ExpectedFileDef def,
        string actualRaw,
        string? engineName,
        string specId,
        string? specDir,
        string? stepId,
        int stepIndex,
        bool updateSnapshots,
        Func<string, string?> exprResolve,
        Func<SnapshotDivergence, SnapshotWriteTarget>? onDiverge = null,
        TemplateMatcher? templateMatch = null,
        Func<string, string>? templatizeForWrite = null)
    {
        var actual = NormalizeNewlines(actualRaw);
        var ext = FileExtFromRoot(actual);

        // Inline expected content (author-maintained; never rewritten by update mode).
        if (def.Content is { } inline)
        {
            var expectedInline = NormalizeNewlines(inline);
            return CompareExpected(stepIndex, "(inline)", expectedInline, actual, exprResolve, templateMatch);
        }

        // Side-file resolved by the step's id.
        if (stepId is not { Length: > 0 } key)
        {
            return $"Step {stepIndex}: expectedFile side-file requires the step to have an 'id'";
        }
        if (specDir is null)
        {
            return $"Step {stepIndex}: expectedFile side-file needs a spec loaded from disk (no SourceDirectory)";
        }

        if (updateSnapshots)
        {
            // Roster snapshots are stored templated (volatile ids → ${{ … }} tokens) so they stay
            // deterministic and diffable; gamedata writes verbatim (templatizeForWrite is identity).
            var toWrite = NormalizeNewlines(templatizeForWrite?.Invoke(actual) ?? actual);
            WriteSnapshot(engineName, specDir, specId, key, ext, toWrite, onDiverge ?? DefaultOnDiverge);
            return null;
        }

        // Read/compare: engine-agnostic (never consults the base-engine name).
        var path = GameDataSnapshotResolver.Resolve(specDir, specId, key, engineName, ext);
        if (path is null)
        {
            return $"Step {stepIndex}: no expected file for snapshot '{key}' (engine '{engineName}', .{ext}); " +
                "run with --update-snapshots (or BSSPEC_UPDATE_SNAPSHOTS=1) to create it";
        }

        var expected = NormalizeNewlines(File.ReadAllText(path));
        return CompareExpected(stepIndex, Path.GetFileName(path), expected, actual, exprResolve, templateMatch);
    }

    /// <summary>
    /// Compare an expected snapshot (templated or literal) against the actual export. A templated
    /// expected (contains <c>${{</c>) with a <paramref name="templateMatch"/> available is matched by
    /// token (step ids / match() regexes); otherwise the expected is resolved via
    /// <paramref name="exprResolve"/> and compared verbatim.
    /// </summary>
    private static string? CompareExpected(
        int stepIndex, string source, string expected, string actual,
        Func<string, string?> exprResolve, TemplateMatcher? templateMatch)
    {
        if (templateMatch is not null && expected.Contains("${{", StringComparison.Ordinal))
        {
            return templateMatch(expected, actual, out var detail)
                ? null
                : $"Step {stepIndex}: exported file does not match expected ({source}). {detail}";
        }

        var resolved = NormalizeNewlines(exprResolve(expected) ?? expected);
        return resolved != actual ? Mismatch(stepIndex, source, resolved, actual) : null;
    }

    private static string Mismatch(int stepIndex, string source, string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var detail = $"expected {e.Length} line(s), actual {a.Length} line(s)";
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var el = i < e.Length ? e[i] : "(missing)";
            var al = i < a.Length ? a[i] : "(missing)";
            if (el != al)
            {
                detail = $"first diff at line {i + 1}:\n      expected: {el}\n      actual:   {al}";
                break;
            }
        }

        return $"Step {stepIndex}: exported file does not match expected ({source}). {detail}";
    }

    /// <summary>
    /// (Re)write an expected side-file. The base engine (and an unknown/null engine) writes the base
    /// (no infix). For any other engine: if an override already exists it is updated in place (or
    /// deleted once the output matches the base again); otherwise, if the output equals the base
    /// nothing is written; otherwise the <paramref name="onDiverge"/> policy chooses whether to move
    /// the base or pin a new family override.
    /// </summary>
    private static void WriteSnapshot(
        string? engine, string specDir, string specId, string key, string ext, string actual,
        Func<SnapshotDivergence, SnapshotWriteTarget> onDiverge)
    {
        var basePath = GameDataSnapshotResolver.BasePath(specDir, specId, key, ext);
        var baseContent = File.Exists(basePath) ? NormalizeNewlines(File.ReadAllText(basePath)) : null;

        // Base engine (or unknown engine) defines the base file.
        if (engine is null || GameDataSnapshotResolver.IsBaseEngine(engine))
        {
            SafeWriteSnapshot(basePath, actual);
            return;
        }

        // 1. An override already exists for this engine — keep updating it in place,
        //    or drop it once the output matches the base again.
        var existingOverride = GameDataSnapshotResolver.ExistingOverride(specDir, specId, key, engine, ext);
        if (existingOverride is not null)
        {
            if (baseContent == actual)
            {
                DeleteSnapshotIfExists(existingOverride);
            }
            else
            {
                SafeWriteSnapshot(existingOverride, actual);
            }
            return;
        }

        // 2. No override, and the output matches the base — nothing to write.
        if (baseContent == actual)
        {
            return;
        }

        // 3. No base to compare against — write it so a reference exists (warn: it may not be canonical).
        if (baseContent is null)
        {
            Console.Error.WriteLine(
                $"[snapshot] no base for '{key}'; writing it from engine '{engine}'. Regenerate with the base " +
                $"engine ('{GameDataSnapshotResolver.BaseEngineName}') if this is not the reference.");
            SafeWriteSnapshot(basePath, actual);
            return;
        }

        // 4. Diverges from the base with no override yet — ask base-vs-override.
        var family = GameDataSnapshotResolver.Family(engine);
        var overridePath = GameDataSnapshotResolver.OverridePath(specDir, specId, key, family, ext);
        var target = onDiverge(new SnapshotDivergence(engine, specId, key, basePath, overridePath));
        SafeWriteSnapshot(target == SnapshotWriteTarget.Base ? basePath : overridePath, actual);
    }

    private static void DeleteSnapshotIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void SafeWriteSnapshot(string path, string content)
    {
        // Don't de-templatize an author-maintained templated expected: skip only when the existing file
        // carries ${{ ... }} tokens and the new content does not (roster snapshots write templated
        // content deliberately — that MUST be allowed to overwrite).
        if (!content.Contains("${{", StringComparison.Ordinal)
            && File.Exists(path) && File.ReadAllText(path).Contains("${{", StringComparison.Ordinal))
        {
            return;
        }

        var dir = Path.GetDirectoryName(path);
        if (dir is { Length: > 0 })
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, content);
    }
}
