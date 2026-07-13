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
    /// Recognized keys: <c>workers=N</c> (positive integer; also sets <see cref="ConcurrencyPlan.PoolSize"/>
    /// and <see cref="ConcurrencyPlan.MaxParallelThreads"/>), <c>reuse=on|off</c> (both domains at once),
    /// <c>reuse-roster=on|off</c>, <c>reuse-gamedata=on|off</c>. A key given more than once uses the last
    /// occurrence. Unset keys leave <paramref name="basePlan"/>'s value unchanged.
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
        var maxParallelThreads = basePlan.MaxParallelThreads;
        var reuseRoster = basePlan.ReuseRoster;
        var reuseGameData = basePlan.ReuseGameData;

        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new FormatException($"--policy: invalid entry '{entry}' — expected KEY=VALUE.");
            }

            var key = entry[..separator].Trim();
            var value = entry[(separator + 1)..].Trim();

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
                    maxParallelThreads = parsedWorkers;
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
                    throw new FormatException($"--policy: unknown key '{key}'.");
            }
        }

        return basePlan with
        {
            Workers = workers,
            PoolSize = poolSize,
            MaxParallelThreads = maxParallelThreads,
            ReuseRoster = reuseRoster,
            ReuseGameData = reuseGameData,
        };
    }

    private static bool ParseOnOff(string key, string value) => value switch
    {
        "on" => true,
        "off" => false,
        _ => throw new FormatException($"--policy: '{key}' must be 'on' or 'off', got '{value}'."),
    };
}
