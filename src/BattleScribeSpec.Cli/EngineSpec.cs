using System.CommandLine;
using BattleScribeSpec.Concurrency;
using BattleScribeSpec.Engines;

namespace BattleScribeSpec.Cli;

/// <summary>Which kind of spec the engine is editing.</summary>
internal enum EngineDomain
{
    Roster,
    Gamedata,
}

/// <summary>State dump rendering format.</summary>
internal enum OutputFormat
{
    Tree,
    Json,
}

/// <summary>A resolved engine selection: registry entry + domain + launch shaping.</summary>
/// <param name="Entry">The resolved registry entry (built-in or launchable).</param>
/// <param name="Domain">Which kind of spec this selection edits.</param>
/// <param name="Headed">Show the browser/app window instead of running headless.</param>
/// <param name="KeepAlive">Force the child to stay alive between specs (interactive debugging sugar for reuse=on).</param>
/// <param name="PlanOverride">
/// A user-supplied override of the policy's own answer (Tasks 5-6 wire <c>run</c>/<c>compare</c>'s
/// <c>--policy</c> to it). Null does <b>not</b> mean "let the child decide" — the parent still
/// computes and sends a plan; null merely means "no override, use what
/// <see cref="ConcurrencyPolicy"/> says". See <see cref="EffectivePlan"/>.
/// </param>
internal sealed record EngineSelection(EngineEntry Entry, EngineDomain Domain, bool Headed, bool KeepAlive, ConcurrencyPlan? PlanOverride = null)
{
    /// <summary>Identity for applicability/assertions/labels; null for anonymous ad-hoc adapters.</summary>
    public string? EngineName => Entry.Name;

    /// <summary>Assertion engine: strip a trailing "-ui" from the identity.</summary>
    public string? AssertionEngineName =>
        EngineName is { } n ? (n.EndsWith("-ui", StringComparison.Ordinal) ? n[..^3] : n) : null;

    public string Display => $"{(Domain == EngineDomain.Gamedata ? "gamedata" : "roster")}/{EngineName ?? "adapter"}";

    /// <summary>
    /// The plan this selection sends to the child, on <b>every</b> spawn: what
    /// <see cref="ConcurrencyPolicy"/> decides for this machine and this engine, with any
    /// <see cref="PlanOverride"/> replacing it and <see cref="KeepAlive"/> layered on top as
    /// "force reuse on".
    /// </summary>
    /// <remarks>
    /// <b>The parent decides; the child is told.</b> The plan is computed HERE and passed down —
    /// never recomputed by the child, which is a separate process that may see a different machine
    /// (container CPU limits, cgroup quotas) and could therefore silently disagree. Two
    /// decision-makers for one decision is the defect this design exists to remove.
    /// </remarks>
    public ConcurrencyPlan EffectivePlan
    {
        get
        {
            var plan = PlanOverride ?? ConcurrencyPolicy.For(MachineProfile.Current(), Entry.Profile);

            // --keep-alive is interactive-debugging sugar for "force reuse on"; it is folded into
            // the plan HERE so the child sees one decision, not a flag it must reconcile.
            return KeepAlive ? plan with { ReuseRoster = true, ReuseGameData = true } : plan;
        }
    }

    /// <summary>
    /// Compose the child's command line. Built-in engines are told the plan via <c>--policy</c>;
    /// launchable (<c>exec:</c>/<c>dotnet:</c>) adapters have no channel to receive one, so they get
    /// none — and an explicit <see cref="PlanOverride"/> against one is an error rather than a
    /// silent drop (see <see cref="EngineHostLocator.Resolve"/> and #305).
    /// </summary>
    public EngineLaunch ResolveLaunch() =>
        EngineHostLocator.Resolve(Entry, Headed, KeepAlive, plan: Entry.Builtin ? EffectivePlan : PlanOverride);

    /// <summary>Start the adapter process for this selection, with optional extra child environment.</summary>
    public Protocol.AdapterProcess StartProcess(IReadOnlyDictionary<string, string>? environment = null)
    {
        var launch = ResolveLaunch();
        return Protocol.AdapterProcess.Start(launch.Executable, launch.Arguments, environment);
    }
}

/// <summary>
/// The shared engine-selection options (<c>--engine</c>, <c>--ui</c>,
/// <c>--gamedata</c>/<c>--roster</c>, <c>--headed</c>). One instance per command;
/// <see cref="Resolve"/> turns the parsed values plus the spec input into an
/// <see cref="EngineSelection"/>.
/// </summary>
internal sealed class EngineOptions
{
    public Option<string> Engine { get; } = new("--engine")
    {
        Description = "Engine to use: a built-in name (battlescribe, battlescribe-ui, newrecruit, " +
            "newrecruit-ui), a connectable (exec:<command>, dotnet:<dll-path>, <name>=<connectable>), " +
            "or a name from engines.json.",
        DefaultValueFactory = _ => "battlescribe",
    };

    public Option<bool> Ui { get; } = new("--ui")
    {
        Description = "Drive the real desktop/browser app instead of the in-process/API engine.",
    };

    public Option<bool> Gamedata { get; } = new("--gamedata")
    {
        Description = "Force the gamedata domain (otherwise inferred from the spec path).",
    };

    public Option<bool> Roster { get; } = new("--roster")
    {
        Description = "Force the roster domain (otherwise inferred from the spec path).",
    };

    public Option<bool> Headed { get; } = new("--headed")
    {
        Description = "Show the browser/app window (UI engines; default is headless).",
    };

    public void AddTo(Command command)
    {
        command.Options.Add(Engine);
        command.Options.Add(Ui);
        command.Options.Add(Gamedata);
        command.Options.Add(Roster);
        command.Options.Add(Headed);
    }

    /// <summary>Resolve the parsed axes into a concrete <see cref="EngineSelection"/>.</summary>
    public EngineSelection Resolve(ParseResult parseResult, string? specInput)
    {
        var gamedata = parseResult.GetValue(Gamedata);
        var roster = parseResult.GetValue(Roster);
        if (gamedata && roster)
        {
            throw new CliInputException("--gamedata and --roster are mutually exclusive.");
        }

        var domain = (gamedata, roster) switch
        {
            (true, _) => EngineDomain.Gamedata,
            (_, true) => EngineDomain.Roster,
            _ => SpecLoading.InferEngineType(specInput) == "gamedata" ? EngineDomain.Gamedata : EngineDomain.Roster,
        };

        EngineConnectable connectable;
        try
        {
            connectable = EngineConnectable.Parse(parseResult.GetValue(Engine)!);
        }
        catch (FormatException ex)
        {
            throw new CliInputException(ex.Message);
        }

        var ui = parseResult.GetValue(Ui);
        if (ui && connectable.IsLaunchable)
        {
            throw new CliInputException(
                "--ui cannot be combined with an exec:/dotnet: connectable; name the engine variant directly.");
        }

        // --ui sugar: append -ui to a plain registry name (idempotent).
        if (ui && connectable is { IsLaunchable: false, Name: { } plain }
            && !plain.EndsWith("-ui", StringComparison.Ordinal))
        {
            connectable = connectable with { Name = plain + "-ui" };
        }

        EngineEntry entry;
        try
        {
            entry = EngineRegistry.LoadDefault().Resolve(connectable);
        }
        catch (KeyNotFoundException ex)
        {
            throw new CliInputException(ex.Message);
        }

        return new EngineSelection(entry, domain, parseResult.GetValue(Headed), KeepAlive: false);
    }
}

/// <summary>Raised for user-facing input errors; handlers translate it to a red message + exit 1.</summary>
internal sealed class CliInputException(string message) : Exception(message);
