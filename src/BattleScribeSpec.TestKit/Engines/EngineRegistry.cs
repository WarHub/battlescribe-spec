using System.Globalization;
using System.Text.Json;
using BattleScribeSpec.Concurrency;

namespace BattleScribeSpec.Engines;

/// <summary>Resolved engine selection: identity + launch info + metadata.</summary>
/// <param name="Name">Registry identity (spec applicability, report labels); null for anonymous ad-hoc adapters.</param>
/// <param name="Executable">Launch executable; null for built-ins (resolved by the engine host integration).</param>
/// <param name="Arguments">Launch arguments; null when none.</param>
/// <param name="Domains">Spec domains the engine claims; the describe handshake narrows this at runtime.</param>
/// <param name="Profile">What the engine declares about itself — the single source of <c>MaxParallel</c> etc.</param>
/// <param name="Builtin">True for the in-box engines.</param>
/// <param name="RosterEndpoint">
/// Where this engine's <b>roster</b> service lives (see <see cref="EngineEndpoint"/>). Null =
/// undeclared, which resolves to <see cref="Concurrency.LoadTarget.ThirdPartyLive"/> — an entry
/// constructed without stating this fails <em>safe</em>, not fast.
/// </param>
/// <param name="GameDataEndpoint">
/// Where this engine's <b>gamedata</b> service lives. Separate from <paramref name="RosterEndpoint"/>
/// because the built-in NewRecruit engines genuinely differ by domain: the roster engine honours
/// <c>NR_ENGINE_URL</c> and can go live; the gamedata engine is always a frozen static dir.
/// </param>
public sealed record EngineEntry(
    string? Name,
    string? Executable,
    string? Arguments,
    IReadOnlyList<string> Domains,
    EngineProfile Profile,
    bool Builtin,
    EngineEndpoint? RosterEndpoint = null,
    EngineEndpoint? GameDataEndpoint = null)
{
    /// <summary>
    /// This engine's endpoint declaration for <paramref name="domain"/> (<c>"roster"</c> /
    /// <c>"gamedata"</c>), never null: an absent declaration is
    /// <see cref="EngineEndpoint.Undeclared"/>, which is the fail-safe answer, not the convenient one.
    /// </summary>
    /// <param name="domain">The spec domain the run is in.</param>
    public EngineEndpoint EndpointFor(string domain) =>
        (string.Equals(domain, "gamedata", StringComparison.Ordinal) ? GameDataEndpoint : RosterEndpoint)
            ?? EngineEndpoint.Undeclared;
}

/// <summary>
/// Maps engine names to launch info: built-in entries plus optional repo-level
/// <c>engines.json</c> registrations (eshost-style named host registry).
/// </summary>
public sealed class EngineRegistry
{
    private static readonly string[] BothDomains = ["roster", "gamedata"];

    // Conservative default for engines that declare nothing: cheap to construct, no reuse claimed
    // (reuse must be earned — see EngineProfile's remarks), and NO declared ceiling of its own —
    // MaxParallel: 0 means "unlimited", not "serial". What actually bounds such an engine is
    // ConcurrencyPolicy.UndeclaredMemoryWorkerCap, which binds precisely because
    // MemPerInstanceBytes stays 0 = "undeclared". That is deliberate and permanent: an engine that
    // has not declared its memory footprint does not get machine-width parallelism.
    private static readonly EngineProfile DefaultProfile = new(
        MaxParallel: 0, ColdStartCost.Cheap, ReuseSafeRoster: false, ReuseSafeGameData: false);

    // Values transcribed from what has been MEASURED — never invented. Reuse-safety and cold-start
    // cost come from docs/warm-reuse.md; MemPerInstanceBytes and OversubscriptionFactor (the PROCESS
    // axis — adapter processes on the CLI path) come from docs/concurrency-policy-measurements.md
    // §1–§6; ContextPoolSize and MemPerContextBytes (the CONTEXT axis — browser contexts in the xUnit
    // fixture pools, which is what every NR CI lane runs) come from §7 of the same document.
    //
    // THE TWO AXES ARE MEASURED ON DIFFERENT PATHS AND DISAGREE. Do not "reconcile" them: on a 4-vCPU
    // runner newrecruit-ui wants 4 worker processes and 16 browser contexts, and both numbers are
    // right. Feeding one number to both is the bug this separation fixes (#314).
    private static readonly Dictionary<string, EngineEntry> Builtins = new()
    {
        // MemPerInstanceBytes UNDECLARED (0) — never measured. While it is 0 this engine is bound by
        // ConcurrencyPolicy.UndeclaredMemoryWorkerCap, which is the correct, conservative answer for
        // an engine nobody has measured. Measure it and the cap retires for this engine by itself.
        //
        // CONTEXT AXIS: also undeclared (0) → ConcurrencyPolicy.UndeclaredContextPoolSize. This engine
        // is in-process IKVM with no browser and no context pool, so no fixture asks it for a pool
        // size today; the default is what it would get if one ever did, and 4 is the low end of the
        // measured band (see the constant). Do not invent a number for it — measure it, like the rest.
        //
        // ENDPOINT: this machine, in both domains, unconditionally — an in-process IKVM engine with no
        // network code at all. It is the reason the load target cannot be "is NR_ENGINE_URL set?": that
        // question would throttle THIS engine in any shell that happened to export the variable, for a
        // service it does not have. Each engine declares its own endpoint; nobody else's environment
        // speaks for it (EngineEndpoint).
        ["battlescribe"] = new(
            "battlescribe", null, null, BothDomains,
            new EngineProfile(MaxParallel: 0, ColdStartCost.Cheap, ReuseSafeRoster: false, ReuseSafeGameData: false),
            Builtin: true,
            RosterEndpoint: EngineEndpoint.OnThisMachine,
            GameDataEndpoint: EngineEndpoint.OnThisMachine),

        // MemPerInstanceBytes MEASURED: 1,055,391,744 B (≈0.98 GiB) — one JVM (app + -javaagent in
        // the same process) plus its bs-engine-host adapter, peak working set over a full 54-spec
        // gamedata run (docs/concurrency-policy-measurements.md §4). MaxParallel: 1 means the memory
        // bound can never actually bind here; the number exists so the policy can *prove* that
        // rather than assume it. OversubscriptionFactor is therefore moot and stays at its default.
        //
        // CONTEXT AXIS: MaxContexts: 1 — this engine has no browser-context pool at all (it drives ONE
        // JavaFX desktop app through one Java agent), so one context is all there can ever be. It is
        // declared on the CONTEXT axis, in its own field, and NOT inherited from MaxParallel: 1 — which
        // is what the policy used to do, generalizing this engine's coincidence (its two ceilings happen
        // to be the same number) into a cross-axis rule that then silently halved a third-party adapter's
        // measured pool. Two facts about two axes that agree; not one fact wearing two hats.
        // ContextPoolSize stays undeclared: MaxContexts already pins it at 1, so there is no number to
        // measure. Pinned by Policy_BattlescribeUi_StaysAtOneWorker_OnEveryProfile across four machine
        // profiles.
        //
        // ENDPOINT: this machine, in both domains — a JavaFX desktop app driven over a local Java agent.
        ["battlescribe-ui"] = new(
            "battlescribe-ui", null, null, BothDomains,
            new EngineProfile(
                MaxParallel: 1, ColdStartCost.Expensive, ReuseSafeRoster: true, ReuseSafeGameData: true,
                MemPerInstanceBytes: 1_055_391_744L, MaxContexts: 1),
            Builtin: true,
            RosterEndpoint: EngineEndpoint.OnThisMachine,
            GameDataEndpoint: EngineEndpoint.OnThisMachine),

        // MemPerInstanceBytes MEASURED: 1,313,420,083 B (≈1.22 GiB) per worker — adapter (≈543 MB) +
        // Playwright driver (≈377 MB) + chrome-headless-shell tree (≈332 MB). Lighter than
        // newrecruit-ui's 1.44 GiB, as expected: no heavy SPA in the page
        // (docs/concurrency-policy-measurements.md §5).
        //
        // OversubscriptionFactor: 0.375 is MEASURED — and is deliberately BELOW the measured optimum
        // of 0.47 (P=15 on 32 logical cpus). Two things you must know before touching it:
        //
        // 1. THE CLIFF IS BRUTALLY ASYMMETRIC. P=15 → P=16 costs 1.97x for ONE extra worker (15.8s →
        //    31.0s, reproduced in all four runs; p95 blows up 4358ms → 16033ms while p50 barely
        //    moves — a starved tail). Overshooting by one worker doubles the wall-clock; undershooting
        //    by one costs a few percent. Fitting below the optimum is correct here, not timid.
        //    ceil(32 × 0.375) = 12 workers = 18.4s, 17% off the optimum for 3 workers of margin.
        //
        // 2. THIS CONSTANT IS NOT PORTABLE, AND THE MODEL CANNOT SAY SO PROPERLY. The cliff lands on
        //    the box's PHYSICAL core count (16 of 32 logical — a 2:1 SMT box). Physically the optimum
        //    is "one worker per physical core"; it is not a property of the number 0.47. But
        //    MachineProfile only knows Environment.ProcessorCount — LOGICAL processors — so k has to
        //    encode the SMT ratio of the box it was fitted on. On another 2:1 SMT machine 0.375 lands
        //    at or below physical cores (safe). ON A NON-SMT MACHINE IT UNDER-PROVISIONS BY ~2x.
        //    This engine is CPU-bound (p50 2.4s/spec, pure compute) so it gets nothing from
        //    hyperthreads; newrecruit-ui is I/O-bound (p50 17.1s/spec) and scales past them fine —
        //    which is exactly why k is per-engine (1.0 vs 0.375: a 2.7x spread on identical hardware).
        //    A PhysicalCoreCount input to MachineProfile is the real fix; it is filed as a follow-up,
        //    not attempted here. Do not read 0.375 as a portable truth.
        //
        // ---- CONTEXT AXIS (the xUnit fixture pool — `nr-frozen`, `nr-live-conformance`) ----
        //
        // ContextPoolSize: 4 is MEASURED, and it is an ABSOLUTE COUNT, NOT ceil(cpuCount × anything).
        // The `dotnet test` wall bottoms out at pool 4 on a 32-core box AND on a 4-CPU/16 GiB
        // container — the same 4 (§7.2). The [Fact] wall FLOORS at ~9s (dev) / ~11s (container) from
        // pool 4 and never improves again however many contexts you add: all contexts share ONE
        // Chromium and ONE Playwright Node driver, and every CDP message funnels through that single
        // driver. Per-spec work is 19 ms — the driver round-trip IS the workload. Past 4, extra
        // contexts buy nothing and cost linear pool-init plus tail contention (p95 33 ms → 1289 ms,
        // ~40×): six consecutive worsening levels, +77% at pool 32. Verdict-safe at every pool size
        // swept, 1–32 (§7.6).
        //
        // NOW ALSO FITTED ON A REAL GITHUB RUNNER (§10.3) — the third hardware class, and the one CI
        // pays for. 6 paired blocks (every level on one runner, so GitHub's random CPU assignment
        // cancels): pools 2, 4, 6 and 8 are ALL WITHIN NOISE of each other (4 is the reference; 2 is
        // −1.5% ±8.4%, 8 is −2.2% ±8.4%), and then it rises hard — 12 is +19.9% and 16 is +23.6%,
        // both distinguishable at 95%. 4 sits mid-plateau, two levels clear of the rise. The runner
        // agrees with the dev box and the container. Do NOT chase the ~1–3% inside the plateau: a raw
        // read of this same data made pool 2 look like a 3.4% winner (it "beat" 4 in 6 of 6 blocks),
        // and that was an artefact of run-order, not a real effect (§10.3).
        //
        // MemPerContextBytes MEASURED: 225,863,270 B (215.4 MiB) — the least-squares SLOPE across the
        // pool sweep on the 4-CPU Linux container (R²=0.99); the 32-core Windows box measured 213.4
        // MiB, i.e. this constant reproduces across OS and hardware to within 1%, unlike k. Take the
        // larger. Each context adds exactly one Chromium renderer process. Note this is ~5.8× SMALLER
        // than MemPerInstanceBytes above — a context is not a process family, and charging one at the
        // other's rate is precisely the mistake that motivated separating the axes.
        //
        // MemPoolBaselineBytes MEASURED: 1,109,393,408 B (1058 MiB) — the INTERCEPT of that same
        // least-squares fit (§7.7), i.e. what the pool costs at zero contexts: one shared Chromium, one
        // Playwright Node driver, one test host. Slope AND intercept come from the SAME 4-CPU Linux
        // regression, deliberately — mixing the slope of one fit with the intercept of another is not a
        // line. The intercept used to be charged nowhere, which made the pool's memory bound a marginal
        // slope consumed as a total charge (see ConcurrencyPolicy).
        //
        // ---- ENDPOINT (the axis neither profile above can see) ----
        //
        // ROSTER: NR_ENGINE_URL. Set ⇒ HostEngineFactory.CreateRosterEngineAsync drives that URL live;
        // unset ⇒ it replays .testdata/newrecruit-har/newrecruit.har off local disk. SAME ENGINE, SAME
        // PROFILE, SAME MEASURED NUMBERS — and one of the two is a third party's production website. That
        // is the whole fact the CLI was missing: `bs-spec run --all --engine newrecruit` with the variable
        // set spawned ceil(32 × 0.375) = 12 headless browsers at newrecruit.eu, because the parent
        // computing the plan never asked which of the two it was. Everything else in this file is a
        // throughput number fitted against the HAR file; NONE of it may size the live case.
        //
        // GAMEDATA: this machine, unconditionally. CreateGameDataEngineAsync does not read NR_ENGINE_URL
        // at all — the NR gamedata engine is always a frozen static dir (.testdata/nr-editor). Declared
        // per-domain precisely so a gamedata run keeps its full measured worker count in a shell that has
        // NR_ENGINE_URL exported for live roster work. Pinned by
        // ConcurrencyConfigurationDriftTests.HostEngineFactory_LiveEndpointRoutes_AreDeclaredByTheRegistry.
        ["newrecruit"] = new(
            "newrecruit", null, null, BothDomains,
            new EngineProfile(
                MaxParallel: 0, ColdStartCost.Cheap, ReuseSafeRoster: false, ReuseSafeGameData: false,
                MemPerInstanceBytes: 1_313_420_083L, OversubscriptionFactor: 0.375,
                ContextPoolSize: 4, MemPerContextBytes: 225_863_270L,
                MemPoolBaselineBytes: 1_109_393_408L),
            Builtin: true,
            RosterEndpoint: EngineEndpoint.FromUrlVariable("NR_ENGINE_URL"),
            GameDataEndpoint: EngineEndpoint.OnThisMachine),

        // MemPerInstanceBytes MEASURED: 1,548,969,984 B (≈1.44 GiB) per worker — bs-engine-host
        // adapter (≈520 MiB) + the Playwright Node driver (≈432 MiB) + the whole
        // chrome-headless-shell tree (≈526 MiB). A worker cannot exist without all three, so all
        // three are counted (docs/concurrency-policy-measurements.md §3).
        //
        // OversubscriptionFactor: 1.0 is now MEASURED, not assumed. The knee is at P=32 on 32
        // logical processors (32 ÷ 32 = 1.0): P=48 is *slower* than P=32 in both independent arms
        // (+19% / +22% wall) and its p50 explodes to 2.6× serial, while P=32 beats P=24 in both
        // arms. It was previously 1.0 only because that is the record's default — right by luck.
        // Caveat that must travel with it: this k was fitted on a 32-core box. The 4-vCPU CI runner
        // is NOT measured, and the design doc records the two hardware classes disagreeing.
        //
        // ---- CONTEXT AXIS (the xUnit fixture pool — `nr-editor-ui-frozen`) ----
        //
        // ContextPoolSize: 16 is MEASURED — on BOTH hardware classes, and it is the SAME 16 (§7.3).
        // Read that again before you reach for cpuCount: 16 contexts is optimal on a 32-core box and
        // on a 4-CPU container alike. The decisive evidence is at pool=1, same 112 specs: 240.05 s on
        // 32 CPUs vs 241.17 s on 4 CPUs — an 8× CPU cut costs 0.5%. This workload is latency-bound
        // (p50 ≈1.34 s/spec, flat even at 2× oversubscription on 4 CPUs), so oversubscription pays
        // right up to 16 and then stops: bracketed four levels past on both boxes (+28% at 64 on dev,
        // +29% at 48 on the container). Verdict-safe at every pool size swept, 1–64 (§7.6).
        //
        // TWO THINGS THIS 16 REPLACES, BOTH TOO LOW. The policy's mirrored PoolSize gave this lane 4
        // on CI (2.0× slower than optimal), and the hand-set NR_PARALLEL: 6 before it gave 6 (still
        // 50% off). This lane has never once been run near its optimum.
        //
        // 16 IS NOW ALSO FITTED ON A REAL GITHUB RUNNER (§10.2) — and this is the constant somebody was
        // about to LOWER. §8.8 saw the runner deliver only −6% where §7 predicted −33%, reasoned that
        // per-context init must be dearer there, and wrote that "the true runner optimum is plausibly
        // 8–12". IT IS NOT. Swept on the runner in 6 paired blocks with a Latin-square run order, so
        // that neither GitHub's random CPU assignment nor page-cache warmth can leak into the ranking:
        //
        //     pool  8 : +5.6% SLOWER than 16   [95% CI +3.1, +8.2]   loses 5 of 6 blocks
        //     pool 12 : +3.8% SLOWER than 16   [95% CI +1.2, +6.4]   loses 6 of 6 blocks
        //     pool 16 / 20 / 24 : STATISTICALLY TIED (every CI spans zero) — a flat plateau
        //
        // Nothing at or below 12 beats 16. 20 is nominally 0.8% faster and is deliberately NOT crowned:
        // that is inside the noise, and 16 is already the measured optimum on the dev box and on the
        // container. The mechanism §8.8 guessed at is REAL but far too small — per-context init is
        // 0.32 s and serial, so 4 → 16 costs +4 s of init and buys −24% of execution (§10.4). CI saw
        // −6% instead of −33% because THE RUNNER IS ~2.7× SLOWER than the container that modelled it
        // (92.4 s vs 34.2 s at pool 16), which makes this workload more CPU-bound and oversubscription
        // worth less; the genuine 6 → 16 gain there is ~10%. The model box got the optimum right and
        // the speed wrong — and this constant encodes the optimum.
        //
        // MemPerContextBytes MEASURED: 235,824,742 B (224.9 MiB) — least-squares SLOPE, 4-CPU Linux
        // container (R²=0.98); the 32-core Windows box measured 162.6 MiB. Take the LARGER, i.e. the
        // CI-class figure: it is the conservative one, and CI is the machine that has to survive it.
        //
        // MemPoolBaselineBytes MEASURED: 1,373,634,560 B (1310 MiB) — the INTERCEPT of that same 4-CPU
        // Linux fit (§7.7): the shared Chromium + Node driver + test host, which exist at pool 0. Same
        // regression as the slope above, on purpose.
        //
        // AND THE MEMORY HEADROOM HERE IS NOT WHAT THIS COMMENT USED TO SAY. It said "at pool 16 the
        // whole container peaked at 6.16 GiB of 16 GiB — memory does not bind at the optimum". The 16 GiB
        // is the CONTAINER the sweep ran in. THE REAL CI RUNNER IS 2 vCPU / 7.8 GiB (measured: `nproc` 2,
        // `MemTotal` 7.8 GiB — §11.6), so that same 6.16 GiB peak is 79% of the machine CI actually runs
        // on, not 39% of a machine it does not have. Memory still does not BIND at 16 there — the model
        // now charges 1310 MiB + 16 × 224.9 MiB = 4.79 GiB against 0.8 × 7.8 GiB = 6.24 GiB — but the
        // margin is a fraction of what "of 16 GiB" implies, and the pool is memory-bounded at 22 on that
        // box. Contention still binds before memory; it no longer binds by a mile.
        //
        // ENDPOINT: same shape as `newrecruit`, and the sharper case for the process axis — k = 1.0, so a
        // live `bs-spec run --all --engine newrecruit-ui` on this 32-core box planned a FULL 32 browsers
        // against newrecruit.eu. Roster: NR_ENGINE_URL (NrRosterUiEngine.CreateAsync vs CreateFrozenAsync).
        // Gamedata: always the frozen .testdata/nr-editor static dir.
        ["newrecruit-ui"] = new(
            "newrecruit-ui", null, null, BothDomains,
            new EngineProfile(
                MaxParallel: 0, ColdStartCost.Cheap, ReuseSafeRoster: false, ReuseSafeGameData: false,
                MemPerInstanceBytes: 1_548_969_984L, OversubscriptionFactor: 1.0,
                ContextPoolSize: 16, MemPerContextBytes: 235_824_742L,
                MemPoolBaselineBytes: 1_373_634_560L),
            Builtin: true,
            RosterEndpoint: EngineEndpoint.FromUrlVariable("NR_ENGINE_URL"),
            GameDataEndpoint: EngineEndpoint.OnThisMachine),
    };

    private readonly Dictionary<string, EngineEntry> _configured;

    private EngineRegistry(Dictionary<string, EngineEntry> configured) => _configured = configured;

    public IReadOnlyCollection<string> KnownNames =>
        [.. _configured.Keys.Union(Builtins.Keys).Order()];

    /// <summary>Load from an explicit engines.json path; null → built-ins only.</summary>
    public static EngineRegistry Load(string? configPath)
    {
        if (configPath is null)
        {
            return new EngineRegistry([]);
        }

        var config = JsonSerializer.Deserialize(
            File.ReadAllText(configPath), EnginesConfigJsonContext.Default.EnginesConfig)
            ?? throw new InvalidDataException($"Invalid engines config: {configPath}");

        var configured = new Dictionary<string, EngineEntry>();
        foreach (var (name, entry) in config.Engines)
        {
            EngineConnectable? launch = null;
            if (entry.Exec is { Length: > 0 })
            {
                try
                {
                    launch = EngineConnectable.Parse($"exec:{entry.Exec}");
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException(
                        $"Invalid engines config '{configPath}', entry '{name}': {ex.Message}", ex);
                }
            }
            Validate(configPath, name, entry);

            // AN ENTRY WITH NO `exec` CANNOT REPLACE A BUILT-IN, SO IT MAY NOT SHADOW ONE. A built-in's
            // launch is bs-engine-host (EngineHostLocator); a config entry carries only an `exec`. So
            // `{"battlescribe": {"endpoint": "local"}}` — the shape somebody reaches for when they want to
            // ANNOTATE a built-in — replaced the built-in with an entry that has Executable = null and
            // Builtin = false, and `bs-spec run --engine battlescribe` (the primary documented usage) died
            // with "Engine 'battlescribe' has no executable configured". Reproduced; that is why this
            // throws instead. A built-in's declarations are MEASURED and live in code, next to the
            // measurements; they are not overridable from a config file, and an entry that silently half-
            // became one is the failure mode the rest of Validate exists to close.
            if (launch is null && Builtins.ContainsKey(name))
            {
                throw new InvalidDataException(
                    $"Invalid engines config '{configPath}', entry '{name}': '{name}' is a BUILT-IN engine " +
                    $"and this entry declares no \"exec\", so it has nothing to launch — it would replace " +
                    $"the built-in with an engine that has no executable, and `--engine {name}` would then " +
                    $"fail with \"no executable configured\". Give the entry an \"exec\" to genuinely " +
                    $"replace the built-in under that name, or pick a different name. A built-in's profile " +
                    $"and endpoint are measured and declared in code (EngineRegistry.Builtins); they are " +
                    $"not overridable from config. To declare the endpoint of an ad-hoc adapter you launch " +
                    $"under a built-in's name (`--engine \"{name}=exec:…\"`), pass --engine-endpoint.");
            }

            // A third-party engine that omits memPerInstanceBytes gets 0 — i.e. "undeclared" — and is
            // therefore bound by ConcurrencyPolicy.UndeclaredMemoryWorkerCap rather than the machine's
            // full width. Declaring a measured footprint is how an engine opts into full parallelism;
            // this is the safe default for engines we did not write and cannot measure, not an oversight.
            // The endpoint declaration is the same bargain as memPerInstanceBytes, on the axis that costs
            // a THIRD PARTY rather than this box: an engine that does not say where its service lives is
            // treated as driving someone else's live site and held to
            // ConcurrencyPolicy.ThirdPartyLiveLoadLimit. `"endpoint": "local"` is how an adapter author
            // states the fact and takes the machine's full width. Omitting it costs wall-clock; guessing
            // "local" on their behalf would cost a stranger's bandwidth.
            var endpoint = ParseEndpoint(configPath, name, entry.Endpoint);

            configured[name] = new EngineEntry(
                name,
                launch?.Executable,
                launch?.Arguments,
                entry.Domains is { Count: > 0 } ? [.. entry.Domains] : BothDomains,
                new EngineProfile(
                    entry.MaxParallel,
                    entry.ColdStartCost,
                    entry.ReuseSafeRoster,
                    entry.ReuseSafeGameData,
                    entry.MemPerInstanceBytes,
                    entry.OversubscriptionFactor,
                    entry.ContextPoolSize,
                    entry.MemPerContextBytes,
                    entry.MaxContexts,
                    entry.MemPoolBaselineBytes),
                Builtin: false,
                RosterEndpoint: endpoint,
                GameDataEndpoint: endpoint);
        }

        return new EngineRegistry(configured);
    }

    /// <summary>
    /// Parse an <c>engines.json</c> <c>"endpoint"</c> declaration: <c>"local"</c> (this machine — takes
    /// the machine's full measured width), <c>"third-party-live"</c> (someone else's production service —
    /// held to <see cref="ConcurrencyPolicy.ThirdPartyLiveLoadLimit"/>), or a URL-variable form
    /// <c>"url-var:NAME"</c> (live iff <c>NAME</c> names a non-loopback URL, exactly like the built-in
    /// NewRecruit engines' <c>NR_ENGINE_URL</c>).
    /// </summary>
    /// <remarks>
    /// <b>Absent is not "local".</b> An omitted declaration yields <see cref="EngineEndpoint.Undeclared"/>
    /// ⇒ <see cref="LoadTarget.ThirdPartyLive"/>. We did not write this adapter and cannot see what it
    /// drives; the harness will not spend a stranger's capacity on an assumption. An unrecognized value
    /// is rejected outright rather than being quietly read as "undeclared" — a config that says something
    /// the loader silently ignores is the failure mode the rest of <see cref="Validate"/> exists to close.
    /// </remarks>
    private static EngineEndpoint? ParseEndpoint(string configPath, string name, string? declared)
    {
        if (declared is null or "")
        {
            return null;
        }

        // The grammar lives in EngineEndpoint.Parse — one implementation, shared with the CLI's
        // --engine-endpoint, so the two channels a human can declare an endpoint through cannot come to
        // mean different things. Only the file/entry context is added here.
        try
        {
            return EngineEndpoint.Parse(declared);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException(
                $"Invalid engines config '{configPath}', entry '{name}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Reject the numbers a third-party <c>engines.json</c> can state that the policy cannot safely
    /// interpret. These are hostile-to-the-machine values, not typos to be silently normalized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A negative <c>memPerInstanceBytes</c> used to unbind the safety cap.</b>
    /// <c>ConcurrencyPolicy</c>'s memory bound is gated on <c>&gt; 0</c> and its undeclared-engine cap
    /// was gated on <c>== 0</c>, so <c>-1</c> escaped BOTH: no memory bound, no cap, and a 64-core box
    /// would launch 64 instances of an engine nobody measured — reachable with one minus sign. The
    /// policy's gate is now <c>&lt;= 0</c> (so such a profile is merely treated as undeclared), but a
    /// negative memory footprint is meaningless and almost certainly a mistake in the author's file:
    /// it is better to say so at load than to quietly pick an interpretation for them.
    /// </para>
    /// <para>
    /// <c>oversubscriptionFactor</c> must be positive for the same reason — <c>ceil(cpu × k)</c> with
    /// <c>k &lt;= 0</c> is a worker count of zero, silently floored back to 1 — and <c>maxParallel</c>
    /// is a count, where 0 already has the meaning "unlimited" and a negative is nonsense.
    /// </para>
    /// <para>
    /// The context-axis pair (<c>contextPoolSize</c>, <c>memPerContextBytes</c>) is validated the same
    /// way and for the same reason: both are gated on <c>&gt; 0</c> in the policy, so a negative would
    /// fall through to the undeclared default while looking, in the author's file, like a declaration.
    /// A config that says one thing and means another is the failure mode being closed here, on both
    /// axes. (Neither is a floating-point value, so unlike <c>oversubscriptionFactor</c> there is no
    /// NaN to reject.)
    /// </para>
    /// </remarks>
    private static void Validate(string configPath, string name, EngineConfigEntry entry)
    {
        if (entry.MemPerInstanceBytes < 0)
        {
            throw new InvalidDataException(
                $"Invalid engines config '{configPath}', entry '{name}': memPerInstanceBytes must be >= 0 " +
                $"(got {entry.MemPerInstanceBytes}). Omit it (or use 0) to declare the footprint unknown, " +
                $"which caps the engine at ConcurrencyPolicy's conservative default; state a measured " +
                $"byte count to opt into full machine-width parallelism.");
        }

        if (entry.OversubscriptionFactor <= 0 || double.IsNaN(entry.OversubscriptionFactor))
        {
            throw new InvalidDataException(
                $"Invalid engines config '{configPath}', entry '{name}': oversubscriptionFactor must be > 0 " +
                $"(got {entry.OversubscriptionFactor.ToString(CultureInfo.InvariantCulture)}). It is the 'k' in " +
                $"workers ≈ cpuCount × k; 1.0 means one instance per logical processor. It sizes worker " +
                $"PROCESSES only — the browser-context pool is contextPoolSize, an absolute count.");
        }

        if (entry.MaxParallel < 0)
        {
            throw new InvalidDataException(
                $"Invalid engines config '{configPath}', entry '{name}': maxParallel must be >= 0 " +
                $"(got {entry.MaxParallel}). 0 means unlimited. It is a ceiling on concurrent adapter " +
                $"PROCESSES — the same number your describe handshake advertises as capabilities.maxParallel " +
                $"— and it does not bound the harness's in-process browser-context pool; that is maxContexts.");
        }

        if (entry.MaxContexts < 0)
        {
            throw new InvalidDataException(
                $"Invalid engines config '{configPath}', entry '{name}': maxContexts must be >= 0 " +
                $"(got {entry.MaxContexts}). 0 means unlimited. It is a ceiling on concurrent browser " +
                $"CONTEXTS in one in-process pool — declare it only if your engine physically cannot hold " +
                $"more than N at once. It is not maxParallel: that one bounds processes.");
        }

        if (entry.ContextPoolSize < 0)
        {
            throw new InvalidDataException(
                $"Invalid engines config '{configPath}', entry '{name}': contextPoolSize must be >= 0 " +
                $"(got {entry.ContextPoolSize}). It is an ABSOLUTE measured pool size — the number of " +
                $"browser contexts one in-process pool should hold — not a factor of cpuCount. Omit it " +
                $"(or use 0) to declare it unknown and take ConcurrencyPolicy's conservative default.");
        }

        if (entry.MemPerContextBytes < 0)
        {
            throw new InvalidDataException(
                $"Invalid engines config '{configPath}', entry '{name}': memPerContextBytes must be >= 0 " +
                $"(got {entry.MemPerContextBytes}). It is the memory cost of ONE ADDITIONAL browser context " +
                $"(~225 MiB for the built-in browser engines) — the SLOPE of a pool sweep, not the cost of a " +
                $"whole adapter process (that is memPerInstanceBytes, roughly 6x larger) and not the cost of " +
                $"the pool (that needs memPoolBaselineBytes too). Omit it (or use 0) to declare it unknown; " +
                $"the pool then gets no memory bound, which is safe only because the undeclared pool size is " +
                $"small.");
        }

        if (entry.MemPoolBaselineBytes < 0)
        {
            throw new InvalidDataException(
                $"Invalid engines config '{configPath}', entry '{name}': memPoolBaselineBytes must be >= 0 " +
                $"(got {entry.MemPoolBaselineBytes}). It is the pool's FIXED cost — the shared browser, the " +
                $"Playwright/Node driver and the test host, which exist before the first context does — i.e. " +
                $"the INTERCEPT of the same regression whose slope is memPerContextBytes.");
        }

        // A SLOPE WITHOUT AN INTERCEPT IS NOT A MEMORY MODEL. memPerContextBytes is the MARGINAL cost of
        // one more context; charging N x slope against the machine's memory and calling that the pool's
        // cost is a marginal slope consumed as a total charge, and it under-counts by the entire fixed
        // baseline (1.0-1.6 GiB of shared browser + driver + test host — 17-21% of a 7.8 GiB CI runner).
        // Neither half is optional and neither may be guessed, so a config that declares one and not the
        // other is rejected rather than being quietly completed with a zero we invented.
        if ((entry.MemPerContextBytes > 0) != (entry.MemPoolBaselineBytes > 0))
        {
            throw new InvalidDataException(
                $"Invalid engines config '{configPath}', entry '{name}': memPerContextBytes " +
                $"({entry.MemPerContextBytes}) and memPoolBaselineBytes ({entry.MemPoolBaselineBytes}) must " +
                $"be declared together or not at all. They are the SLOPE and the INTERCEPT of one measured " +
                $"regression — bytes per additional browser context, and the fixed cost of the shared " +
                $"browser + driver + test host that exists at pool 0. A slope alone under-charges the pool " +
                $"by the whole baseline (~1.0-1.6 GiB for the built-in browser engines), which is exactly " +
                $"the memory bound this pair exists to make honest; an intercept alone is never read, " +
                $"because the bound is gated on the slope. Measure both (sweep the pool and fit a line — " +
                $"docs/concurrency-policy-measurements.md §7.7), or declare neither and take " +
                $"ConcurrencyPolicy.UndeclaredContextPoolSize.");
        }
    }

    /// <summary>Walk up from <paramref name="startDirectory"/> looking for engines.json.</summary>
    public static EngineRegistry LoadDefault(string? startDirectory = null)
    {
        var dir = new DirectoryInfo(startDirectory ?? Directory.GetCurrentDirectory());
        for (; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "engines.json");
            if (File.Exists(candidate))
            {
                return Load(candidate);
            }
        }

        return new EngineRegistry([]);
    }

    /// <summary>Resolve a parsed connectable to a full entry (see class doc for rules).</summary>
    public EngineEntry Resolve(EngineConnectable connectable)
    {
        if (connectable.IsLaunchable)
        {
            // A LAUNCHABLE IS A FOREIGN BINARY, AND ITS NAME IS NOT EVIDENCE ABOUT IT.
            //
            // To the user and to RunBatch (`EngineFilter = selection.EngineName`), the name is an
            // APPLICABILITY LABEL: it selects which specs apply and which assertion set is used. That is
            // precisely why a third party writing their own NewRecruit adapter has no choice but to call
            // it `newrecruit` — the documented `<name>=<connectable>` usage. It says what the adapter is
            // FOR. It says nothing whatever about what the binary is.
            //
            // SO IT MAY NOT INHERIT A BUILT-IN'S DECLARATIONS. That fallback existed here briefly (it was
            // meant to fix the CI case below) and it FAILED OPEN in exactly the shape this branch exists
            // to close: `--engine "newrecruit=exec:./anything"` inherited built-in `newrecruit`'s measured
            // EngineProfile (k = 0.375, MemPerInstanceBytes = 1.22 GiB) AND its
            // `url-var:NR_ENGINE_URL` endpoint. With that variable unset — the default state of every
            // shell that is not running the live lane — ServedByThisMachine(null) is TRUE, so an unknown
            // executable resolved to LoadTarget.Local at ceil(32 × 0.375) = 12 workers on this box:
            // through BOTH the undeclared-endpoint fail-safe AND UndeclaredMemoryWorkerCap, because the
            // declared footprint it borrowed retires that cap. A profile is MEASURED. Nothing about an
            // arbitrary executable has been measured because of what somebody called it.
            //
            // An ad-hoc adapter therefore declares NOTHING unless somebody declares it: endpoints stay
            // null ⇒ EngineEndpoint.Undeclared ⇒ LoadTarget.ThirdPartyLive, and DefaultProfile ⇒
            // UndeclaredMemoryWorkerCap. Both fail-safes hold, together, for every binary we did not
            // write. The alternative is assuming, of an executable we have never seen, that nobody else
            // pays for its traffic.
            //
            // DECLARATION, NEVER INHERITANCE. The two channels that state the fact and take the machine's
            // full width back, both explicit and both written by a human who knows what the binary is:
            //   * `--engine-endpoint local` on the command line — for an ad-hoc adapter, which is what CI
            //     runs (`--engine "battlescribe=dotnet:…/bs-reference-adapter.dll" --engine-endpoint
            //     local`: an in-process IKVM engine with no network code, which the fail-safe used to
            //     announce as "third-party live service — held to 2 concurrent sessions" on every push);
            //   * an `engines.json` entry for the name (docs/adapter-guide.md). That IS name-keyed — but
            //     it is a file the operator wrote about their own adapters, not a table we shipped about
            //     ours, and it is read below and here alike.
            var declared = connectable.Name is { } claimed ? _configured.GetValueOrDefault(claimed) : null;

            return new EngineEntry(
                connectable.Name,
                connectable.Executable,
                connectable.Arguments,
                declared?.Domains ?? BothDomains,
                declared?.Profile ?? DefaultProfile,
                Builtin: false,
                RosterEndpoint: declared?.RosterEndpoint,
                GameDataEndpoint: declared?.GameDataEndpoint);
        }

        var name = connectable.Name!;
        if (_configured.TryGetValue(name, out var configured))
        {
            return configured;
        }

        if (Builtins.TryGetValue(name, out var builtin))
        {
            return builtin;
        }

        throw new KeyNotFoundException(
            $"Unknown engine '{name}'. Known engines: {string.Join(", ", KnownNames)}.");
    }
}
