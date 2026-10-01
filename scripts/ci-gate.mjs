#!/usr/bin/env node
// The `ci-gate` verdict: the one required check, computed from every job it needs. Run by the
// `ci-gate` job of .github/workflows/ci.yml:
//
//   NEEDS='${{ toJSON(needs) }}' EVENT='${{ github.event_name }}' node scripts/ci-gate.mjs
//
// Prints a table, writes `failed=true|false` and `failed-jobs=<JSON array>` to $GITHUB_OUTPUT (the
// scheduled-failure issue lists those), appends the table to $GITHUB_STEP_SUMMARY, and exits 1 on any
// failure.
//
// WHAT IT REPLACES. The inline check accepted `success` OR `skipped` for every job. A skipped job is
// either a decision (the gate said this run does not owe the thorough suites) or an accident (a draft,
// a gate output nobody wrote, a typo in an `if:`), and that check could not tell them apart. So:
//
//   * a draft PR read green — `gate`, `checks`, `docker` and `smoke` all skip on a draft, and a
//     required check whose every input skipped is a pass;
//   * a gate output that was missing or malformed skipped the thorough lanes on every PR, green;
//   * the issue the weekly run opens listed its failed jobs by hand and had dropped `docker`.
//
// The rule now: every job must succeed, except that a thorough job may be skipped when the gate says
// `thorough=false`, and a live job when it says `live=false` — and the gate's outputs must be exactly
// "true" or "false", and both "true" on a scheduled or manual run. Anything else is a failure with a
// reason, not a pass.

import { appendFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

import { FULL_EVENTS, loadConfig } from "./thorough-inputs.mjs";

function flag(outputs, name) {
  const value = outputs?.[name];
  if (value === "true") return true;
  if (value === "false") return false;
  return undefined;
}

/**
 * The verdict over `needs` (the parsed `toJSON(needs)`). Returns every job's row, the failures, and
 * the gate flags as read. `config` is scripts/ci-gate.json.
 */
export function evaluate({ needs, event, config }) {
  const rows = [];
  const failures = [];
  const fail = (job, result, why) => {
    rows.push({ job, result, ok: false, why });
    failures.push({ job, why });
  };

  if (needs === null || typeof needs !== "object" || Array.isArray(needs) || Object.keys(needs).length === 0) {
    fail("(needs)", "-", "ci-gate received no needs: `needs:` is empty or NEEDS was not passed, so there is nothing to vouch for");
    return { ok: false, rows, failures, thorough: undefined, live: undefined };
  }

  const kindOf = (job) =>
    config.thoroughJobs.includes(job) ? "thorough" : config.liveJobs.includes(job) ? "live" : "every-run";

  // ── the gate itself ──
  const gate = needs.gate;
  let thorough;
  let live;
  let gateRan = false;
  if (!gate) {
    fail("gate", "-", "ci-gate does not need the `gate` job, so nothing explains a skipped lane");
  } else if (gate.result === "skipped") {
    fail("gate", gate.result, "draft: nothing ran — `gate` skips only on a draft PR; mark the PR ready for review");
  } else if (gate.result !== "success") {
    fail("gate", gate.result, `the gate ${gate.result}, so whether the thorough and live lanes were owed is unknown`);
  } else {
    gateRan = true;
    thorough = flag(gate.outputs, "thorough");
    live = flag(gate.outputs, "live");
    const bad = ["thorough", "live"].filter((name) => flag(gate.outputs, name) === undefined);
    if (bad.length > 0) {
      fail(
        "gate",
        gate.result,
        bad.map((name) => `output \`${name}\` is ${JSON.stringify(gate.outputs?.[name] ?? null)}`).join(", ") +
          ' — each must be exactly "true" or "false"',
      );
    } else if (FULL_EVENTS.includes(event) && !(thorough && live)) {
      fail("gate", gate.result, `a ${event} run must run everything, but the gate said thorough=${thorough}, live=${live}`);
    } else if (live && !thorough) {
      fail("gate", gate.result, "the gate said live=true but thorough=false; every reason that forces live also forces thorough");
    } else {
      rows.push({ job: "gate", result: gate.result, ok: true, why: `thorough=${thorough}, live=${live}` });
    }
  }

  // ── the data file and the needs list must name the same gated jobs ──
  for (const job of [...config.thoroughJobs, ...config.liveJobs]) {
    if (!(job in needs)) {
      fail(job, "-", `scripts/ci-gate.json names \`${job}\` but ci-gate does not need it, so its result is never checked`);
    }
  }

  // ── every other job ──
  for (const [job, need] of Object.entries(needs)) {
    if (job === "gate") continue;
    const result = need?.result ?? "(none)";
    const kind = kindOf(job);

    if (result === "success") {
      rows.push({ job, result, ok: true, why: "" });
      continue;
    }

    if (result === "skipped") {
      if (!gateRan) {
        fail(job, result, gate?.result === "skipped" ? "skipped with the gate (draft)" : "skipped, and the gate did not run to explain it");
        continue;
      }
      if (kind === "every-run") {
        fail(job, result, "skipped, but this job runs on every non-draft run");
        continue;
      }
      const name = kind; // "thorough" or "live": the gate output that explains this job
      const said = kind === "thorough" ? thorough : live;
      if (said === false) {
        rows.push({ job, result, ok: true, why: `not owed: the gate said ${name}=false` });
      } else if (said === true) {
        fail(job, result, `skipped although the gate said ${name}=true`);
      } else {
        fail(job, result, `skipped, and the gate gave no valid \`${name}\` output to explain it`);
      }
      continue;
    }

    fail(job, result, result === "cancelled" ? "cancelled (a timeout, a superseded run, or a manual cancel)" : result);
  }

  return { ok: failures.length === 0, rows, failures, thorough, live };
}

export function renderTable({ rows }) {
  const lines = ["| Job | Result | Verdict | Why |", "|---|---|---|---|"];
  for (const r of rows) {
    lines.push(`| ${r.job} | ${r.result} | ${r.ok ? "ok" : "**FAIL**"} | ${r.why.replace(/\|/g, "\\|")} |`);
  }
  return lines.join("\n");
}

function main(env) {
  if (env.NEEDS === undefined) throw new Error("NEEDS is not set; pass `toJSON(needs)` through the step's env");
  let needs;
  try {
    needs = JSON.parse(env.NEEDS);
  } catch (err) {
    throw new Error(`NEEDS is not valid JSON (${err.message})`);
  }

  const verdict = evaluate({ needs, event: env.EVENT, config: loadConfig() });
  const table = renderTable(verdict);

  console.log(table);
  for (const f of verdict.failures) console.log(`::error::ci-gate: ${f.job}: ${f.why}`);
  console.log(verdict.ok ? "All required jobs passed, or were skipped because the gate said they were not owed." : `${verdict.failures.length} problem(s).`);

  if (env.GITHUB_OUTPUT) {
    const list = JSON.stringify(verdict.failures.map((f) => `${f.job}: ${f.why}`));
    appendFileSync(env.GITHUB_OUTPUT, `failed=${!verdict.ok}\nfailed-jobs=${list}\n`);
  }
  if (env.GITHUB_STEP_SUMMARY) {
    appendFileSync(env.GITHUB_STEP_SUMMARY, `### ci-gate: ${verdict.ok ? "passed" : "FAILED"}\n\n${table}\n`);
  }
  return verdict.ok ? 0 : 1;
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isMain) {
  try {
    process.exitCode = main(process.env);
  } catch (err) {
    console.error(`::error::ci-gate: ${err.message}`);
    if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `failed=true\nfailed-jobs=${JSON.stringify([`ci-gate: ${err.message}`])}\n`);
    process.exitCode = 1;
  }
}
