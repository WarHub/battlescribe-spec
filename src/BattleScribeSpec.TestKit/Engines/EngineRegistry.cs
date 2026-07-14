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
public sealed record EngineEntry(
    string? Name,
    string? Executable,
    string? Arguments,
    IReadOnlyList<string> Domains,
    EngineProfile Profile,
    bool Builtin);

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
        ["battlescribe"] = new(
            "battlescribe", null, null, BothDomains,
            new EngineProfile(MaxParallel: 0, ColdStartCost.Cheap, ReuseSafeRoster: false, ReuseSafeGameData: false),
            Builtin: true),

        // MemPerInstanceBytes MEASURED: 1,055,391,744 B (≈0.98 GiB) — one JVM (app + -javaagent in
        // the same process) plus its bs-engine-host adapter, peak working set over a full 54-spec
        // gamedata run (docs/concurrency-policy-measurements.md §4). MaxParallel: 1 means the memory
        // bound can never actually bind here; the number exists so the policy can *prove* that
        // rather than assume it. OversubscriptionFactor is therefore moot and stays at its default.
        //
        // CONTEXT AXIS: undeclared, and moot for the same reason. This engine has no browser-context
        // pool at all (it drives one JavaFX desktop app), and MaxParallel: 1 clamps PoolSize to 1 on
        // every machine regardless of what the undeclared default would otherwise give. Pinned by
        // Policy_BattlescribeUi_StaysAtOneWorker_OnEveryProfile across four machine profiles.
        ["battlescribe-ui"] = new(
            "battlescribe-ui", null, null, BothDomains,
            new EngineProfile(
                MaxParallel: 1, ColdStartCost.Expensive, ReuseSafeRoster: true, ReuseSafeGameData: true,
                MemPerInstanceBytes: 1_055_391_744L),
            Builtin: true),

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
        // MemPerContextBytes MEASURED: 225,863,270 B (215.4 MiB) — the least-squares slope across the
        // pool sweep on the 4-CPU Linux container (R²=0.99); the 32-core Windows box measured 213.4
        // MiB, i.e. this constant reproduces across OS and hardware to within 1%, unlike k. Take the
        // larger. Each context adds exactly one Chromium renderer process. Note this is ~5.8× SMALLER
        // than MemPerInstanceBytes above — a context is not a process family, and charging one at the
        // other's rate is precisely the mistake that motivated separating the axes.
        ["newrecruit"] = new(
            "newrecruit", null, null, BothDomains,
            new EngineProfile(
                MaxParallel: 0, ColdStartCost.Cheap, ReuseSafeRoster: false, ReuseSafeGameData: false,
                MemPerInstanceBytes: 1_313_420_083L, OversubscriptionFactor: 0.375,
                ContextPoolSize: 4, MemPerContextBytes: 225_863_270L),
            Builtin: true),

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
        // MemPerContextBytes MEASURED: 235,824,742 B (224.9 MiB) — least-squares slope, 4-CPU Linux
        // container (R²=0.98); the 32-core Windows box measured 162.6 MiB. Take the LARGER, i.e. the
        // CI-class figure: it is the conservative one, and CI is the machine that has to survive it.
        // At pool 16 the whole container peaked at 6.16 GiB of 16 GiB — memory does not bind at the
        // optimum on this axis; contention does. That is the exact opposite of the process axis.
        ["newrecruit-ui"] = new(
            "newrecruit-ui", null, null, BothDomains,
            new EngineProfile(
                MaxParallel: 0, ColdStartCost.Cheap, ReuseSafeRoster: false, ReuseSafeGameData: false,
                MemPerInstanceBytes: 1_548_969_984L, OversubscriptionFactor: 1.0,
                ContextPoolSize: 16, MemPerContextBytes: 235_824_742L),
            Builtin: true),
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

            // A third-party engine that omits memPerInstanceBytes gets 0 — i.e. "undeclared" — and is
            // therefore bound by ConcurrencyPolicy.UndeclaredMemoryWorkerCap rather than the machine's
            // full width. Declaring a measured footprint is how an engine opts into full parallelism;
            // this is the safe default for engines we did not write and cannot measure, not an oversight.
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
                    entry.MemPerContextBytes),
                Builtin: false);
        }

        return new EngineRegistry(configured);
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
                $"(got {entry.MaxParallel}). 0 means unlimited.");
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
                $"(got {entry.MemPerContextBytes}). It is the memory cost of ONE browser context (~225 MiB " +
                $"for the built-in browser engines), not of a whole adapter process — that is " +
                $"memPerInstanceBytes, and it is roughly 6x larger. Omit it (or use 0) to declare it " +
                $"unknown; the pool then gets no memory bound, which is safe only because the undeclared " +
                $"pool size is small.");
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
            // Ad-hoc launch; merge metadata when the identity is a configured name.
            var metadata = connectable.Name is not null && _configured.TryGetValue(connectable.Name, out var known)
                ? known
                : null;
            return new EngineEntry(
                connectable.Name,
                connectable.Executable,
                connectable.Arguments,
                metadata?.Domains ?? BothDomains,
                metadata?.Profile ?? DefaultProfile,
                Builtin: false);
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
