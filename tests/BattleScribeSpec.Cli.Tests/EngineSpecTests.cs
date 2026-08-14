using System.CommandLine;
using System.Diagnostics;
using BattleScribeSpec.Concurrency;
using BattleScribeSpec.Engines;

namespace BattleScribeSpec.Cli.Tests;

/// <summary>
/// Tests the string-based engine surface: <see cref="EngineOptions.Resolve"/> parses
/// <c>--engine</c> as a name/connectable/engines.json entry via <see cref="EngineConnectable"/>
/// and <see cref="EngineRegistry"/>, the <c>--ui</c> sugar, the UI→non-UI assertion mapping,
/// and how the domain is derived (explicit override vs. spec-path inference).
/// </summary>
[Trait("Category", "Unit")]
public sealed class EngineSpecTests
{
    [Theory]
    [InlineData("battlescribe")]
    [InlineData("battlescribe-ui")]
    [InlineData("newrecruit")]
    [InlineData("newrecruit-ui")]
    public void Resolve_ResolvesEachBuiltinName(string name)
    {
        var selection = Resolve("plain-spec-id", "--engine", name);
        Assert.Equal(name, selection.EngineName);
    }

    [Fact]
    public void Resolve_UiSugar_AppendsUiToAPlainName()
    {
        var selection = Resolve("plain-spec-id", "--engine", "newrecruit", "--ui");
        Assert.Equal("newrecruit-ui", selection.EngineName);
    }

    [Fact]
    public void Resolve_UiSugar_IsIdempotentOnAnAlreadyUiName()
    {
        var selection = Resolve("plain-spec-id", "--engine", "newrecruit-ui", "--ui");
        Assert.Equal("newrecruit-ui", selection.EngineName);
    }

    [Fact]
    public void Resolve_NameEqualsDotnetLaunch_CarriesIdentityAndLaunch()
    {
        var selection = Resolve("plain-spec-id", "--engine", "wham=dotnet:adapter.dll");

        Assert.Equal("wham", selection.EngineName);
        Assert.Equal("dotnet", selection.Entry.Executable);
        Assert.Equal("adapter.dll", selection.Entry.Arguments);
        Assert.False(selection.Entry.Builtin);
    }

    [Fact]
    public void Resolve_UnknownEngineName_ThrowsCliInputException()
    {
        Assert.Throws<CliInputException>(() => Resolve("plain-spec-id", "--engine", "warscroll"));
    }

    [Fact]
    public void Resolve_UiCombinedWithExecConnectable_ThrowsCliInputException()
    {
        Assert.Throws<CliInputException>(() => Resolve("plain-spec-id", "--engine", "exec:node adapter.js", "--ui"));
    }

    [Theory]
    [InlineData("battlescribe", "battlescribe")]
    [InlineData("battlescribe-ui", "battlescribe")]
    [InlineData("newrecruit", "newrecruit")]
    [InlineData("newrecruit-ui", "newrecruit")]
    public void AssertionEngineName_DropsTheUiSuffix(string engineName, string expected)
    {
        var selection = Resolve("plain-spec-id", "--engine", engineName);
        Assert.Equal(expected, selection.AssertionEngineName);
    }

    [Theory]
    [InlineData(false, false, "roster/battlescribe")]
    [InlineData(true, true, "gamedata/battlescribe-ui")]
    public void Display_CombinesDomainAndEngineName(bool gamedata, bool ui, string expected)
    {
        string[] domainArgs = gamedata ? ["--gamedata"] : [];
        string[] uiArgs = ui ? ["--ui"] : [];
        string[] args = ["--engine", "battlescribe", .. domainArgs, .. uiArgs];

        var selection = Resolve("plain-spec-id", args);
        Assert.Equal(expected, selection.Display);
    }

    [Fact]
    public void Resolve_DefaultsToBattlescribeRoster()
    {
        var selection = Resolve("plain-spec-id");

        Assert.Equal("battlescribe", selection.EngineName);
        Assert.Equal(EngineDomain.Roster, selection.Domain);
    }

    [Fact]
    public void Resolve_InfersGamedataFromSpecPath()
    {
        var selection = Resolve("specs/gamedata/entry/add-entry-basic");
        Assert.Equal(EngineDomain.Gamedata, selection.Domain);
    }

    [Fact]
    public void Resolve_RosterFlagOverridesGamedataPath()
    {
        var selection = Resolve("specs/gamedata/entry/add-entry-basic", "--roster");
        Assert.Equal(EngineDomain.Roster, selection.Domain);
    }

    [Fact]
    public void Resolve_GamedataFlagForcesGamedata()
    {
        var selection = Resolve("plain-roster-spec", "--gamedata");
        Assert.Equal(EngineDomain.Gamedata, selection.Domain);
    }

    [Fact]
    public void Resolve_RejectsConflictingDomainFlags()
    {
        var options = new EngineOptions();
        var result = ParseWith(options, "spec", "--gamedata", "--roster");

        Assert.Throws<CliInputException>(() => options.Resolve(result, "spec"));
    }

    // ---- The parent decides; the child is told. ----
    // These pin the property that distinguishes "parent decides" from "both guess and hopefully
    // agree": the composed child command line must CARRY the decision. A child that computed its
    // own plan would still work on this machine and would still be wrong — it is a separate process
    // that may see a different machine (container CPU limits, cgroup quotas) and drift silently.

    [Theory]
    [InlineData("battlescribe-ui")]
    [InlineData("battlescribe")]
    [InlineData("newrecruit")]
    [InlineData("newrecruit-ui")]
    public void ResolveLaunch_AlwaysTellsTheChildThePolicy_EvenWithNoUserOverride(string engineName)
    {
        var selection = Resolve("plain-spec-id", "--engine", engineName);

        var launch = selection.ResolveLaunch();

        // Not "only when the user asked for an override" — EVERY spawn carries the plan.
        Assert.Contains("--policy ", launch.Arguments, StringComparison.Ordinal);

        // And it carries THIS plan's values, not a re-derivation the child could disagree with.
        var plan = selection.EffectivePlan;
        Assert.Contains($"workers={plan.Workers}", launch.Arguments, StringComparison.Ordinal);
        Assert.Contains($"reuse-roster={(plan.ReuseRoster ? "on" : "off")}", launch.Arguments, StringComparison.Ordinal);
        Assert.Contains($"reuse-gamedata={(plan.ReuseGameData ? "on" : "off")}", launch.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectivePlan_ComesFromTheConcurrencyPolicy_ForThisMachineAndEngine()
    {
        var selection = Resolve("plain-spec-id", "--engine", "battlescribe-ui");

        var expected = ConcurrencyPolicy.For(MachineProfile.Current(), selection.Entry.Profile);

        Assert.Equal(expected, selection.EffectivePlan);

        // battlescribe-ui is the engine whose reuse was measured verdict-neutral AND faster in both
        // domains; the plan the child is told must actually say so, or the refactor turned reuse off.
        Assert.True(selection.EffectivePlan.ReuseRoster);
        Assert.True(selection.EffectivePlan.ReuseGameData);
    }

    // EffectivePlan_KeepAlive_ForcesReuseOn_AndIsFoldedIntoThePolicySentToTheChild lived here. It
    // constructed `EngineSelection with { KeepAlive = true }` DIRECTLY — a state no user could reach:
    // both production construction sites passed KeepAlive: false, `run --keep-alive` had been deleted
    // from the CLI, and the field's only branch was therefore dead. A test that manufactures the input
    // its subject cannot receive proves the branch compiles, not that anything uses it. The field is
    // gone (see EngineSelection); reuse is ConcurrencyPolicy's decision and arrives in the plan.

    [Fact]
    public void ResolveLaunch_LaunchableAdapter_GetsNoPolicy_AndIsNotGivenAFabricatedOne()
    {
        // exec:/dotnet: adapters have no --policy channel (#305 is the sibling gap for --headed).
        // They must not be handed a policy flag they'd choke on...
        var selection = Resolve("plain-spec-id", "--engine", "wham=dotnet:adapter.dll");

        var launch = selection.ResolveLaunch();

        Assert.DoesNotContain("--policy", launch.Arguments, StringComparison.Ordinal);
        Assert.Equal("adapter.dll", launch.Arguments);
    }

    [Fact]
    public void ResolveLaunch_LaunchableAdapter_WithAnExplicitOverride_ThrowsRatherThanSilentlyDropIt()
    {
        // ...but an override the USER explicitly asked for must never be silently ignored.
        var selection = Resolve("plain-spec-id", "--engine", "wham=dotnet:adapter.dll") with
        {
            PlanOverride = new ConcurrencyPlan(2, 2, ReuseRoster: true, ReuseGameData: true),
        };

        Assert.Throws<InvalidOperationException>(selection.ResolveLaunch);
    }

    // ===================================================================================================
    //  THE LOAD TARGET: whose machine pays for this run's traffic (#317).
    //
    //  `bs-spec run --all --engine newrecruit` resolves the SAME EngineEntry and the SAME EngineProfile
    //  whether the child will replay a HAR file off local disk or drive newrecruit.eu. Before this, the
    //  parent computing the plan never asked which — so it planned ceil(cpuCount × k) adapter processes,
    //  EACH WITH ITS OWN BROWSER, at a volunteer-run website: 12 on a 32-core box, up from the serial
    //  `--workers 1` default that preceded the policy. Nothing else in src/BattleScribeSpec.NewRecruit/
    //  bounds that load — no retry, no backoff, no throttle, no 429 handling. Concurrency IS the brake.
    // ===================================================================================================

    /// <summary>
    /// <b>The regression itself.</b> A CLI run resolved against a LIVE NewRecruit endpoint is held to
    /// <see cref="ConcurrencyPolicy.ThirdPartyLiveLoadLimit"/> workers — not the machine's measured width.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Falsifiable, and precisely against the defect being fixed:</b> delete the load-target derivation
    /// from <see cref="EngineSelection.EffectivePlan"/> (i.e. go back to
    /// <c>ConcurrencyPolicy.For(MachineProfile.Current(), Entry.Profile)</c>, which defaults to
    /// <see cref="LoadTarget.Local"/>) and the first assertion goes red with the real machine-width number
    /// — 12 on the 32-core dev box, 4 on a 4-vCPU runner. Remove the <c>RosterEndpoint</c> declaration
    /// from the registry instead and it stays green (undeclared fails safe), but
    /// <see cref="EffectivePlan_FrozenNewRecruit_KeepsTheFullMeasuredWorkerCount"/> goes red — the two
    /// tests pin the derivation from both sides, so it cannot be satisfied by throttling everything.
    /// </para>
    /// <para>
    /// The engine is <c>newrecruit-ui</c> because its measured <c>k</c> is 1.0: it is the sharper case
    /// (a full <c>cpuCount</c> browsers at the live site), and it keeps the last assertion meaningful on
    /// a 4-vCPU CI runner, where <c>newrecruit</c>'s own <c>ceil(4 × 0.375) = 2</c> would coincide with
    /// the limit and prove nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void EffectivePlan_LiveNewRecruit_IsHeldToTheThirdPartyLoadLimit_NotTheMachinesWidth()
    {
        var selection = Live(Resolve("plain-spec-id", "--engine", "newrecruit-ui"));

        Assert.Equal(LoadTarget.ThirdPartyLive, selection.LoadTarget);

        // The machine's own answer, HELD TO the limit — a ceiling, not a floor. (Asserting the bare
        // limit here encodes the box the test was written on; see LiveWorkersEntitlement.)
        Assert.Equal(LiveWorkersEntitlement(selection.Entry.Profile), selection.EffectivePlan.Workers);
        Assert.True(selection.EffectivePlan.Workers <= ConcurrencyPolicy.ThirdPartyLiveLoadLimit);

        // Both axes: the remote host feels requests in flight and cannot see whether we spawned them as
        // processes or as browser contexts.
        Assert.True(selection.EffectivePlan.PoolSize <= ConcurrencyPolicy.ThirdPartyLiveLoadLimit);

        // And it is STRICTLY below what this machine would otherwise have been given — wherever the two
        // numbers can differ at all. On a box already under the limit on its own (the 2-vCPU CI runner:
        // ceil(2 × 1.0) = 2) they coincide, and there is nothing here to prove; the machine-independent
        // form of this proof is ConcurrencyPolicyTests.Policy_LoadLimit_DoesNotScaleWithCpuCountOrMemory,
        // which uses synthetic MachineProfiles precisely so the box it runs on cannot silence it.
        var machineWidth = LocalWorkers(selection.Entry.Profile);
        if (machineWidth > ConcurrencyPolicy.ThirdPartyLiveLoadLimit)
        {
            Assert.True(
                selection.EffectivePlan.Workers < machineWidth,
                $"a live run got {selection.EffectivePlan.Workers} workers — the machine's own width is " +
                $"{machineWidth}, and nothing would be capped");
        }
    }

    /// <summary>
    /// <b>The other half: the 14.3× must survive.</b> A frozen NewRecruit run keeps the full measured
    /// worker count. The fix is a derivation, not a blanket throttle.
    /// </summary>
    /// <remarks>
    /// Falsifiable: default the CLI to <see cref="LoadTarget.ThirdPartyLive"/> "to be safe" — the lazy
    /// version of this fix, which would look identical in the live test above — and this goes red on any
    /// box whose measured worker count exceeds 2 (every machine this repo runs on, CI included).
    /// </remarks>
    [Fact]
    public void EffectivePlan_FrozenNewRecruit_KeepsTheFullMeasuredWorkerCount()
    {
        // NR_ENGINE_URL empty is exactly what HostEngineFactory reads as "replay the frozen HAR"
        // (`url is { Length: > 0 }`), and it makes this test independent of the ambient shell.
        var selection = Frozen(Resolve("plain-spec-id", "--engine", "newrecruit-ui"));

        Assert.Equal(LoadTarget.Local, selection.LoadTarget);

        var measured = ConcurrencyPolicy.For(MachineProfile.Current(), selection.Entry.Profile, LoadTarget.Local);
        Assert.Equal(measured, selection.EffectivePlan);

        if (Environment.ProcessorCount >= 3)
        {
            Assert.True(
                selection.EffectivePlan.Workers > ConcurrencyPolicy.ThirdPartyLiveLoadLimit,
                "a frozen run must not be throttled to the third-party load limit — it never leaves this box");
        }
    }

    /// <summary>
    /// The NR <b>gamedata</b> engine is always a frozen static dir — <c>HostEngineFactory</c>'s gamedata
    /// switch does not read <c>NR_ENGINE_URL</c> at all — so a gamedata run keeps its full width even in a
    /// shell that exports the variable for live roster work.
    /// </summary>
    /// <remarks>
    /// Falsifiable: declare the endpoint once per engine instead of once per domain (the obvious
    /// simplification) and this goes red — a developer with <c>NR_ENGINE_URL</c> exported would silently
    /// drop from the machine's width to 2 workers on a suite that never touches the network.
    /// </remarks>
    [Fact]
    public void EffectivePlan_GameDataDomain_IsLocal_EvenWithTheLiveUrlSet()
    {
        var selection = Live(Resolve("plain-spec-id", "--engine", "newrecruit", "--gamedata"));

        Assert.Equal(LoadTarget.Local, selection.LoadTarget);
        Assert.Equal(
            ConcurrencyPolicy.For(MachineProfile.Current(), selection.Entry.Profile, LoadTarget.Local),
            selection.EffectivePlan);
    }

    /// <summary>
    /// <b>The endpoint variable means whatever it means TO THE CHILD.</b> <c>--config-a
    /// "nr_engine_url=…"</c> and <c>--config-a "NR_ENGINE_URL=…"</c> are the <em>same</em> variable to a
    /// Windows child (<see cref="ProcessStartInfo.Environment"/> is case-insensitive there) and
    /// <em>different</em> variables to a Linux one. The parent's <see cref="EngineSelection.LoadTarget"/>
    /// must be whatever the child will actually do — on the platform it is actually running on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the gate that did not exist, and the defect it catches was live.</b> The parent used to
    /// look the variable up in its own <c>Dictionary&lt;string,string&gt;(StringComparer.Ordinal)</c>, so
    /// a single lowercased letter made it a <em>miss</em> ⇒ <c>LoadTarget.Local</c> ⇒
    /// <c>ceil(cpuCount × 1.0)</c> workers — <b>32 live browsers at newrecruit.eu on a 32-core box</b> —
    /// while the child, reading the OS's own case-insensitive environment, went live anyway. One name,
    /// two meanings, and the safety limit evaporated between them.
    /// </para>
    /// <para>
    /// <b>The expectation is taken from the OS, not from the code under test.</b>
    /// <see cref="ProcessStartInfo.Environment"/> IS the dictionary the child is handed, and it carries
    /// the platform's own name semantics. Deriving the expectation from it is what makes this test
    /// falsifiable <em>on both platforms</em> rather than encoding one platform's answer as a constant:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Windows</b> — revert to a case-sensitive lookup (the defect) and this goes red: the OS says the
    /// child gets the URL, the parent says <c>Local</c>.
    /// </description></item>
    /// <item><description>
    /// <b>Linux</b> — "fix" it by hard-coding <see cref="StringComparer.OrdinalIgnoreCase"/> (the obvious
    /// wrong fix, which trades one platform's bug for the other's) and this goes red: the OS says the
    /// child never sees <c>NR_ENGINE_URL</c> at all and replays the frozen HAR, while the parent throttles
    /// a local run to 2 workers.
    /// </description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public void EffectivePlan_EndpointConfigKey_IsReadWithTheChildProcessesOwnCasingRules()
    {
        const string MixedCaseKey = "nr_engine_url";
        const string LiveUrl = "https://www.newrecruit.eu";

        var selection = Resolve("plain-spec-id", "--engine", "newrecruit-ui") with
        {
            ChildEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MixedCaseKey] = LiveUrl,
            },
        };

        // Ground truth: exactly what AdapterProcess hands the child (psi.Environment[key] = value),
        // through the BCL type whose comparer is the OS's own — NOT through any comparer of ours.
        var psi = new ProcessStartInfo();
        psi.Environment[MixedCaseKey] = LiveUrl;
        var childGoesLive =
            psi.Environment.TryGetValue("NR_ENGINE_URL", out var seen) && !string.IsNullOrEmpty(seen);

        Assert.Equal(childGoesLive ? LoadTarget.ThirdPartyLive : LoadTarget.Local, selection.LoadTarget);

        // ...and the worker count follows it. A live child is held to the limit; a child that will replay
        // the frozen HAR keeps the machine's measured width. (The live figure is the machine's own answer
        // HELD TO the limit — a ceiling, not a floor. See LiveWorkersEntitlement.)
        var expectedWorkers = childGoesLive
            ? LiveWorkersEntitlement(selection.Entry.Profile)
            : LocalWorkers(selection.Entry.Profile);

        Assert.Equal(expectedWorkers, selection.EffectivePlan.Workers);
    }

    /// <summary>
    /// <b>The fail-safe.</b> An engine whose target cannot be established — any <c>exec:</c>/<c>dotnet:</c>
    /// adapter we did not write — gets <see cref="LoadTarget.ThirdPartyLive"/>, not
    /// <see cref="LoadTarget.Local"/>.
    /// </summary>
    /// <remarks>
    /// Falsifiable: give <c>EngineEntry</c>'s endpoint parameters a "sensible" default of
    /// <c>EngineEndpoint.OnThisMachine</c>, or make <c>EngineRegistry.Resolve</c>'s ad-hoc branch hand one
    /// out, and this goes red. Getting this wrong costs an unknown adapter some wall-clock; getting it
    /// wrong the other way spends a stranger's production capacity on an assumption. The adapter can
    /// state the fact and take the full width back with one line of engines.json:
    /// <c>"endpoint": "local"</c>.
    /// </remarks>
    [Fact]
    public void EffectivePlan_UnknownAdapter_FailsSafeToThirdPartyLive()
    {
        var selection = Resolve("plain-spec-id", "--engine", "exec:./some-third-party-adapter");

        // The fail-safe is the DERIVATION — an undeclared endpoint is treated as live. The worker count
        // that follows from it is the machine's own answer held to the limit (a ceiling, not a floor:
        // on a 2-vCPU runner an unmeasured engine is already under it).
        Assert.Equal(LoadTarget.ThirdPartyLive, selection.LoadTarget);
        Assert.Equal(LiveWorkersEntitlement(selection.Entry.Profile), selection.EffectivePlan.Workers);
        Assert.True(selection.EffectivePlan.Workers <= ConcurrencyPolicy.ThirdPartyLiveLoadLimit);
    }

    /// <summary>
    /// <b>A NAME IS AN APPLICABILITY LABEL, NOT A WARRANT.</b> A launchable adapter that claims a
    /// built-in's name inherits <em>nothing</em> from it — not its endpoint declaration, not its measured
    /// <see cref="EngineProfile"/>. It declares what it is, or it is treated as a stranger's live site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The regression this pins, reproduced.</b> <c>EngineRegistry.Resolve</c> briefly let a launchable
    /// claiming a built-in's name inherit that built-in's whole entry. <c>--engine
    /// "newrecruit=exec:./anything"</c> then inherited <c>newrecruit</c>'s <c>url-var:NR_ENGINE_URL</c>
    /// endpoint — and with that variable <b>unset</b> (the default state of every shell not running the
    /// live lane) <c>ServedByThisMachine(null)</c> is <b>true</b>, so it resolved to
    /// <see cref="LoadTarget.Local"/>. It also inherited <c>newrecruit</c>'s measured <c>k = 0.375</c> and
    /// its 1.22 GiB <c>MemPerInstanceBytes</c>, which <em>retires</em>
    /// <c>ConcurrencyPolicy.UndeclaredMemoryWorkerCap</c>. Both fail-safes, off, for a binary nobody has
    /// ever run: <c>ceil(32 × 0.375) = 12</c> concurrent processes on a 32-core box, at whatever that
    /// executable drives. A third party writing their own NewRecruit adapter <b>must</b> call it
    /// <c>newrecruit</c> — <c>RunBatch</c> selects specs by engine name — so this is the live case, not a
    /// contrived one.
    /// </para>
    /// <para>
    /// <b>Falsifiable, and it was verified red against <c>c45ff69</c>:</b> restore the <c>Builtins</c>
    /// fallback in <c>EngineRegistry.Resolve</c>'s launchable branch and the first two blocks go red
    /// (<c>ThirdPartyLive</c> → <c>Local</c>, 2 workers → 12 on this box). The previous gate tested this
    /// same connectable with <c>NR_ENGINE_URL</c> <em>set</em> — which is the half that stayed safe.
    /// </para>
    /// </remarks>
    [Fact]
    public void LaunchableAdapter_ClaimingABuiltinName_InheritsNothing_AndStillFailsSafe()
    {
        // THE CASE THAT FAILED OPEN: the endpoint variable is unset, which is what makes an inherited
        // `url-var:` declaration resolve to Local. Nothing here sets it, and nothing may.
        Assert.True(
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NR_ENGINE_URL")),
            "this test is about the UNSET case — the shell running it must not have NR_ENGINE_URL set");

        var claimsNr = Resolve("plain-spec-id", "--engine", "newrecruit=exec:./unknown-adapter");

        // 1. It did not inherit the endpoint. An unknown binary is a third party's service until declared.
        Assert.Equal(EngineEndpointKind.Undeclared, claimsNr.Entry.EndpointFor("roster").Kind);
        Assert.Equal(LoadTarget.ThirdPartyLive, claimsNr.LoadTarget);

        // 2. It did not inherit the MEASURED profile — which is the half that turns the memory cap off.
        //    `newrecruit` declares k = 0.375 and 1.22 GiB/process; an unknown adapter declares neither, so
        //    UndeclaredMemoryWorkerCap binds and the machine-width answer is never reached.
        Assert.Equal(0L, claimsNr.Entry.Profile.MemPerInstanceBytes);
        Assert.Equal(1.0, claimsNr.Entry.Profile.OversubscriptionFactor);
        Assert.NotEqual(Builtin("newrecruit").Profile, claimsNr.Entry.Profile);

        // 3. THE NUMBER. It must not be machine-width. It is held to the third-party load limit, which on
        //    a big box is far below both ceil(cpuCount × 0.375) and the undeclared cap.
        Assert.Equal(LiveWorkersEntitlement(claimsNr.Entry.Profile), claimsNr.EffectivePlan.Workers);
        Assert.True(claimsNr.EffectivePlan.Workers <= ConcurrencyPolicy.ThirdPartyLiveLoadLimit);

        // The teeth, wherever the numbers can differ at all: on a 32-core box the inherited profile bought
        // 12 workers and the fail-safe buys 2. (On the 2-vCPU CI runner every candidate answer collapses to
        // 2, which is exactly why this could not be caught there — the guard says so rather than passing
        // vacuously and pretending it proved something.)
        var machineWidth = ConcurrencyPolicy.For(
            MachineProfile.Current(), Builtin("newrecruit").Profile, LoadTarget.Local).Workers;
        if (machineWidth > ConcurrencyPolicy.ThirdPartyLiveLoadLimit)
        {
            Assert.True(
                claimsNr.EffectivePlan.Workers < machineWidth,
                $"an unknown adapter merely NAMED 'newrecruit' got {claimsNr.EffectivePlan.Workers} workers — " +
                $"the built-in's own machine-width answer is {machineWidth}. A name is not a measurement.");
        }

        // And the same holds when the variable IS set: it never had a declaration to derive from.
        var claimsNrLive = claimsNr with
        {
            ChildEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["NR_ENGINE_URL"] = "https://www.newrecruit.eu",
            },
        };
        Assert.Equal(LoadTarget.ThirdPartyLive, claimsNrLive.LoadTarget);
    }

    /// <summary>
    /// <b>The reference adapter is not a third party's website, and it says so — with a declaration, not
    /// with its name.</b> <c>--engine-endpoint local</c> is the line CI runs, and it is the only thing
    /// that lifts the fail-safe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>--engine "battlescribe=dotnet:…/bs-reference-adapter.dll"</c> is a <em>launchable</em>
    /// connectable: an in-process IKVM engine with no network code at all, which the harness cannot
    /// distinguish from any other unknown executable. Undeclared, it fails safe to
    /// <see cref="LoadTarget.ThirdPartyLive"/> — CI printed <i>"Load target: third-party live service —
    /// held to 2 concurrent sessions"</i> for it on every push. The answer is for the operator who chose
    /// the binary to state the fact, which is what CI now does.
    /// </para>
    /// <para>
    /// <b>Falsifiable:</b> drop <c>--engine-endpoint</c>'s application in <c>EngineOptions.Resolve</c> and
    /// the declared arm goes red (it falls back to the fail-safe); make the flag apply to built-ins and
    /// the rejection assertion goes red. The undeclared arm pins the direction that must never change.
    /// </para>
    /// </remarks>
    [Fact]
    public void LaunchableAdapter_TakesTheMachinesWidth_OnlyWhenItsEndpointIsDeclared()
    {
        const string RefAdapter = "battlescribe=dotnet:bs-reference-adapter.dll";

        // Undeclared: a binary we have never seen. The fail-safe holds.
        var undeclared = Resolve("plain-spec-id", "--engine", RefAdapter);
        Assert.Equal(LoadTarget.ThirdPartyLive, undeclared.LoadTarget);

        // Declared: the operator states what the binary is, and it takes the machine's own answer.
        var declared = Resolve("plain-spec-id", "--engine", RefAdapter, "--engine-endpoint", "local");
        Assert.Equal(LoadTarget.Local, declared.LoadTarget);
        Assert.Equal(LocalWorkers(declared.Entry.Profile), declared.EffectivePlan.Workers);

        // The teeth, wherever the two can differ. (On the 2-vCPU CI runner the machine's own answer for an
        // undeclared engine IS 2 — the throttle was invisible there, which is exactly how it survived.)
        if (LocalWorkers(declared.Entry.Profile) > ConcurrencyPolicy.ThirdPartyLiveLoadLimit)
        {
            Assert.True(
                declared.EffectivePlan.Workers > undeclared.EffectivePlan.Workers,
                "a declared-local adapter must not be held to a stranger's courtesy limit");
        }

        // What the declaration does NOT do: hand over a measured profile. It says where the traffic lands,
        // and nothing else — an undeclared footprint still binds ConcurrencyPolicy's conservative cap for
        // engines nobody has measured, whatever the endpoint says. The two arms differ ONLY in endpoint.
        Assert.Equal(0L, declared.Entry.Profile.MemPerInstanceBytes);
        Assert.Equal(undeclared.Entry.Profile, declared.Entry.Profile);

        // And a built-in refuses the flag: its endpoints are measured facts, per domain, and one word on a
        // command line may not turn the live fail-safe off.
        var refused = Assert.Throws<CliInputException>(
            () => Resolve("plain-spec-id", "--engine", "newrecruit", "--engine-endpoint", "local"));
        Assert.Contains("its endpoints are measured facts, declared", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>#317 review — the tenth instance of this branch's signature defect.</b> The old guard was
    /// <c>!connectable.IsLaunchable</c>: a fact about the <em>syntax of the <c>--engine</c> token</em>
    /// (did it carry an <c>exec:</c>/<c>dotnet:</c> target?), stood in for "is this a built-in", a fact
    /// about the <em>resolved engine</em>. The two part company for exactly one input: an
    /// <c>engines.json</c>-registered engine referenced by plain name — foreign code, no measured
    /// profile, <see cref="EngineEntry.Builtin"/> false, and no <c>exec:</c>/<c>dotnet:</c> token
    /// anywhere in what the user typed. The old predicate rejected <c>--engine-endpoint</c> for it with
    /// a message that was false on every clause ("'wham' is a built-in engine…").
    /// </summary>
    /// <remarks>
    /// Falsifiable: restore <c>!connectable.IsLaunchable</c> as the guard in
    /// <c>EngineOptions.Resolve</c> and the declared arm goes red — <c>Resolve</c> throws
    /// <see cref="CliInputException"/> for a plain-name registry engine that is not built-in.
    /// </remarks>
    [Fact]
    public void EnginesJsonEngine_ReferencedByPlainName_MayDeclareAnEndpoint()
    {
        var dir = Directory.CreateTempSubdirectory("bsspec-engine-options-test-");
        try
        {
            File.WriteAllText(
                Path.Combine(dir.FullName, "engines.json"),
                """{"engines":{"wham":{"exec":"node w.js"}}}""");
            var options = new EngineOptions { RegistryStartDirectory = dir.FullName };

            // Undeclared: a plain-name registry engine with no endpoint in engines.json still gets the
            // fail-safe answer — the same as any other foreign adapter nobody has measured.
            var undeclared = Resolve(options, "plain-spec-id", "--engine", "wham");
            Assert.False(undeclared.Entry.Builtin);
            Assert.Equal(LoadTarget.ThirdPartyLive, undeclared.LoadTarget);

            // THE BUG: a plain-name registry engine must be ALLOWED to declare --engine-endpoint. It
            // carries no exec:/dotnet: token, so IsLaunchable is false for it — the old predicate treated
            // that as "this is a built-in" and refused the flag with a message that was false about it.
            var declared = Resolve(options, "plain-spec-id", "--engine", "wham", "--engine-endpoint", "local");
            Assert.False(declared.Entry.Builtin);
            Assert.Equal(LoadTarget.Local, declared.LoadTarget);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static EngineEntry Builtin(string name) =>
        EngineRegistry.Load(null).Resolve(EngineConnectable.Parse(name));

    /// <summary>
    /// A <c>--policy</c> override may lower the load on a third party's site; it may not raise it — and it
    /// is refused, not silently clamped (#305: a flag is honoured or rejected, never dropped).
    /// </summary>
    /// <remarks>
    /// Falsifiable: drop the check in <c>RunCommand.ApplyPolicyOverride</c> and the first assertion goes
    /// red (the override sails through). Drop the <c>ClampToLoadTarget</c> backstop in
    /// <see cref="EngineSelection.EffectivePlan"/> and the last one goes red — a plan built by any other
    /// path would put 32 browsers on the live site.
    /// </remarks>
    [Fact]
    public void PolicyOverride_CannotRaiseTheLoadLimit_OnALiveEngine()
    {
        var live = Live(Resolve("plain-spec-id", "--engine", "newrecruit"));

        var ex = Assert.Throws<CliInputException>(
            () => RunCommand.ApplyPolicyOverride(live, "workers=32", _ => { }, UnsafeReuse.Refuse));
        Assert.Contains("load question", ex.Message, StringComparison.Ordinal);

        // Lowering it is always allowed.
        var quieter = RunCommand.ApplyPolicyOverride(live, "workers=1", _ => { }, UnsafeReuse.Refuse);
        Assert.Equal(1, quieter.EffectivePlan.Workers);

        // An override that says nothing about workers must not resurrect the machine-width count through
        // the base plan it edits: `--policy reuse-roster=on` is not a request for 12 browsers.
        //
        // THIS IS THE ASSERTION THAT WENT RED THE FIRST TIME CI EVER RAN THIS PROJECT (#317 I5). It read
        // `Assert.Equal(ThirdPartyLiveLoadLimit, …Workers)` and the runner said "expected 2, actual 1" —
        // because the runner has 2 logical processors, `ceil(2 × 0.375) = 1`, and this engine is already
        // under the limit on its own. The CODE was right. The test had turned a ceiling into a floor.
        //
        // AllowForAblation, because this line forces reuse on `newrecruit`, which declares neither
        // domain reuse-safe — `run` refuses that outright (#313) and would throw before reaching the
        // assertion below. The question here is a plan-arithmetic one, and `compare` is the verb that
        // can legitimately ask it.
        var reuseOnly = RunCommand.ApplyPolicyOverride(
            live, "reuse-roster=on", _ => { }, UnsafeReuse.AllowForAblation);

        Assert.True(reuseOnly.EffectivePlan.ReuseRoster, "the flag the user actually passed was dropped");
        Assert.Equal(LiveWorkersEntitlement(live.Entry.Profile), reuseOnly.EffectivePlan.Workers);

        var machineWidth = LocalWorkers(live.Entry.Profile);
        if (machineWidth > ConcurrencyPolicy.ThirdPartyLiveLoadLimit)
        {
            Assert.True(
                reuseOnly.EffectivePlan.Workers < machineWidth,
                $"`--policy reuse-roster=on` — which says nothing about workers — resurrected the " +
                $"machine-width count ({machineWidth}) through the base plan it edits");
        }

        // The backstop: even a plan handed in directly cannot exceed the limit. Machine-independent —
        // 32 is above the limit on every box.
        var forced = live with { PlanOverride = new ConcurrencyPlan(32, 32, ReuseRoster: false, ReuseGameData: false) };
        Assert.Equal(ConcurrencyPolicy.ThirdPartyLiveLoadLimit, forced.EffectivePlan.Workers);
        Assert.Equal(ConcurrencyPolicy.ThirdPartyLiveLoadLimit, forced.EffectivePlan.PoolSize);
    }

    /// <summary>A frozen engine's <c>--policy workers=N</c> is untouched — the limit binds live runs only.</summary>
    [Fact]
    public void PolicyOverride_OnAFrozenEngine_IsHonouredInFull()
    {
        var frozen = Frozen(Resolve("plain-spec-id", "--engine", "newrecruit"));

        var overridden = RunCommand.ApplyPolicyOverride(frozen, "workers=32", _ => { }, UnsafeReuse.Refuse);

        Assert.Equal(32, overridden.EffectivePlan.Workers);
    }

    /// <summary>
    /// <b>The worker count a live run is entitled to on THIS machine: the machine's own answer, held to
    /// <see cref="ConcurrencyPolicy.ThirdPartyLiveLoadLimit"/>.</b> A ceiling, not a target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asserting the bare limit is a machine-dependent test, and it went red the first time CI ever
    /// ran this project.</b> On the GitHub runner — which reports <b>2</b> logical processors, not the
    /// 4 this repo's docs assume — <c>newrecruit</c>'s own answer is <c>ceil(2 × 0.375) = 1</c>, already
    /// <em>below</em> the limit of 2. So <c>Assert.Equal(ThirdPartyLiveLoadLimit, …Workers)</c> read
    /// "expected 2, actual 1" — and the code was right: the load limit is a **ceiling**. A test that
    /// demands a run be *exactly* as parallel as the limit permits has quietly turned a ceiling into a
    /// floor, and would fail any box small enough to be under it on its own.
    /// </para>
    /// <para>
    /// This is exactly what #317's I5 was about: these tests had never been executed by CI, so every one
    /// of them encodes the 32-core box they were written on.
    /// </para>
    /// </remarks>
    private static int LiveWorkersEntitlement(EngineProfile profile) =>
        Math.Min(
            ConcurrencyPolicy.For(MachineProfile.Current(), profile, LoadTarget.Local).Workers,
            ConcurrencyPolicy.ThirdPartyLiveLoadLimit);

    /// <summary>The machine's unclamped answer for this engine — what a live run must NOT be given.</summary>
    private static int LocalWorkers(EngineProfile profile) =>
        ConcurrencyPolicy.For(MachineProfile.Current(), profile, LoadTarget.Local).Workers;

    /// <summary>Point the selection's children at the live site (as <c>NR_ENGINE_URL</c> in the shell would).</summary>
    private static EngineSelection Live(EngineSelection selection) => selection with
    {
        ChildEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NR_ENGINE_URL"] = "https://www.newrecruit.eu",
        },
    };

    /// <summary>
    /// Pin the selection to the frozen HAR regardless of the ambient shell: an empty <c>NR_ENGINE_URL</c>
    /// is what <c>HostEngineFactory</c> reads as "no live URL — load the frozen HAR".
    /// </summary>
    private static EngineSelection Frozen(EngineSelection selection) => selection with
    {
        ChildEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NR_ENGINE_URL"] = string.Empty,
        },
    };

    // ---- --headed: a flag is accepted or rejected, never silently dropped (#305, #271 Task 5) ----

    [Fact]
    public void Resolve_HeadedAgainstLaunchableAdapter_ThrowsNamingNoChannel()
    {
        // EngineHostLocator.Resolve has no channel to convey --headed to an exec:/dotnet: adapter
        // and used to just drop it on the floor. Reject at the CLI layer instead, before any
        // process is spawned.
        var ex = Assert.Throws<CliInputException>(
            () => Resolve("plain-spec-id", "--engine", "wham=dotnet:adapter.dll", "--headed"));

        Assert.Contains("no channel", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_HeadedAgainstNonUiBuiltin_ThrowsNamingNoUi()
    {
        // "battlescribe" (no -ui suffix) has no window to show; --headed against it is a mistake,
        // not a no-op.
        var ex = Assert.Throws<CliInputException>(
            () => Resolve("plain-spec-id", "--engine", "battlescribe", "--headed"));

        Assert.Contains("no UI", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_HeadedAgainstUiBuiltin_Succeeds()
    {
        var selection = Resolve("plain-spec-id", "--engine", "battlescribe-ui", "--headed");
        Assert.True(selection.Headed);
    }

    [Fact]
    public void Resolve_HeadedWithUiSugar_Succeeds()
    {
        // --ui appends "-ui" to a plain name BEFORE the headed capability check runs, so this must
        // not throw even though the user typed the non-ui name.
        var selection = Resolve("plain-spec-id", "--engine", "newrecruit", "--ui", "--headed");
        Assert.True(selection.Headed);
    }

    private static EngineSelection Resolve(string specInput, params string[] extraArgs) =>
        Resolve(new EngineOptions(), specInput, extraArgs);

    /// <summary>Overload taking a caller-built <see cref="EngineOptions"/> — the seam for tests that
    /// need <see cref="EngineOptions.RegistryStartDirectory"/> pointed at a temp-dir engines.json.</summary>
    private static EngineSelection Resolve(EngineOptions options, string specInput, params string[] extraArgs)
    {
        string[] args = [specInput, .. extraArgs];
        return options.Resolve(ParseWith(options, args), specInput);
    }

    private static ParseResult ParseWith(EngineOptions options, params string[] args)
    {
        var command = new Command("probe-engine-options");
        command.Arguments.Add(new Argument<string>("spec"));
        options.AddTo(command);
        return command.Parse(args);
    }
}
