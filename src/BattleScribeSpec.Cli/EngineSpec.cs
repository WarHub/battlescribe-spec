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
    /// Reads the endpoint variable <b>out of the environment the child is actually handed</b> —
    /// <see cref="Protocol.AdapterProcess.ComposeChildEnvironment"/>, the same code, the same dictionary
    /// and the same comparer that <see cref="StartProcess"/> spawns with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is not enough for the two to hold the same pairs; they must be the same lookup.</b> This
    /// method used to consult <see cref="ChildEnvironment"/> directly — a dictionary the CLI builds with
    /// <see cref="StringComparer.Ordinal"/> — and fall back to <c>Environment.GetEnvironmentVariable</c>.
    /// That is a <em>second</em> implementation of "what does this variable name mean", and on Windows it
    /// disagreed with the first: <c>ProcessStartInfo.Environment</c> is case-INsensitive there, so
    /// <c>--config-a "nr_engine_url=https://www.newrecruit.eu"</c> was a miss here (⇒
    /// <see cref="LoadTarget.Local"/> ⇒ <c>ceil(cpuCount × k)</c> browsers) and a hit for the child (⇒
    /// live). The clamp vanished on one lowercased letter. Composing the child's environment and reading
    /// the answer back out of <em>it</em> leaves no second implementation to drift — and it is right on
    /// Linux too, where the same variable genuinely is a different one.
    /// </para>
    /// </remarks>
    private string? LookupChildEnvironment(string variable) =>
        Protocol.AdapterProcess.ComposeChildEnvironment(ChildEnvironment).GetValueOrDefault(variable);

    /// <summary>Assertion engine: strip a trailing "-ui" from the identity.</summary>
    public string? AssertionEngineName =>
        EngineName is { } n ? (n.EndsWith("-ui", StringComparison.Ordinal) ? n[..^3] : n) : null;

    public string Display => $"{(Domain == EngineDomain.Gamedata ? "gamedata" : "roster")}/{EngineName ?? "adapter"}";

    /// <summary>
    /// The plan this selection sends to the child, on <b>every</b> spawn: what
    /// <see cref="ConcurrencyPolicy"/> decides for this machine, this engine and this
    /// <see cref="LoadTarget"/>, with any <see cref="PlanOverride"/> replacing it — and the load limit
    /// applied last, to whichever of the two it ends up being.
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
            return ConcurrencyPolicy.ClampToLoadTarget(plan, loadTarget);
        }
    }

    /// <summary>
    /// Compose the child's command line. Built-in engines are told the plan via <c>--policy</c>;
    /// launchable (<c>exec:</c>/<c>dotnet:</c>) adapters have no channel to receive one, so they get
    /// none — and an explicit <see cref="PlanOverride"/> against one is an error rather than a
    /// silent drop (see <see cref="EngineHostLocator.Resolve"/> and #305).
    /// </summary>
    public EngineLaunch ResolveLaunch() =>
        EngineHostLocator.Resolve(Entry, Headed, plan: Entry.Builtin ? EffectivePlan : PlanOverride);

    /// <summary>Start the adapter process for this selection, with optional extra child environment.</summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="ChildEnvironment"/> is applied HERE, not by the caller.</b> It used to be the
    /// caller's job (<c>compare</c> copied its <c>--config-*</c> into the environment it passed), which
    /// meant the environment that <em>decided the load target</em> and the environment the child was
    /// <em>actually started with</em> were assembled by two different pieces of code — the shape of every
    /// bug on this branch. Now the selection carries the fact and applies it, so a child cannot be
    /// spawned into an environment its own plan was not computed against.
    /// </para>
    /// <para>
    /// <paramref name="environment"/> is the harness's own wiring (the telemetry collector's endpoint,
    /// the worker index); <see cref="ChildEnvironment"/> is the user's, and it goes on top — a
    /// <c>--config-*</c> may override our wiring, never the reverse. It is also the only one of the two
    /// that may name an <em>endpoint</em> variable, which is what lets <see cref="LoadTarget"/> be derived
    /// from it alone.
    /// </para>
    /// </remarks>
    public Protocol.AdapterProcess StartProcess(IReadOnlyDictionary<string, string>? environment = null)
    {
        var launch = ResolveLaunch();
        return Protocol.AdapterProcess.Start(launch.Executable, launch.Arguments, ChildOverlay(environment));
    }

    /// <summary>The overlay a child of this selection is started with: <paramref name="extra"/> with <see cref="ChildEnvironment"/> layered on top.</summary>
    private IReadOnlyDictionary<string, string>? ChildOverlay(IReadOnlyDictionary<string, string>? extra)
    {
        if (ChildEnvironment is null || ChildEnvironment.Count == 0)
        {
            return extra;
        }

        if (extra is null || extra.Count == 0)
        {
            return ChildEnvironment;
        }

        var merged = new Dictionary<string, string>(extra, StringComparer.Ordinal);
        foreach (var (key, value) in ChildEnvironment)
        {
            merged[key] = value;
        }

        return merged;
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
    /// <summary>
    /// Test seam for <see cref="EngineRegistry.LoadDefault"/>'s walk-up start point. Null (the
    /// production default — every production caller constructs <see cref="EngineOptions"/> with no
    /// initializer) means "start from <see cref="Directory.GetCurrentDirectory"/>", exactly the
    /// behaviour before this property existed. Tests set it to reach a temp-directory
    /// <c>engines.json</c> without mutating the process-wide working directory, which would leak into
    /// every other test in the run.
    /// </summary>
    internal string? RegistryStartDirectory { get; init; }

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

    /// <summary>
    /// Records <b>who mutated NewRecruit's Pinia stores, and from where</b>, into the failure
    /// diagnostic report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rest of the diagnostics answer "what is the state now?". This answers "who changed it?",
    /// which is the question every recent NR bug actually posed (#334, #336, #337, #339) and which a
    /// snapshot cannot answer. It is the technique that cracked two of them, made repeatable: wrap
    /// the store action, keep the caller stack. See <c>NrStoreTraceJs</c>.
    /// </para>
    /// <para>
    /// Off by default and never implied. Wrapping replaces the store's function identities, so this
    /// perturbs the thing under observation — which is tolerable while debugging and not tolerable
    /// in <c>bs-spec compare</c>, whose whole job is to measure one variable at a time.
    /// </para>
    /// </remarks>
    public Option<bool> TraceStore { get; } = new("--trace-store")
    {
        Description = "Record NR Pinia store mutations (action, args, before/after, caller stack) " +
            "into the failure diagnostic report. Answers 'who changed this state?', which the " +
            "state snapshot cannot. NR UI engines only; off by default.",
    };

    /// <summary>
    /// <b>The declaration channel for an ad-hoc adapter's endpoint</b> — the one thing an
    /// <c>exec:</c>/<c>dotnet:</c> connectable cannot state any other way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An adapter the harness has never seen has an <em>undeclared</em> endpoint, and an undeclared
    /// endpoint is treated as a third party's live service — held to
    /// <see cref="ConcurrencyPolicy.ThirdPartyLiveLoadLimit"/> on both axes. That fail-safe is right, and
    /// it costs wall-clock, and there has to be a way to take the wall-clock back <b>by stating a fact</b>
    /// rather than by the harness guessing one. A registered engine states it in <c>engines.json</c>
    /// (<c>"endpoint": "local"</c>); an ad-hoc <c>--engine "name=exec:…"</c> is registered nowhere, and
    /// states it here.
    /// </para>
    /// <para>
    /// <b>Why this exists at all, and why it is not "inherit the built-in's declaration by name".</b> That
    /// was tried. <c>EngineRegistry.Resolve</c> briefly let a launchable claiming a built-in's name
    /// inherit that built-in's endpoint <em>and</em> its measured <c>EngineProfile</c> — and
    /// <c>--engine "newrecruit=exec:./anything"</c> with <c>NR_ENGINE_URL</c> unset then resolved to
    /// <see cref="LoadTarget.Local"/> at <c>ceil(cpuCount × 0.375)</c> workers, straight through both
    /// fail-safes, for a binary nobody had ever run. <b>A name is an applicability label, not evidence.</b>
    /// A declaration is a person saying what they know about the executable they chose; a name is what
    /// <c>RunBatch</c> uses to pick specs.
    /// </para>
    /// <para>
    /// <b>Built-in engines reject it.</b> They declare their own endpoints, measured, per domain
    /// (<c>newrecruit</c>'s roster is <c>url-var:NR_ENGINE_URL</c> and its gamedata is always this
    /// machine), and letting a flag overwrite that would hand anyone a one-word override of the fail-safe
    /// this branch exists to install — <c>--engine newrecruit --engine-endpoint local</c> with the live
    /// URL set is precisely the 12-browsers-at-newrecruit.eu bug, spelled differently.
    /// </para>
    /// </remarks>
    public Option<string?> EndpointDeclaration { get; } = new("--engine-endpoint")
    {
        Description = "Declare where an exec:/dotnet: adapter's service lives: 'local' (this machine — " +
            "takes the machine's full measured width), 'third-party-live' (it drives someone else's " +
            "production site), or 'url-var:NAME' (live iff NAME holds a non-loopback URL). Ad-hoc adapters " +
            "are otherwise undeclared, and an undeclared endpoint is held to the third-party live load " +
            "limit. Built-in engines declare their own and reject this flag.",
    };

    /// <summary>
    /// Applies <c>--trace-store</c> by exporting the switch the NR drivers read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An environment variable rather than a plumbed parameter because the engine usually lives in a
    /// <c>bs-engine-host</c> <b>child process</b>, and an env var is the channel that survives that
    /// hop — the same reason <c>compare</c>'s <c>--config-a</c>/<c>--config-b</c> take that shape.
    /// The name is inlined rather than referenced from <c>NrStoreTraceJs</c> because the CLI
    /// deliberately does not reference driver types (see <c>ReportDiagnosticDumps</c>, which inlines
    /// the BS-UI diagnostics path for the same reason). <c>NrStoreTraceJs.EnableVariable</c> is the
    /// source of truth; <c>CliDiagnosticSwitchTests</c> pins the two together.
    /// </para>
    /// <para>
    /// This is a diagnostics switch, not a policy knob. The retired-knob rule that deleted
    /// <c>NR_PARALLEL</c> and <c>BS_UI_KEEP_ALIVE</c> is about inputs to <c>ConcurrencyPolicy</c> —
    /// things that must have exactly one source. This changes what gets recorded, not what runs.
    /// </para>
    /// </remarks>
    public void ApplyDiagnosticSwitches(ParseResult parseResult)
    {
        if (parseResult.GetValue(TraceStore))
        {
            Environment.SetEnvironmentVariable("NR_TRACE_STORE", "1");
        }
    }

    public void AddTo(Command command)
    {
        command.Options.Add(Engine);
        command.Options.Add(Ui);
        command.Options.Add(Gamedata);
        command.Options.Add(Roster);
        command.Options.Add(Headed);
        command.Options.Add(TraceStore);
        command.Options.Add(EndpointDeclaration);
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
            entry = EngineRegistry.LoadDefault(RegistryStartDirectory).Resolve(connectable);
        }
        catch (KeyNotFoundException ex)
        {
            throw new CliInputException(ex.Message);
        }

        if (parseResult.GetValue(EndpointDeclaration) is { Length: > 0 } declaration)
        {
            // Only a foreign adapter may be declared here. A built-in's endpoints are measured facts about
            // engines we wrote, declared per domain in EngineRegistry.Builtins — and this flag would
            // otherwise be a one-word override of the fail-safe: `--engine newrecruit --engine-endpoint
            // local` with NR_ENGINE_URL pointed at the live site is exactly the regression this branch
            // exists to prevent. Rejected, not silently ignored (#305: a flag is honoured or refused).
            //
            // Test the RESOLVED engine, not the shape of the --engine token. IsLaunchable is a fact about
            // the token's syntax (did it carry an exec:/dotnet: target?); Builtin is a fact about what the
            // token resolved to. They part company for an engines.json-registered engine referenced by
            // plain name — foreign code, no measured profile, and every word of the message below false
            // about it. That confusion of one fact for a neighbouring one is this branch's signature bug;
            // --headed makes the same decision 30 lines down and gets the predicate right.
            if (entry.Builtin)
            {
                throw new CliInputException(
                    "--engine-endpoint declares where a foreign/ad-hoc adapter's service lives — " +
                    $"'{entry.Name}' is a built-in engine, and its endpoints are measured facts, declared " +
                    "per domain (EngineRegistry), not something a flag may state on its behalf. Overriding " +
                    "that from the command line would let one word turn the third-party load limit off for " +
                    "a live site.");
            }

            EngineEndpoint endpoint;
            try
            {
                endpoint = EngineEndpoint.Parse(declaration);
            }
            catch (FormatException ex)
            {
                throw new CliInputException($"--engine-endpoint: {ex.Message}");
            }

            // One declaration, both domains: an ad-hoc adapter is one binary and the operator is telling us
            // where it goes. (The built-ins split roster from gamedata because they genuinely differ —
            // newrecruit's roster can go live and its gamedata never does. Nothing about a foreign binary
            // supports drawing that distinction on its behalf, so we do not invent one.)
            entry = entry with { RosterEndpoint = endpoint, GameDataEndpoint = endpoint };
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

        return new EngineSelection(entry, domain, headed);
    }
}

/// <summary>Raised for user-facing input errors; handlers translate it to a red message + exit 1.</summary>
internal sealed class CliInputException(string message) : Exception(message);
