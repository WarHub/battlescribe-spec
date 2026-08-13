using BattleScribeSpec.Roster;

namespace BattleScribeSpec;

/// <summary>
/// Resolves ${{ steps.&lt;stepId&gt;.&lt;field&gt; }} expressions against stored step outputs.
/// Supports dotted paths for nested lookups into the selections and categories maps.
/// <para>
/// <b>Naming one of several sibling nodes (#428).</b> <c>selections</c> and <c>categories</c> group
/// the nodes a step minted by the catalogue entry each came from, because that entry id is the only
/// name a spec can write down — node ids are minted per run. A step routinely mints more than one
/// node of one entry, so each key holds a LIST in roster order and a trailing <c>[n]</c> picks one:
/// </para>
/// <code>
/// ${{ steps.add-patrol.selections.se-unit-a }}      # the first  Unit A this step created
/// ${{ steps.add-patrol.selections.se-unit-a[1] }}   # the second
/// ${{ steps.add-patrol.categories.cat-troops[1] }}  # the second Troops node of this force
/// </code>
/// <para>
/// Bracket-index rather than a dotted <c>.1</c> or a <c>#1</c> suffix: <c>[</c> cannot occur in a
/// BattleScribe id, so the split is exact rather than a guess about where the key ends, and the
/// bare form is exactly <c>[0]</c> — the first node — so no existing reference changes meaning.
/// An index past the end is an error naming how many nodes there actually are, never a silent null:
/// a spec that asks for the third of two has stopped describing the roster it is running against.
/// </para>
/// </summary>
public sealed class ExpressionResolver
{
    private const string ExprStart = "${{";
    private const string ExprEnd = "}}";
    private const string StepsPrefix = "steps.";

    private readonly Dictionary<string, ActionOutputs> _stepOutputs = [];

    /// <summary>
    /// Store the outputs from a named step.
    /// </summary>
    public void StoreOutputs(string stepId, ActionOutputs outputs)
    {
        _stepOutputs[stepId] = outputs;
    }

    /// <summary>
    /// Reverse map: minted instance id (a value produced by some step) → the <c>${{ steps.… }}</c>
    /// token that resolves to it. Used to templatize a roster export on snapshot-write, turning the
    /// run's volatile ids back into stable, meaningful step references. Covers <c>forceId</c>,
    /// <c>selectionId</c>, each auto-/multi-selection in the step's <c>selections</c> map, and each
    /// category node in its <c>categories</c> map — an exported roster writes category node ids as
    /// <c>&lt;category id="…"&gt;</c>, and without this a snapshot would bake in one run's.
    /// <para>
    /// The Nth sibling of one entry writes back as <c>…[n]</c>, and the first as the bare key, so a
    /// snapshot written before nodes past the first were addressable is byte-identical to one
    /// written after.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> BuildIdReverseIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (stepId, outputs) in _stepOutputs)
        {
            if (outputs.ForceId is { Length: > 0 } fid)
            {
                map[fid] = $"{ExprStart} steps.{stepId}.forceId {ExprEnd}";
            }
            if (outputs.SelectionId is { Length: > 0 } sid)
            {
                map[sid] = $"{ExprStart} steps.{stepId}.selectionId {ExprEnd}";
            }

            AddNodeTokens(map, outputs.Selections, stepId, "selections");
            AddNodeTokens(map, outputs.Categories, stepId, "categories");
        }

        return map;
    }

    private static void AddNodeTokens(
        Dictionary<string, string> map,
        Dictionary<string, List<string>>? nodes,
        string stepId,
        string field)
    {
        if (nodes is null)
        {
            return;
        }

        foreach (var (entryId, ids) in nodes)
        {
            for (var i = 0; i < ids.Count; i++)
            {
                if (ids[i] is { Length: > 0 } nodeId)
                {
                    var index = i == 0 ? string.Empty : $"[{i}]";
                    map[nodeId] = $"{ExprStart} steps.{stepId}.{field}.{entryId}{index} {ExprEnd}";
                }
            }
        }
    }

    /// <summary>
    /// Match an <paramref name="actual"/> string against a <paramref name="template"/> that may carry
    /// embedded <c>${{ steps.… }}</c> tokens (each resolved to this run's minted id) and
    /// <c>${{ match("regex") }}</c> tokens (each a regex fragment for a volatile id no step captured).
    /// Compares line-by-line so a failure reports the first diverging line. Lines with no tokens must
    /// match verbatim.
    /// </summary>
    public bool TryMatchTemplate(string template, string actual, out string? failDetail)
    {
        var t = template.Split('\n');
        var a = actual.Split('\n');
        for (var i = 0; i < Math.Max(t.Length, a.Length); i++)
        {
            var tl = i < t.Length ? t[i] : null;
            var al = i < a.Length ? a[i] : null;
            if (tl is null || al is null || !System.Text.RegularExpressions.Regex.IsMatch(al, LineRegex(tl)))
            {
                failDetail = $"expected {t.Length} line(s), actual {a.Length} line(s); first diff at line {i + 1}:\n" +
                    $"      expected: {tl ?? "(missing)"}\n      actual:   {al ?? "(missing)"}";
                return false;
            }
        }

        failDetail = null;
        return true;
    }

    /// <summary>Build an anchored regex for one template line: literals escaped, tokens expanded.</summary>
    private string LineRegex(string line)
    {
        var sb = new System.Text.StringBuilder(@"\A");
        var i = 0;
        while (i < line.Length)
        {
            var start = line.IndexOf(ExprStart, i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(System.Text.RegularExpressions.Regex.Escape(line[i..]));
                break;
            }

            sb.Append(System.Text.RegularExpressions.Regex.Escape(line[i..start]));
            var end = line.IndexOf(ExprEnd, start, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidOperationException($"Unterminated '{ExprStart}' in template line: {line}");
            }

            var expr = line[(start + ExprStart.Length)..end].Trim();
            sb.Append(TokenToRegex(expr));
            i = end + ExprEnd.Length;
        }

        sb.Append(@"\z");
        return sb.ToString();
    }

    /// <summary>A single <c>${{ … }}</c> token → a regex fragment (match() as-is, steps.* as a literal).</summary>
    private string TokenToRegex(string expr)
    {
        if (expr.StartsWith("match(", StringComparison.Ordinal))
        {
            return "(?:" + ExtractMatchArg(expr) + ")";
        }

        // A steps.* reference resolves to this run's concrete id, matched literally.
        return System.Text.RegularExpressions.Regex.Escape(ResolveExpression(expr, $"{ExprStart} {expr} {ExprEnd}"));
    }

    /// <summary>Extract the regex argument from <c>match("regex")</c> (single or double quotes).</summary>
    private static string ExtractMatchArg(string expr)
    {
        var open = expr.IndexOf('(');
        var close = expr.LastIndexOf(')');
        if (open < 0 || close < open)
        {
            throw new InvalidOperationException($"Malformed match() expression: {expr}");
        }

        var arg = expr[(open + 1)..close].Trim();
        if (arg.Length >= 2 && ((arg[0] == '"' && arg[^1] == '"') || (arg[0] == '\'' && arg[^1] == '\'')))
        {
            arg = arg[1..^1];
        }

        return arg;
    }

    /// <summary>
    /// Resolve a string value that may contain a ${{ }} expression.
    /// Returns the original value if it's not an expression.
    /// </summary>
    public string? Resolve(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith(ExprStart, StringComparison.Ordinal) || !trimmed.EndsWith(ExprEnd, StringComparison.Ordinal))
        {
            return value;
        }

        var expr = trimmed[ExprStart.Length..^ExprEnd.Length].Trim();
        return ResolveExpression(expr, value);
    }

    private string ResolveExpression(string expr, string rawExpr)
    {
        if (!expr.StartsWith(StepsPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Invalid expression '{rawExpr}': only 'steps.' expressions are supported.");
        }

        var path = expr[StepsPrefix.Length..];
        var dotIndex = path.IndexOf('.');
        if (dotIndex < 0)
        {
            throw new InvalidOperationException(
                $"Invalid expression '{rawExpr}': expected 'steps.<stepId>.<field>'.");
        }

        var stepId = path[..dotIndex];
        var field = path[(dotIndex + 1)..];

        if (!_stepOutputs.TryGetValue(stepId, out var outputs))
        {
            throw new InvalidOperationException(
                $"Expression '{rawExpr}': step '{stepId}' not found. " +
                $"Available steps: [{string.Join(", ", _stepOutputs.Keys)}].");
        }

        return ResolveField(outputs, field, rawExpr);
    }

    private static string ResolveField(ActionOutputs outputs, string field, string rawExpr)
    {
        // Simple field: forceId, selectionId
        if (field == "forceId")
        {
            return outputs.ForceId
                ?? throw new InvalidOperationException(
                    $"Expression '{rawExpr}': step has no forceId output.");
        }

        if (field == "selectionId")
        {
            return outputs.SelectionId
                ?? throw new InvalidOperationException(
                    $"Expression '{rawExpr}': step has no selectionId output.");
        }

        // Dotted path into the selections map: selections.se-lasgun, selections.se-lasgun[1].
        if (field.StartsWith("selections.", StringComparison.Ordinal))
        {
            return ResolveNode(
                outputs.Selections, field["selections.".Length..], "selections", "entry", rawExpr);
        }

        // Dotted path into the categories map: categories.cat-troops. Keyed by CATEGORY ENTRY id
        // because a force mints all its categories at once and no action creates one, so there is
        // nothing to name them by except the entry they came from.
        if (field.StartsWith("categories.", StringComparison.Ordinal))
        {
            return ResolveNode(
                outputs.Categories, field["categories.".Length..], "categories", "category", rawExpr);
        }

        throw new InvalidOperationException(
            $"Expression '{rawExpr}': unknown field '{field}'. " +
            $"Supported: forceId, selectionId, selections.<entryId>[n], categories.<categoryEntryId>[n].");
    }

    /// <summary>
    /// Look up one node in a step's <c>selections</c>/<c>categories</c> map. <paramref name="path"/>
    /// is the key with an optional trailing <c>[n]</c>; no index means the first node.
    /// </summary>
    private static string ResolveNode(
        Dictionary<string, List<string>>? nodes,
        string path,
        string field,
        string keyNoun,
        string rawExpr)
    {
        var (key, index) = SplitIndex(path, field, rawExpr);

        if (nodes is null || !nodes.TryGetValue(key, out var ids) || ids.Count == 0)
        {
            throw new InvalidOperationException(
                $"Expression '{rawExpr}': {keyNoun} '{key}' not found in step's {field} map. " +
                $"Available: [{string.Join(", ", nodes?.Keys ?? (IEnumerable<string>)[])}].");
        }

        if (index >= ids.Count)
        {
            // Loud, not null: a spec asking for the third of two has stopped describing the roster
            // it runs against, and the count is what tells the author which one they meant.
            throw new InvalidOperationException(
                $"Expression '{rawExpr}': {keyNoun} '{key}' has {ids.Count} node(s) in step's " +
                $"{field} map, so index {index} is out of range (valid: 0..{ids.Count - 1}).");
        }

        return ids[index];
    }

    /// <summary>
    /// Split <c>se-unit-a[1]</c> into its key and index. No brackets means index 0 — the bare form
    /// names the first node, which is what every reference written before <c>[n]</c> existed meant.
    /// </summary>
    private static (string Key, int Index) SplitIndex(string path, string field, string rawExpr)
    {
        if (!path.EndsWith(']'))
        {
            // A '[' with no closing ']' is a typo, not a key: ids never contain either character.
            return path.Contains('[', StringComparison.Ordinal)
                ? throw MalformedIndex(path, field, rawExpr)
                : (path, 0);
        }

        var open = path.IndexOf('[', StringComparison.Ordinal);
        if (open <= 0)
        {
            throw MalformedIndex(path, field, rawExpr);
        }

        var indexText = path[(open + 1)..^1];
        if (!int.TryParse(indexText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            throw MalformedIndex(path, field, rawExpr);
        }

        return (path[..open], index);
    }

    private static InvalidOperationException MalformedIndex(string path, string field, string rawExpr)
        => new($"Expression '{rawExpr}': malformed sibling index in '{field}.{path}'. " +
            $"Write '{field}.<id>' for the first node or '{field}.<id>[n]' for the (n+1)th, " +
            "with n a non-negative integer.");
}
