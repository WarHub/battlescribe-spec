#!/usr/bin/env node
// Mutation checks for guards: break the guarded thing on purpose, run the guard, see it go red, put the
// file back. Run from the checkout root:
//
//   node tools/mutate.mjs <mutations.json> [name-substring]
//
// mutations.json is a list of { "name", "file", "find", "replace", "filter" }: `find` must occur exactly
// once in `file`, and `filter` is the --filter expression that selects the guard's tests. Optional:
// "project" (default tests/BattleScribeSpec.Tests.csproj), and "build": true for a file the build reads
// that is not C# or MSBuild (an embedded resource). A mutation is KILLED when the filtered run fails a
// test (exit 2). Anything else — the guard passed, the filter selected no test (exit 8), the mutant did
// not compile, another exit code — is named, and makes the batch exit 1.
//
// A text guard (a lint over YAML, Markdown, JSON or MSBuild files) is better proven by a broken sample in
// the lint itself (tests/Infrastructure/LintSamples.cs), which keeps the proof as a test. This is for what
// a sample cannot reach, chiefly C# guard logic — which Stryker.NET cannot mutate: it mutates only
// projects that are not test projects, and the guards live in the test projects (#529).
//
// SAFE WITH UNCOMMITTED WORK. A file is restored from a byte copy taken before the mutation, never from
// git: `git checkout -- <file>` restores the index copy and discards everything else in the file. The
// copy is also written to artifacts/mutate/ first, so a run killed mid-mutation leaves it recoverable, and
// the next run refuses to start until it has been restored.
//
// BUILDS. One build (-p:FunctionalBuild=true) before the batch, and one baseline run of every filter on
// the unmutated tree: a guard that is red already proves nothing by going red. A mutation the build does
// not read runs against that build (--no-build); a code mutation costs one build of its own; and a batch
// that built a mutant builds once more at the end, so the binaries left behind are the source's.

import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { extname, join } from "node:path";

const OUT = "artifacts/mutate";
const DEFAULT_PROJECT = "tests/BattleScribeSpec.Tests.csproj";
const BUILT = new Set([".cs", ".csproj", ".props", ".targets"]);
const VERDICTS = { 2: "KILLED", 0: "SURVIVED (the guard passed)", 8: "SELECTED NO TEST (exit 8: fix the filter)" };

const fail = (message) => {
  console.error(`mutate: ${message}`);
  process.exit(1);
};
const run = (args) => spawnSync("dotnet", args, { encoding: "utf8", maxBuffer: 256 * 1024 * 1024 });
const build = (project) => run(["build", project, "-p:FunctionalBuild=true"]);
const test = (project, filter) => run(["run", "--project", project, "--no-build", "--", "--filter", filter]);
const output = (r) => `${r.stdout ?? ""}${r.stderr ?? ""}`;
const backupOf = (file) => join(OUT, `${file.replaceAll(/[\\/]/g, "__")}.orig`);

const [listPath, only] = process.argv.slice(2);
if (!listPath) fail("usage: node tools/mutate.mjs <mutations.json> [name-substring]");
if (!existsSync("BattleScribeSpec.slnx")) fail("run from the checkout root");
mkdirSync(OUT, { recursive: true });
const leftover = readdirSync(OUT).filter((f) => f.endsWith(".orig"));
if (leftover.length > 0) {
  fail(`a run did not finish. Each of these is the original of the file its name spells (__ for /): copy it back, `
    + `delete it, then run again:\n  ${leftover.map((f) => join(OUT, f)).join("\n  ")}`);
}

// Checked before anything is touched. Text mutations first: they all run against the first build.
const mutations = JSON.parse(readFileSync(listPath, "utf8"))
  .filter((m) => !only || m.name.includes(only))
  .map((m) => ({ project: DEFAULT_PROJECT, ...m, build: m.build ?? BUILT.has(extname(m.file).toLowerCase()) }))
  .sort((a, b) => a.build - b.build);
if (mutations.length === 0) fail(`no mutation in ${listPath}${only ? ` matches '${only}'` : ""}`);
for (const m of mutations) {
  for (const key of ["name", "file", "find", "replace", "filter"]) {
    if (typeof m[key] !== "string") fail(`'${m.name ?? "?"}' has no string '${key}'`);
  }
  const count = readFileSync(m.file, "utf8").split(m.find).length - 1;
  if (count !== 1) fail(`'${m.name}': find occurs ${count} times in ${m.file}, not once`);
}

const projects = [...new Set(mutations.map((m) => m.project))];
for (const project of projects) {
  const built = build(project);
  if (built.status !== 0) fail(`${project} does not build:\n${output(built)}`);
  const union = mutations.filter((m) => m.project === project).map((m) => `(${m.filter})`).join("|");
  const baseline = test(project, union);
  if (baseline.status !== 0) fail(`the unmutated baseline is not green (exit ${baseline.status}):\n${output(baseline)}`);
}

let builtMutant = false;
let aborted = false;
const results = [];
for (const m of mutations) {
  const original = readFileSync(m.file);
  const backup = backupOf(m.file);
  writeFileSync(backup, original);
  writeFileSync(m.file, original.toString("utf8").replace(m.find, () => m.replace));
  let verdict;
  let log = "";
  try {
    if (m.build) {
      builtMutant = true;
      const built = build(m.project);
      log += output(built);
      if (built.status !== 0) verdict = "DID NOT COMPILE";
    }
    if (!verdict) {
      const r = test(m.project, m.filter);
      log += output(r);
      aborted = r.status === null;
      verdict = VERDICTS[r.status] ?? `EXIT ${r.status ?? r.signal}`;
    }
  } finally {
    writeFileSync(m.file, original);
    if (!readFileSync(m.file).equals(original)) fail(`could not restore ${m.file}; its original is ${backup}`);
    rmSync(backup);
  }
  const logFile = join(OUT, `${m.name.replaceAll(/[^\w.-]+/g, "-")}.log`);
  writeFileSync(logFile, log);
  const failed = [...log.matchAll(/^\s*failed (\S+)/gm)].map((x) => x[1]);
  results.push({ name: m.name, killed: verdict === "KILLED" });
  console.log(`${verdict.padEnd(8)} ${m.name}${failed.length ? ` — ${failed.join(", ")}` : ""}  (${logFile})`);
  if (aborted) break;
}

if (builtMutant) {
  for (const project of projects) {
    if (build(project).status !== 0) console.error(`mutate: ${project} did not rebuild from the restored source; build it before trusting a --no-build run`);
  }
}
const survivors = results.filter((r) => !r.killed);
console.log(`\n${results.length - survivors.length} of ${results.length} killed${aborted ? " (stopped: interrupted)" : ""}.`);
process.exitCode = survivors.length > 0 || aborted ? 1 : 0;
