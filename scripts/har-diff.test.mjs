import { describe, it } from "node:test";
import assert from "node:assert/strict";

import {
  classifyUrl,
  extractComponentName,
  extractBundleFingerprint,
  formatSize,
  formatDelta,
  bundleLabel,
  jaccard,
  matchJsBundles,
  computeCssDiff,
  renderChangesTable,
  renderHeader,
} from "./har-diff.mjs";

// --- helpers to build synthetic HAR-like Maps ---

function jsEntry(filename, text, sizeOverride) {
  const url = `https://www.newrecruit.eu/_nuxt/${filename}`;
  const contentSize = sizeOverride ?? text.length;
  return {
    url,
    method: "GET",
    status: 200,
    contentSize,
    transferSize: contentSize,
    category: "js",
    component: extractComponentName(url),
    fingerprint: extractBundleFingerprint(text),
  };
}

function cssEntry(filename, size) {
  const url = `https://www.newrecruit.eu/_nuxt/${filename}`;
  return {
    url,
    method: "GET",
    status: 200,
    contentSize: size,
    transferSize: size,
    category: "css",
    component: extractComponentName(url),
    fingerprint: null,
  };
}

function toMap(entries) {
  return new Map(entries.map((e) => [e.url, e]));
}

// ─── classifyUrl ────────────────────────────────────────────

describe("classifyUrl", () => {
  it("classifies _nuxt JS", () => {
    assert.equal(
      classifyUrl("https://newrecruit.eu/_nuxt/B8Gp5BKz.js"),
      "js"
    );
  });
  it("classifies _nuxt CSS", () => {
    assert.equal(
      classifyUrl("https://newrecruit.eu/_nuxt/entry.DKFMTdLi.css"),
      "css"
    );
  });
  it("classifies fonts", () => {
    assert.equal(
      classifyUrl("https://fonts.gstatic.com/s/inter/v18/abc.woff2"),
      "font"
    );
  });
  it("classifies images", () => {
    assert.equal(
      classifyUrl("https://newrecruit.eu/assets/logo.png"),
      "image"
    );
  });
  it("classifies API calls", () => {
    assert.equal(
      classifyUrl("https://newrecruit.eu/api/version"),
      "api"
    );
  });
  it("classifies html root", () => {
    assert.equal(classifyUrl("https://newrecruit.eu/"), "html");
  });
  it("classifies /app as html", () => {
    assert.equal(classifyUrl("https://newrecruit.eu/app"), "html");
  });
  it("classifies unknown extensions as other", () => {
    assert.equal(
      classifyUrl("https://newrecruit.eu/_nuxt/data.json"),
      "other"
    );
  });
});

// ─── extractComponentName ───────────────────────────────────

describe("extractComponentName", () => {
  it("extracts named CSS component", () => {
    assert.equal(
      extractComponentName("https://x.eu/_nuxt/PopupDialog.DNxOsQma.css"),
      "PopupDialog"
    );
  });
  it("extracts multi-part name", () => {
    assert.equal(
      extractComponentName("https://x.eu/_nuxt/entry.DKFMTdLi.css"),
      "entry"
    );
  });
  it("returns null for anonymous JS hash", () => {
    assert.equal(
      extractComponentName("https://x.eu/_nuxt/B8Gp5BKz.js"),
      null
    );
  });
  it("returns null for short anonymous hash", () => {
    assert.equal(
      extractComponentName("https://x.eu/_nuxt/a7FJwEbC.js"),
      null
    );
  });
});

// ─── extractBundleFingerprint ───────────────────────────────

describe("extractBundleFingerprint", () => {
  it("extracts __name values", () => {
    const fp = extractBundleFingerprint(
      'const c={__name:"Foo"};const d={__name:"Bar"};'
    );
    assert.deepEqual([...fp.names].sort(), ["Bar", "Foo"]);
  });

  it("extracts import references", () => {
    const fp = extractBundleFingerprint(
      'import{x}from"./abc123.js";import{y}from"./def456.js";'
    );
    assert.deepEqual([...fp.imports].sort(), ["abc123.js", "def456.js"]);
  });

  it("detects entry bundle via __vite__mapDeps", () => {
    const fp = extractBundleFingerprint(
      'const __vite__mapDeps=(i,m)=>i.map(i=>m[i]);'
    );
    assert.equal(fp.isEntry, true);
  });

  it("non-entry bundle", () => {
    const fp = extractBundleFingerprint('const x = 1;');
    assert.equal(fp.isEntry, false);
    assert.equal(fp.names.size, 0);
  });
});

// ─── formatSize / formatDelta ───────────────────────────────

describe("formatSize", () => {
  it("formats bytes", () => assert.equal(formatSize(42), "42 B"));
  it("formats KB", () => assert.equal(formatSize(2048), "2.0 KB"));
  it("formats MB", () => assert.equal(formatSize(1536 * 1024), "1.5 MB"));
});

describe("formatDelta", () => {
  it("positive delta", () => assert.equal(formatDelta(1024), "+1.0 KB"));
  it("negative delta", () => assert.match(formatDelta(-512), /512 B/));
  it("zero", () => assert.equal(formatDelta(0), "unchanged"));
});

// ─── bundleLabel ────────────────────────────────────────────

describe("bundleLabel", () => {
  it("labels entry bundle", () => {
    const info = jsEntry(
      "entry.js",
      'const __vite__mapDeps=1;const c={__name:"Root"};'
    );
    assert.equal(bundleLabel(info), "entry (core)");
  });

  it("labels single-component bundle", () => {
    const info = jsEntry("abc.js", 'const c={__name:"LoginForm"};');
    assert.equal(bundleLabel(info), "LoginForm");
  });

  it("labels multi-component bundle (≤3)", () => {
    const info = jsEntry(
      "abc.js",
      'const a={__name:"Foo"};const b={__name:"Bar"};const c={__name:"Baz"};'
    );
    assert.equal(bundleLabel(info), "Bar, Baz, Foo");
  });

  it("labels 4+ components with +N", () => {
    const info = jsEntry(
      "abc.js",
      'const a={__name:"A"};const b={__name:"B"};const c={__name:"C"};const d={__name:"D"};'
    );
    assert.equal(bundleLabel(info), "A, B, C +1 more");
  });

  it("returns null for nameless bundle", () => {
    const info = jsEntry("abc.js", "export const x = 1;");
    assert.equal(bundleLabel(info), null);
  });
});

// ─── jaccard ────────────────────────────────────────────────

describe("jaccard", () => {
  it("identical sets = 1", () => {
    assert.equal(jaccard(new Set(["a", "b"]), new Set(["a", "b"])), 1);
  });
  it("disjoint sets = 0", () => {
    assert.equal(jaccard(new Set(["a"]), new Set(["b"])), 0);
  });
  it("partial overlap", () => {
    assert.equal(
      jaccard(new Set(["a", "b", "c"]), new Set(["b", "c", "d"])),
      0.5
    );
  });
  it("both empty = 0", () => {
    assert.equal(jaccard(new Set(), new Set()), 0);
  });
});

// ─── matchJsBundles ─────────────────────────────────────────

describe("matchJsBundles", () => {
  it("matches by exact URL (unchanged bundle)", () => {
    const bundle = jsEntry("ABC123.js", 'const c={__name:"Foo"};');
    const oldMap = toMap([bundle]);
    const newMap = toMap([bundle]);
    const result = matchJsBundles(oldMap, newMap);
    assert.equal(result.matched.length, 1);
    assert.equal(result.unmatchedOld.length, 0);
    assert.equal(result.unmatchedNew.length, 0);
  });

  it("matches renamed hashes by __name fingerprint", () => {
    const oldBundle = jsEntry(
      "OLDhash.js",
      'const c={__name:"LoginForm"};const d={__name:"SignUp"};'
    );
    const newBundle = jsEntry(
      "NEWhash.js",
      'const c={__name:"LoginForm"};const d={__name:"SignUp"};// updated'
    );
    const result = matchJsBundles(toMap([oldBundle]), toMap([newBundle]));
    assert.equal(result.matched.length, 1);
    assert.equal(result.matched[0].old.url, oldBundle.url);
    assert.equal(result.matched[0].new.url, newBundle.url);
  });

  it("matches by Jaccard when components shift between chunks", () => {
    // Old: one bundle with A,B,C,D
    const oldBundle = jsEntry(
      "old.js",
      'const a={__name:"A"};const b={__name:"B"};const c={__name:"C"};const d={__name:"D"};'
    );
    // New: bundle keeps A,B,C (D moved elsewhere)
    const newBundle = jsEntry(
      "new.js",
      'const a={__name:"A"};const b={__name:"B"};const c={__name:"C"};'
    );
    const newD = jsEntry("newD.js", 'const d={__name:"D"};');
    const result = matchJsBundles(toMap([oldBundle]), toMap([newBundle, newD]));

    // old matched to new (jaccard 3/4 = 0.75 > 0.5)
    assert.equal(result.matched.length, 1);
    assert.equal(result.matched[0].old.url, oldBundle.url);
    assert.equal(result.matched[0].new.url, newBundle.url);
    // newD is unmatched (newly split out)
    assert.equal(result.unmatchedNew.length, 1);
    assert.equal(result.unmatchedNew[0].url, newD.url);
  });

  it("does not match when Jaccard is too low", () => {
    const oldBundle = jsEntry(
      "old.js",
      'const a={__name:"A"};const b={__name:"B"};const c={__name:"C"};'
    );
    // Completely different components
    const newBundle = jsEntry(
      "new.js",
      'const x={__name:"X"};const y={__name:"Y"};const z={__name:"Z"};'
    );
    const result = matchJsBundles(toMap([oldBundle]), toMap([newBundle]));
    assert.equal(result.matched.length, 0);
    assert.equal(result.unmatchedOld.length, 1);
    assert.equal(result.unmatchedNew.length, 1);
  });

  it("matches nameless utilities by import signature", () => {
    // Named bundle imports a utility
    const oldNamed = jsEntry(
      "oldNamed.js",
      'import{x}from"./oldUtil.js";const c={__name:"Foo"};'
    );
    const oldUtil = jsEntry("oldUtil.js", "export const x = 1;");

    const newNamed = jsEntry(
      "newNamed.js",
      'import{x}from"./newUtil.js";const c={__name:"Foo"};'
    );
    const newUtil = jsEntry("newUtil.js", "export const x = 2;");

    const result = matchJsBundles(
      toMap([oldNamed, oldUtil]),
      toMap([newNamed, newUtil])
    );
    // Both the named bundle and the utility should be matched
    assert.equal(result.matched.length, 2);
    assert.equal(result.unmatchedOld.length, 0);
    assert.equal(result.unmatchedNew.length, 0);
  });

  it("reports truly added and removed bundles", () => {
    const kept = jsEntry("kept.js", 'const c={__name:"Kept"};');
    const removed = jsEntry("removed.js", 'const c={__name:"Removed"};');
    const added = jsEntry("added.js", 'const c={__name:"Added"};');

    const result = matchJsBundles(
      toMap([kept, removed]),
      toMap([kept, added])
    );
    assert.equal(result.matched.length, 1);
    assert.equal(result.unmatchedOld.length, 1);
    assert.equal(result.unmatchedOld[0].url, removed.url);
    assert.equal(result.unmatchedNew.length, 1);
    assert.equal(result.unmatchedNew[0].url, added.url);
  });
});

// ─── computeCssDiff ─────────────────────────────────────────

describe("computeCssDiff", () => {
  it("detects changed CSS by component name", () => {
    const oldMap = toMap([cssEntry("PopupDialog.OLD123.css", 100)]);
    const newMap = toMap([cssEntry("PopupDialog.NEW456.css", 120)]);
    const changes = computeCssDiff(oldMap, newMap);
    assert.equal(changes.length, 1);
    assert.equal(changes[0].type, "changed");
    assert.equal(changes[0].name, "PopupDialog");
    assert.equal(changes[0].oldSize, 100);
    assert.equal(changes[0].newSize, 120);
  });

  it("detects added and removed CSS", () => {
    const oldMap = toMap([cssEntry("Old.AAAAAA.css", 50)]);
    const newMap = toMap([cssEntry("New.BBBBBB.css", 80)]);
    const changes = computeCssDiff(oldMap, newMap);
    const added = changes.find((c) => c.type === "added");
    const removed = changes.find((c) => c.type === "removed");
    assert.ok(added);
    assert.equal(added.name, "New");
    assert.ok(removed);
    assert.equal(removed.name, "Old");
  });

  it("reports nothing for identical CSS", () => {
    const entry = cssEntry("Theme.AAAAAA.css", 200);
    const changes = computeCssDiff(toMap([entry]), toMap([entry]));
    assert.equal(changes.length, 0);
  });
});

// ─── renderChangesTable ─────────────────────────────────────

describe("renderChangesTable", () => {
  it("returns empty for no changes", () => {
    assert.deepEqual(renderChangesTable([], "JS"), []);
  });

  it("renders a table with all change types", () => {
    const items = [
      { type: "changed", name: "Foo", oldSize: 1024, newSize: 2048 },
      { type: "added", name: "Bar", newSize: 512 },
      { type: "removed", name: "Baz", oldSize: 256 },
    ];
    const lines = renderChangesTable(items, "JS bundles");
    const text = lines.join("\n");

    // Heading includes counts
    assert.ok(text.includes("JS bundles (~1 changed, +1 added, -1 removed)"));

    // Wrapped in details/summary
    assert.ok(text.includes("<details><summary>"));
    assert.ok(text.includes("</details>"));

    // Summary line has compact counts
    assert.ok(text.includes("<summary>~1 changed, +1 added, -1 removed</summary>"));

    // Table content present
    assert.ok(text.includes("Foo"));
    assert.ok(text.includes("Bar"));
    assert.ok(text.includes("Baz"));

    // Added should come first in sorted output
    const barLine = lines.findIndex((l) => l.includes("Bar"));
    const fooLine = lines.findIndex((l) => l.includes("Foo"));
    const bazLine = lines.findIndex((l) => l.includes("Baz"));
    assert.ok(barLine < fooLine, "added before changed");
    assert.ok(fooLine < bazLine, "changed before removed");
  });

  it("renders counts correctly for single type", () => {
    const items = [
      { type: "changed", name: "A", oldSize: 100, newSize: 200 },
      { type: "changed", name: "B", oldSize: 300, newSize: 400 },
    ];
    const text = renderChangesTable(items, "CSS").join("\n");
    assert.ok(text.includes("CSS (~2 changed)"));
    assert.ok(text.includes("<summary>~2 changed</summary>"));
  });
});

// ─── renderHeader ───────────────────────────────────────────

describe("renderHeader", () => {
  it("titles on the tag transition, and keeps the client version separate", () => {
    const text = renderHeader({
      hasOld: true,
      oldTag: "v35.27",
      newTag: "v35.28",
      oldVersion: "35.27",
      newVersion: "35.28",
    }).join("\n");
    assert.ok(text.includes("## NR Snapshot: v35.27 → v35.28"));
    assert.ok(text.includes("**Client version:** 35.27 → 35.28"));
  });

  it("still shows the tag transition when the client version is unchanged", () => {
    // A same-day re-snapshot. The version-only title used to collapse to a bare "v35.27",
    // which reads as "nothing changed" on a PR that swaps the pinned tag.
    const text = renderHeader({
      hasOld: true,
      oldTag: "v35.27",
      newTag: "v35.27-20260813",
      oldVersion: "35.27",
      newVersion: "35.27",
    }).join("\n");
    assert.ok(text.includes("## NR Snapshot: v35.27 → v35.27-20260813"));
    assert.ok(text.includes("**Client version:** 35.27"));
    assert.ok(!text.includes("**Client version:** 35.27 →"));
  });

  it("falls back to versions when no tags are supplied", () => {
    const text = renderHeader({
      hasOld: true,
      oldVersion: "35.27",
      newVersion: "35.28",
    }).join("\n");
    assert.ok(text.includes("## NR Snapshot: v35.27 → v35.28"));
  });

  it("does not invent a transition when there is no baseline", () => {
    const text = renderHeader({
      hasOld: false,
      newTag: "v35.28",
      newVersion: "35.28",
    }).join("\n");
    assert.ok(text.includes("## NR Snapshot: v35.28"));
    assert.ok(!text.includes("→"));
  });

  it("degrades to a bare title with nothing to go on", () => {
    assert.deepEqual(renderHeader({ hasOld: false }), [
      "## NR Snapshot Update",
      "",
    ]);
  });
});
