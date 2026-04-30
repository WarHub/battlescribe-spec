namespace BattleScribeSpec;

/// <summary>
/// Parses and applies tag filter expressions for spec filtering.
/// Supports comma-separated tags with +/- prefix:
///   "cost,constraint"       → include specs with cost OR constraint
///   "-undefined-behavior"   → exclude specs with undefined-behavior
///   "cost,-undefined-behavior" → include cost, exclude undefined-behavior
/// Include uses OR semantics (any match). Exclude overrides include.
/// </summary>
public sealed class TagFilter
{
    public IReadOnlyList<string> IncludeTags { get; }
    public IReadOnlyList<string> ExcludeTags { get; }

    private TagFilter(IReadOnlyList<string> includeTags, IReadOnlyList<string> excludeTags)
    {
        IncludeTags = includeTags;
        ExcludeTags = excludeTags;
    }

    /// <summary>
    /// Parse a tag filter expression. Returns null if expression is null or empty.
    /// </summary>
    public static TagFilter? Parse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        var includes = new List<string>();
        var excludes = new List<string>();

        foreach (var raw in expression.Split(','))
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            if (token.StartsWith('-'))
            {
                var tag = token[1..].Trim();
                if (tag.Length > 0)
                {
                    excludes.Add(tag);
                }
            }
            else if (token.StartsWith('+'))
            {
                var tag = token[1..].Trim();
                if (tag.Length > 0)
                {
                    includes.Add(tag);
                }
            }
            else
            {
                includes.Add(token);
            }
        }

        if (includes.Count == 0 && excludes.Count == 0)
        {
            return null;
        }

        return new TagFilter(includes, excludes);
    }


    /// <summary>
    /// Check if a spec's tags match this filter.
    /// Include: spec must have at least one included tag (OR semantics).
    /// Exclude: spec must not have any excluded tag (overrides include).
    /// </summary>
    public bool Matches(IReadOnlyList<string>? tags)
    {
        tags ??= [];

        // Exclude check first — any excluded tag disqualifies
        if (ExcludeTags.Count > 0)
        {
            foreach (var exclude in ExcludeTags)
            {
                if (tags.Any(t => string.Equals(t, exclude, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
        }

        // Include check — if includes specified, at least one must match
        if (IncludeTags.Count > 0)
        {
            var hasAny = false;
            foreach (var include in IncludeTags)
            {
                if (tags.Any(t => string.Equals(t, include, StringComparison.OrdinalIgnoreCase)))
                {
                    hasAny = true;
                    break;
                }
            }
            if (!hasAny)
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        foreach (var tag in IncludeTags)
        {
            parts.Add(tag);
        }

        foreach (var tag in ExcludeTags)
        {
            parts.Add($"-{tag}");
        }

        return string.Join(",", parts);
    }
}
