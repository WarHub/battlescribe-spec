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

        throw new InvalidOperationException(
            $"Expression '{rawExpr}': unknown field '{field}'. " +
            $"Supported: forceId, selectionId, selections.<entryId>.");
    }
}
