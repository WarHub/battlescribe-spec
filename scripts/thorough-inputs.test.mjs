import { describe, it } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import {
  CONFIG_PATH,
  decide,
  entryKind,
  globToRegExp,
  loadConfig,
  matchesEntry,
  matchInputs,
  parseLabels,
  parsePrFiles,
  renderSummary,
} from "./thorough-inputs.mjs";

const SCRIPT = fileURLToPath(new URL("./thorough-inputs.mjs", import.meta.url));
const REPO_ROOT = dirname(dirname(SCRIPT));
const config = loadConfig();
const inputs = config.thoroughInputs;

/** Every file in the working tree git would consider part of the repo (tracked, or new and not ignored). */
function repoFiles() {
  return execFileSync("git", ["ls-files", "--cached", "--others", "--exclude-standard"], {
    cwd: REPO_ROOT,
    encoding: "utf8",
    maxBuffer: 64 * 1024 * 1024,
  })
    .split("\n")
    .map((l) => l.trim())
    .filter(Boolean);
}

const files = repoFiles();

function pr({ base = "main", labels = [], changed = [] } = {}) {
  return decide({
    event: "pull_request",
    labels,
    baseRef: base,
    defaultBranch: "main",
    changedFiles: changed,
    inputs,
  });
}

// ─── the data file ──────────────────────────────────────────

describe("scripts/ci-gate.json", () => {
  it("lists inputs, thorough jobs and live jobs", () => {
    assert.ok(inputs.length > 0);
    assert.ok(config.thoroughJobs.length > 0);
    assert.ok(config.liveJobs.length > 0);
  });

  it("names no job as both thorough and live", () => {
    const both = config.thoroughJobs.filter((j) => config.liveJobs.includes(j));
    assert.deepEqual(both, []);
  });

  // A dead entry is the quiet failure: rename a file and its entry keeps "forcing" on a path no PR can
  // touch, while the file under its new name forces nothing. Every entry must still match something.
  it("every input matches at least one file in the repo (non-vacuity)", () => {
    assert.ok(files.length > 100, `git ls-files returned only ${files.length} files — is this a checkout?`);
    const dead = inputs.filter((e) => !files.some((f) => matchesEntry(f, e.path))).map((e) => e.path);
    assert.deepEqual(
      dead,
      [],
      `these thoroughInputs entries match no file in the repo: ${dead.join(", ")}. ` +
        "Fix the path, or delete the entry if the input is gone (CONFIG: " +
        CONFIG_PATH +
        ")",
    );
  });
});

// ─── matching ───────────────────────────────────────────────

describe("matchesEntry", () => {
  it("classifies entries", () => {
    assert.equal(entryKind("testdata.json"), "exact");
    assert.equal(entryKind(".github/workflows/"), "prefix");
    assert.equal(entryKind("tests/**/*.csproj"), "glob");
  });

  it("exact paths match only themselves", () => {
    assert.ok(matchesEntry("global.json", "global.json"));
    assert.ok(!matchesEntry("src/global.json", "global.json"));
    assert.ok(!matchesEntry("global.json.bak", "global.json"));
  });

  it("prefixes match below the directory, not a sibling with the same stem", () => {
    assert.ok(matchesEntry(".github/workflows/ci.yml", ".github/workflows/"));
    assert.ok(matchesEntry(".github/workflows/sub/x.yml", ".github/workflows/"));
    assert.ok(!matchesEntry(".github/workflows-old/ci.yml", ".github/workflows/"));
    assert.ok(!matchesEntry(".github/dependabot.yml", ".github/workflows/"));
  });

  it("a double star spans zero or more directories; a single star stays in one segment", () => {
    assert.ok(matchesEntry("tests/xunit.runner.json", "tests/**/xunit.runner.json"));
    assert.ok(matchesEntry("tests/A/B/xunit.runner.json", "tests/**/xunit.runner.json"));
    assert.ok(matchesEntry("tests/A.csproj", "tests/**/*.csproj"));
    assert.ok(matchesEntry("tests/A/A.csproj", "tests/**/*.csproj"));
    assert.ok(!matchesEntry("tests/A.csproj.user", "tests/**/*.csproj"));
    assert.ok(!matchesEntry("src/A/A.csproj", "tests/**/*.csproj"));
    assert.ok(!matchesEntry("tests/a/b.csproj", "tests/*.csproj"));
    assert.ok(matchesEntry("tests/b.csproj", "tests/*.csproj"));
  });

  it("escapes regex metacharacters in globs", () => {
    assert.ok(globToRegExp("a.b/*.c").test("a.b/x.c"));
    assert.ok(!globToRegExp("a.b/*.c").test("aXb/x.c"));
  });

  it("matchInputs reports each matching file once, with the entry it matched", () => {
    const matches = matchInputs(["README.md", "global.json", ".github/workflows/ci.yml"], inputs);
    assert.deepEqual(
      matches.map((m) => [m.file, m.entry.path]),
      [
        ["global.json", "global.json"],
        [".github/workflows/ci.yml", ".github/workflows/"],
      ],
    );
  });
});

// ─── the decision ───────────────────────────────────────────

describe("decide", () => {
  for (const entry of inputs) {
    it(`an edit to ${entry.path} forces thorough and live on a PR against the default branch`, () => {
      const file = files.find((f) => matchesEntry(f, entry.path));
      assert.ok(file, `no repo file matches ${entry.path}`);
      const d = pr({ changed: [file] });
      assert.equal(d.thorough, true);
      assert.equal(d.live, true);
      assert.ok(d.reasons.some((r) => r.reason.includes(file) && r.reason.includes(entry.why)));
    });
  }

  it("a stack PR forces thorough but not live", () => {
    const d = pr({ base: "claude/mtp-l1-ci-gate", changed: ["README.md"] });
    assert.equal(d.thorough, true);
    assert.equal(d.live, false);
    assert.match(d.reasons[0].reason, /stack PR/);
  });

  it("an input edited on a stack PR still forces thorough only", () => {
    const d = pr({ base: "claude/mtp-l1-ci-gate", changed: [".github/workflows/ci.yml"] });
    assert.equal(d.thorough, true);
    assert.equal(d.live, false);
    assert.equal(d.reasons.length, 2);
  });

  it("the thorough-ci label forces both", () => {
    const d = pr({ labels: ["area: ci", "thorough-ci"], changed: ["README.md"] });
    assert.equal(d.thorough, true);
    assert.equal(d.live, true);
  });

  it("the label forces both on a stack PR too", () => {
    const d = pr({ base: "claude/mtp-l5b", labels: ["thorough-ci"] });
    assert.equal(d.thorough, true);
    assert.equal(d.live, true);
  });

  for (const event of ["schedule", "workflow_dispatch"]) {
    it(`${event} forces both`, () => {
      const d = decide({ event, inputs });
      assert.equal(d.thorough, true);
      assert.equal(d.live, true);
    });
  }

  it("an unrelated file on a PR against the default branch forces neither", () => {
    const d = pr({ changed: ["README.md", "docs/telemetry.md", "specs/roster/cost/x.yaml", "src/BattleScribeSpec.Cli/Program.cs"] });
    assert.equal(d.thorough, false);
    assert.equal(d.live, false);
    assert.deepEqual(d.reasons, []);
  });

  it("a push forces neither (the [nr-test] commit-message trigger is gone)", () => {
    const d = decide({ event: "push", labels: [], inputs });
    assert.equal(d.thorough, false);
    assert.equal(d.live, false);
  });

  it("refuses a PR run without the changed-file list", () => {
    assert.throws(
      () => decide({ event: "pull_request", baseRef: "main", defaultBranch: "main", inputs }),
      /changed-file list/,
    );
  });

  it("refuses a PR run without the base or default branch", () => {
    assert.throws(
      () => decide({ event: "pull_request", baseRef: "", defaultBranch: "main", changedFiles: [], inputs }),
      /BASE_REF and DEFAULT_BRANCH/,
    );
  });

  it("refuses to guess the event", () => {
    assert.throws(() => decide({ event: "", inputs }), /EVENT is not set/);
  });
});

describe("parseLabels", () => {
  it("reads a JSON array", () => assert.deepEqual(parseLabels('["a","thorough-ci"]'), ["a", "thorough-ci"]));
  it("treats a missing label list as empty", () => {
    for (const raw of [undefined, null, "", "null", "[]"]) assert.deepEqual(parseLabels(raw), []);
  });
  it("refuses anything that is not an array", () => assert.throws(() => parseLabels('"thorough-ci"'), /JSON array/));
});

describe("parsePrFiles", () => {
  const page = (...entries) => entries.map((e) => (typeof e === "string" ? { filename: e, status: "modified" } : e));

  it("flattens the slurped pages", () => {
    assert.deepEqual(parsePrFiles(JSON.stringify([page("a"), page("b", "c")])), ["a", "b", "c"]);
  });

  it("accepts a single unslurped page", () => {
    assert.deepEqual(parsePrFiles(JSON.stringify(page("a", "b"))), ["a", "b"]);
  });

  it("reads both paths of a rename", () => {
    const renamed = { filename: "docs/x.runsettings", previous_filename: "tests/test-profiles/x.runsettings", status: "renamed" };
    assert.deepEqual(parsePrFiles(JSON.stringify([page(renamed)])), ["docs/x.runsettings", "tests/test-profiles/x.runsettings"]);
  });

  it("a rename out of an input path forces thorough", () => {
    const moved = { filename: "docs/ci.yml", previous_filename: ".github/workflows/ci.yml", status: "renamed" };
    const result = pr({ changed: parsePrFiles(JSON.stringify([page(moved)])) });
    assert.equal(result.thorough, true);
    assert.ok(result.reasons.some((r) => r.reason.includes("`.github/workflows/ci.yml`")));
  });

  it("an empty PR touches nothing", () => {
    assert.deepEqual(parsePrFiles("[[]]"), []);
    assert.deepEqual(parsePrFiles("[]"), []);
  });

  it("refuses a plain file list, a non-array and an entry without a filename", () => {
    assert.throws(() => parsePrFiles("global.json\nsetup.ps1\n"), /not JSON/);
    assert.throws(() => parsePrFiles('{"filename":"a"}'), /must be a JSON array/);
    assert.throws(() => parsePrFiles(JSON.stringify([[{ status: "added" }]])), /no 'filename'/);
  });
});

describe("renderSummary", () => {
  it("says what would force the suites when nothing did", () => {
    assert.match(renderSummary(pr()), /Nothing forces/);
  });
  it("lists each reason with what it forces", () => {
    const text = renderSummary(pr({ base: "stack", changed: ["global.json"] }));
    assert.match(text, /thorough=true, live=false/);
    assert.match(text, /\| thorough \| stack PR/);
    assert.match(text, /`global.json`/);
  });
});

// ─── the script as CI runs it ───────────────────────────────

describe("thorough-inputs.mjs (process)", () => {
  // `changed` is file names or API file objects, written as one slurped page: the shape the gate's
  // `gh api --paginate --slurp` step writes. A string is written verbatim, to hand the script a wrong shape.
  function run(env, changed) {
    const dir = mkdtempSync(join(tmpdir(), "thorough-inputs-"));
    try {
      const list = join(dir, "pr-files.json");
      const output = join(dir, "output");
      const summary = join(dir, "summary");
      writeFileSync(
        list,
        typeof changed === "string"
          ? changed
          : JSON.stringify([changed.map((c) => (typeof c === "string" ? { filename: c, status: "modified" } : c))]),
      );
      writeFileSync(output, "");
      writeFileSync(summary, "");
      execFileSync(process.execPath, [SCRIPT, list], {
        env: { ...process.env, GITHUB_OUTPUT: output, GITHUB_STEP_SUMMARY: summary, ...env },
        encoding: "utf8",
        // Piped, not inherited: the child prints ::error:: lines, which on a runner would annotate this
        // step with errors that belong to a deliberately failing case.
        stdio: "pipe",
      });
      return { output: readFileSync(output, "utf8"), summary: readFileSync(summary, "utf8") };
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  }

  // ci-gate rejects anything but the exact strings, so the exact bytes are the contract.
  it("writes exactly thorough=<bool> and live=<bool> to GITHUB_OUTPUT", () => {
    const { output, summary } = run(
      { EVENT: "pull_request", LABELS: "[]", BASE_REF: "claude/x", DEFAULT_BRANCH: "main" },
      [".github/workflows/ci.yml"],
    );
    assert.equal(output, "thorough=true\nlive=false\n");
    assert.match(summary, /\.github\/workflows\/ci\.yml/);
  });

  it("needs no changed-file list off a PR", () => {
    const { output } = run({ EVENT: "schedule", LABELS: "null", BASE_REF: "", DEFAULT_BRANCH: "main" }, []);
    assert.equal(output, "thorough=true\nlive=true\n");
  });

  it("fails the step when the PR context is missing", () => {
    assert.throws(
      () => run({ EVENT: "pull_request", LABELS: "[]", BASE_REF: "", DEFAULT_BRANCH: "" }, ["README.md"]),
      (err) => err.status === 1 && /BASE_REF and DEFAULT_BRANCH/.test(err.stderr),
    );
  });

  // The case the old `--jq '.[].filename'` list missed: a file moved out of an input path.
  it("forces thorough when a PR to main renames a file out of an input path", () => {
    const { output, summary } = run(
      { EVENT: "pull_request", LABELS: "[]", BASE_REF: "main", DEFAULT_BRANCH: "main" },
      [{ filename: "docs/old-profiles.cs", previous_filename: "tests/TestProfiles/TestProfiles.cs", status: "renamed" }],
    );
    assert.equal(output, "thorough=true\nlive=true\n");
    assert.match(summary, /tests\/TestProfiles\/TestProfiles\.cs/);
  });

  it("fails the step on a plain file list instead of the API response", () => {
    assert.throws(
      () => run({ EVENT: "pull_request", LABELS: "[]", BASE_REF: "main", DEFAULT_BRANCH: "main" }, "global.json\n"),
      (err) => err.status === 1 && /not JSON/.test(err.stderr),
    );
  });
});
