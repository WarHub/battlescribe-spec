# Decision: Main is protected — always use PRs

**Date:** 2026-05-08  
**Source:** User directive (Amadeusz Sadowski)

## Decision

Main branch is protected. All changes must go through pull requests. Direct commits to main are not permitted.

## Rationale

Branch protection rules enforce code review and CI gates. All agents and contributors must branch first, then open a PR.
