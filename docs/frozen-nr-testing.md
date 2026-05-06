# Frozen New Recruit Testing

The frozen NR testing infrastructure allows running conformance tests against a snapshot of
the [New Recruit](https://newrecruit.eu) roster editor — fully offline, deterministic, and
immune to site downtime or breaking changes.

## How it Works

1. **HAR recording** — A Playwright browser session navigates newrecruit.eu, capturing all
   network traffic into an [HTTP Archive (HAR)](https://en.wikipedia.org/wiki/HAR_(file_format)) file.
   The recording is post-processed to strip requests to non-essential domains (only the NR app domains are kept) and deduplicate entries.

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
- Captures all network traffic
- Post-processes: strips 24+ ad/tracker domains, deduplicates requests
- Extracts the NR `clientVersion` from `__NUXT_CONFIG__`
- Computes and prints the SHA256 hash of the HAR file
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
2. Computes SHA256 and compares with the current latest release's asset digest
3. If unchanged, exits early (no action needed)
4. If changed:
   - Determines the tag: `v{version}` for new versions, `v{version}-{YYYYMMDD}` if the
     version is unchanged but content differs
   - Publishes a new release to [WarHub/newrecruit-har](https://github.com/WarHub/newrecruit-har)
   - Opens a PR updating `testdata.json` with the new tag

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
