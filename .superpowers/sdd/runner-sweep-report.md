# The context axis, fitted on a real GitHub runner — final report

**Branch:** `perf/concurrency-model` (PR #317) · **Scratch branch:** `perf/context-axis-ci-sweep` — **deleted**
**Doc:** `docs/concurrency-policy-measurements.md` **§10** (new), §8.8 corrected in place.

---

## Verdict in one line

**The hypothesis that the runner's optimum is 8–12 is REFUTED. Both constants stay: `newrecruit-ui` =
16, `newrecruit` = 4.** On the runner, **pool 8 is 5.6% slower than 16 and pool 12 is 3.8% slower** —
distinguishable at 95%, losing in 5–6 of 6 paired blocks. The WSL container modelled the runner's
*optimum* correctly; it only modelled its *speed* badly, and the constant encodes the optimum.

This is a null result, and it is the one the brief said to report honestly rather than nudge.

---

## 1. What was measured, and how

**Timed quantity:** the **`dotnet test` invocation wall** — a stopwatch around the invocation only.
Never job wall-clock. §8.8 was right that a ~6 s delta inside a ~100 s step inside a ~12 min job is
unmeasurable; the job wall is build/cache noise.

**Cross-check, per point (all recorded):**
- `[Fact]` duration from the TRX — the `Parallel.ForEachAsync` execution alone.
- **invocation − `[Fact]`** = test-host start + the fixture's **serial** pool construction. This is
  the exact term the hypothesis was about, so it was measured rather than inferred.
- **The pool actually built**, two independent ways: the fixture's own `Pool size: N contexts` (read
  back from the constructed pool), and the harness's OTel telemetry (peak `harness.resource.count`
  for `browser-context` = N). **A point whose lever was not connected aborted rather than reporting.**
- Verdict counts per run (`N passed / M skipped / X xfail / F failed`), plus the failing spec IDs.

**Which one is fitted:** the `dotnet test` invocation wall. It is what CI pays for the step.

### Three runs, because the first design could not answer the question

| run | design | jobs | within-level spread | usable to rank levels? |
|---|---|--:|--:|---|
| 1. cold matrix | `pool × rep`, 1 level/job, 3 reps (the brief's design) | 39 | **17–27%** | **No** |
| 2. blocked | one job = one runner = every level | 12 | 2–9% | Yes, except 16-vs-20 |
| 3. tie-break | blocked + clean Latin square, levels 8–24 | 6 | residual sd **2.3%** | Yes |

**Why run 1 failed.** GitHub assigns runner CPU at random, and the fleet is heterogeneous — **EPYC
7763, EPYC 9V45, EPYC 9V74, Xeon Platinum 8573C, Xeon 6973P-C**. The hardware spread (17–27%) is as
large as every between-level difference. Pool 24 drew three of the slowest (7763); pool 4 drew none.
Blocking beats averaging: put **every level on the same runner**, compare within the block, and runner
speed cancels exactly.

**A design defect I introduced, and fixed.** Run 2's ordering (rotate by block, reverse on even blocks)
**preserves position parity** over an even number of levels — every level landed only in odd slots or
only in even slots. Later slots run ~1–3% faster (page-cache warmth), so the one comparison straddling
the classes (16 = even slots vs 20 = odd slots) was confounded. It did *not* affect the finding that
matters (within each parity class, independently, everything ≤12 loses to the 16/20 region), but it
made 16-vs-20 unanswerable. Run 3 fixes it: **five levels — an odd count — with rotation only is a
proper cyclic Latin square**, every level visits every position, warmth cancels. Verified: position
effects ≤1.5%, and the model-based and raw paired readings agree.

---

## 2. `newrecruit-ui` — runner optimum, median + spread

**Tie-break (run 3): 6 blocks, clean Latin square, 112 specs.**

| pool | median wall | vs 16 (block+position removed) | 95% CI | faster than 16 in |
|--:|--:|--:|--:|--:|
| 8 | 97.9 s | **+5.6%** | [+3.1, +8.2] | 1 of 6 blocks |
| 12 | 96.0 s | **+3.8%** | [+1.2, +6.4] | **0 of 6 blocks** |
| **16** | **92.4 s** | — | — | — |
| 20 | 91.2 s | −0.8% | [−3.4, +1.8] | 5 of 6 blocks |
| 24 | 93.5 s | +0.7% | [−1.9, +3.3] | 2 of 6 blocks |

**Is the spread small enough to distinguish the winner from its neighbours? Partly — and here is
exactly where the line falls:**

- **YES against 8 and 12.** Both CIs exclude zero; 12 loses in 6 of 6 blocks. **The 8–12 hypothesis is
  dead.**
- **NO against 20 and 24.** 16, 20 and 24 are **statistically indistinguishable** (every CI includes
  zero). The bottom of the curve is a **plateau, not a point**.
- **20 is nominally 0.8–1.0% faster than 16 and is NOT crowned** — that is inside the noise. Per the
  brief: if the levels are within noise, say so and leave the constant alone.

**Bracketing.** Below: 12 (+3.8%) and 8 (+5.6%), two levels deep, measurably worse. Above: a plateau
rather than a cliff — 24 is +0.7% here and +2.3% in run 2's internally-clean even-parity class; §7
already showed 32/48/64 degrading hard on both other boxes. A minimum in a flat basin is a real
finding, and it is *why* the exact value within 16–24 barely matters.

Run 2 (wider, levels 4–24) places the small pools: 4 → **+16.4%**, 6 → **+10.7%**, 8 → +3.8%,
10 → +5.2%, 12 → +1.2% vs 16.

---

## 3. `newrecruit` — 4 confirmed

**Run 2: 6 blocks, levels 2–16, 365 specs.** Residual sd **7.4%** (a much noisier engine than
`newrecruit-ui`'s 2.3%) — hence wide intervals, which is itself the result.

| pool | median wall | vs 4 (block+position removed) | 95% CI | reading |
|--:|--:|--:|--:|---|
| 2 | 48.9 s | −1.5% | [−9.9, +6.9] | within noise of 4 |
| **4** | **50.5 s** | — | — | **shipped — stays** |
| 6 | 49.5 s | +2.3% | [−6.1, +10.7] | within noise of 4 |
| 8 | 51.8 s | −2.2% | [−10.6, +6.2] | within noise of 4 |
| 12 | 59.0 s | **+19.9%** | [+11.5, +28.3] | **distinguishable — worse** |
| 16 | 64.7 s | **+23.6%** | [+15.2, +32.0] | **distinguishable — worse** |

**2, 4, 6, 8 are mutually indistinguishable; 12 and 16 are far worse.** 4 sits mid-plateau with two
levels of margin before the rise. Bracketed above by 12 and 16.

**A 3% trap avoided.** The *raw* paired read made pool 2 look like a winner: −3.4%, beating 4 in **6 of
6 blocks**. That was the position-parity confound — the balanced fit dissolves it to −1.5% ± 8.4%, a
tie. Exactly the sort of "significant" 3% that a careless sweep ships.

---

## 4. Does the runner disagree with the WSL fit, beyond noise?

**No — for the decision. Yes — for the reason CI looked disappointing.**

| | WSL container (§7) | real runner (§10) |
|---|--:|--:|
| `newrecruit-ui` optimum | **16** | **16–20–24 plateau (16 is in it)** |
| `newrecruit` optimum | **4** | **2–8 plateau (4 is in it)** |
| `newrecruit-ui` wall at pool 16 | 34.2 s | **92.4 s (2.7× slower)** |

**The mechanism, measured on the runner** (invocation − `[Fact]`, per point):

| | `newrecruit-ui` | `newrecruit` |
|---|---|---|
| per-context construction cost | **≈0.32 s** | **≈1.26 s** (4× dearer) |
| `[Fact]` wall, small pool → optimum | 98.7 s → 75.3 s (**−24%**) | 34.9 s → 29.6 s |
| `[Fact]` wall past the optimum | 74.9 s at 20 **and** 24 — floors | 33.1 s at 16 — degrades |

§8.8's mechanism was **real but far too small**: for `newrecruit-ui`, going 4 → 16 costs **+4 s** of
serial init and buys **−24%** of execution. Init cannot drag that optimum below 16. It *is*, however,
exactly why `newrecruit`'s optimum is low — its contexts cost 4× more to build (HAR route interception
+ Pinia wait on a real SPA, vs static files for the editor).

**The −6%-not-−33% is explained without touching the constant:** the runner is ~2.7× slower per core,
which makes this workload more CPU-bound and oversubscription worth less. The genuine 6 → 16 gain on
the runner is **≈10–11%** (104.5 s → 92.9 s), and the single CI sample that read −6% was a noisy
observation of that. **The model box got the optimum right and the speed wrong**, and only the optimum
is what the constant encodes.

---

## 5. Verdict safety on the runner — PASSED

| engine | levels swept | timed runs | verdicts |
|---|---|--:|---|
| `newrecruit` | 2, 4, 6, 8, 12, 16 | 51 | **363 passed / 2 skipped / 0 failed — identical at every level** |
| `newrecruit-ui` | 4, 6, 8, 10, 12, 16, 20, 24 | 102 | **112 passed / 1 skipped / 0 failed — identical at every level** |

Pass/fail spec counts are **identical across every pool level**, on the real runner, for both engines.
No level is unusable. The pool was proven live at every point (fixture-reported size **and** OTel
`browser-context` peak).

**One failure in 153 runs — reported in full, and it is not a conformance divergence:**

- **Spec: `constraint/constraint-create-and-fields`** (`newrecruit-ui`, run 2, block 3, **pool 6**,
  last position in its block).
- **`Setup error: Navigation to editor failed: Timeout 30000ms exceeded`** — a Playwright page load
  that did not finish in 30 s. **The spec's assertions never ran; no spec produced a different answer.**
- **Not pool-dependent:** it hit **pool 6** — a *low*-contention level — while 20 and 24 were clean in
  every block, and pool 6 was clean in the other five blocks. Contention shows at the top of a range,
  not the bottom.
- Rate ≈0.7%. Excluded from the timing curves (its 30 s timeout sits inside its wall), never averaged
  away silently.

---

## 6. Scaffolding — all of it is gone

- Scratch branch `perf/context-axis-ci-sweep`: **deleted** (local + remote), with its worktree.
- `.github/workflows/context-axis-sweep.yml`, `.github/sweep/*.ps1`: existed only on that branch.
- The temporary `BSSPEC_SWEEP_POOL` override in `FixtureConcurrency` (needed so one build could serve
  every level inside one job): existed only on that branch. **Never on `perf/concurrency-model`.**
- The retired-knob lint gate (`ConcurrencyConfigurationDriftTests`) was **never weakened or deleted**,
  and is green. The sweep deliberately did not reuse a retired knob name.

## 7. What §10 did not reach

- `nr-ui-frozen` / `nr-editor-frozen` — still unswept on any hardware.
- The live lanes — deliberately untouched (see §9 / #318); a sweep is not something to point at a third
  party's website.
- Above pool 24 on the runner — the right-hand bracket is a plateau, not a measured cliff.
- One workload, one spec set.

---

## 8. CI payoff — run [29332238785](https://github.com/WarHub/battlescribe-spec/actions/runs/29332238785), all 8 jobs green

**Read this with one caveat up front: my change moves NO constant, so its contribution to CI
wall-clock is exactly zero.** The deltas below against the *previous* branch run are the live-lane fix
(`4db354c`/`c411e2f`, another agent's work, which deliberately returns the live pool 4 → 2 as a
courtesy limit to newrecruit.eu) plus runner noise. The comparison that matters is **against `main`**.

| job | `main` baseline | branch (before this work) | **now** | vs `main` |
|---|--:|--:|--:|--:|
| `nr-conformance` | 10.15 min | 8.27 min | **10.00 min** | −1.5% |
| **`thorough-conformance`** | 11.75 min | 12.02 min | **10.62 min** | **−9.6%** ✅ |
| `thorough-ui-bs` (0 / 1) | 8.03 / 6.87 min | 7.68 / 7.10 min | **7.47 / 7.00 min** | −7.0% / +1.9% |
| `checks` / `smoke` | 4.63 / 5.38 min | 4.27 / 5.18 min | 4.35 / 5.07 min | −6.0% / −5.8% |

**Step times — the only steps the context pool can move:**

| step (lane) | pool | `main` | **now** | Δ |
|---|--:|--:|--:|--:|
| Full frozen NR Editor GameData UI (`nr-editor-ui-frozen`) | **16** | 101 s | **75 s** | **−26%** |
| Full frozen NR roster (`nr-frozen`) | **4** | 53 s | **49 s** | −8% |

**`thorough-conformance` — the lane these two constants govern — is now −9.6% against `main`**, where
the previous branch run measured +2.3% (parity). Nothing in the constants changed between those two
runs. That swing *is* the runner-CPU noise this whole report is about: a single CI sample of these
steps carries the same ±17–27% hardware spread that made the cold matrix unusable. The blocked sweep
(§10.2/§10.3) is the reliable estimate of these lanes' behaviour; a single CI run is not.

⚠️ **`nr-conformance` gave back its −18.5%, on purpose and not by my hand.** That win came from the
live lane's pool going 2 → 4 — which the live-lane fix has since identified as unearned traffic to a
third party's production website and returned to 2. Correct call; the lane is back at `main` parity
(−1.5%). Not a regression in this work, and not mine to re-take.
