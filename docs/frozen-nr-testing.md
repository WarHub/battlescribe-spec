# Frozen New Recruit Testing

The frozen NR testing infrastructure allows running conformance tests against a snapshot of
the [New Recruit](https://newrecruit.eu) roster editor — fully offline, deterministic, and
immune to site downtime or breaking changes.

## How it Works

1. **HAR recording** — A Playwright browser session navigates newrecruit.eu, capturing all
   network traffic into an [HTTP Archive (HAR)](https://en.wikipedia.org/wiki/HAR_(file_format)) file.
   The recording is post-processed: an allowlist of required domains is kept
   (`newrecruit.eu`, `raw.githubusercontent.com`, Google Fonts), everything else is stripped, and duplicate requests are deduplicated.

2. **Snapshot storage** — HAR snapshots are published as GitHub Releases in
   [WarHub/newrecruit-har](https://github.com/WarHub/newrecruit-har), tagged by the NR client
   version (e.g. `v34.14`).

3. **Version pinning** — The file `testdata.json` in the repo root pins the exact release tag
   to use. The `setup.ps1` script reads it and downloads the pinned version.

4. **HAR replay** — During testing, Playwright's `Page.RouteFromHARAsync()` serves all
   requests from the downloaded snapshot. Unmatched requests are aborted (`HarNotFound.Abort`)
   ensuring true offline execution.

5. **Automated updates** — A daily GitHub Actions workflow records a fresh snapshot, compares
   it against the current release, and opens a PR if changes are detected.

## Architecture

```
┌──────────────────────────┐
│  WarHub/newrecruit-har   │  GitHub Releases (tagged by NR version)
│  (HAR snapshots)         │  ← recorded by bs-nr-har-tool
└────────────┬─────────────┘
             │ setup.ps1 (reads testdata.json)
             ▼
┌──────────────────────────┐
│  .testdata/newrecruit-har│  Local (gitignored)
│  ├── newrecruit.har      │
│  ├── metadata.json       │
│  └── .tag                │  ← pinned tag marker
└────────────┬─────────────┘
             │ RouteFromHARAsync
             ▼
┌──────────────────────────┐
│  FrozenNewRecruit tests  │  xUnit test collection
│  (offline Playwright)    │
└──────────────────────────┘
```

## Version Pinning

The `testdata.json` file pins the HAR release version:

```json
{
  "newrecruit-har": {
    "repo": "WarHub/newrecruit-har",
    "tag": "v34.53-20260506"
  }
}
```

This ensures all developers and CI use the same snapshot version. To update, change the `tag`
value and re-run `setup.ps1`.

## Recording a New Snapshot

Use the console tool to record a fresh HAR snapshot:

```bash
# Record (headless by default)
dotnet run --project src/BattleScribeSpec.NewRecruit.HarTool

# Record with visible browser
dotnet run --project src/BattleScribeSpec.NewRecruit.HarTool -- --headed

# Custom output directory
dotnet run --project src/BattleScribeSpec.NewRecruit.HarTool -- -o my-output/
```

The tool:
- Navigates newrecruit.eu landing page and `/app` route
- Dismisses consent dialogs
- **Loads a synthetic game system through `systemsStore.loadSystemFromFs`**, then selects it, opens
  its book, builds a roster and adds it to the lists store — the adapter's own setup sequence
  (see below)
- Walks the pages the drivers use: MySystems, Add More Games, **the roster editor at
  `/app/Lists/{listKey}`** (its own route chunk, and where the adapter exports and reloads), and the
  Create List dialog
- Captures all network traffic
- Post-processes: keeps an allowlist of required domains (`newrecruit.eu`, `raw.githubusercontent.com`, Google Fonts), strips everything else, deduplicates requests
- **Replays the finished HAR offline and re-runs that setup sequence**, refusing to write
  `metadata.json` if the snapshot cannot serve it
- Extracts the NR `clientVersion` from `__NUXT_CONFIG__`
- Computes and prints the SHA256 hash of the HAR file
- Writes `newrecruit.har` and `metadata.json`
- Prints a suggested `gh release create` command

### Why the recorder loads a system

A recording is a *guess* about which chunks NR loads eagerly, and the file-loading path is the one
the adapters depend on that no UI journey reaches — NR imports game data from GitHub or a directory
picker, neither of which a recorder can drive. Whichever chunks that path pulls were captured only
as long as NR happened to bundle them into the eager entry chunk.

v35.76 stopped doing that: it split the XML parser into a chunk fetched on first parse. The
recording never parsed anything, so the chunk never entered the snapshot, and offline replay aborted
the import. NR catches parse errors internally, so `loadSystemFromFs` returned an empty array rather
than surfacing the missing module, and all ~500 frozen NR specs failed at setup with
`System not found in localLibrary after load` — a message that points at the store, not at a
missing bundle.

Driving the same store call the adapters make keeps that class of split captured by construction,
and the offline replay afterwards proves it rather than assuming it: a snapshot with a hole fails
the recorder instead of turning CI red a day later.

The verification watches for the browser's own
`Failed to fetch dynamically imported module` rather than for aborted requests. Nuxt prefetches
route chunks it never uses, so a plain "did any `/_nuxt/` request fail" check reports every route
the recording did not visit; only code that actually awaited an import produces this message.

### Publishing a Snapshot

After recording, publish to the HAR repository:

```bash
gh release create v<version> \
  .testdata/newrecruit-har/newrecruit.har \
  .testdata/newrecruit-har/metadata.json \
  -R WarHub/newrecruit-har \
  --title "NR snapshot v<version>"
```

## Running Frozen Tests

### Locally

```bash
# Download the pinned snapshot
./setup.ps1

# Install Playwright browsers (first time)
pwsh src/BattleScribeSpec.NewRecruit/bin/Debug/net10.0/playwright.ps1 install chromium

# Run frozen tests
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FrozenNewRecruitConformanceTests"
```

### In CI

The `nr-frozen` job in `.github/workflows/ci.yml` handles this automatically:
1. Runs `setup.ps1` to download the pinned HAR snapshot
2. Installs Playwright Chromium
3. Runs `FrozenNewRecruitConformanceTests` (parallel by default)

### Skipping

Set `NR_FROZEN_SKIP=true` to skip frozen tests, or simply don't download the HAR file —
the fixture gracefully skips when the HAR is not found.

## Automated Daily Updates

The `update-nr-snapshot.yml` workflow runs daily and on manual dispatch:

1. Records a fresh HAR from newrecruit.eu
2. Asks two independent questions:
   - **`content_new`** — does the recording's SHA256 differ from the newest release's asset digest?
   - **`pin_stale`** — is `testdata.json` pinned to something other than the newest release?
3. If neither, exits early (no action needed)
4. Otherwise:
   - Picks the target tag. A changed recording earns a new one — `v{version}`, or
     `v{version}-{YYYYMMDD}` if the version is unchanged but the content differs. An unchanged
     recording with a lagging pin adopts the release that already exists.
   - Diffs the new HAR against **the release `testdata.json` currently pins** and writes the
     summary that becomes both the release notes and the PR body
   - Publishes a new release to [WarHub/newrecruit-har](https://github.com/WarHub/newrecruit-har)
     — only when `content_new`; there is nothing to publish when just the pin lagged
   - Opens a PR updating `testdata.json` with the target tag, labelled `thorough-ci`

`pin_stale` is what lets the pin catch up on its own. Without it the workflow could only react to
NR: close a snapshot PR unmerged and the pin stays behind indefinitely, because every later run
compares the recording against a release that already exists, finds them identical, and exits. Any
closed snapshot PR is re-proposed by the next run.

The workflow compares against two different things on purpose. Change detection and tag naming
use the **newest release** — they ask "is this snapshot new to the world?", and anchoring them to
the pin instead would republish an identical HAR under a fresh date-suffixed tag every night that
a snapshot PR sat unmerged. The **diff** uses the pin, because that is the snapshot the frozen
suites replay today and the one the reviewer is being asked to stop replaying. The two agree on
the common path and diverge the moment a PR waits a day.

The diff baseline is downloaded fresh into `.har-old/` on every run, and a download that fails
means *no baseline* — a summary with no comparison — rather than a fallback to whatever is on
disk. #443 is why that is spelled out: `.har-old/` was committed to the repo by the bot's own #80,
`gh release download` will not overwrite an existing file without `--clobber`, and the step that
consumed it only checked that the file existed. Every PR body and release note from `v34.21` to
`v35.28` claimed a diff against `v34.18`, describing five months of accumulated change as if it
were one night's.

A snapshot bump changes what every frozen suite replays, and the every-push CI lanes trim those
suites to kitchen-sink — so the bump PR must run the full ones or it proves nothing. `ci.yml`
turns the `thorough-conformance` lane on for **any** PR whose diff touches `testdata.json`,
whether the bot or a maintainer made the edit; the `thorough-ci` label the bot applies is the
visible marker of the same decision. #301 (`v34.93-20260708` → `v35.12`) is why: it was green
through three weeks of daily bot re-runs, merged, and broke the NR-UI roster driver and the
store-direct roster export.

## Key Design Decisions

- **Separate test collection** — Frozen tests run independently from live NR tests, in their
  own xUnit collection (`FrozenNewRecruit`) with a dedicated fixture.
- **External HAR storage** — HAR files (~11 MB) are stored as GitHub Releases in a separate
  repo to keep the spec repository lightweight.
- **Version pinning** — `testdata.json` pins the exact release tag, ensuring reproducible
  test runs across all environments.
- **`HarNotFound.Abort`** — Unmatched requests fail immediately rather than falling through
  to the network, ensuring true offline testing.
- **`WaitUntilState.Load`** — Frozen mode uses `Load` instead of `NetworkIdle` to avoid
  hanging on aborted requests that would never complete.
- **Version tagging** — Snapshots are tagged with the NR `clientVersion` (e.g. `v34.14`)
  extracted from the app's `__NUXT_CONFIG__`. Same-version changes get a date suffix.
- **SHA256 comparison** — Change detection uses content hashing rather than version checks,
  catching content changes even when the NR version stays the same.

## Project Layout

| Path | Description |
|------|-------------|
| `testdata.json` | Pinned HAR release version |
| `setup.ps1` | Clones dependencies and downloads pinned test data |
| `src/BattleScribeSpec.NewRecruit/HarRecorder.cs` | Recording, post-processing, version extraction |
| `src/BattleScribeSpec.NewRecruit.HarTool/` | Console app for recording HAR snapshots |
| `tests/Infrastructure/FrozenNewRecruitFixture.cs` | xUnit fixture (browser context pool for parallel execution) |
| `tests/Conformance/FrozenNewRecruitConformanceTests.cs` | Parallel conformance tests against frozen snapshot |
| `.github/workflows/ci.yml` | `nr-frozen` CI job |
| `.github/workflows/update-nr-snapshot.yml` | Daily snapshot update workflow |
| `.testdata/newrecruit-har/` | Downloaded HAR files (gitignored) |
