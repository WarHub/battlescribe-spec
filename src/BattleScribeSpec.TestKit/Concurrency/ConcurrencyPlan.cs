namespace BattleScribeSpec.Concurrency;

/// <summary>
/// One decision, for one engine on one machine. Every concurrency and reuse knob in the harness
/// reads from this — the CLI's worker count, the in-process pools' size, xUnit's collection
/// parallelism, and whether engines are reused across setups.
/// </summary>
/// <remarks>
/// <para>
/// One policy governing everything is a single point of failure, deliberately. Today a bad
/// NR_PARALLEL degrades one lane and a bad --workers default degrades another, independently and
/// inconsistently. One place to be wrong is one place to measure, fix and tune.
/// </para>
/// <para>
/// <b>What is bounded and what is not (Task 7 residual — tracked, not yet fixed):</b> this plan
/// bounds xUnit's own thread count (<see cref="MaxParallelThreads"/>, hardcoded to
/// <see cref="ConcurrencyPolicy.ProvisionalUnmeasuredMemoryCap"/> in both <c>xunit.runner.json</c>
/// files) and it bounds each individual pool's size (<see cref="PoolSize"/>). It does <b>not</b>
/// bound the product across simultaneously-live xUnit collection fixtures. A collection fixture
/// lives for the whole collection, not for one thread-slot, so two independent collections (e.g.
/// <c>FrozenNrRosterFixture</c> and <c>FrozenNrGameDataUiFixture</c>) can each be fully alive at
/// once, each with a pool sized up to this cap — total live browser-contexts can reach the sum
/// across collections, not the cap itself. <c>BrowserResourceRaceGate</c> only serializes a
/// fixture against its own resource-metrics test, not against sibling fixtures. The original
/// problem — "collections × pools × the JVM compose multiplicatively with nothing capping the
/// product" — is bounded per-factor here, not as a product. See the tracking issue referenced from
/// Task 7 of <c>docs/superpowers/plans/2026-07-13-harness-concurrency-model.md</c> for the real
/// fix (most likely a shared budget the pools draw from, rather than each independently capped).
/// </para>
/// </remarks>
/// <param name="Workers">How many instances of the engine may run concurrently.</param>
/// <param name="PoolSize">Size of the in-process reuse pool. Currently mirrors <see cref="Workers"/>. Bounded per-pool only — see remarks for the cross-collection gap this does not close.</param>
/// <param name="MaxParallelThreads">Degree of parallelism to hand to the test runner (e.g. xUnit collections). Bounds thread count only — see remarks for what it does not bound.</param>
/// <param name="ReuseRoster">Whether the roster engine may be reused across setups instead of cold-started each time.</param>
/// <param name="ReuseGameData">Whether the gamedata engine may be reused across setups instead of cold-started each time.</param>
public sealed record ConcurrencyPlan(
    int Workers,
    int PoolSize,
    int MaxParallelThreads,
    bool ReuseRoster,
    bool ReuseGameData);
