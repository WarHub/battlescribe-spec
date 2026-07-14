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
/// <param name="ChildEnvironment">
/// Extra environment this selection's child processes are started with (today: <c>compare</c>'s
/// per-arm <c>--config-a</c>/<c>--config-b</c>). It is part of the selection rather than a loose
/// argument because it can <b>change which service the engine drives</b> — <c>--config-a
/// NR_ENGINE_URL=https://www.newrecruit.eu</c> takes an arm live from a parent shell that has no such
/// variable — and therefore it is an input to <see cref="LoadTarget"/>. Null = the child sees the
/// parent's environment unchanged.
/// </param>
internal sealed record EngineSelection(
    EngineEntry Entry,
    EngineDomain Domain,
    bool Headed,
    bool KeepAlive,
    ConcurrencyPlan? PlanOverride = null,
    IReadOnlyDictionary<string, string>? ChildEnvironment = null)
{
    /// <summary>Identity for applicability/assertions/labels; null for anonymous ad-hoc adapters.</summary>
    public string? EngineName => Entry.Name;

    /// <summary>
    /// <b>Whose machine pays for this run's traffic</b> — derived from what the engine declares about
    /// its endpoint (<see cref="EngineEndpoint"/>) and the environment its children will actually see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the fix for the load regression.</b> <c>bs-spec run --all --engine newrecruit</c>
    /// resolves the same <see cref="EngineEntry"/> and the same <see cref="EngineProfile"/> whether the
    /// child will replay a HAR file off local disk or drive <c>newrecruit.eu</c> — the only thing that
    /// differs is <c>NR_ENGINE_URL</c>, which the parent never read. So the policy, which cannot see an
    /// environment and must never string-match an engine name, gave both the same machine-width answer:
    /// <c>ceil(32 × 0.375)</c> = <b>12 adapter processes, each with its own browser</b>, pointed at a
    /// volunteer-run website — up from the serial <c>--workers 1</c> default that preceded it, and chosen
    /// by nobody.
    /// </para>
    /// <para>
    /// The engine declares the fact; this derives the answer; <see cref="ConcurrencyPolicy.For"/> acts on
    /// it. Three steps, one decision-maker each. And it is <b>fail-safe</b>: an engine that has not
    /// declared where its service lives (any <c>exec:</c>/<c>dotnet:</c> adapter we did not write)
    /// resolves to <see cref="LoadTarget.ThirdPartyLive"/> — see
    /// <see cref="EngineEndpoint.ResolveLoadTarget"/> for why the unsafe answer is the one that has to be
    /// earned.
    /// </para>
    /// </remarks>
    public LoadTarget LoadTarget =>
        Entry.EndpointFor(Domain == EngineDomain.Gamedata ? "gamedata" : "roster")
            .ResolveLoadTarget(LookupChildEnvironment);

    /// <summary>
    /// The environment the <b>child</b> will see: <see cref="ChildEnvironment"/> layered over this
    /// process's own, which is exactly what <see cref="StartProcess"/> hands it (a child inherits the
    /// parent's environment, and the caller's extras override). The parent's verdict about the endpoint
    /// and the child's behaviour therefore cannot disagree — they read the same value.
    /// </summary>
    private string? LookupChildEnvironment(string variable) =>
        ChildEnvironment is not null && ChildEnvironment.TryGetValue(variable, out var value)
            ? value
            : Environment.GetEnvironmentVariable(variable);

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
    /// <para>
    /// <b>The parent decides; the child is told.</b> The plan is computed HERE and passed down —
    /// never recomputed by the child, which is a separate process that may see a different machine
    /// (container CPU limits, cgroup quotas) and could therefore silently disagree. Two
    /// decision-makers for one decision is the defect this design exists to remove.
    /// </para>
    /// <para>
    /// <b>The load limit is applied last, and it also holds against a <see cref="PlanOverride"/>.</b>
    /// An override replaces the policy's answer wholesale, so without the final clamp
    /// <c>--policy workers=32</c> — or even <c>--policy reuse-roster=on</c>, whose <em>base</em> plan
    /// would have been recomputed without a load target — would put a machine's worth of browsers back
    /// on a third party's website. <c>run --policy</c> rejects such an override before we get here, so
    /// the clamp is never silently dropping a flag; it is the backstop that makes the ceiling true for
    /// any path that constructs a plan, not just the one that asks nicely.
    /// </para>
    /// </remarks>
    public ConcurrencyPlan EffectivePlan
    {
        get
        {
            var loadTarget = LoadTarget;
            var plan = PlanOverride ?? ConcurrencyPolicy.For(MachineProfile.Current(), Entry.Profile, loadTarget);
            plan = ConcurrencyPolicy.ClampToLoadTarget(plan, loadTarget);

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

        var headed = parseResult.GetValue(Headed);
        if (headed)
        {
            // A flag is accepted or rejected — never silently dropped (#305). Historically
            // EngineHostLocator.Resolve just dropped --headed on the floor for launchable
            // (exec:/dotnet:) adapters and for built-ins with no window to show; the user believed
            // they configured something, and they had not. Reject here, before a process is ever
            // spawned, naming what the engine actually supports.
            if (!entry.Builtin)
            {
                throw new CliInputException(
                    $"--headed cannot be delivered to '{entry.Name ?? entry.Executable}': launchable " +
                    "(exec:/dotnet:) adapters have no channel to receive it. Use a built-in -ui engine " +
                    "(battlescribe-ui, newrecruit-ui) instead.");
            }

            if (entry.Name is not { } name || !name.EndsWith("-ui", StringComparison.Ordinal))
            {
                throw new CliInputException(
                    $"engine '{entry.Name}' has no UI to show; --headed only applies to -ui engines " +
                    "(battlescribe-ui, newrecruit-ui). Pass --ui, or select an -ui engine variant directly.");
            }
        }

        return new EngineSelection(entry, domain, headed, KeepAlive: false);
    }
}

/// <summary>Raised for user-facing input errors; handlers translate it to a red message + exit 1.</summary>
internal sealed class CliInputException(string message) : Exception(message);
