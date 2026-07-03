namespace BattleScribeSpec.GameData;

/// <summary>
/// Shared byte-compare / snapshot-update logic for <c>expectedFile</c> assertions, used by both the
/// gamedata and roster runners. Compares an engine's exported XML against a per-engine snapshot
/// side-file (or inline content), or — in update mode — (re)writes the snapshot. The base (no-infix)
/// file is the <see cref="GameDataSnapshotResolver.BaseEngineName"/> output; other engines get an
/// override only where they diverge. See <see cref="GameDataSnapshotResolver"/> for path layout.
/// </summary>
public static class ExportSnapshotAssertion
{
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
    /// Assert the exported <paramref name="actualRaw"/> against the engine's snapshot, or (re)write it
    /// in <paramref name="updateSnapshots"/> mode. Returns null on success, or an error message
    /// describing the mismatch / missing snapshot / misconfiguration. Pass <paramref name="def"/>
    /// already merged via <see cref="ExpectedFileDef.ForEngine"/>; <paramref name="exprResolve"/>
    /// resolves <c>${{ ... }}</c> step expressions in expected content.
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
        Func<string, string?> exprResolve)
    {
        var actual = NormalizeNewlines(actualRaw);
        var ext = FileExtFromRoot(actual);

        // Inline expected content (author-maintained; never rewritten by update mode).
        if (def.Content is { } inline)
        {
            var expectedInline = NormalizeNewlines(exprResolve(inline) ?? inline);
            return expectedInline != actual ? Mismatch(stepIndex, "(inline)", expectedInline, actual) : null;
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

        var engine = engineName ?? GameDataSnapshotResolver.BaseEngineName;

        if (updateSnapshots)
        {
            WriteSnapshot(engine, specDir, specId, key, ext, actual);
            return null;
        }

        var path = GameDataSnapshotResolver.Resolve(specDir, specId, key, engine, ext);
        if (path is null)
        {
            return $"Step {stepIndex}: no expected file for snapshot '{key}' (engine '{engine}', .{ext}); " +
                "run with --update-snapshots (or BSSPEC_UPDATE_SNAPSHOTS=1) to create it";
        }

        var expected = NormalizeNewlines(File.ReadAllText(path));
        expected = NormalizeNewlines(exprResolve(expected) ?? expected);
        return expected != actual ? Mismatch(stepIndex, Path.GetFileName(path), expected, actual) : null;
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
    /// (Re)write an expected side-file. The base engine writes the base (no infix). A non-base engine
    /// writes its family override (e.g. newrecruit + newrecruit-ui → <c>.newrecruit.</c>) when it is
    /// the family-canonical engine; a family <em>variant</em> shares that family file when its output
    /// matches and otherwise pins an exact-engine override the resolver prefers. Overrides equal to the
    /// base are removed.
    /// </summary>
    private static void WriteSnapshot(string engine, string specDir, string specId, string key, string ext, string actual)
    {
        var basePath = GameDataSnapshotResolver.BasePath(specDir, specId, key, ext);
        if (GameDataSnapshotResolver.IsBaseEngine(engine))
        {
            SafeWriteSnapshot(basePath, actual);
            return;
        }

        var family = GameDataSnapshotResolver.Family(engine);
        var familyPath = GameDataSnapshotResolver.OverridePath(specDir, specId, key, family, ext);
        var exactPath = GameDataSnapshotResolver.OverridePath(specDir, specId, key, engine, ext);
        var baseContent = File.Exists(basePath) ? NormalizeNewlines(File.ReadAllText(basePath)) : null;

        // Matches the base: no override needed; drop any stale override this engine owns.
        if (baseContent == actual)
        {
            DeleteSnapshotIfExists(exactPath);
            if (engine == family)
            {
                DeleteSnapshotIfExists(familyPath);
            }
            return;
        }

        if (baseContent is null)
        {
            Console.Error.WriteLine($"[snapshot] base missing for '{key}'. " +
                $"Generate the base ('{GameDataSnapshotResolver.BaseEngineName}') first.");
        }

        // The family-canonical engine (name == family) owns the shared family override.
        if (engine == family)
        {
            SafeWriteSnapshot(familyPath, actual);
            return;
        }

        // A family variant: share the family file when identical, else pin an exact-engine override.
        var familyContent = File.Exists(familyPath) ? NormalizeNewlines(File.ReadAllText(familyPath)) : null;
        if (familyContent == actual)
        {
            DeleteSnapshotIfExists(exactPath);
            return;
        }

        SafeWriteSnapshot(exactPath, actual);
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
        // Don't clobber an author-maintained templated expected.
        if (File.Exists(path) && File.ReadAllText(path).Contains("${{", StringComparison.Ordinal))
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
