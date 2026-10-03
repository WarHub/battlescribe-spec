#!/usr/bin/env node
// Decides whether this CI run owes the thorough suites and the live NR lane. Run by the `gate` job of
// .github/workflows/ci.yml:
//
//   node scripts/thorough-inputs.mjs pr-files.json
//
// with EVENT, LABELS, BASE_REF and DEFAULT_BRANCH in the environment. pr-files.json is the PR-files API
// response as `gh api --paginate --slurp` writes it (see parsePrFiles). Writes `thorough=` and `live=`
// (each exactly `true` or `false`) to $GITHUB_OUTPUT and every reason to $GITHUB_STEP_SUMMARY.
//
// TWO OUTPUTS, BECAUSE THEY COST DIFFERENT PEOPLE. `thorough` spends our runner minutes on the full
// offline and frozen suites; `live` spends a volunteer-run website's capacity (nr-conformance drives
// newrecruit.eu). Every PR in a stack must run thorough on its own — each layer has to be green alone —
// but a stack of ten PRs forcing live would put ten concurrent conformance runs on that site on every
// restack, which is exactly the parallel live traffic the load budget exists to prevent. So a stack PR
// forces thorough and not live, and the label (one PR at a time) remains the way to ask for live.
//
// WHY A BASE OTHER THAN THE DEFAULT BRANCH FORCES THOROUGH. A stack PR's own diff is one layer, and
// most layers touch nothing on the input list. Label-only coverage fails open the moment a label is
// forgotten, and ci-gate accepts a skipped thorough job whenever this script says false — so before
// this rule, a forgotten label was a green stack that had never run the suites it changes.
//
// The input list lives in scripts/ci-gate.json, not here: the gate, ci-gate and the C# lints all read
// the same file, and the node test asserts every entry still matches a tracked file.

import { appendFileSync, readFileSync } from "node:fs";
import { fileURLToPath, pathToFileURL } from "node:url";

export const CONFIG_PATH = fileURLToPath(new URL("./ci-gate.json", import.meta.url));

/** The label that asks for both the thorough suites and the live lane. */
export const THOROUGH_LABEL = "thorough-ci";

/** Events that always run everything: the weekly drift check and a deliberate manual run. */
export const FULL_EVENTS = Object.freeze(["schedule", "workflow_dispatch"]);

export function loadConfig(path = CONFIG_PATH) {
  const config = JSON.parse(readFileSync(path, "utf8"));
  for (const key of ["thoroughInputs", "thoroughJobs", "liveJobs"]) {
    if (!Array.isArray(config[key]) || config[key].length === 0) {
      throw new Error(`${path}: '${key}' must be a non-empty array`);
    }
  }
  for (const entry of config.thoroughInputs) {
    if (typeof entry?.path !== "string" || entry.path === "" || typeof entry?.why !== "string" || entry.why === "") {
      throw new Error(`${path}: every thoroughInputs entry needs a non-empty 'path' and 'why' (got ${JSON.stringify(entry)})`);
    }
  }
  return config;
}

/** `prefix` for a trailing slash, `glob` for anything with a `*` or `?`, otherwise `exact`. */
export function entryKind(path) {
  if (path.endsWith("/")) return "prefix";
  if (/[*?]/.test(path)) return "glob";
  return "exact";
}

// A glob as an anchored regex over repo-relative, forward-slash paths. A double star followed by a
// slash spans zero or more directories (tests/**/x matches tests/x and tests/a/b/x); `*` and `?` stay
// inside one path segment.
export function globToRegExp(glob) {
  let source = "";
  for (let i = 0; i < glob.length; i++) {
    const c = glob[i];
    if (c === "*" && glob[i + 1] === "*") {
      if (glob[i + 2] === "/") {
        source += "(?:.*/)?";
        i += 2;
      } else {
        source += ".*";
        i += 1;
      }
    } else if (c === "*") {
      source += "[^/]*";
    } else if (c === "?") {
      source += "[^/]";
    } else {
      source += c.replace(/[.+^${}()|[\]\\]/g, "\\$&");
    }
  }
  return new RegExp(`^${source}$`);
}

export function matchesEntry(file, path) {
  switch (entryKind(path)) {
    case "prefix":
      return file.startsWith(path);
    case "glob":
      return globToRegExp(path).test(file);
    default:
      return file === path;
  }
}

/**
 * The paths a PR touches, from the PR-files API (`GET /repos/{owner}/{repo}/pulls/{n}/files`) as
 * `gh api --paginate --slurp` writes it: an array of pages, each an array of file objects. A single
 * unslurped page is accepted too.
 *
 * A RENAME TOUCHES TWO PATHS. The API gives a renamed file's new path in `filename` and its old one in
 * `previous_filename`; a list of `filename`s alone drops the old one, so moving a test profile or a
 * workflow OUT of an input path forced nothing — and a move-out is the realistic way to drop one. Both
 * are returned. Anything that is not this shape throws: a projection like `--jq '.[].filename'` would
 * otherwise read as "no input changed", and that is the silent direction.
 */
export function parsePrFiles(text) {
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch (err) {
    throw new Error(`the PR-files list is not JSON (expected \`gh api …/files --paginate --slurp\` output): ${err.message}`);
  }
  if (!Array.isArray(parsed)) throw new Error("the PR-files list must be a JSON array (of pages, or of files)");

  const entries = parsed.every(Array.isArray) ? parsed.flat() : parsed;
  const paths = [];
  for (const entry of entries) {
    if (typeof entry?.filename !== "string" || entry.filename === "") {
      throw new Error(`a PR-files entry has no 'filename': ${JSON.stringify(entry)?.slice(0, 200)}`);
    }
    paths.push(entry.filename);
    if (typeof entry.previous_filename === "string" && entry.previous_filename !== "") {
      paths.push(entry.previous_filename);
    }
  }
  return paths;
}

/** Every changed file that matches an input, with the first entry it matched. */
export function matchInputs(changedFiles, inputs) {
  const matches = [];
  for (const file of changedFiles) {
    const entry = inputs.find((e) => matchesEntry(file, e.path));
    if (entry) matches.push({ file, entry });
  }
  return matches;
}

/**
 * `toJSON(github.event.pull_request.labels.*.name)` is a JSON array on a PR. Off a PR the property
 * chain is null, which arrives as `null`, `[]` or nothing depending on the runner; all mean "none".
 */
export function parseLabels(raw) {
  if (raw === undefined || raw === null) return [];
  const text = String(raw).trim();
  if (text === "" || text === "null") return [];
  const parsed = JSON.parse(text);
  if (parsed === null) return [];
  if (!Array.isArray(parsed)) throw new Error(`LABELS must be a JSON array, got ${text}`);
  return parsed.map(String);
}

/**
 * The decision. Each reason says which outputs it forces; an output is true when any reason forces it.
 * `changedFiles` is required on a pull_request event and ignored otherwise.
 */
export function decide({ event, labels = [], baseRef, defaultBranch, changedFiles, inputs }) {
  if (!event) throw new Error("EVENT is not set: the gate cannot tell what kind of run this is");

  const reasons = [];

  if (FULL_EVENTS.includes(event)) {
    reasons.push({ thorough: true, live: true, reason: `${event} run` });
  }

  if (event === "pull_request") {
    if (!Array.isArray(changedFiles)) {
      throw new Error("a pull_request run needs the PR's changed-file list (the `inputs` step writes it)");
    }
    if (!baseRef || !defaultBranch) {
      throw new Error(
        `a pull_request run needs BASE_REF and DEFAULT_BRANCH (got '${baseRef ?? ""}' and '${defaultBranch ?? ""}')`,
      );
    }

    if (labels.includes(THOROUGH_LABEL)) {
      reasons.push({ thorough: true, live: true, reason: `the \`${THOROUGH_LABEL}\` label` });
    }

    const againstDefault = baseRef === defaultBranch;
    if (!againstDefault) {
      reasons.push({
        thorough: true,
        live: false,
        reason: `stack PR: base branch \`${baseRef}\` is not the default branch \`${defaultBranch}\``,
      });
    }

    for (const { file, entry } of matchInputs(changedFiles, inputs)) {
      reasons.push({
        thorough: true,
        live: againstDefault,
        reason: `\`${file}\` (matches \`${entry.path}\`: ${entry.why})`,
      });
    }
  }

  return {
    thorough: reasons.some((r) => r.thorough),
    live: reasons.some((r) => r.live),
    reasons,
  };
}

function forcesLabel(r) {
  return [r.thorough && "thorough", r.live && "live"].filter(Boolean).join(" + ");
}

export function renderSummary({ thorough, live, reasons }) {
  const lines = [`### Gate: thorough=${thorough}, live=${live}`, ""];
  if (reasons.length === 0) {
    lines.push(
      "Nothing forces the thorough suites or the live lane on this run. " +
        `The \`${THOROUGH_LABEL}\` label asks for both; the inputs that force them are listed in \`scripts/ci-gate.json\`.`,
    );
  } else {
    lines.push("| Forces | Why |", "|---|---|");
    for (const r of reasons) {
      lines.push(`| ${forcesLabel(r)} | ${r.reason.replace(/\|/g, "\\|")} |`);
    }
  }
  return lines.join("\n") + "\n";
}

function main(argv, env) {
  const event = env.EVENT;
  let changedFiles;
  if (event === "pull_request") {
    const listPath = argv[0];
    if (!listPath) throw new Error("usage: thorough-inputs.mjs <pr-files.json>");
    changedFiles = parsePrFiles(readFileSync(listPath, "utf8"));
    console.log(`This PR touches ${changedFiles.length} path(s), old paths of renames included.`);
  }

  const result = decide({
    event,
    labels: parseLabels(env.LABELS),
    baseRef: env.BASE_REF,
    defaultBranch: env.DEFAULT_BRANCH,
    changedFiles,
    inputs: loadConfig().thoroughInputs,
  });

  for (const r of result.reasons) console.log(`forces ${forcesLabel(r)}: ${r.reason}`);
  console.log(`thorough=${result.thorough}`);
  console.log(`live=${result.live}`);

  if (env.GITHUB_OUTPUT) {
    appendFileSync(env.GITHUB_OUTPUT, `thorough=${result.thorough}\nlive=${result.live}\n`);
  }
  if (env.GITHUB_STEP_SUMMARY) {
    appendFileSync(env.GITHUB_STEP_SUMMARY, renderSummary(result));
  }
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isMain) {
  try {
    main(process.argv.slice(2), process.env);
  } catch (err) {
    console.error(`::error::thorough-inputs: ${err.message}`);
    process.exit(1);
  }
}
