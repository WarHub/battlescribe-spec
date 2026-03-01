# CI Integration Guide

Run BattleScribe spec conformance checks in your CI pipeline.

## GitHub Actions

### Using the .NET Runner Directly

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

      # Option 1: Install runner as a tool (future — not yet published)
      # - run: dotnet tool install -g BattleScribeSpec.Runner

      # Option 2: Clone the spec repo and build
      - run: |
          git clone https://github.com/WarHub/battlescribe-spec.git /tmp/spec
          dotnet build /tmp/spec/src/BattleScribeSpec.Runner/ -c Release

      # Build your adapter
      - run: dotnet build src/MyAdapter/ -c Release

      # Run conformance tests
      - run: |
          dotnet /tmp/spec/src/BattleScribeSpec.Runner/bin/Release/net10.0/bs-spec-runner.dll \
            --adapter "dotnet:src/MyAdapter/bin/Release/net10.0/my-adapter.dll" \
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
      #       ghcr.io/warhub/bs-spec-runner:latest \
      #       --adapter "/adapter/my-adapter" --output github-actions
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
        var runner = new SpecRunner(engine);
        runner.Run(spec);
    }
}
```

> **Note:** The TestKit NuGet package is not yet published. For now, add a
> project reference to `BattleScribeSpec.TestKit.csproj`.

## Output Formats

The runner supports three output formats via `--output`:

| Format | Use Case |
|--------|----------|
| `summary` | Human-readable terminal output (default) |
| `json` | Machine-readable results for custom processing |
| `github-actions` | Annotates failures as GitHub Actions errors |

## Filtering Specs

Run a subset of specs using `--filter` and `--tag`:

```bash
# Run only cost-related specs
--filter "cost/"

# Run specs tagged with a specific tag
--tag "modifier"

# Combine filters
--filter "constraint/" --tag "validation"
```

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | All specs passed |
| 1 | One or more specs failed |
| 2 | Runner error (bad args, adapter crash, etc.) |
