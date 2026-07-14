using System.Globalization;

namespace BattleScribeSpec.Concurrency;

/// <summary>
/// The one <c>--policy k=v,...</c> parser, shared by <c>serve</c>, <c>run</c>, and <c>compare</c>.
/// Applies overrides on top of a base <see cref="ConcurrencyPlan"/> — usually
/// <see cref="ConcurrencyPolicy.For(MachineProfile, EngineProfile)"/>'s own answer — so a caller
/// can nudge one knob (e.g. force <c>reuse=off</c> for an ablation) without inventing a
/// command-specific flag for it. One vocabulary for this idea, not three.
/// </summary>
public static class PolicyOverride
{
    /// <summary>
    /// Parse <paramref name="raw"/> and apply recognized keys on top of <paramref name="basePlan"/>.
    /// Recognized keys: <c>workers=N</c> (positive integer; also sets <see cref="ConcurrencyPlan.PoolSize"/>,
    /// which mirrors it), <c>reuse=on|off</c> (both domains at once), <c>reuse-roster=on|off</c>,
    /// <c>reuse-gamedata=on|off</c>. A key given more than once uses the last occurrence. Unset keys
    /// leave <paramref name="basePlan"/>'s value unchanged.
    /// </summary>
    /// <param name="raw">Comma-separated <c>KEY=VALUE</c> pairs; null or empty means no overrides.</param>
    /// <param name="basePlan">The plan the overrides apply on top of.</param>
    /// <returns><paramref name="basePlan"/> with any recognized keys overridden.</returns>
    /// <exception cref="FormatException">
    /// An entry isn't <c>KEY=VALUE</c>, the key is unrecognized, or a value fails to parse.
    /// </exception>
    public static ConcurrencyPlan Apply(string? raw, ConcurrencyPlan basePlan)
    {
        ArgumentNullException.ThrowIfNull(basePlan);
        if (string.IsNullOrEmpty(raw))
        {
            return basePlan;
        }

        var workers = basePlan.Workers;
        var poolSize = basePlan.PoolSize;
        var reuseRoster = basePlan.ReuseRoster;
        var reuseGameData = basePlan.ReuseGameData;

        foreach (var (key, value) in ParseEntries(raw))
        {
            switch (key)
            {
                case "workers":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWorkers)
                        || parsedWorkers < 1)
                    {
                        throw new FormatException($"--policy: 'workers' must be a positive integer, got '{value}'.");
                    }

                    workers = parsedWorkers;
                    poolSize = parsedWorkers;
                    break;

                case "reuse":
                    var reuse = ParseOnOff(key, value);
                    reuseRoster = reuse;
                    reuseGameData = reuse;
                    break;

                case "reuse-roster":
                    reuseRoster = ParseOnOff(key, value);
                    break;

                case "reuse-gamedata":
                    reuseGameData = ParseOnOff(key, value);
                    break;

                default:
                    // Unreachable: ParseEntries already rejected unknown keys. Kept so that adding a
                    // key there without handling it here fails loudly instead of being ignored.
                    throw new FormatException($"--policy: unknown key '{key}'.");
            }
        }

        return basePlan with
        {
            Workers = workers,
            PoolSize = poolSize,
            ReuseRoster = reuseRoster,
            ReuseGameData = reuseGameData,
        };
    }

    /// <summary>
    /// The set of keys <paramref name="raw"/> actually mentions, parsed and validated by exactly the
    /// same rules as <see cref="Apply"/> (so an unknown key or a malformed entry throws here too).
    /// </summary>
    /// <remarks>
    /// This exists so a command can reject a key that is <b>inapplicable to it</b> before acting on
    /// the plan. <c>bs-spec run &lt;spec&gt; --policy workers=8</c> used to be accepted, forwarded to
    /// the child, and completely inert (a single-spec run spawns exactly one adapter, and the child
    /// never reads <c>workers</c>) — a silently-dropped flag, which is the failure mode #305 exists
    /// to forbid. Knowing which keys were <em>typed</em> — not which values differ from the default —
    /// is what lets <c>RunCommand</c> reject it: <c>workers=1</c> is just as meaningless as
    /// <c>workers=8</c> there, and comparing plans could not tell the two apart.
    /// </remarks>
    /// <param name="raw">Comma-separated <c>KEY=VALUE</c> pairs; null or empty means no keys.</param>
    /// <returns>The recognized keys present in <paramref name="raw"/>.</returns>
    /// <exception cref="FormatException">
    /// An entry isn't <c>KEY=VALUE</c>, or the key is unrecognized.
    /// </exception>
    public static IReadOnlySet<string> Keys(string? raw)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(raw))
        {
            return keys;
        }

        foreach (var (key, _) in ParseEntries(raw))
        {
            keys.Add(key);
        }

        return keys;
    }

    /// <summary>Split into KEY=VALUE pairs, rejecting malformed entries and unknown keys.</summary>
    private static IEnumerable<(string Key, string Value)> ParseEntries(string raw)
    {
        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new FormatException($"--policy: invalid entry '{entry}' — expected KEY=VALUE.");
            }

            var key = entry[..separator].Trim();
            if (key is not ("workers" or "reuse" or "reuse-roster" or "reuse-gamedata"))
            {
                throw new FormatException($"--policy: unknown key '{key}'.");
            }

            yield return (key, entry[(separator + 1)..].Trim());
        }
    }

    private static bool ParseOnOff(string key, string value) => value switch
    {
        "on" => true,
        "off" => false,
        _ => throw new FormatException($"--policy: '{key}' must be 'on' or 'off', got '{value}'."),
    };
}
