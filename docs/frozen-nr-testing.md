# Frozen New Recruit Testing

The frozen NR testing infrastructure allows running conformance tests against a snapshot of
the [New Recruit](https://newrecruit.eu) roster editor — fully offline, deterministic, and
immune to site downtime or breaking changes.

## How it Works

1. **HAR recording** — A Playwright browser session navigates newrecruit.eu, capturing all
   network traffic into an [HTTP Archive (HAR)](https://en.wikipedia.org/wiki/HAR_(file_format)) file.
   The recording is post-processed to strip ad/tracker domains and deduplicate entries.

2. **Snapshot storage** — HAR snapshots are published as GitHub Releases in
   [WarHub/newrecruit-har](https://github.com/WarHub/newrecruit-har), tagged by the NR client
   version (e.g. `v34.14`).

3. **HAR replay** — During testing, the HAR file is downloaded and Playwright's
   `Page.RouteFromHARAsync()` serves all requests from the snapshot. Unmatched requests are
   aborted (`HarNotFound.Abort`) ensuring true offline execution.

## Architecture

```
┌──────────────────────────┐
│  WarHub/newrecruit-har   │  GitHub Releases
│  (HAR snapshots)         │  ← recorded by bs-nr-har-tool
└────────────┬─────────────┘
             │ gh release download
             ▼
┌──────────────────────────┐
│  .testdata/newrecruit-har│  Local (gitignored)
│  ├── newrecruit.har      │
│  └── metadata.json       │
└────────────┬─────────────┘
             │ RouteFromHARAsync
             ▼
┌──────────────────────────┐
│  FrozenNewRecruit tests  │  xUnit test collection
│  (offline Playwright)    │
└──────────────────────────┘
```

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
- Captures all network traffic
- Post-processes: strips 24+ ad/tracker domains, deduplicates requests
- Extracts the NR `clientVersion` from `__NUXT_CONFIG__`
- Writes `newrecruit.har` and `metadata.json`
- Prints a suggested `gh release create` command

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
# Download the latest snapshot
gh release download -R WarHub/newrecruit-har -D .testdata/newrecruit-har

# Install Playwright browsers (first time)
pwsh src/BattleScribeSpec.NewRecruit/bin/Debug/net10.0/playwright.ps1 install chromium

# Run frozen tests
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "FrozenNewRecruitConformanceTests"
```

### In CI

The `nr-frozen` job in `.github/workflows/ci.yml` handles this automatically:
1. Downloads the latest HAR release from `WarHub/newrecruit-har`
2. Installs Playwright Chromium
3. Runs `FrozenNewRecruitConformanceTests`

### Skipping

Set `NR_FROZEN_SKIP=true` to skip frozen tests, or simply don't download the HAR file —
the fixture gracefully skips when the HAR is not found.

## Key Design Decisions

- **Separate test collection** — Frozen tests run independently from live NR tests, in their
  own xUnit collection (`FrozenNewRecruit`) with a dedicated fixture.
- **External HAR storage** — HAR files (~11 MB) are stored as GitHub Releases in a separate
  repo to keep the spec repository lightweight.
- **`HarNotFound.Abort`** — Unmatched requests fail immediately rather than falling through
  to the network, ensuring true offline testing.
- **`WaitUntilState.Load`** — Frozen mode uses `Load` instead of `NetworkIdle` to avoid
  hanging on aborted requests that would never complete.
- **Version tagging** — Snapshots are tagged with the NR `clientVersion` (e.g. `v34.14`)
  extracted from the app's `__NUXT_CONFIG__`.

## Project Layout

| Path | Description |
|------|-------------|
| `src/BattleScribeSpec.NewRecruit/HarRecorder.cs` | Recording, post-processing, version extraction |
| `src/BattleScribeSpec.NewRecruit.HarTool/` | Console app for recording HAR snapshots |
| `tests/FrozenNewRecruitFixture.cs` | xUnit fixture (HAR discovery, engine setup) |
| `tests/FrozenNewRecruitConformanceTests.cs` | Conformance tests against frozen snapshot |
| `.github/workflows/ci.yml` | `nr-frozen` CI job |
| `.testdata/newrecruit-har/` | Downloaded HAR files (gitignored) |
