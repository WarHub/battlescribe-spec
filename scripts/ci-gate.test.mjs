import { describe, it } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

import { evaluate, renderTable } from "./ci-gate.mjs";
import { loadConfig } from "./thorough-inputs.mjs";

const SCRIPT = fileURLToPath(new URL("./ci-gate.mjs", import.meta.url));
const config = loadConfig();

// A fixed config, so these cases test the rules rather than whatever ci-gate.json lists today.
const fixture = { thoroughJobs: ["thorough-conformance", "thorough-ui-bs"], liveJobs: ["nr-conformance"] };

/** A complete, all-green `needs` — every job ci.yml has — with the gate saying thorough/live as given. */
function needs({ thorough = "true", live = "true", ...overrides } = {}) {
  const base = {
    gate: { result: "success", outputs: { thorough, live } },
    checks: { result: "success", outputs: {} },
    docker: { result: "success", outputs: {} },
    smoke: { result: "success", outputs: {} },
    "thorough-conformance": { result: "success", outputs: {} },
    "thorough-nr-ui-roster": { result: "success", outputs: {} },
    "thorough-ui-bs": { result: "success", outputs: {} },
    "nr-conformance": { result: "success", outputs: {} },
  };
  for (const [job, value] of Object.entries(overrides)) {
    if (value === undefined) delete base[job];
    else base[job] = typeof value === "string" ? { result: value, outputs: {} } : value;
  }
  return base;
}

function verdict(n, event = "pull_request", cfg = fixture) {
  return evaluate({ needs: n, event, config: cfg });
}

function failedJobs(v) {
  return v.failures.map((f) => f.job);
}

describe("ci-gate evaluate", () => {
  it("passes an all-green run", () => {
    const v = verdict(needs());
    assert.equal(v.ok, true, renderTable(v));
    assert.deepEqual(v.failures, []);
  });

  it("lists a docker failure (the job the old issue list forgot)", () => {
    const v = verdict(needs({ docker: "failure" }));
    assert.equal(v.ok, false);
    assert.deepEqual(failedJobs(v), ["docker"]);
  });

  it("fails a cancelled job", () => {
    const v = verdict(needs({ smoke: "cancelled" }));
    assert.equal(v.ok, false);
    assert.match(v.failures[0].why, /cancelled/);
  });

  it("accepts a thorough job skipped when the gate says thorough=false", () => {
    const v = verdict(
      needs({ thorough: "false", live: "false", "thorough-conformance": "skipped", "thorough-ui-bs": "skipped", "nr-conformance": "skipped" }),
    );
    assert.equal(v.ok, true, renderTable(v));
  });

  it("fails a thorough job skipped when the gate says thorough=true", () => {
    const v = verdict(needs({ live: "false", "thorough-ui-bs": "skipped", "nr-conformance": "skipped" }));
    assert.equal(v.ok, false);
    assert.deepEqual(failedJobs(v), ["thorough-ui-bs"]);
    assert.match(v.failures[0].why, /thorough=true/);
  });

  it("accepts nr-conformance skipped when the gate says live=false", () => {
    const v = verdict(needs({ live: "false", "nr-conformance": "skipped" }));
    assert.equal(v.ok, true, renderTable(v));
  });

  it("fails nr-conformance skipped when the gate says live=true", () => {
    const v = verdict(needs({ "nr-conformance": "skipped" }));
    assert.deepEqual(failedJobs(v), ["nr-conformance"]);
    assert.match(v.failures[0].why, /live=true/);
  });

  it("fails an every-run job that skipped on a non-draft run", () => {
    const v = verdict(needs({ thorough: "false", live: "false", checks: "skipped", "thorough-conformance": "skipped", "thorough-ui-bs": "skipped", "nr-conformance": "skipped" }));
    assert.deepEqual(failedJobs(v), ["checks"]);
  });

  for (const [label, outputs] of [
    ["missing", {}],
    ["empty", { thorough: "", live: "false" }],
    ["malformed", { thorough: "True", live: "false" }],
    ["a boolean rather than the string", { thorough: true, live: false }],
  ]) {
    it(`fails when a gate output is ${label}, and does not excuse the skips it would have explained`, () => {
      const v = verdict(
        needs({
          gate: { result: "success", outputs },
          "thorough-conformance": "skipped",
          "thorough-ui-bs": "skipped",
          "nr-conformance": "skipped",
        }),
      );
      assert.equal(v.ok, false);
      assert.ok(failedJobs(v).includes("gate"), renderTable(v));
      assert.match(v.failures.find((f) => f.job === "gate").why, /exactly "true" or "false"/);
      assert.ok(failedJobs(v).includes("thorough-conformance"));
    });
  }

  for (const event of ["schedule", "workflow_dispatch"]) {
    it(`fails a ${event} run whose gate said false`, () => {
      const v = verdict(needs({ thorough: "true", live: "false", "nr-conformance": "skipped" }), event);
      assert.equal(v.ok, false);
      assert.match(v.failures.find((f) => f.job === "gate").why, new RegExp(`${event} run must run everything`));
    });
  }

  it("fails live=true with thorough=false (nothing forces live without thorough)", () => {
    const v = verdict(needs({ thorough: "false", live: "true", "thorough-conformance": "skipped", "thorough-ui-bs": "skipped" }));
    assert.equal(v.ok, false);
    assert.ok(failedJobs(v).includes("gate"));
  });

  it("fails a draft: the gate skipped and nothing ran", () => {
    const all = Object.fromEntries(Object.keys(needs()).map((job) => [job, { result: "skipped", outputs: {} }]));
    const v = verdict(all);
    assert.equal(v.ok, false);
    assert.equal(v.failures[0].job, "gate");
    assert.match(v.failures[0].why, /draft: nothing ran/);
  });

  it("fails a gate that failed, and every skip it leaves unexplained", () => {
    const v = verdict(needs({ gate: { result: "failure", outputs: {} }, "thorough-conformance": "skipped", "thorough-ui-bs": "skipped", "nr-conformance": "skipped" }));
    assert.equal(v.ok, false);
    assert.deepEqual(failedJobs(v), ["gate", "thorough-conformance", "thorough-ui-bs", "nr-conformance"]);
  });

  for (const empty of [{}, null, [], "x"]) {
    it(`fails empty or unusable needs (${JSON.stringify(empty)})`, () => {
      const v = verdict(empty);
      assert.equal(v.ok, false);
      assert.match(v.failures[0].why, /no needs/);
    });
  }

  it("fails when the gate job is not among the needs", () => {
    const v = verdict(needs({ gate: undefined }));
    assert.equal(v.ok, false);
    assert.ok(failedJobs(v).includes("gate"));
  });

  it("fails when a job the data file names is not among the needs", () => {
    const v = verdict(needs({ "thorough-ui-bs": undefined }));
    assert.deepEqual(failedJobs(v), ["thorough-ui-bs"]);
    assert.match(v.failures[0].why, /ci-gate does not need it/);
  });

  it("reads the real scripts/ci-gate.json", () => {
    const v = verdict(needs(), "pull_request", config);
    assert.equal(v.ok, true, renderTable(v));
  });
});

describe("ci-gate.mjs (process)", () => {
  function run(env) {
    const dir = mkdtempSync(join(tmpdir(), "ci-gate-"));
    try {
      const output = join(dir, "output");
      const summary = join(dir, "summary");
      writeFileSync(output, "");
      writeFileSync(summary, "");
      let status = 0;
      let stdout = "";
      try {
        stdout = execFileSync(process.execPath, [SCRIPT], {
          env: { ...process.env, GITHUB_OUTPUT: output, GITHUB_STEP_SUMMARY: summary, ...env },
          encoding: "utf8",
          // Piped, not inherited: the child prints ::error:: lines, which on a runner would annotate this
          // step with errors that belong to a deliberately failing case.
          stdio: "pipe",
        });
      } catch (err) {
        status = err.status;
        stdout = err.stdout + err.stderr;
      }
      return { status, stdout, output: readFileSync(output, "utf8"), summary: readFileSync(summary, "utf8") };
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  }

  it("exits 0 and writes failed=false on a green run", () => {
    const r = run({ NEEDS: JSON.stringify(needs()), EVENT: "pull_request" });
    assert.equal(r.status, 0, r.stdout);
    assert.equal(r.output, "failed=false\nfailed-jobs=[]\n");
    assert.match(r.summary, /ci-gate: passed/);
  });

  it("exits 1 and lists the failed jobs as JSON for the scheduled-failure issue", () => {
    const r = run({ NEEDS: JSON.stringify(needs({ docker: "failure" })), EVENT: "schedule" });
    assert.equal(r.status, 1);
    const [failedLine, listLine] = r.output.trim().split("\n");
    assert.equal(failedLine, "failed=true");
    assert.deepEqual(JSON.parse(listLine.slice("failed-jobs=".length)), ["docker: failure"]);
    assert.match(r.stdout, /::error::ci-gate: docker: failure/);
  });

  it("exits 1 on NEEDS that is not JSON", () => {
    const r = run({ NEEDS: "{not json", EVENT: "pull_request" });
    assert.equal(r.status, 1);
    assert.match(r.stdout, /NEEDS is not valid JSON/);
    assert.match(r.output, /^failed=true\n/);
  });
});
