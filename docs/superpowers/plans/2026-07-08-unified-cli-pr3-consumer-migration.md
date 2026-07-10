# Unified `bs-spec` CLI — PR 3: consumer migration & Runner deletion

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Migrate every consumer of the retired `bs-spec-runner` (2 CI call sites, docker) to `bs-spec run --all`, reframe the docs, and delete `src/BattleScribeSpec.Runner`, completing issue #271.

**Architecture:** PR 1 extracted the batch pipeline into TestKit (`SpecSuiteRunner`/`SpecSuiteOutput`); PR 2 gave the CLI `run --all`/`run --matrix` over that same pipeline. The Runner is now a thin `Program.cs` shell that no other project references. This PR repoints its consumers and removes it — no production behavior changes, only which binary the callers invoke. The old `--adapter <conn> --engine <name>` pair folds into the CLI's single `--engine name=connectable` form (`battlescribe=dotnet:…`).

**Tech Stack:** .NET 10 CLI (`System.CommandLine`), GitHub Actions YAML, Docker (Dockerfile + compose), Markdown docs.

## Global Constraints

- **CLI binary:** `bs-spec.dll` (AssemblyName `bs-spec`) at `artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll`. Reference adapter: `artifacts/bin/BattleScribeSpec.ReferenceAdapter/debug/bs-reference-adapter.dll`.
- **Command translation (verbatim shape):** `bs-spec-runner --adapter "dotnet:<X>" --engine battlescribe [flags]` → `bs-spec run --all --engine "battlescribe=dotnet:<X>" [same flags]`. All of `--specs`, `--filter`, `--tags`, `--report`, `--expected-failures`, `--assertion-engine`, `--workers`, `--output {summary|json|github-actions}` map 1:1 (same names). The old `--engine battlescribe` identity is preserved by the `battlescribe=` prefix on the connectable.
- **CI is the gate.** The two migrated call sites must produce the same pass/fail verdict and exit code as the Runner did. Docker is **not** built in CI — treat docker changes as best-effort, verified only by a local `docker build`.
- **Order:** migrate consumers (CI, docker, docs) *before* deleting the Runner project, so the branch never references a binary that no longer builds. The parity check in Task 1 must run while the Runner still exists.
- Repo conventions: `dotnet build` before any `--no-build`; `TreatWarningsAsErrors=true`; `UseArtifactsOutput=true` (`artifacts/bin/<proj>/<pivot>/`); central package management (no `Version=` attributes); solution file is `BattleScribeSpec.slnx` (XML `<Project Path=… />` entries).
- Do **not** touch the protocol wire, the batch pipeline, or any engine — this PR is deletion + repointing + prose only. `#272` (interactive/introspection redesign) stays a separate follow-up; do not fold it in.

---

### Task 1: Migrate the two CI call sites (with a pre-deletion parity check)

**Files:**
- Modify: `.github/workflows/ci.yml:80-89` (job `checks`, step "Reference adapter (dotnet) — roster kitchen-sink")
- Modify: `.github/workflows/ci.yml:346-354` (job `nr-conformance`, step "Generate BattleScribe conformance report")

**Interfaces:**
- Consumes: `bs-spec run --all` surface from PR 2 (`RunCommand`/`RunBatch`), unchanged here.
- Produces: nothing downstream depends on this task's output; it is a leaf edit.

**Context:** Both jobs already run `dotnet build` (full solution) before these steps, so `bs-spec.dll` and `bs-reference-adapter.dll` are present. Both call sites launch the reference adapter *as* `battlescribe` (roster domain) — neither is an NR-UI lane, so the host-side warm-reuse follow-up is **not** a prerequisite for this migration.

- [ ] **Step 1: Build the solution locally**

Run: `dotnet build`
Expected: build succeeds (Runner + Cli + ReferenceAdapter all produced).

- [ ] **Step 2: Capture the OLD runner's output as the parity baseline (checks call site)**

Run (from repo root; single line — the `\` continuations are for readability, join them if your shell needs it):

```bash
dotnet artifacts/bin/BattleScribeSpec.Runner/debug/bs-spec-runner.dll \
  --adapter "dotnet:artifacts/bin/BattleScribeSpec.ReferenceAdapter/debug/bs-reference-adapter.dll" \
  --specs specs/roster --filter "protocol/protocol-kitchen-sink,category/" \
  --engine battlescribe --expected-failures battlescribe --output github-actions --workers 2 \
  > /tmp/pr3-old.txt; echo "OLD exit=$?"
```

Expected: exit=0, and `/tmp/pr3-old.txt` holds the github-actions summary lines.

- [ ] **Step 3: Capture the NEW cli output and diff it against the baseline**

Run:

```bash
dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll run --all \
  --engine "battlescribe=dotnet:artifacts/bin/BattleScribeSpec.ReferenceAdapter/debug/bs-reference-adapter.dll" \
  --specs specs/roster --roster --filter "protocol/protocol-kitchen-sink,category/" \
  --expected-failures battlescribe --output github-actions --workers 2 \
  > /tmp/pr3-new.txt; echo "NEW exit=$?"
diff /tmp/pr3-old.txt /tmp/pr3-new.txt && echo "PARITY OK"
```

Expected: NEW exit=0 and `PARITY OK` (identical stdout — both go through `SpecSuiteRunner`/`SpecSuiteOutput`). If the diff shows only ordering/whitespace noise from parallel workers, re-run with `--workers 1` on both to confirm the verdict and counts match; a genuine pass/fail-count difference is a real defect — stop and report it.

*(Note: `--roster` is added to the migrated command to pin the domain explicitly. `specs/roster` is a roster-only tree and the reference adapter describes only the roster domain, so this is behavior-preserving — it just guarantees no future gamedata spec placed under that path silently changes the run.)*

- [ ] **Step 4: Edit the `checks` call site**

Replace `.github/workflows/ci.yml:80-89` (the whole `- name: Reference adapter (dotnet) — roster kitchen-sink` step body) with:

```yaml
      - name: Reference adapter (dotnet) — roster kitchen-sink
        run: |
          dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll run --all \
            --engine "battlescribe=dotnet:artifacts/bin/BattleScribeSpec.ReferenceAdapter/debug/bs-reference-adapter.dll" \
            --specs specs/roster \
            --roster \
            --filter "protocol/protocol-kitchen-sink,category/" \
            --expected-failures battlescribe \
            --output github-actions \
            --workers 2
```

- [ ] **Step 5: Edit the `nr-conformance` call site**

Replace `.github/workflows/ci.yml:346-354` (the whole `- name: Generate BattleScribe conformance report` step body) with:

```yaml
      - name: Generate BattleScribe conformance report
        run: |
          dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll run --all \
            --engine "battlescribe=dotnet:artifacts/bin/BattleScribeSpec.ReferenceAdapter/debug/bs-reference-adapter.dll" \
            --specs specs/roster \
            --roster \
            --expected-failures battlescribe \
            --output github-actions \
            --report artifacts/battlescribe-conformance-report.json
```

- [ ] **Step 6: Verify the second call site's report output locally**

Run:

```bash
dotnet artifacts/bin/BattleScribeSpec.Cli/debug/bs-spec.dll run --all \
  --engine "battlescribe=dotnet:artifacts/bin/BattleScribeSpec.ReferenceAdapter/debug/bs-reference-adapter.dll" \
  --specs specs/roster --roster --expected-failures battlescribe \
  --output github-actions --report /tmp/pr3-report.json; echo "exit=$?"
test -s /tmp/pr3-report.json && echo "REPORT WRITTEN"
```

Expected: exit=0 and `REPORT WRITTEN` (non-empty conformance JSON produced, proving `--report` works over the CLI).

- [ ] **Step 7: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: migrate reference-adapter conformance steps from bs-spec-runner to bs-spec run --all (#271)"
```

---

### Task 2: Migrate Docker to `bs-spec run --all`

**Files:**
- Create: `docker/bs-spec.Dockerfile`
- Delete: `docker/runner.Dockerfile`
- Modify: `docker/docker-compose.yaml`

**Interfaces:**
- Consumes: the `bs-spec` CLI project and the reference-adapter image (unchanged, built by `docker/reference-adapter.Dockerfile`).
- Produces: a `bs-spec:local` image running `bs-spec run --all` against the reference adapter.

**Context:** The Cli references XmlGen, which `ProjectReference`s the vendored `wham` submodule at `.deps/wham`. The image build therefore needs `.deps/` copied into the build context (the current `runner.Dockerfile` didn't, because the Runner shell doesn't pull XmlGen). The Cli is engine-free (no IKVM), so no `lib/*.jar` is needed. Docker is not CI-gated, so the gate here is a successful local `docker build`.

- [ ] **Step 1: Write `docker/bs-spec.Dockerfile`**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy solution + build props for restore
COPY BattleScribeSpec.slnx .
COPY Directory.Build.props .
COPY Directory.Packages.props .

# The CLI is engine-free but pulls XmlGen, which ProjectReferences the vendored
# wham submodule at .deps/wham — copy it before restore.
COPY .deps/ .deps/
COPY src/BattleScribeSpec.TestKit/BattleScribeSpec.TestKit.csproj src/BattleScribeSpec.TestKit/
COPY src/BattleScribeSpec.XmlGen/BattleScribeSpec.XmlGen.csproj src/BattleScribeSpec.XmlGen/
COPY src/BattleScribeSpec.Cli/BattleScribeSpec.Cli.csproj src/BattleScribeSpec.Cli/
RUN dotnet restore src/BattleScribeSpec.Cli/BattleScribeSpec.Cli.csproj

# Copy source and specs
COPY src/ src/
COPY specs/ specs/

# Publish (framework-dependent; PublishAot is blocked upstream — see README AOT note)
RUN dotnet publish src/BattleScribeSpec.Cli/BattleScribeSpec.Cli.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0-preview
WORKDIR /app
COPY --from=build /app .
COPY --from=build /src/specs /specs

ENTRYPOINT ["dotnet", "bs-spec.dll"]
CMD ["run", "--all", "--specs", "/specs", "--output", "summary"]
```

- [ ] **Step 2: Delete the old Dockerfile**

```bash
git rm docker/runner.Dockerfile
```

- [ ] **Step 3: Rewrite `docker/docker-compose.yaml`**

Replace the whole file with (the `runner` service becomes a `bs-spec` service; the reference adapter is passed as an anonymous `dotnet:` connectable, matching the old command's engine-agnostic "run all specs against the reference adapter" behavior):

```yaml
# Docker Compose for running BattleScribe spec conformance tests.
#
# Usage:
#   docker compose -f docker/docker-compose.yaml up --build
#
# bs-spec starts the reference adapter as a subprocess via stdin/stdout,
# so both images are built but only bs-spec is launched directly.
# For external adapters, override the --engine connectable (exec:/dotnet:).

services:
  # Builds the bs-spec CLI image (engine-free orchestrator + embedded specs)
  bs-spec:
    build:
      context: ..
      dockerfile: docker/bs-spec.Dockerfile
    image: bs-spec:local
    # Default: run all specs against the reference adapter (anonymous dotnet: connectable)
    command: ["run", "--all", "--engine", "dotnet:/adapter/bs-reference-adapter.dll", "--specs", "/specs", "--output", "summary"]
    volumes:
      - adapter-bin:/adapter:ro
    depends_on:
      reference-adapter-build:
        condition: service_completed_successfully

  # Build-only service that publishes the reference adapter binary
  reference-adapter-build:
    build:
      context: ..
      dockerfile: docker/reference-adapter.Dockerfile
    image: bs-reference-adapter:local
    # Copy the published adapter to the shared volume, then exit
    entrypoint: ["sh", "-c", "cp -r /app/* /output/"]
    volumes:
      - adapter-bin:/output

volumes:
  adapter-bin:
```

- [ ] **Step 4: Verify the image builds (best-effort gate)**

Run: `docker build -f docker/bs-spec.Dockerfile -t bs-spec:local .`
Expected: build succeeds through publish and the runtime stage.
If Docker is unavailable in the execution environment, skip this step and record in the task report that the Dockerfile was **not** build-verified (docker is not CI-gated); do not claim it passed.

- [ ] **Step 5: Commit**

```bash
git add docker/bs-spec.Dockerfile docker/docker-compose.yaml
git rm docker/runner.Dockerfile
git commit -m "docker: build/run bs-spec run --all instead of bs-spec-runner (#271)"
```

---

### Task 3: Delete `src/BattleScribeSpec.Runner`

**Files:**
- Delete: `src/BattleScribeSpec.Runner/` (`Program.cs`, `BattleScribeSpec.Runner.csproj`, `packages.lock.json`)
- Modify: `BattleScribeSpec.slnx:5` (remove the Runner `<Project>` entry)

**Interfaces:**
- Consumes: nothing — grep confirms the only references to `BattleScribeSpec.Runner` are the `.slnx` entry and the project's own csproj. No other project `ProjectReference`s it. `tests/Regression/RunnerAndProtocolRegressionTests.cs` tests TestKit's `RosterRunner`, not this project, and is unaffected.
- Produces: a solution with no Runner project.

**Context:** Must run *after* Tasks 1–2 so no consumer points at the deleted binary. The batch pipeline it used lives in TestKit (`SpecSuiteRunner`), which stays.

- [ ] **Step 1: Remove the solution entry**

Delete this line from `BattleScribeSpec.slnx` (line 5):

```xml
    <Project Path="src/BattleScribeSpec.Runner/BattleScribeSpec.Runner.csproj" />
```

- [ ] **Step 2: Delete the project directory**

```bash
git rm -r src/BattleScribeSpec.Runner
```

- [ ] **Step 3: Confirm nothing else references it**

Run: `grep -rn "BattleScribeSpec.Runner\|bs-spec-runner" --include=*.cs --include=*.csproj --include=*.slnx --include=*.yml --include=*.yaml --include=Dockerfile .`
Expected: no matches outside `docs/` and `.superpowers/` (stale planning scratch). Any hit in `src/`, `tests/`, `.github/`, or `docker/` is a missed reference — fix it before continuing.

- [ ] **Step 4: Build and run the offline test suite**

Run:
```bash
dotnet build
dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "Category!=Conformance"
```
Expected: build succeeds (no missing-project error from the `.slnx`), tests pass. This mirrors the `checks` job's offline lane and proves the deletion left the solution coherent.

- [ ] **Step 5: Re-run the parity command one more time (Runner now gone)**

Run the Task 1 Step 3 NEW command again (the CLI path, not the deleted Runner path) and confirm exit=0. This proves the CI `checks` step still works after deletion.

- [ ] **Step 6: Commit**

```bash
git add BattleScribeSpec.slnx
git rm -r src/BattleScribeSpec.Runner
git commit -m "chore: delete BattleScribeSpec.Runner; bs-spec is the sole CLI (#271)"
```

---

### Task 4: Reframe the docs

**Files:**
- Modify: `README.md` (project-tree entry `:142`, "Future Steps" `:282-284`)
- Modify: `docs/ci-guide.md` (`:7-63` runner/docker examples, `:98` "runner", `:156` exit-code row)
- Modify: `docs/adapter-guide.md` (`:206-210` "Legacy Runner" section)
- Modify: `docs/adapter-protocol.md` (`:1-45` intro + mermaid + section headers naming "bs-spec-runner"/"Runner")
- Modify: `docs/adr/001-spec-test-kit-architecture.md` (status note near top)

**Interfaces:** Prose only — no code depends on this task.

**Context:** README and adapter-guide were mostly modernized to `bs-spec` in PR 2; only the residual `bs-spec-runner`/"Legacy Runner" mentions remain. The protocol doc still frames the client as "bs-spec-runner". Keep the protocol *wire* description unchanged; only rename the client role. Do not expand into #272's interactive-surface redesign.

- [ ] **Step 1: README — remove the Runner tree entry**

Delete this line from `README.md` (`:142`):

```
│   ├── BattleScribeSpec.Runner/   # Legacy CLI runner (bs-spec-runner)
```

- [ ] **Step 2: README — fix "Future Steps" wording**

In `README.md:282-284`, replace the two "runner" bullets so they name `bs-spec`:

```markdown
- [ ] Publish TestKit as NuGet package
- [ ] Publish `bs-spec` as a Docker image to GHCR
- [ ] Publish `bs-spec` as a dotnet global tool
```

- [ ] **Step 3: ci-guide — replace the "Using the .NET Runner Directly" example**

Replace `docs/ci-guide.md:7-40` (heading through the closing fence of the first YAML block) with a `bs-spec`-based example:

```markdown
### Using the `bs-spec` CLI Directly

```yaml
name: BattleScribe Conformance
on: [push, pull_request]

jobs:
  conformance:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      # Clone the spec repo and build the CLI
      - run: |
          git clone https://github.com/WarHub/battlescribe-spec.git /tmp/spec
          dotnet build /tmp/spec/src/BattleScribeSpec.Cli/ -c Release

      # Build your adapter
      - run: dotnet build src/MyAdapter/ -c Release

      # Run conformance tests (adapter as an anonymous dotnet: connectable)
      - run: |
          dotnet /tmp/spec/artifacts/bin/BattleScribeSpec.Cli/release/bs-spec.dll run --all \
            --engine "dotnet:src/MyAdapter/bin/Release/net10.0/my-adapter.dll" \
            --specs /tmp/spec/specs \
            --output github-actions
```
```

- [ ] **Step 4: ci-guide — update the Docker example**

In `docs/ci-guide.md:44-63`, update the commented image reference from `ghcr.io/warhub/bs-spec-runner:latest ... --adapter "/adapter/my-adapter"` to the `bs-spec` form:

```yaml
      # Run conformance (future — image not yet published)
      # - run: |
      #     docker run --rm \
      #       -v $(pwd)/my-adapter:/adapter \
      #       ghcr.io/warhub/bs-spec:latest \
      #       run --all --engine "dotnet:/adapter/my-adapter.dll" --output github-actions
```

- [ ] **Step 5: ci-guide — fix the two residual "runner" nouns**

- `docs/ci-guide.md:98`: change "The runner supports three output formats via `--output`:" to "`bs-spec` supports three output formats via `--output`:".
- `docs/ci-guide.md:156`: change the exit-code row "Runner error (bad args, adapter crash, etc.)" to "`bs-spec` error (bad args, adapter crash, etc.)".

- [ ] **Step 6: adapter-guide — delete the "Legacy Runner" section**

Remove `docs/adapter-guide.md:206-210` entirely (the `### Legacy Runner` heading and its paragraph). The runner no longer exists; `bs-spec run` is the only entry point and is already documented directly above.

- [ ] **Step 7: adapter-protocol — rename the client role**

In `docs/adapter-protocol.md`:
- `:4` change "the **bs-spec-runner** (conformance test runner)" to "the **`bs-spec` CLI** (via `bs-engine-host` or any adapter)".
- `:9` change "The runner launches the adapter" to "The client (`bs-spec`, through `bs-engine-host` or an external adapter) launches the adapter".
- `:19` mermaid: change `participant Runner as bs-spec-runner` to `participant Client as bs-spec`, and update the `Runner->>Adapter` / `Adapter-->>Runner` message lines (`:22-33`) to use `Client` in place of `Runner`.
- `:41` change "The runner sends exactly one command" to "The client sends exactly one command".
- `:45` section heading "## Runner → Adapter Commands" → "## Client → Adapter Commands".
- `:235` section heading "## Adapter → Runner Responses" → "## Adapter → Client Responses".

Leave every message schema, field, and example unchanged — only the client's *name* changes.

- [ ] **Step 8: ADR-001 — add a supersession note**

At the top of `docs/adr/001-spec-test-kit-architecture.md` (immediately after the title line, before the first section), insert:

```markdown
> **Status update (2026-07, #271):** Layers 3–4 were unified. The standalone
> `BattleScribeSpec.Runner` (`bs-spec-runner`) described below has been deleted;
> its batch pipeline moved into the TestKit and is now driven by the engine-free
> `bs-spec` CLI over the adapter protocol, with built-in engines served by
> `bs-engine-host`. The layer model and protocol rationale below remain accurate;
> the runner-specific mechanics are historical.
```

Do not rewrite the ADR body — it is a historical record.

- [ ] **Step 9: Verify no stale runner references remain in docs**

Run: `grep -rn "bs-spec-runner\|BattleScribeSpec.Runner" README.md docs/ci-guide.md docs/adapter-guide.md docs/adapter-protocol.md docs/adr/001-spec-test-kit-architecture.md`
Expected: the only remaining hits are inside the deliberate ADR-001 supersession note (which names the deleted project by design). Any other hit is a missed edit.

- [ ] **Step 10: Commit**

```bash
git add README.md docs/ci-guide.md docs/adapter-guide.md docs/adapter-protocol.md docs/adr/001-spec-test-kit-architecture.md
git commit -m "docs: reframe runner references to bs-spec; note ADR-001 supersession (#271)"
```

---

## Final verification (before opening the PR)

- [ ] `dotnet build` — clean.
- [ ] `dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "Category!=Conformance"` — green (offline lane parity with CI `checks`).
- [ ] `grep -rn "bs-spec-runner\|BattleScribeSpec.Runner"` across `src/ tests/ .github/ docker/` returns nothing.
- [ ] Push the branch and open the PR; confirm CI (`checks` + `smoke` + `ci-gate`) goes green. The `checks` lane exercises the migrated call site; `smoke` proves the engines still wire up.

## Out of scope (follow-ups)

- **NR-UI host-side warm-reuse** — `bs-engine-host` recreates the server-side engine per `setup`, cold-starting Chromium per spec. Not triggered by anything in this PR (both migrated call sites are the roster reference adapter), but MUST land before any NR conformance CI lane is repointed at `bs-spec run --all`.
- **#272** — interactive/introspection protocol surface + adapter plugin model (unifies probe/discover/break into one protocol-native verb). Separate issue.
- Publishing `bs-spec` as a NuGet/dotnet-tool/GHCR image (tracked in README "Future Steps").
