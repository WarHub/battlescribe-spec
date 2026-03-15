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
import { get as httpsGet } from "node:https";
import { get as httpGet } from "node:http";

// --- CLI parsing ---

const args = process.argv.slice(2);
let oldPath, newPath, oldVersion, newVersion;
let newsUrl = "https://www.newrecruit.eu/news";
let skipNews = false;

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

function parseHar(path) {
  const raw = JSON.parse(readFileSync(path, "utf8"));
  const entries = raw?.log?.entries || [];
  const map = new Map();
  for (const entry of entries) {
    const url = entry.request?.url;
    if (!url) continue;
    const contentSize = (entry.response?.content?.text || "").length;
    const transferSize =
      entry.response?.content?.size ??
      entry.response?.bodySize ??
      contentSize;
    map.set(url, {
      url,
      method: entry.request.method,
      status: entry.response?.status,
      contentSize,
      transferSize: Math.max(0, transferSize),
      category: classifyUrl(url),
      component: extractComponentName(url),
    });
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

// --- Diff logic ---

function computeDiff(oldMap, newMap) {
  const changes = { js: [], css: [] };
  const oldMatched = new Set();
  const newMatched = new Set();

  // Group named components by (category, componentName)
  const oldByComponent = new Map();
  const newByComponent = new Map();

  for (const [url, info] of oldMap) {
    if (
      info.component !== null &&
      (info.category === "js" || info.category === "css")
    ) {
      const key = `${info.category}:${info.component}`;
      if (!oldByComponent.has(key)) oldByComponent.set(key, []);
      oldByComponent.get(key).push({ url, ...info });
    }
  }
  for (const [url, info] of newMap) {
    if (
      info.component !== null &&
      (info.category === "js" || info.category === "css")
    ) {
      const key = `${info.category}:${info.component}`;
      if (!newByComponent.has(key)) newByComponent.set(key, []);
      newByComponent.get(key).push({ url, ...info });
    }
  }

  const allComponentKeys = new Set([
    ...oldByComponent.keys(),
    ...newByComponent.keys(),
  ]);

  for (const key of allComponentKeys) {
    const [cat] = key.split(":");
    const component = key.split(":").slice(1).join(":");
    const oldEntries = oldByComponent.get(key) || [];
    const newEntries = newByComponent.get(key) || [];

    for (const e of oldEntries) oldMatched.add(e.url);
    for (const e of newEntries) newMatched.add(e.url);

    if (oldEntries.length > 0 && newEntries.length > 0) {
      const oldUrls = new Set(oldEntries.map((e) => e.url));
      const newUrls = new Set(newEntries.map((e) => e.url));
      const same = [...oldUrls].every((u) => newUrls.has(u));
      if (!same) {
        const oldSize = oldEntries.reduce((s, e) => s + e.contentSize, 0);
        const newSize = newEntries.reduce((s, e) => s + e.contentSize, 0);
        changes[cat].push({
          type: "changed",
          name: component,
          oldSize,
          newSize,
        });
      }
    } else if (newEntries.length > 0) {
      const size = newEntries.reduce((s, e) => s + e.contentSize, 0);
      changes[cat].push({ type: "added", name: component, newSize: size });
    } else {
      const size = oldEntries.reduce((s, e) => s + e.contentSize, 0);
      changes[cat].push({ type: "removed", name: component, oldSize: size });
    }
  }

  // Anonymous chunks — match by exact URL
  for (const [url, info] of oldMap) {
    if (oldMatched.has(url)) continue;
    if (info.category !== "js" && info.category !== "css") continue;
    oldMatched.add(url);
    if (newMap.has(url)) {
      newMatched.add(url);
    } else {
      changes[info.category].push({
        type: "removed",
        name: shortUrl(url),
        oldSize: info.contentSize,
      });
    }
  }
  for (const [url, info] of newMap) {
    if (newMatched.has(url)) continue;
    if (info.category !== "js" && info.category !== "css") continue;
    newMatched.add(url);
    changes[info.category].push({
      type: "added",
      name: shortUrl(url),
      newSize: info.contentSize,
    });
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

  const lines = [`### ${label}`, "", "| Change | File | Size |", "|--------|------|------|"];
  for (const item of items) {
    const marker =
      item.type === "added"
        ? "✚ added"
        : item.type === "removed"
          ? "✕ removed"
          : "△ changed";
    let sizeStr;
    if (item.type === "added") sizeStr = formatSize(item.newSize);
    else if (item.type === "removed") sizeStr = formatSize(item.oldSize);
    else
      sizeStr = `${formatSize(item.oldSize)} → ${formatSize(item.newSize)}`;
    lines.push(`| ${marker} | \`${item.name}\` | ${sizeStr} |`);
  }
  lines.push("");
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
    lines.push(`## NR Snapshot: v${oldVersion} → v${newVersion}`, "");
    lines.push(`**Client version:** ${oldVersion} → ${newVersion}`);
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
      `**HAR entries:** ${oldMap.size} → ${newMap.size} (${entryDelta >= 0 ? "+" : ""}${entryDelta})`
    );
    lines.push(
      `**Total size:** ${formatSize(oldTotal)} → ${formatSize(newTotal)} (${formatDelta(newTotal - oldTotal)})`
    );
  } else {
    lines.push(`**HAR entries:** ${newMap.size}`);
    lines.push(`**Total size:** ${formatSize(newTotal)}`);
  }
  lines.push("");

  if (!hasOld) {
    lines.push("_No previous snapshot available for comparison._");
  } else {
    const changes = computeDiff(oldMap, newMap);
    lines.push(...renderChangesTable(changes.js, "JS bundles"));
    lines.push(...renderChangesTable(changes.css, "CSS"));
    lines.push(...renderOtherCategories(oldMap, newMap));
  }

  // NR News
  if (!skipNews && oldVersion && newVersion && oldVersion !== newVersion) {
    try {
      const html = await fetchUrl(newsUrl);
      const posts = parseNewsPosts(html);
      const relevant = filterRelevantPosts(posts, oldVersion, newVersion);
      if (relevant.length > 0) {
        lines.push("### NR News", "");
        for (const post of relevant.slice(0, 5)) {
          const dateStr = post.date ? ` (${post.date})` : "";
          lines.push(`> **${post.title}**${dateStr}`);
          lines.push(
            `> ${post.body.slice(0, 300)}${post.body.length > 300 ? "\u2026" : ""}`
          );
          lines.push("");
        }
      }
    } catch {
      // News scraping failed — silently skip
    }
  }

  console.log(lines.join("\n"));
}

main().catch((err) => {
  console.error("har-diff error:", err.message);
  process.exit(1);
});
