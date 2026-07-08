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

### Using Docker

```yaml
name: BattleScribe Conformance
on: [push, pull_request]

jobs:
  conformance:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      # Build your adapter image
      - run: docker build -t my-adapter .

      # Run conformance (future — image not yet published)
      # - run: |
      #     docker run --rm \
      #       -v $(pwd)/my-adapter:/adapter \
      #       ghcr.io/warhub/bs-spec:latest \
      #       run --all --engine "dotnet:/adapter/my-adapter.dll" --output github-actions
```

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

The BattleScribe conformance tests expose spec tags as xUnit traits. Filter by tag
with `dotnet test --filter`:

```bash
# Run only cost-tagged specs
dotnet test --filter "Tag=cost"

# Run specs tagged with either cost or constraint
dotnet test --filter "Tag=cost|Tag=constraint"

# Combine tag filter with engine filter
dotnet test --filter "Tag=cost&Category=Conformance"
```

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | All specs passed |
| 1 | One or more specs failed |
| 2 | `bs-spec` error (bad args, adapter crash, etc.) |
