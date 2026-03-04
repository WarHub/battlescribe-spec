# Frozen NR Snapshots

This directory contains Playwright HAR (HTTP Archive) files for offline NR testing.

## Purpose

The HAR file captures all HTTP responses from the NR website, enabling tests to run
without a network connection. This provides:

- **Stability**: Tests pass even when newrecruit.eu is down or changes
- **Determinism**: Same NR version every run
- **Speed**: No network latency

## Updating the frozen snapshot

To record a new snapshot from the live site:

```bash
dotnet test tests/BattleScribeSpec.Tests.csproj --filter "HarRecordingTests" -e NR_ENGINE_URL=https://newrecruit.eu
```

After recording, review the changes and commit the updated files.

## Files

- `newrecruit.har` — Playwright HAR file with all HTTP responses
- `metadata.json` — Recording metadata (timestamp, source URL)
