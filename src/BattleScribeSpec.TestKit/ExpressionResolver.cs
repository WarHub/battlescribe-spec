using BattleScribeSpec.Roster;

namespace BattleScribeSpec;

/// <summary>
/// Resolves ${{ steps.&lt;stepId&gt;.&lt;field&gt; }} expressions against stored step outputs.
/// Supports dotted paths for nested lookups into the selections map.
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
            if (outputs.Selections is { } sels)
            {
                foreach (var (entryId, selId) in sels)
                {
                    if (selId is { Length: > 0 })
                    {
                        map[selId] = $"{ExprStart} steps.{stepId}.selections.{entryId} {ExprEnd}";
                    }
                }
            }
            if (outputs.Categories is { } cats)
            {
                foreach (var (categoryEntryId, catId) in cats)
                {
                    if (catId is { Length: > 0 })
                    {
                        map[catId] = $"{ExprStart} steps.{stepId}.categories.{categoryEntryId} {ExprEnd}";
                    }
                }
            }
        }

        return map;
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

        // Dotted path into selections map: selections.se-lasgun
        if (field.StartsWith("selections.", StringComparison.Ordinal))
        {
            var entryId = field["selections.".Length..];
            if (outputs.Selections is null || !outputs.Selections.TryGetValue(entryId, out var selId))
            {
                throw new InvalidOperationException(
                    $"Expression '{rawExpr}': entry '{entryId}' not found in step's selections map. " +
                    $"Available: [{string.Join(", ", outputs.Selections?.Keys ?? (IEnumerable<string>)[])}].");
            }

            return selId;
        }

        // Dotted path into categories map: categories.cat-troops. Keyed by CATEGORY ENTRY id
        // because a force mints all its categories at once and no action creates one, so there is
        // nothing to name them by except the entry they came from.
        if (field.StartsWith("categories.", StringComparison.Ordinal))
        {
            var categoryEntryId = field["categories.".Length..];
            if (outputs.Categories is null || !outputs.Categories.TryGetValue(categoryEntryId, out var catId))
            {
                throw new InvalidOperationException(
                    $"Expression '{rawExpr}': category '{categoryEntryId}' not found in step's categories map. " +
                    $"Available: [{string.Join(", ", outputs.Categories?.Keys ?? (IEnumerable<string>)[])}].");
            }

            return catId;
        }

        throw new InvalidOperationException(
            $"Expression '{rawExpr}': unknown field '{field}'. " +
            $"Supported: forceId, selectionId, selections.<entryId>, categories.<categoryEntryId>.");
    }
}
