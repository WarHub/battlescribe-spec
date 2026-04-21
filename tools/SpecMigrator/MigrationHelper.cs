using System.Text.RegularExpressions;
using BattleScribeSpec;
using BattleScribeSpec.Protocol;

namespace SpecMigrator;

/// <summary>
/// Migrates spec YAML text from index-based to ID-based addressing.
/// Uses regex to parse old fields from raw YAML (since SpecFileModels no longer has them).
/// </summary>
public static class MigrationHelper
{
    public static bool HasOldFields(string text) =>
        text.Contains("forceEntryIndex:") ||
        text.Contains("entryIndex:") ||
        text.Contains("childEntryIndex:") ||
        Regex.IsMatch(text, @"(?m)^\s+forceIndex:") ||
        text.Contains("selectionIndex:") ||
        text.Contains("catalogueIndex:") ||
        Regex.IsMatch(text, @"(?m)^\s+forcePath:") ||
        Regex.IsMatch(text, @"(?m)^\s+selectionPath:");

    public static string MigrateSpec(string originalText, SpecFile spec)
    {
        var setup = spec.Setup;
        var lines = originalText.Split('\n');

        // Build lookup tables from setup
        var allForceEntries = CollectForceEntries(setup);
        var catalogues = (IReadOnlyList<ProtocolCatalogue>)(setup.Catalogues ?? []);

        // Split YAML into step blocks
        var stepBlocks = SplitSteps(lines);

        // Parse old fields from raw YAML
        var oldFields = stepBlocks.Select(b => ParseOldFields(b.Text)).ToList();

        // Track what each step creates
        int nextForceIdx = 0;
        var forceSelCounters = new Dictionary<int, int>();
        var stepCreatesForceIdx = new int?[stepBlocks.Count];
        var stepCreatesSelKey = new string?[stepBlocks.Count];

        for (int i = 0; i < oldFields.Count; i++)
        {
            var f = oldFields[i];
            switch (f.Action)
            {
                case "addForce" when f.ForcePath is null or { Length: 0 }:
                    stepCreatesForceIdx[i] = nextForceIdx++;
                    break;
                case "selectEntry" or "duplicateSelection":
                {
                    var fi = f.ForceIndex ?? f.ForcePath?.FirstOrDefault() ?? 0;
                    forceSelCounters.TryGetValue(fi, out var si);
                    stepCreatesSelKey[i] = $"{fi}-{si}";
                    forceSelCounters[fi] = si + 1;
                    break;
                }
            }
        }

        // Which creations are referenced by later steps?
        var referencedForces = new HashSet<int>();
        var referencedSels = new HashSet<string>();

        for (int i = 0; i < oldFields.Count; i++)
        {
            var f = oldFields[i];
            if (f.Action is null) continue;
            if (f.ForceIndex is { } fi) referencedForces.Add(fi);
            if (f.ForcePath is not null) foreach (var idx in f.ForcePath) referencedForces.Add(idx);
            if (f.SelectionIndex is { } si && f.ForceIndex is { } fi2)
                referencedSels.Add($"{fi2}-{si}");
            if (f.SelectionPath is { Length: > 0 } sp)
            {
                var fi3 = f.ForceIndex ?? f.ForcePath?.FirstOrDefault() ?? 0;
                referencedSels.Add($"{fi3}-{sp[0]}");
            }
        }

        // Assign step IDs to creation steps that are referenced
        var usedIds = new HashSet<string>();
        var forceIdxToStepId = new Dictionary<int, string>();
        var selKeyToStepId = new Dictionary<string, string>();
        var stepIds = new string?[stepBlocks.Count];

        string GenId(string prefix)
        {
            var id = prefix;
            var counter = 1;
            while (!usedIds.Add(id)) { counter++; id = $"{prefix}-{counter}"; }
            return id;
        }

        for (int i = 0; i < stepBlocks.Count; i++)
        {
            if (stepCreatesForceIdx[i] is { } forceIdx && referencedForces.Contains(forceIdx))
            {
                var fei = oldFields[i].ForceEntryIndex;
                var name = fei is { } idx && idx < allForceEntries.Count
                    ? allForceEntries[idx].Name : "force";
                var sid = GenId($"add-{Kebab(name)}");
                stepIds[i] = sid;
                forceIdxToStepId[forceIdx] = sid;
            }
            if (stepCreatesSelKey[i] is { } selKey && referencedSels.Contains(selKey))
            {
                var f = oldFields[i];
                var catIdx = f.CatalogueIndex ?? 0;
                var entries = GetSelectableEntries(catalogues, catIdx);
                var eName = f.EntryIndex is { } ei && ei < entries.Count
                    ? entries[ei].name : "entry";
                var sid = GenId($"select-{Kebab(eName)}");
                stepIds[i] = sid;
                selKeyToStepId[selKey] = sid;
            }
        }

        // Rebuild file with replacements
        return RebuildFile(lines, stepBlocks, oldFields, stepIds,
            allForceEntries, catalogues, forceIdxToStepId, selKeyToStepId);
    }

    // ─── Rebuild file ───────────────────────────────────────────────────

    private static string RebuildFile(
        string[] lines,
        List<StepBlock> stepBlocks,
        List<OldStepFields> oldFields,
        string?[] stepIds,
        List<ProtocolForceEntry> allForceEntries,
        IReadOnlyList<ProtocolCatalogue> catalogues,
        Dictionary<int, string> forceIdxToStepId,
        Dictionary<string, string> selKeyToStepId)
    {
        var result = new List<string>();
        var inSteps = false;
        var stepBlockIdx = -1;

        // Build set of step start lines for fast lookup
        var stepStartLines = new HashSet<int>(stepBlocks.Select(b => b.StartLine));

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var stripped = line.TrimEnd().TrimStart();

            if (stripped == "steps:")
            {
                inSteps = true;
                result.Add(line);
                continue;
            }

            if (!inSteps)
            {
                result.Add(line);
                continue;
            }

            // Track which step block we're in
            if (stepStartLines.Contains(i))
                stepBlockIdx++;

            if (stepBlockIdx < 0 || stepBlockIdx >= stepBlocks.Count)
            {
                result.Add(line);
                continue;
            }

            // Skip old field lines
            if (IsOldFieldLine(stripped))
                continue;

            // Handle the action line (may start with "- " list marker)
            if (stripped.StartsWith("action:") || stripped.StartsWith("- action:"))
            {
                var fields = oldFields[stepBlockIdx];
                var actionLine = line;

                // Change addForce→addChildForce if had forcePath
                if (fields.Action == "addForce" && fields.ForcePath is { Length: > 0 })
                    actionLine = actionLine.Replace("action: addForce", "action: addChildForce");

                result.Add(actionLine);

                // Add step ID if assigned
                if (stepIds[stepBlockIdx] is { } sid)
                    result.Add($"    id: {sid}");

                // Compute and add new fields
                var newFields = ComputeNewFields(fields, allForceEntries, catalogues,
                    forceIdxToStepId, selKeyToStepId);
                foreach (var (fname, fval) in newFields)
                    result.Add($"    {fname}: {fval}");

                continue;
            }

            result.Add(line);
        }

        return string.Join('\n', result);
    }

    private static bool IsOldFieldLine(string stripped) =>
        stripped.StartsWith("forceEntryIndex:") ||
        stripped.StartsWith("entryIndex:") ||
        stripped.StartsWith("childEntryIndex:") ||
        stripped.StartsWith("forceIndex:") ||
        stripped.StartsWith("selectionIndex:") ||
        stripped.StartsWith("catalogueIndex:") ||
        stripped.StartsWith("forcePath:") ||
        stripped.StartsWith("selectionPath:");

    // ─── New field computation ──────────────────────────────────────────

    private static List<(string name, string value)> ComputeNewFields(
        OldStepFields fields,
        List<ProtocolForceEntry> allForceEntries,
        IReadOnlyList<ProtocolCatalogue> catalogues,
        Dictionary<int, string> forceIdxToStepId,
        Dictionary<string, string> selKeyToStepId)
    {
        var result = new List<(string, string)>();
        var isChildForce = fields.Action == "addForce" && fields.ForcePath is { Length: > 0 };

        switch (fields.Action)
        {
            case "addForce" when !isChildForce:
                AddForceEntryId(result, fields.ForceEntryIndex, allForceEntries);
                AddCatalogueId(result, fields.CatalogueIndex, catalogues);
                break;

            case "addForce" when isChildForce:
                AddForceIdFromPath(result, fields.ForcePath, forceIdxToStepId);
                AddForceEntryId(result, fields.ForceEntryIndex, allForceEntries);
                break;

            case "selectEntry":
                AddForceId(result, fields, forceIdxToStepId);
                AddEntryId(result, fields.EntryIndex, fields.CatalogueIndex ?? 0, catalogues);
                break;

            case "selectChildEntry":
                AddForceId(result, fields, forceIdxToStepId);
                AddSelectionId(result, fields, selKeyToStepId);
                AddChildEntryId(result, fields);
                break;

            case "deselectSelection":
            case "setSelectionCount":
            case "duplicateSelection":
                AddForceId(result, fields, forceIdxToStepId);
                AddSelectionId(result, fields, selKeyToStepId);
                break;
        }

        return result;
    }

    private static void AddForceEntryId(List<(string, string)> r, int? idx,
        List<ProtocolForceEntry> entries)
    {
        if (idx is null) return;
        r.Add(("forceEntryId", idx < entries.Count ? entries[idx.Value].Id : $"UNKNOWN_FE_{idx}"));
    }

    private static void AddCatalogueId(List<(string, string)> r, int? idx,
        IReadOnlyList<ProtocolCatalogue> cats)
    {
        if (idx is null || idx == 0) return;
        r.Add(("catalogueId", idx < cats.Count ? cats[idx.Value].Id : $"UNKNOWN_CAT_{idx}"));
    }

    private static void AddForceId(List<(string, string)> r, OldStepFields f,
        Dictionary<int, string> forceIdxToStepId)
    {
        var fi = f.ForceIndex ?? f.ForcePath?.FirstOrDefault();
        if (fi is null) return;
        if (forceIdxToStepId.TryGetValue(fi.Value, out var stepId))
            r.Add(("forceId", $"${{{{ steps.{stepId}.forceId }}}}"));
    }

    private static void AddForceIdFromPath(List<(string, string)> r, int[]? fp,
        Dictionary<int, string> forceIdxToStepId)
    {
        if (fp is null or { Length: 0 }) return;
        if (forceIdxToStepId.TryGetValue(fp[0], out var stepId))
            r.Add(("forceId", $"${{{{ steps.{stepId}.forceId }}}}"));
    }

    private static void AddEntryId(List<(string, string)> r, int? idx, int catIdx,
        IReadOnlyList<ProtocolCatalogue> catalogues)
    {
        if (idx is null) return;
        var entries = GetSelectableEntries(catalogues, catIdx);
        r.Add(("entryId", idx < entries.Count ? entries[idx.Value].id : $"UNKNOWN_ENTRY_{idx}"));
    }

    private static void AddSelectionId(List<(string, string)> r, OldStepFields f,
        Dictionary<string, string> selKeyToStepId)
    {
        var fi = f.ForceIndex ?? f.ForcePath?.FirstOrDefault() ?? 0;
        int? selIdx = f.SelectionPath is { Length: > 0 } sp ? sp[0]
            : f.SelectionIndex;
        if (selIdx is null) return;
        var selKey = $"{fi}-{selIdx}";
        if (selKeyToStepId.TryGetValue(selKey, out var stepId))
            r.Add(("selectionId", $"${{{{ steps.{stepId}.selectionId }}}}"));
    }

    private static void AddChildEntryId(List<(string, string)> r, OldStepFields f)
    {
        if (f.ChildEntryIndex is { } cei)
            r.Add(("entryId", $"CHILD_{cei}"));
    }

    // ─── Setup data helpers ─────────────────────────────────────────────

    private static List<ProtocolForceEntry> CollectForceEntries(SetupDef setup)
    {
        var entries = new List<ProtocolForceEntry>();
        if (setup.GameSystem?.ForceEntries is { } gsfe)
            entries.AddRange(gsfe);
        if (setup.Catalogues is not null)
            foreach (var cat in setup.Catalogues)
                if (cat.ForceEntries is { } cfe)
                    entries.AddRange(cfe);
        return entries;
    }

    private static List<(string id, string name)> GetSelectableEntries(
        IReadOnlyList<ProtocolCatalogue> catalogues, int catIdx)
    {
        var result = new List<(string id, string name)>();
        if (catIdx >= catalogues.Count) return result;
        var cat = catalogues[catIdx];
        if (cat.SelectionEntries is not null)
            foreach (var e in cat.SelectionEntries)
                result.Add((e.Id, e.Name));
        if (cat.EntryLinks is not null)
            foreach (var e in cat.EntryLinks)
                result.Add((e.Id, e.Name));
        return result;
    }

    // ─── YAML step splitting ────────────────────────────────────────────

    private record StepBlock(int StartLine, int EndLine, string Text);

    private static List<StepBlock> SplitSteps(string[] lines)
    {
        var steps = new List<StepBlock>();
        var inSteps = false;
        var currentStart = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd().TrimStart() == "steps:")
            {
                inSteps = true;
                continue;
            }
            if (!inSteps) continue;

            if (lines[i].StartsWith("  - "))
            {
                if (currentStart >= 0)
                    steps.Add(new StepBlock(currentStart, i - 1,
                        string.Join("\n", lines[currentStart..i])));
                currentStart = i;
            }
        }
        if (currentStart >= 0)
            steps.Add(new StepBlock(currentStart, lines.Length - 1,
                string.Join("\n", lines[currentStart..])));

        return steps;
    }

    // ─── Old field parsing from raw YAML ────────────────────────────────

    private record OldStepFields(
        string? Action,
        int? ForceEntryIndex,
        int? EntryIndex,
        int? ChildEntryIndex,
        int? ForceIndex,
        int? SelectionIndex,
        int? CatalogueIndex,
        int[]? ForcePath,
        int[]? SelectionPath);

    private static OldStepFields ParseOldFields(string stepText)
    {
        string? action = null;
        int? fei = null, ei = null, cei = null, fi = null, si = null, ci = null;
        int[]? fp = null, sp = null;

        if (Match(stepText, @"action:\s*(\S+)") is { } am) action = am;
        if (MatchInt(stepText, @"forceEntryIndex:\s*(\d+)") is { } fv) fei = fv;
        if (MatchInt(stepText, @"(?<!\w)entryIndex:\s*(\d+)") is { } ev) ei = ev;
        if (MatchInt(stepText, @"childEntryIndex:\s*(\d+)") is { } cv) cei = cv;
        if (MatchInt(stepText, @"(?:^|\n)\s*forceIndex:\s*(\d+)") is { } fiv) fi = fiv;
        if (MatchInt(stepText, @"selectionIndex:\s*(\d+)") is { } sv) si = sv;
        if (MatchInt(stepText, @"catalogueIndex:\s*(\d+)") is { } civ) ci = civ;
        if (MatchIntArray(stepText, @"forcePath:\s*\[([^\]]+)\]") is { } fpv) fp = fpv;
        if (MatchIntArray(stepText, @"selectionPath:\s*\[([^\]]+)\]") is { } spv) sp = spv;

        return new OldStepFields(action, fei, ei, cei, fi, si, ci, fp, sp);
    }

    private static string? Match(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static int? MatchInt(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    private static int[]? MatchIntArray(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success
            ? m.Groups[1].Value.Split(',').Select(s => int.Parse(s.Trim())).ToArray()
            : null;
    }

    private static string Kebab(string name) =>
        Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
}
