# Frozen NR Snapshots

This directory holds Playwright HAR (HTTP Archive) files for offline NR testing.
Files are **not** committed here — they are downloaded from
[WarHub/newrecruit-har](https://github.com/WarHub/newrecruit-har) releases at CI time.

## Purpose

The HAR file captures all HTTP responses from the NR website, enabling tests to run
without a network connection. This provides:

- **Stability**: Tests pass even when newrecruit.eu is down or changes
- **Determinism**: Same NR version every run
- **Speed**: No network latency

## Local setup

Download the snapshot for local testing:

```bash
gh release download v1 -R WarHub/newrecruit-har -D frozen/newrecruit
```

## Updating the frozen snapshot

1. Record a new snapshot from the live site:

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "HarRecordingTests" -e NR_ENGINE_URL=https://newrecruit.eu
```

2. Create a new release in [WarHub/newrecruit-har](https://github.com/WarHub/newrecruit-har):

```bash
gh release create v2 frozen/newrecruit/newrecruit.har frozen/newrecruit/metadata.json -R WarHub/newrecruit-har --title "NR snapshot v2"
```

3. Update the release tag in `.github/workflows/ci.yml` if needed.
