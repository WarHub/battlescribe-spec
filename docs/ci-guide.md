# CI Integration Guide

Run BattleScribe spec conformance checks in your CI pipeline.

## GitHub Actions

### Using the `bs-spec` CLI Directly

```yaml
name: BattleScribe Conformance
on: [push, pull_request]

jobs:
  conformance:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7

      # The spec repo, submodule included, next to your code
      - uses: actions/checkout@v7
        with:
          repository: WarHub/battlescribe-spec
          path: spec
          submodules: recursive

      # The SDK band the spec repo pins in its global.json — not a floating '10.0.x', which moves
      # under you when a runner image does and can bring analyzer rules the CLI was never built with
      - uses: actions/setup-dotnet@v6
        with:
          global-json-file: spec/global.json

      # Build the CLI from inside spec/: dotnet picks the SDK by the global.json nearest the
      # working directory, not the project, so from your root the pin above would not apply
      - run: dotnet build src/BattleScribeSpec.Cli/ -c Release
        working-directory: spec

      # Build your adapter
      - run: dotnet build src/MyAdapter/ -c Release

      # Run conformance tests (adapter as an anonymous dotnet: connectable)
      - run: |
          dotnet spec/artifacts/bin/BattleScribeSpec.Cli/release/bs-spec.dll run --all \
            --engine "dotnet:src/MyAdapter/bin/Release/net10.0/my-adapter.dll" \
            --specs spec/specs \
            --output github-actions
```

### Using Docker

```yaml
name: BattleScribe Conformance
on: [push, pull_request]

jobs:
  conformance:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7

      # Build your adapter image
      - run: docker build -t my-adapter .

      # Run conformance (future — image not yet published to GHCR; build it from a checkout with
      # `docker build -f docker/bs-spec.Dockerfile -t bs-spec:local .` in the meantime)
      # - run: |
      #     docker run --rm \
      #       -v $(pwd)/my-adapter:/adapter:ro \
      #       ghcr.io/warhub/bs-spec:latest \
      #       run --all --engine "myengine=dotnet:/adapter/my-adapter.dll" \
      #       --engine-endpoint local --output github-actions
```

The image carries the specs and the runner and **no engine** — the built-in `battlescribe` one is
IKVM-compiled from third-party jars that cannot be redistributed in it — so `--engine` is required,
not optional. Name your adapter (`myengine=`) so per-engine spec applicability and
`--expected-failures` apply to it, and declare `--engine-endpoint local`: an undeclared endpoint fails
safe to "a third party's live service" and is throttled to 2 concurrent sessions.

### Using the TestKit NuGet Package (Recommended for .NET engines)

If your engine is written in .NET, reference the TestKit directly:

```xml
<PackageReference Include="BattleScribeSpec.TestKit" Version="*" />
```

Then create an xUnit test:

```csharp
public class ConformanceTests
{
    public static IEnumerable<object[]> AllSpecs() =>
        SpecLoader.DiscoverEmbeddedSpecs()
            .Select(s => new object[] { s.Id, s });

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void Spec(string id, SpecFile spec)
    {
        var engine = new MyRosterEngine();
        var runner = new RosterRunner(engine, new DataSourceResolver(), engineName: "my-engine");
        runner.Run(spec);
    }
}
```

> **Note:** The TestKit NuGet package is not yet published. For now, add a
> project reference to `BattleScribeSpec.TestKit.csproj`.

## Output Formats

`bs-spec` supports three output formats via `--output`:

| Format | Use Case |
|--------|----------|
| `summary` | Human-readable terminal output (default) |
| `json` | Machine-readable results for custom processing |
| `github-actions` | Annotates failures as GitHub Actions errors |

## Filtering Specs

Run a subset of specs using `--filter` or `--tags`:

```bash
# Run only cost-related specs (by path pattern)
--filter "cost/"

# Run specs tagged with a specific tag
--tags "cost"

# Run specs with any of several tags (OR semantics)
--tags "cost,constraint"

# Exclude specs with a tag
--tags "-undefined-behavior"

# Combine include and exclude (include cost OR constraint, exclude undefined-behavior)
--tags "cost,constraint,-undefined-behavior"

# Use + prefix explicitly for includes (equivalent to no prefix)
--tags "+cost,+constraint,-undefined-behavior"
```

Tags use **OR** semantics for includes (spec matches if it has *any* included tag)
and **AND** semantics for excludes (spec excluded if it has *any* excluded tag).
Exclude overrides include.

### Filtering in xUnit

In this repository's own test suite, every per-spec theory row carries its spec's tags as `Tag` traits, so
`--filter` selects by tag. Name the test project: a solution-wide run starts the CLI's tests too, which
have no tags, and a run that executes no test fails. Narrowing a profile keeps the run to that profile's
lanes — `pre-push`'s are the offline ones; without a profile, a tag filter also reaches the desktop-app
lane wherever the app is provisioned:

```bash
# Cost-tagged specs through the BattleScribe reference engine
dotnet test --project tests/BattleScribeSpec.Tests.csproj -p:TestProfile=bs --filter "Tag=cost"

# Specs tagged cost or constraint, through every offline per-spec lane
dotnet test --project tests/BattleScribeSpec.Tests.csproj -p:TestProfile=pre-push --filter "Tag=cost|Tag=constraint"

# A tag and an engine, without a profile
dotnet test --project tests/BattleScribeSpec.Tests.csproj --filter "Tag=constraint&Engine=BsGameData"
```

A tag filter never selects the five aggregate lanes — `FrozenNrRoster`, `FrozenNrUiRoster`,
`FrozenNrGameDataUi`, `LiveNrRoster` and `LiveNrUiRoster`. The frozen three are two `[Fact]`s each,
`KitchenSink` and `OtherSpecs`, and the live two one `AllSpecs` each; every one runs its specs itself, so
it has no per-spec rows to carry a tag ([running-tests.md](running-tests.md#selecting-tests-by-name)).

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Every executed spec passed |
| 1 | One or more specs failed, or `bs-spec` refused the command (bad arguments, a missing specs directory) |
| 8 | Nothing executed: the selection matched no spec, or every selected spec was skipped — a run that checks nothing does not pass. The last line on stderr says which, as one unwrapped line starting `error: selected N of M specs, executed 0`. 8 is the code Microsoft.Testing.Platform uses for "zero tests ran". |

An adapter that crashes mid-run fails the specs it was running (exit 1); it is not a separate code.
