#!/usr/bin/env node

// har-diff.mjs — Compare two NR HAR snapshots and produce a markdown summary.
// Usage: node scripts/har-diff.mjs --old <path> --new <path> [options]
//
// Options:
//   --old <path>          Path to old HAR file
//   --new <path>          Path to new HAR file
//   --old-version <ver>   Old client version (for news filtering)
//   --new-version <ver>   New client version
//   --news-url <url>      URL to scrape for news (default: https://www.newrecruit.eu/news)
//   --no-news             Skip news scraping
//   -h, --help            Show help

import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";
import { get as httpsGet } from "node:https";
import { get as httpGet } from "node:http";

// --- CLI state (populated only when run as main) ---

let oldPath, newPath, oldVersion, newVersion;
let newsUrl = "https://www.newrecruit.eu/news";
let skipNews = false;

// --- HAR parsing helpers ---

function classifyUrl(url) {
  try {
    const u = new URL(url);
    const path = u.pathname.toLowerCase();
    if (path.startsWith("/_nuxt/") && path.endsWith(".js")) return "js";
    if (path.startsWith("/_nuxt/") && path.endsWith(".css")) return "css";
    if (path.endsWith(".js")) return "js";
    if (path.endsWith(".css")) return "css";
    if (
      path.endsWith(".woff2") ||
      path.endsWith(".woff") ||
      path.endsWith(".ttf")
    )
      return "font";
    if (
      path.endsWith(".png") ||
      path.endsWith(".jpg") ||
      path.endsWith(".gif") ||
      path.endsWith(".svg") ||
      path.endsWith(".ico")
    )
      return "image";
    if (path.includes("/api/")) return "api";
    if (path.endsWith(".html") || path === "/" || path === "/app")
      return "html";
    if (!path.includes(".")) return "html";
    return "other";
  } catch {
    return "other";
  }
}

/**
 * Nuxt content-hashed assets: ComponentName.HASH.ext or HASH.ext
 * Returns the stable component name, or null for anonymous chunks.
 */
function extractComponentName(url) {
  try {
    const u = new URL(url);
    const filename = u.pathname.split("/").pop();
    // Named: News.DYT3GdjI.css, entry.DKFMTdLi.css
    const named = filename.match(/^(.+)\.[A-Za-z0-9_-]{6,}\.(js|css)$/);
    if (named) return named[1];
    // Anonymous chunk: BsM9VQE9.js
    const anon = filename.match(/^[A-Za-z0-9_-]{6,}\.(js|css)$/);
    if (anon) return null;
    return filename;
  } catch {
    return null;
  }
}

/**
 * Extract Vue component __name values and static import references from JS content.
 * These form a stable fingerprint for matching bundles across hash changes.
 */
function extractBundleFingerprint(text) {
  const names = new Set();
  for (const m of text.matchAll(/__name:\s*"([^"]+)"/g)) {
    names.add(m[1]);
  }
  const imports = new Set();
  for (const m of text.matchAll(/from\s*"\.\/([^"]+\.js)"/g)) {
    imports.add(m[1]);
  }
  const isEntry = text.includes("__vite__mapDeps");
  return { names, imports, isEntry };
}

function parseHar(path) {
  const raw = JSON.parse(readFileSync(path, "utf8"));
  const entries = raw?.log?.entries || [];
  const map = new Map();
  for (const entry of entries) {
    const url = entry.request?.url;
    if (!url) continue;
    const text = entry.response?.content?.text || "";
    const contentSize = text.length;
    const transferSize =
      entry.response?.content?.size ??
      entry.response?.bodySize ??
      contentSize;
    const category = classifyUrl(url);
    const info = {
      url,
      method: entry.request.method,
      status: entry.response?.status,
      contentSize,
      transferSize: Math.max(0, transferSize),
      category,
      component: extractComponentName(url),
      fingerprint: null,
    };
    if (category === "js") {
      info.fingerprint = extractBundleFingerprint(text);
    }
    map.set(url, info);
  }
  return map;
}

function formatSize(bytes) {
  if (bytes >= 1024 * 1024) return (bytes / (1024 * 1024)).toFixed(1) + " MB";
  if (bytes >= 1024) return (bytes / 1024).toFixed(1) + " KB";
  return bytes + " B";
}

function formatDelta(delta) {
  if (delta > 0) return `+${formatSize(delta)}`;
  if (delta < 0) return `\u2212${formatSize(Math.abs(delta))}`;
  return "unchanged";
}

function shortUrl(url) {
  try {
    const u = new URL(url);
    return u.pathname.replace(/^\/_nuxt\//, "");
  } catch {
    return url;
  }
}

function filenameOf(url) {
  try {
    return new URL(url).pathname.split("/").pop();
  } catch {
    return url;
  }
}

/**
 * Build a human-readable label for a JS bundle from its fingerprint.
 * Prefers __name values; falls back to "entry (core)" or null.
 */
function bundleLabel(info) {
  const fp = info.fingerprint;
  if (!fp) return shortUrl(info.url);
  if (fp.isEntry) return "entry (core)";
  if (fp.names.size === 0) return null;
  const sorted = [...fp.names].sort();
  if (sorted.length <= 3) return sorted.join(", ");
  return `${sorted.slice(0, 3).join(", ")} +${sorted.length - 3} more`;
}

/**
 * Compute Jaccard similarity between two sets.
 */
function jaccard(a, b) {
  if (a.size === 0 && b.size === 0) return 0;
  let intersection = 0;
  for (const v of a) {
    if (b.has(v)) intersection++;
  }
  return intersection / (a.size + b.size - intersection);
}

// --- News scraping ---

function fetchUrl(url) {
  return new Promise((resolve, reject) => {
    const getter = url.startsWith("https") ? httpsGet : httpGet;
    getter(url, { timeout: 10_000 }, (res) => {
      if (
        res.statusCode >= 300 &&
        res.statusCode < 400 &&
        res.headers.location
      ) {
        fetchUrl(new URL(res.headers.location, url).href).then(
          resolve,
          reject
        );
        return;
      }
      let data = "";
      res.on("data", (chunk) => (data += chunk));
      res.on("end", () => resolve(data));
      res.on("error", reject);
    }).on("error", reject);
  });
}

function parseNewsPosts(html) {
  const posts = [];
  const parts = html.split(/<h3[^>]*>/i);
  for (let i = 1; i < parts.length; i++) {
    const part = parts[i];
    const titleEnd = part.indexOf("</h3>");
    if (titleEnd === -1) continue;
    const title = part
      .slice(0, titleEnd)
      .replace(/<[^>]+>/g, "")
      .trim();
    const rest = part.slice(titleEnd);
    const dateMatch = rest.match(/class="date"[^>]*>([^<]+)/i);
    const date = dateMatch ? dateMatch[1].trim() : "";
    const body = rest
      .replace(/<[^>]+>/g, " ")
      .replace(/\s+/g, " ")
      .trim()
      .slice(0, 500);
    const versionRefs = [
      ...title.matchAll(/(\d{2,}\.?\d*)/g),
      ...body.matchAll(/version\s+(\d{2,}\.?\d*)/gi),
    ].map((m) => parseFloat(m[1]));
    posts.push({ title, date, body, versionRefs });
  }
  return posts;
}

function filterRelevantPosts(posts, oldVer, newVer) {
  const lo = parseFloat(oldVer);
  const hi = parseFloat(newVer);
  if (isNaN(lo) || isNaN(hi)) return posts.slice(0, 3);
  return posts.filter((p) => p.versionRefs.some((v) => v > lo && v <= hi));
}

// --- JS bundle matching ---

/**
 * Match JS bundles between old and new HAR snapshots using content fingerprints.
 *
 * Matching phases:
 *  1. Exact URL match (hash unchanged)
 *  2. __name fingerprint — exact set match
 *  3. __name fingerprint — best Jaccard overlap (>0.5 threshold)
 *  4. Import-signature match for nameless utility chunks
 *  5. Remaining bundles are unmatched (added/removed)
 */
function matchJsBundles(oldMap, newMap) {
  const oldJs = [...oldMap.values()].filter((e) => e.category === "js");
  const newJs = [...newMap.values()].filter((e) => e.category === "js");

  const matched = [];
  const oldMatched = new Set();
  const newMatched = new Set();

  function markMatch(oldInfo, newInfo) {
    oldMatched.add(oldInfo.url);
    newMatched.add(newInfo.url);
    matched.push({ old: oldInfo, new: newInfo });
  }

  // Phase 1: Exact URL match (same hash — unchanged bundle)
  for (const oldInfo of oldJs) {
    const newInfo = newMap.get(oldInfo.url);
    if (newInfo && newInfo.category === "js") {
      markMatch(oldInfo, newInfo);
    }
  }

  // Phase 2: Exact __name set match
  const unmatchedNew = () => newJs.filter((e) => !newMatched.has(e.url));
  const unmatchedOld = () => oldJs.filter((e) => !oldMatched.has(e.url));

  const newByNameKey = new Map();
  for (const info of unmatchedNew()) {
    const fp = info.fingerprint;
    if (!fp || fp.names.size === 0) continue;
    const key = [...fp.names].sort().join("\0");
    if (!newByNameKey.has(key)) newByNameKey.set(key, []);
    newByNameKey.get(key).push(info);
  }

  for (const oldInfo of unmatchedOld()) {
    if (oldMatched.has(oldInfo.url)) continue;
    const fp = oldInfo.fingerprint;
    if (!fp || fp.names.size === 0) continue;
    const key = [...fp.names].sort().join("\0");
    const candidates = newByNameKey.get(key);
    if (candidates) {
      const pick = candidates.find((c) => !newMatched.has(c.url));
      if (pick) markMatch(oldInfo, pick);
    }
  }

  // Phase 3: Best Jaccard overlap for remaining named bundles
  const stillUnmatchedOld = unmatchedOld().filter(
    (e) => e.fingerprint?.names.size > 0
  );
  const stillUnmatchedNew = unmatchedNew().filter(
    (e) => e.fingerprint?.names.size > 0
  );

  const pairs = [];
  for (const o of stillUnmatchedOld) {
    for (const n of stillUnmatchedNew) {
      const sim = jaccard(o.fingerprint.names, n.fingerprint.names);
      if (sim > 0.5) pairs.push({ old: o, new: n, sim });
    }
  }
  pairs.sort((a, b) => b.sim - a.sim);
  for (const pair of pairs) {
    if (oldMatched.has(pair.old.url) || newMatched.has(pair.new.url)) continue;
    markMatch(pair.old, pair.new);
  }

  // Phase 4: Import-signature match for nameless bundles.
  // Signature = set of labels of matched bundles that import this chunk.
  const oldFilenameToLabel = new Map();
  const newFilenameToLabel = new Map();
  for (const m of matched) {
    const label = bundleLabel(m.new) || bundleLabel(m.old) || "?";
    oldFilenameToLabel.set(filenameOf(m.old.url), label);
    newFilenameToLabel.set(filenameOf(m.new.url), label);
  }

  function importSignature(info, allJs, filenameToLabel) {
    const myFilename = filenameOf(info.url);
    const labels = new Set();
    for (const other of allJs) {
      if (!other.fingerprint) continue;
      if (other.fingerprint.imports.has(myFilename)) {
        const otherFilename = filenameOf(other.url);
        const label = filenameToLabel.get(otherFilename);
        if (label) labels.add(label);
      }
    }
    return labels.size > 0 ? [...labels].sort().join("\0") : null;
  }

  const namelessOld = unmatchedOld().filter(
    (e) => e.fingerprint && e.fingerprint.names.size === 0
  );
  const namelessNew = unmatchedNew().filter(
    (e) => e.fingerprint && e.fingerprint.names.size === 0
  );

  const newBySig = new Map();
  for (const info of namelessNew) {
    const sig = importSignature(info, newJs, newFilenameToLabel);
    if (!sig) continue;
    if (!newBySig.has(sig)) newBySig.set(sig, []);
    newBySig.get(sig).push(info);
  }

  for (const oldInfo of namelessOld) {
    if (oldMatched.has(oldInfo.url)) continue;
    const sig = importSignature(oldInfo, oldJs, oldFilenameToLabel);
    if (!sig) continue;
    const candidates = newBySig.get(sig);
    if (!candidates) continue;
    const available = candidates.filter((c) => !newMatched.has(c.url));
    if (available.length === 0) continue;
    // Pick closest by size among same-signature candidates
    available.sort(
      (a, b) =>
        Math.abs(a.contentSize - oldInfo.contentSize) -
        Math.abs(b.contentSize - oldInfo.contentSize)
    );
    markMatch(oldInfo, available[0]);
  }

  return {
    matched,
    unmatchedOld: unmatchedOld(),
    unmatchedNew: unmatchedNew(),
  };
}

// --- CSS diff (filename-based matching) ---

function computeCssDiff(oldMap, newMap) {
  const changes = [];
  const oldCss = [...oldMap.values()].filter((e) => e.category === "css");
  const newCss = [...newMap.values()].filter((e) => e.category === "css");
  const oldCssMatched = new Set();
  const newCssMatched = new Set();

  const oldByComp = new Map();
  const newByComp = new Map();
  for (const info of oldCss) {
    if (info.component !== null) {
      if (!oldByComp.has(info.component)) oldByComp.set(info.component, []);
      oldByComp.get(info.component).push(info);
    }
  }
  for (const info of newCss) {
    if (info.component !== null) {
      if (!newByComp.has(info.component)) newByComp.set(info.component, []);
      newByComp.get(info.component).push(info);
    }
  }

  for (const component of new Set([...oldByComp.keys(), ...newByComp.keys()])) {
    const oldEntries = oldByComp.get(component) || [];
    const newEntries = newByComp.get(component) || [];
    for (const e of oldEntries) oldCssMatched.add(e.url);
    for (const e of newEntries) newCssMatched.add(e.url);

    if (oldEntries.length > 0 && newEntries.length > 0) {
      const oldUrls = new Set(oldEntries.map((e) => e.url));
      const same = [...oldUrls].every((u) => newEntries.some((n) => n.url === u));
      if (!same) {
        const oldSize = oldEntries.reduce((s, e) => s + e.contentSize, 0);
        const newSize = newEntries.reduce((s, e) => s + e.contentSize, 0);
        changes.push({ type: "changed", name: component, oldSize, newSize });
      }
    } else if (newEntries.length > 0) {
      const size = newEntries.reduce((s, e) => s + e.contentSize, 0);
      changes.push({ type: "added", name: component, newSize: size });
    } else {
      const size = oldEntries.reduce((s, e) => s + e.contentSize, 0);
      changes.push({ type: "removed", name: component, oldSize: size });
    }
  }

  // Anonymous CSS — match by exact URL
  for (const info of oldCss) {
    if (oldCssMatched.has(info.url)) continue;
    oldCssMatched.add(info.url);
    if (!newMap.has(info.url)) {
      changes.push({ type: "removed", name: shortUrl(info.url), oldSize: info.contentSize });
    }
  }
  for (const info of newCss) {
    if (newCssMatched.has(info.url)) continue;
    newCssMatched.add(info.url);
    if (!oldMap.has(info.url)) {
      changes.push({ type: "added", name: shortUrl(info.url), newSize: info.contentSize });
    }
  }
  return changes;
}

// --- Render ---

function renderChangesTable(items, label) {
  if (items.length === 0) return [];
  items.sort((a, b) => {
    const order = { added: 0, changed: 1, removed: 2 };
    return order[a.type] - order[b.type];
  });

  const added = items.filter((i) => i.type === "added").length;
  const changed = items.filter((i) => i.type === "changed").length;
  const removed = items.filter((i) => i.type === "removed").length;
  const countParts = [];
  if (changed) countParts.push(`~${changed} changed`);
  if (added) countParts.push(`+${added} added`);
  if (removed) countParts.push(`-${removed} removed`);
  const countLine = countParts.join(", ");

  const tableLines = [
    "| Change | File | Size |",
    "|--------|------|------|",
  ];
  for (const item of items) {
    const marker =
      item.type === "added"
        ? "\u271a added"
        : item.type === "removed"
          ? "\u2715 removed"
          : "\u25b3 changed";
    let sizeStr;
    if (item.type === "added") sizeStr = formatSize(item.newSize);
    else if (item.type === "removed") sizeStr = formatSize(item.oldSize);
    else
      sizeStr = `${formatSize(item.oldSize)} \u2192 ${formatSize(item.newSize)}`;
    tableLines.push(`| ${marker} | \`${item.name}\` | ${sizeStr} |`);
  }

  const lines = [
    `### ${label} (${countLine})`,
    "",
    `<details><summary>${countLine}</summary>`,
    "",
    ...tableLines,
    "",
    "</details>",
    "",
  ];
  return lines;
}

function renderOtherCategories(oldMap, newMap) {
  const categories = ["font", "image", "api", "html", "other"];
  const labels = {
    font: "fonts",
    image: "images",
    api: "API endpoints",
    html: "HTML pages",
    other: "other resources",
  };
  const summaryLines = [];
  for (const cat of categories) {
    const oldUrls = new Set(
      [...oldMap.values()].filter((e) => e.category === cat).map((e) => e.url)
    );
    const newUrls = new Set(
      [...newMap.values()].filter((e) => e.category === cat).map((e) => e.url)
    );
    if (oldUrls.size === 0 && newUrls.size === 0) continue;
    const added = [...newUrls].filter((u) => !oldUrls.has(u)).length;
    const removed = [...oldUrls].filter((u) => !newUrls.has(u)).length;
    const unchanged = [...newUrls].filter((u) => oldUrls.has(u)).length;
    const parts = [];
    if (added) parts.push(`+${added} added`);
    if (removed) parts.push(`-${removed} removed`);
    if (unchanged) parts.push(`${unchanged} unchanged`);
    if (parts.length > 0)
      summaryLines.push(`- ${parts.join(", ")} ${labels[cat]}`);
  }
  if (summaryLines.length === 0) return [];
  return ["### Other", "", ...summaryLines, ""];
}

// --- Main ---

async function main() {
  const newMap = parseHar(newPath);
  const hasOld = !!oldPath;
  const oldMap = hasOld ? parseHar(oldPath) : new Map();

  const lines = [];

  // Header
  if (hasOld && oldVersion && newVersion && oldVersion !== newVersion) {
    lines.push(`## NR Snapshot: v${oldVersion} \u2192 v${newVersion}`, "");
    lines.push(`**Client version:** ${oldVersion} \u2192 ${newVersion}`);
  } else if (newVersion) {
    lines.push(`## NR Snapshot: v${newVersion}`, "");
    lines.push(`**Client version:** ${newVersion}`);
  } else {
    lines.push(`## NR Snapshot Update`, "");
  }

  // Totals
  const newTotal = [...newMap.values()].reduce(
    (s, e) => s + e.contentSize,
    0
  );
  if (hasOld) {
    const oldTotal = [...oldMap.values()].reduce(
      (s, e) => s + e.contentSize,
      0
    );
    const entryDelta = newMap.size - oldMap.size;
    lines.push(
      `**HAR entries:** ${oldMap.size} \u2192 ${newMap.size} (${entryDelta >= 0 ? "+" : ""}${entryDelta})`
    );
    lines.push(
      `**Total size:** ${formatSize(oldTotal)} \u2192 ${formatSize(newTotal)} (${formatDelta(newTotal - oldTotal)})`
    );
  } else {
    lines.push(`**HAR entries:** ${newMap.size}`);
    lines.push(`**Total size:** ${formatSize(newTotal)}`);
  }
  lines.push("");

  if (!hasOld) {
    lines.push("_No previous snapshot available for comparison._");
  } else {
    // JS diff with fingerprint matching
    const { matched, unmatchedOld, unmatchedNew } = matchJsBundles(oldMap, newMap);

    const jsChanges = [];
    let unchangedNamedCount = 0;
    let unchangedNamedSize = 0;
    let unchangedUtilCount = 0;
    let unchangedUtilSize = 0;

    for (const m of matched) {
      const urlSame = m.old.url === m.new.url;
      const sizeSame = m.old.contentSize === m.new.contentSize;
      const label = bundleLabel(m.new) || bundleLabel(m.old);

      if (urlSame && sizeSame) {
        if (label) {
          unchangedNamedCount++;
          unchangedNamedSize += m.new.contentSize;
        } else {
          unchangedUtilCount++;
          unchangedUtilSize += m.new.contentSize;
        }
        continue;
      }

      const displayName = label || `shared utility (${shortUrl(m.old.url)})`;
      jsChanges.push({
        type: "changed",
        name: displayName,
        oldSize: m.old.contentSize,
        newSize: m.new.contentSize,
      });
    }

    for (const info of unmatchedNew) {
      const label = bundleLabel(info) || shortUrl(info.url);
      jsChanges.push({ type: "added", name: label, newSize: info.contentSize });
    }

    for (const info of unmatchedOld) {
      const label = bundleLabel(info) || shortUrl(info.url);
      jsChanges.push({ type: "removed", name: label, oldSize: info.contentSize });
    }

    lines.push(...renderChangesTable(jsChanges, "JS bundles"));

    // Summary of unchanged bundles
    const unchangedParts = [];
    if (unchangedNamedCount > 0)
      unchangedParts.push(`${unchangedNamedCount} named bundles (${formatSize(unchangedNamedSize)})`);
    if (unchangedUtilCount > 0)
      unchangedParts.push(`${unchangedUtilCount} shared utilities (${formatSize(unchangedUtilSize)})`);
    if (unchangedParts.length > 0) {
      lines.push(`_Unchanged JS: ${unchangedParts.join(", ")}_`, "");
    }

    // CSS diff
    const cssChanges = computeCssDiff(oldMap, newMap);
    lines.push(...renderChangesTable(cssChanges, "CSS"));

    // Other categories
    lines.push(...renderOtherCategories(oldMap, newMap));
  }

  // NR News
  if (!skipNews && oldVersion && newVersion && oldVersion !== newVersion) {
    try {
      const html = await fetchUrl(newsUrl);
      const posts = parseNewsPosts(html);
      const relevant = filterRelevantPosts(posts, oldVersion, newVersion);
      if (relevant.length > 0) {
        lines.push(`### [NR News](${newsUrl})`, "");
        for (const post of relevant.slice(0, 5)) {
          const dateStr = post.date ? ` (${post.date})` : "";
          lines.push(`> **${post.title}**${dateStr}  `);
          lines.push(
            `> ${post.body.slice(0, 300)}${post.body.length > 300 ? "\u2026" : ""}`
          );
          lines.push(`> [Read more\u2026](${newsUrl})`);
          lines.push("");
        }
      }
    } catch {
      // News scraping failed — silently skip
    }
  }

  console.log(lines.join("\n"));
}

// --- Exports for testing ---

export {
  classifyUrl,
  extractComponentName,
  extractBundleFingerprint,
  parseHar,
  formatSize,
  formatDelta,
  bundleLabel,
  jaccard,
  matchJsBundles,
  computeCssDiff,
  renderChangesTable,
  renderOtherCategories,
};

// Run main() only when executed directly (not imported).
const isMain =
  process.argv[1] &&
  import.meta.url === pathToFileURL(process.argv[1]).href;
if (isMain) {
  // --- CLI parsing ---
  const args = process.argv.slice(2);
  for (let i = 0; i < args.length; i++) {
    switch (args[i]) {
      case "--old":
        oldPath = args[++i];
        break;
      case "--new":
        newPath = args[++i];
        break;
      case "--old-version":
        oldVersion = args[++i];
        break;
      case "--new-version":
        newVersion = args[++i];
        break;
      case "--news-url":
        newsUrl = args[++i];
        break;
      case "--no-news":
        skipNews = true;
        break;
      case "-h":
      case "--help":
        console.log(
          `Usage: node scripts/har-diff.mjs --old <old.har> --new <new.har> [--old-version X] [--new-version Y] [--news-url URL] [--no-news]`
        );
        process.exit(0);
    }
  }
  if (!newPath) {
    console.error("Error: --new <path> is required");
    process.exit(1);
  }
  main().catch((err) => {
    console.error("har-diff error:", err.message);
    process.exit(1);
  });
}