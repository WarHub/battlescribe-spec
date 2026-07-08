# Unified CLI PR 2 — Engine Host + CLI Rewire Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create `bs-engine-host` (hosting all built-in engines behind the NDJSON protocol, plus the interactive `probe`/`discover` verbs), rewire every `bs-spec` verb onto the protocol via the PR-1 registry/connectables, add `run --all`/`run --matrix` batch modes, and drop all engine-project references from the CLI (`IsAotCompatible`, best-effort `PublishAot`).

**Architecture:** `bs-spec` (CLI) keeps only TestKit + a new `BattleScribeSpec.XmlGen` reference; engines run as child `bs-engine-host` processes resolved through `EngineRegistry` (built-ins) or ad-hoc connectables. `run`/`verify` speak protocol v1.1 (`JsonProtocolEngine`/`JsonProtocolGameDataEngine`, `AdapterDescriber` capability gating); `probe`/`discover` forward to host verbs with inherited stdio. Batch mode reuses TestKit's `SpecSuiteRunner`, extended with the gamedata domain.

**Tech Stack:** .NET 10, System.CommandLine 2.0.9, Spectre.Console 0.57.1, xunit; PR-1 TestKit surfaces: `AdapterProcess`/`IAdapterConnection`, `AdapterOptions`/`AdapterHandler`, `AdapterDescriber`, `JsonProtocolEngine` (incl. `CaptureScreenshot`/`ExportRosterXml`/`StartRecording`/`StopRecording`), `JsonProtocolGameDataEngine`, `EngineConnectable`, `EngineRegistry`/`EngineEntry`, `SpecSuiteRunner`/`SpecSuiteOptions`/`SpecSuiteOutput`.

**Spec:** `docs/superpowers/specs/2026-07-07-unified-cli-design.md` (as amended for host verbs / XmlGen / best-effort AOT).

## Global Constraints

- Branch: `feat/271-unified-cli-pr2`. Commit after every task.
- `bs-spec-runner` (Runner project) must stay untouched and green — it is deleted in PR 3; CI's two runner invocations must keep passing throughout.
- TestKit stays `IsAotCompatible=true`: source-generated JSON only; new TestKit code reflection-free.
- Always `dotnet build` before `dotnet test --no-build` (analyzers-as-errors; stale-dll gotcha).
- Test command shape: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "<filter>" --logger "console;verbosity=minimal"`. CLI tests: `dotnet test tests/BattleScribeSpec.Cli.Tests/BattleScribeSpec.Cli.Tests.csproj`.
- The full offline gate `--filter "Category!=Conformance"` must pass at the END of every task (run it; UI-engine paths are exercised by conformance lanes, not offline).
- `docs/protocol-schema.json` + `tests/Infrastructure/ProtocolSchemaDriftTests.cs` gate protocol message changes. **This PR adds no new protocol messages**; if you think you need one, stop — the design routes non-protocol surfaces through host verbs.
- xUnit1051-as-error: async test calls take `TestContext.Current.CancellationToken` (prefer a `var ct = …;` local, matching each file's convention).
- Style: file-scoped namespaces, sealed classes, XML doc comments on public API, collection expressions.
- New solution projects are registered by adding one `<Project Path="…"/>` line to `BattleScribeSpec.slnx` under the `/src/` folder. All projects use central package versions (no `Version=` attributes) and produce `packages.lock.json` (run `dotnet restore` after adding references so the lock files update — commit them).

---

### Task 1: `BattleScribeSpec.XmlGen` project — move `CatXmlGenerator` out of the NewRecruit engine

**Files:**
- Create: `src/BattleScribeSpec.XmlGen/BattleScribeSpec.XmlGen.csproj`
- Move: `src/BattleScribeSpec.NewRecruit/CatXmlGenerator.cs` → `src/BattleScribeSpec.XmlGen/CatXmlGenerator.cs` (git mv, then edit namespace)
- Modify: `BattleScribeSpec.slnx`; `src/BattleScribeSpec.NewRecruit/BattleScribeSpec.NewRecruit.csproj`; every file using `CatXmlGenerator` (`grep -rln "CatXmlGenerator" src/ tests/ | grep -v artifacts` — currently: BsGameDataUiDriver/BsGameDataUiEngine.cs + BsGameDataUiProbe.cs, BsRosterUiDriver/BsUiRosterEngine.cs, Cli Commands/DiscoverCommand.cs + ExportXmlCommand.cs + ProbeCommand.cs, NewRecruit/NewRecruitRosterEngine.cs + NrEditorStore.cs, NrRosterUiDriver/NrRosterUiEngine.cs, tests/Features/BsUiDataStagingTests.cs + CatXmlGeneratorTests.cs, tests/Integration/LiveNrRosterSmokeTests.cs, tests/Regression/RunnerAndProtocolRegressionTests.cs)
- Test: existing `tests/Features/CatXmlGeneratorTests.cs` (behavior unchanged; only the namespace import changes)

**Interfaces:**
- Consumes: `BattleScribeSpec.Protocol` DTOs (TestKit), `WarHub.ArmouryModel.Source[.BattleScribe]` packages.
- Produces: `namespace BattleScribeSpec.XmlGen; public static class CatXmlGenerator` — same public members as today (`GenerateGameSystemXml(ProtocolGameSystem)`, `GenerateCatalogueXml(ProtocolGameSystem, ProtocolCatalogue)`, plus whatever other public members the file already has — do not change any signature). Tasks 2, 9, 10 depend on this project existing.

- [ ] **Step 1: Create the project**

`src/BattleScribeSpec.XmlGen/BattleScribeSpec.XmlGen.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>BattleScribeSpec.XmlGen</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="WarHub.ArmouryModel.Source" />
    <PackageReference Include="WarHub.ArmouryModel.Source.BattleScribe" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\BattleScribeSpec.TestKit\BattleScribeSpec.TestKit.csproj" />
  </ItemGroup>

</Project>
```

First check how the NewRecruit csproj references the WarHub packages (`grep -n "WarHub" src/BattleScribeSpec.NewRecruit/BattleScribeSpec.NewRecruit.csproj Directory.Packages.props`) and mirror exactly (package ids/central versions). NOTE: deliberately **no** `IsAotCompatible` — this project wraps XmlSerializer/reflection.

Register in `BattleScribeSpec.slnx` under `/src/`.

- [ ] **Step 2: Move the file and update the namespace**

```bash
git mv src/BattleScribeSpec.NewRecruit/CatXmlGenerator.cs src/BattleScribeSpec.XmlGen/CatXmlGenerator.cs
```

Edit its `namespace BattleScribeSpec.NewRecruit;` → `namespace BattleScribeSpec.XmlGen;`.

- [ ] **Step 3: Fix all consumers**

Add `<ProjectReference Include="..\BattleScribeSpec.XmlGen\BattleScribeSpec.XmlGen.csproj" />` to `BattleScribeSpec.NewRecruit.csproj` (its own files use the generator). For each consumer file found by the grep: replace `using BattleScribeSpec.NewRecruit;` with `using BattleScribeSpec.XmlGen;` ONLY where the file used it solely for `CatXmlGenerator` (files that also use other NewRecruit types keep both usings); fully-qualified `BattleScribeSpec.NewRecruit.CatXmlGenerator` call sites (DiscoverCommand.cs) become `BattleScribeSpec.XmlGen.CatXmlGenerator`. Driver projects (BsRosterUiDriver, BsGameDataUiDriver, NrRosterUiDriver) that referenced NewRecruit **only** for the generator: check their csproj references — if NewRecruit was referenced solely for CatXmlGenerator, swap the project reference to XmlGen; otherwise add XmlGen alongside.

- [ ] **Step 4: Build + full offline suite**

Run: `dotnet restore && dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "Category!=Conformance" --logger "console;verbosity=minimal"`
Expected: PASS with the same counts as the branch baseline (record both). Also `dotnet test tests/BattleScribeSpec.Cli.Tests/BattleScribeSpec.Cli.Tests.csproj --no-build` — ExportXmlTests still pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor: move CatXmlGenerator to new BattleScribeSpec.XmlGen project (#271)"
```

---

### Task 2: `bs-engine-host` project — serve mode (adapter protocol for all built-in engines)

**Files:**
- Create: `src/BattleScribeSpec.EngineHost/BattleScribeSpec.EngineHost.csproj`
- Create: `src/BattleScribeSpec.EngineHost/Program.cs`
- Create: `src/BattleScribeSpec.EngineHost/HostEngineFactory.cs` (absorbs `src/BattleScribeSpec.Cli/EngineFactory.cs` — copy now, CLI copy deleted in Task 10)
- Create: `src/BattleScribeSpec.EngineHost/ServeCommand.cs`
- Modify: `BattleScribeSpec.slnx`
- Test: `tests/Features/EngineHostServeTests.cs` (new)

**Interfaces:**
- Consumes: TestKit (`AdapterHandler`, `AdapterOptions`, `AdapterCapabilities`), XmlGen, all 6 engine projects (`BattleScribeRosterEngine`, `BattleScribeGameDataEngine`, `BsUiRosterEngine`+`BsUiOptions`, `BsGameDataUiEngine`, `NewRecruitRosterEngine`, `NewRecruitGameDataEngine`, `NrRosterUiEngine`, `NrGameDataUiEngine`).
- Produces: executable `bs-engine-host` with default verb `serve`:
  `bs-engine-host serve --engine <battlescribe|battlescribe-ui|newrecruit|newrecruit-ui> [--headed] [--keep-alive]` — speaks protocol v1.1 on stdio until stdin closes. Tasks 3–9 depend on this contract; the argv shape is composed by `EngineHostLocator` (Task 3).

- [ ] **Step 1: Project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <RootNamespace>BattleScribeSpec.EngineHost</RootNamespace>
    <AssemblyName>bs-engine-host</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="System.CommandLine" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\BattleScribeSpec.BattleScribe\BattleScribeSpec.BattleScribe.csproj" />
    <ProjectReference Include="..\BattleScribeSpec.BsGameDataUiDriver\BattleScribeSpec.BsGameDataUiDriver.csproj" />
    <ProjectReference Include="..\BattleScribeSpec.BsRosterUiDriver\BattleScribeSpec.BsRosterUiDriver.csproj" />
    <ProjectReference Include="..\BattleScribeSpec.NewRecruit\BattleScribeSpec.NewRecruit.csproj" />
    <ProjectReference Include="..\BattleScribeSpec.NrGameDataUiDriver\BattleScribeSpec.NrGameDataUiDriver.csproj" />
    <ProjectReference Include="..\BattleScribeSpec.NrRosterUiDriver\BattleScribeSpec.NrRosterUiDriver.csproj" />
    <ProjectReference Include="..\BattleScribeSpec.XmlGen\BattleScribeSpec.XmlGen.csproj" />
  </ItemGroup>

</Project>
```

Check whether the ReferenceAdapter's `CopyIkvmAssemblies` target is needed here too (the host references BattleScribeSpec.BattleScribe the same way the reference adapter does — copy that `<Target>` block from `src/BattleScribeSpec.ReferenceAdapter/BattleScribeSpec.ReferenceAdapter.csproj` verbatim, adjusting nothing). Register in slnx.

- [ ] **Step 2: HostEngineFactory**

Copy `src/BattleScribeSpec.Cli/EngineFactory.cs` to `src/BattleScribeSpec.EngineHost/HostEngineFactory.cs`; rename class `EngineFactory` → `HostEngineFactory`, namespace → `BattleScribeSpec.EngineHost`, visibility `internal` stays. Two edits:
- It calls `SpecLoading.FindRepoRoot()` (a Cli type) — inline a private copy:

```csharp
private static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    for (; dir is not null; dir = dir.Parent)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            return dir.FullName;
        }
    }

    return null;
}
```

(compare with the Cli original at `src/BattleScribeSpec.Cli/SpecLoading.cs:138-152` and keep the exact semantics).
- It calls `Ui.Info(...)` (Cli's Spectre wrapper) — replace with `Console.Error.WriteLine($"[bs-engine-host] ...")` (protocol rule: stdout is protocol-only, stderr is free).

- [ ] **Step 3: ServeCommand — AdapterOptions per engine**

`src/BattleScribeSpec.EngineHost/ServeCommand.cs`:

```csharp
using System.CommandLine;
using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.NrRosterUiDriver;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.EngineHost;

/// <summary>
/// <c>bs-engine-host serve --engine X</c> — expose a built-in engine over the NDJSON
/// adapter protocol on stdio. One engine identity per process; the runner side pools
/// processes for parallelism.
/// </summary>
internal static class ServeCommand
{
    public static Command Create()
    {
        var engine = new Option<string>("--engine")
        {
            Description = "Built-in engine: battlescribe, battlescribe-ui, newrecruit, newrecruit-ui.",
            Required = true,
        };
        var headed = new Option<bool>("--headed") { Description = "Show the browser/app window." };
        var keepAlive = new Option<bool>("--keep-alive") { Description = "Keep the BattleScribe app alive between runs (battlescribe-ui)." };

        var command = new Command("serve", "Serve a built-in engine over the NDJSON adapter protocol on stdio.");
        command.Options.Add(engine);
        command.Options.Add(headed);
        command.Options.Add(keepAlive);

        command.SetAction(async (parseResult, ct) =>
        {
            var name = parseResult.GetValue(engine)!;
            var headless = !parseResult.GetValue(headed);
            var keep = parseResult.GetValue(keepAlive);

            await AdapterHandler.RunAsync(BuildOptions(name, headless, keep), Console.In, Console.Out, ct);
            return 0;
        });

        return command;
    }

    internal static AdapterOptions BuildOptions(string name, bool headless, bool keepAlive) => new()
    {
        Name = name,
        Version = typeof(ServeCommand).Assembly.GetName().Version?.ToString(),
        RosterEngineFactory = () =>
            HostEngineFactory.CreateRosterEngineAsync(name, headless, keepAlive).GetAwaiter().GetResult(),
        GameDataEngineFactory = () =>
            HostEngineFactory.CreateGameDataEngineAsync(name, headless).GetAwaiter().GetResult(),
        Capabilities = new AdapterCapabilities
        {
            Screenshot = name is "battlescribe-ui" or "newrecruit-ui",
            Record = name is "battlescribe-ui",
            RosterXml = name is "battlescribe-ui",
            MaxParallel = name is "battlescribe-ui" ? 1 : 0,
        },
        ScreenshotProvider = e => e switch
        {
            BsUiRosterEngine bs => bs.CaptureScreenshotAsync().GetAwaiter().GetResult(),
            NrRosterUiEngine nr => nr.CaptureScreenshotAsync().GetAwaiter().GetResult(),
            _ => null,
        },
        RosterXmlExporter = e => e is BsUiRosterEngine bs ? bs.ExportRosterXmlAsync().GetAwaiter().GetResult() : null,
        RecordStarter = e =>
        {
            if (e is BsUiRosterEngine bs)
            {
                bs.StartRecordingAsync().GetAwaiter().GetResult();
            }
        },
        RecordStopper = e => e is BsUiRosterEngine bs
            ? bs.StopRecordingAsync().GetAwaiter().GetResult()?.ToJsonString()
            : null,
    };
}
```

Adjust the `StopRecordingAsync` mapping to the driver's real return type (RunCommand today does `actions.ToJsonString(new JsonSerializerOptions { WriteIndented = true })` — mirror that, `using System.Text.Json;`). Verify each driver method name via grep before finalizing (`CaptureScreenshotAsync`, `ExportRosterXmlAsync`, `StartRecordingAsync`, `StopRecordingAsync`).

`Program.cs`:

```csharp
using System.CommandLine;
using BattleScribeSpec.EngineHost;

var root = new RootCommand("bs-engine-host — built-in BattleScribe/NewRecruit engines behind the NDJSON adapter protocol.");
root.Subcommands.Add(ServeCommand.Create());

// probe/discover verbs are added in a later task.
return await root.Parse(args).InvokeAsync();
```

Note: `serve` is an explicit verb (not the implicit root action) so `probe`/`discover` can join cleanly; `EngineHostLocator` (Task 3) always passes `serve` explicitly.

- [ ] **Step 4: Wire-level smoke test**

`tests/Features/EngineHostServeTests.cs` — spawn the built host and drive the in-process battlescribe engine over the wire (offline-safe; UI engines are covered by conformance lanes later):

```csharp
using BattleScribeSpec.Protocol;
using Xunit;

namespace BattleScribeSpec.Tests.Features;

public sealed class EngineHostServeTests
{
    private static string FindHostDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BattleScribeSpec.slnx")))
        {
            dir = dir.Parent!;
        }

        Assert.NotNull(dir);
        var dll = Path.Combine(dir.FullName, "artifacts", "bin",
            "BattleScribeSpec.EngineHost", "debug", "bs-engine-host.dll");
        Assert.True(File.Exists(dll), $"Engine host not built: {dll}");
        return dll;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Serve_Battlescribe_DescribesAndRunsRosterSetup()
    {
        var ct = TestContext.Current.CancellationToken;
        using var process = AdapterProcess.Start("dotnet", $"{FindHostDll()} serve --engine battlescribe");

        var described = await AdapterDescriber.DescribeAsync(process);
        Assert.Equal("battlescribe", described.Name);
        Assert.Equal(["roster", "gamedata"], described.Domains);
        Assert.False(described.Capabilities.Screenshot);

        var setup = await process.SendCommandAsync(new SetupCommand
        {
            GameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" },
        }, ct);
        Assert.IsType<SetupResult>(setup);
        Assert.IsType<StateResponse>(await process.SendCommandAsync(new GetStateCommand(), ct));
        Assert.IsType<TeardownResult>(await process.SendCommandAsync(new TeardownCommand(), ct));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Serve_Battlescribe_ScreenshotAnswersNotSupported()
    {
        var ct = TestContext.Current.CancellationToken;
        using var process = AdapterProcess.Start("dotnet", $"{FindHostDll()} serve --engine battlescribe");
        await process.SendCommandAsync(new SetupCommand
        {
            GameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" },
        }, ct);

        var response = await process.SendCommandAsync(new ScreenshotCommand(), ct);
        Assert.IsType<ProtocolError>(response);
    }
}
```

- [ ] **Step 5: Build + tests + commit**

Run: `dotnet restore && dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "FullyQualifiedName~EngineHostServeTests" --logger "console;verbosity=minimal"`
Expected: 2/2 pass. Then the full offline gate.

```bash
git add -A
git commit -m "feat(engine-host): bs-engine-host serve mode for built-in engines (#271)"
```

---

### Task 3: `EngineHostLocator` — resolve built-in registry entries to host launches

**Files:**
- Create: `src/BattleScribeSpec.TestKit/Engines/EngineHostLocator.cs`
- Test: `tests/Features/EngineHostLocatorTests.cs` (new)

**Interfaces:**
- Consumes: `EngineEntry` (PR 1).
- Produces:

```csharp
namespace BattleScribeSpec.Engines;

/// <summary>Launch descriptor: what to Start() for an engine.</summary>
public sealed record EngineLaunch(string Executable, string Arguments);

public static class EngineHostLocator
{
    /// <summary>
    /// Resolve an entry to a concrete launch. Launchable entries pass through
    /// (arguments verbatim). Built-in entries locate bs-engine-host:
    /// 1. env BSSPEC_ENGINE_HOST (path to bs-engine-host.dll or executable),
    /// 2. bs-engine-host.dll next to the current entry assembly,
    /// 3. artifacts/bin/BattleScribeSpec.EngineHost/<pivot>/bs-engine-host.dll relative
    ///    to the repo root (walk up from AppContext.BaseDirectory to a .git dir),
    ///    trying the same pivot as the current assembly's artifacts path, else "debug",
    /// 4. "bs-engine-host" on PATH.
    /// Throws InvalidOperationException naming all probed locations when not found.
    /// A .dll resolution launches via "dotnet"; an executable launches directly.
    /// </summary>
    public static EngineLaunch Resolve(EngineEntry entry, bool headed = false, bool keepAlive = false);
}
```

For built-ins the arguments are `serve --engine <entry.Name>` plus ` --headed` when `headed` and ` --keep-alive` when `keepAlive`. For launchable (non-builtin) entries, `headed`/`keepAlive` are conveyed via env instead — Resolve does NOT handle that (the CLI sets `BSSPEC_HEADED=1`/`BSSPEC_KEEP_ALIVE=1` on the child process env when spawning; document in the XML doc comment). AOT constraint: pure path probing, no reflection.

- [ ] **Step 1: Failing tests**

```csharp
using BattleScribeSpec.Engines;
using Xunit;

namespace BattleScribeSpec.Tests.Features;

public sealed class EngineHostLocatorTests
{
    private static readonly EngineEntry Builtin =
        new("battlescribe", null, null, ["roster", "gamedata"], 0, Builtin: true);

    [Fact]
    public void LaunchableEntry_PassesThrough()
    {
        var entry = new EngineEntry("wham", "node", "adapters/wham.js", ["roster"], 0, Builtin: false);
        var launch = EngineHostLocator.Resolve(entry);
        Assert.Equal("node", launch.Executable);
        Assert.Equal("adapters/wham.js", launch.Arguments);
    }

    [Fact]
    public void Builtin_UsesEnvOverride_WithServeArgs()
    {
        var fake = Path.Combine(Path.GetTempPath(), "fake-host.dll");
        File.WriteAllText(fake, "");
        try
        {
            Environment.SetEnvironmentVariable("BSSPEC_ENGINE_HOST", fake);
            var launch = EngineHostLocator.Resolve(Builtin, headed: true, keepAlive: true);
            Assert.Equal("dotnet", launch.Executable);
            Assert.Equal($"{fake} serve --engine battlescribe --headed --keep-alive", launch.Arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BSSPEC_ENGINE_HOST", null);
            File.Delete(fake);
        }
    }

    [Fact]
    public void Builtin_FindsHostInArtifacts()
    {
        // Running from the repo, probe 3 (artifacts walk) must find the built host.
        var launch = EngineHostLocator.Resolve(Builtin);
        Assert.Contains("bs-engine-host", launch.Arguments + launch.Executable);
        Assert.StartsWith("dotnet", launch.Executable);
        Assert.Contains("serve --engine battlescribe", launch.Arguments);
    }
}
```

(The env-var test mutates process env — keep the try/finally exactly; xunit runs collections in parallel, so also add `[Collection("EnvVars")]`-style isolation ONLY if the suite already has such a collection convention — check `grep -rn "SetEnvironmentVariable" tests/Features/ | head` for prior art and copy it.)

- [ ] **Step 2: Implement** per the XML-doc contract above. Probe order exactly as documented; pivot detection: the current assembly path under `artifacts/bin/<Project>/<pivot>/` — extract `<pivot>` by walking the BaseDirectory path segments after `bin`; fall back to `"debug"`.

- [ ] **Step 3: Build, run the three tests, run the offline gate, commit**

```bash
git add -A
git commit -m "feat(engines): EngineHostLocator resolves builtins to bs-engine-host launches (#271)"
```

---

### Task 4: CLI engine surface — `--engine` as string + registry resolution

**Files:**
- Modify: `src/BattleScribeSpec.Cli/EngineSpec.cs` (full rewrite of `EngineSpec`/`EngineOptions`; `EngineProduct` enum deleted)
- Modify: `tests/BattleScribeSpec.Cli.Tests/EngineSpecTests.cs`
- Test: same file

**Interfaces:**
- Consumes: `EngineConnectable`, `EngineRegistry`, `EngineEntry`, `EngineHostLocator` (TestKit).
- Produces (all later CLI tasks consume exactly this):

```csharp
internal enum EngineDomain { Roster, Gamedata }          // unchanged
internal enum OutputFormat { Tree, Json }                 // unchanged

/// <summary>A resolved engine selection: registry entry + domain + launch shaping.</summary>
internal sealed record EngineSelection(EngineEntry Entry, EngineDomain Domain, bool Headed, bool KeepAlive)
{
    /// <summary>Identity for applicability/assertions/labels; null for anonymous ad-hoc adapters.</summary>
    public string? EngineName => Entry.Name;

    /// <summary>Assertion engine: strip a trailing "-ui" from the identity.</summary>
    public string? AssertionEngineName =>
        EngineName is { } n ? (n.EndsWith("-ui", StringComparison.Ordinal) ? n[..^3] : n) : null;

    public string Display => $"{(Domain == EngineDomain.Gamedata ? "gamedata" : "roster")}/{EngineName ?? "adapter"}";

    /// <summary>Start the adapter process for this selection.</summary>
    public Protocol.AdapterProcess StartProcess()
    {
        var launch = Engines.EngineHostLocator.Resolve(Entry, Headed, KeepAlive);
        return Protocol.AdapterProcess.Start(launch.Executable, launch.Arguments);
    }
}
```

`EngineOptions` keeps the same option set but `Engine` becomes `Option<string>` (default `"battlescribe"`, description listing built-ins + connectable forms + `engines.json` names). `Resolve(ParseResult, string? specInput)` becomes:

```csharp
public EngineSelection Resolve(ParseResult parseResult, string? specInput)
{
    var gamedata = parseResult.GetValue(Gamedata);
    var roster = parseResult.GetValue(Roster);
    if (gamedata && roster)
    {
        throw new CliInputException("--gamedata and --roster are mutually exclusive.");
    }

    var domain = (gamedata, roster) switch
    {
        (true, _) => EngineDomain.Gamedata,
        (_, true) => EngineDomain.Roster,
        _ => SpecLoading.InferEngineType(specInput) == "gamedata" ? EngineDomain.Gamedata : EngineDomain.Roster,
    };

    EngineConnectable connectable;
    try
    {
        connectable = EngineConnectable.Parse(parseResult.GetValue(Engine)!);
    }
    catch (FormatException ex)
    {
        throw new CliInputException(ex.Message);
    }

    // --ui sugar: append -ui to a plain registry name (idempotent).
    if (parseResult.GetValue(Ui) && connectable is { IsLaunchable: false, Name: { } plain }
        && !plain.EndsWith("-ui", StringComparison.Ordinal))
    {
        connectable = connectable with { Name = plain + "-ui" };
    }

    EngineEntry entry;
    try
    {
        entry = EngineRegistry.LoadDefault().Resolve(connectable);
    }
    catch (KeyNotFoundException ex)
    {
        throw new CliInputException(ex.Message);
    }

    return new EngineSelection(entry, domain, parseResult.GetValue(Headed), KeepAlive: false);
}
```

(`KeepAlive` is set by RunCommand from its own `--keep-alive` option via `selection with { KeepAlive = ... }`. `--ui` combined with a launchable connectable is an error: add that guard with `CliInputException("--ui cannot be combined with an exec:/dotnet: connectable; name the engine variant directly.")`. `InferEngineType(specInput)` must tolerate null — check `SpecLoading.InferEngineType`'s signature (it takes `string?` already).)

- [ ] **Step 1: Rewrite EngineSpecTests first** — replace product-enum cases with string cases: resolution of each builtin name, `--ui` sugar (`newrecruit`+`--ui` → entry name `newrecruit-ui`; `newrecruit-ui`+`--ui` idempotent), `name=dotnet:x.dll` identity+launch, unknown name → `CliInputException`, `--ui`+`exec:` → `CliInputException`, `AssertionEngineName` stripping, `--gamedata`/`--roster` exclusivity. Write them against the Produces block above; run to see them fail to compile.
- [ ] **Step 2: Implement the rewrite.** Keep `CliInputException` as-is. Delete `EngineProduct`.
- [ ] **Step 3: Fix the compile fallout in Commands** (RunCommand/VerifyCommand/ProbeCommand reference `EngineProduct`/`EngineSpec` members): make the MINIMAL mechanical adjustments to keep compiling with old behavior where possible — full rewires happen in Tasks 5–9; where a command cannot work without the old enum (ProbeCommand's product switch), temporarily switch on `selection.EngineName` strings. The build must be green at the end of this task even though rewiring is incomplete.
- [ ] **Step 4: Run Cli.Tests + offline gate; commit**

```bash
git add -A
git commit -m "feat(cli): --engine accepts names, connectables, and engines.json entries (#271)"
```

---

### Task 5: RunCommand — single-spec roster path over the protocol

**Files:**
- Modify: `src/BattleScribeSpec.Cli/Commands/RunCommand.cs` (`RunRosterAsync` + `Gate` + recording/save-roster/screenshot plumbing)
- Create: `src/BattleScribeSpec.Cli/ProtocolBreakRepl.cs` (replaces `BreakRepl.cs`, which is deleted in Task 10)
- Test: `tests/BattleScribeSpec.Cli.Tests/RunProtocolTests.cs` (new)

**Interfaces:**
- Consumes: `EngineSelection` (Task 4), `AdapterDescriber`, `JsonProtocolEngine` (+ its 4 parity methods), `RosterRunner`.
- Produces: `RunRosterAsync` driving any engine via protocol. `ProtocolBreakRepl.Run(IAdapterConnection connection, int stepIndex, string stepDescription)` — commands: `state` (getState → dump), `errors` (getErrors), `screenshot <file.png>`, raw `{...}` JSON line passthrough (send verbatim via `AdapterProcess.SendAsync`), `continue`/empty to resume, `quit` to abort the run.

Transformation spec for `RunRosterAsync` (current file is the post-Task-4 state; original logic reference `git show <task4-commit>:src/BattleScribeSpec.Cli/Commands/RunCommand.cs`):
1. Engine creation block (`EngineFactory.CreateRosterEngineAsync`) →

```csharp
Protocol.AdapterProcess process;
DescribeResult described;
try
{
    process = options.Engine.StartProcess();
    described = await AdapterDescriber.DescribeAsync(process);
}
catch (Exception ex)
{
    Ui.Error($"Error starting engine: {ex.Message}");
    return 1;
}

using var _ = process;
var engine = new JsonProtocolEngine(process,
    spec.Setup.DataSource is not null ? TimeSpan.FromMinutes(5) : null);
```

2. `Gate(...)` calls switch from `EngineCapabilities.SupportsX(engine)` to `described.Capabilities.Screenshot` / `.Record` / `.RosterXml`.
3. Screenshot capture in `OnStepCompleted`: `EngineCapabilities.CaptureScreenshotAsync(engine)` → `engine.CaptureScreenshot()` wrapped in the existing try/catch (returns `byte[]`, throws `NotSupportedException` — treat like the old null path by catching it in the same catch).
4. Save-roster block: `engine is BsUiRosterEngine bsUi … bsUi.ExportRosterXmlAsync()` → `engine.ExportRosterXml()` guarded by the already-gated `saveRosterDir` (capability-gated, so NotSupported can't happen; keep the try/catch).
5. Recording blocks: `recordingEngine.StartRecordingAsync()` → `engine.StartRecording()`; `StopRecordingAsync()` → `engine.StopRecording()` returning the actions JSON string — write it to `recordPath` directly (it is already JSON; pretty-print via `JsonSerializer` is NOT available reflection-free in the CLI — write verbatim and note the formatting difference in the task report).
6. `--break`: `BreakRepl.Run(engine, …)` → `ProtocolBreakRepl.Run(process, …)`.
7. NR-UI headed hold-open (`engine is NrRosterUiDriver.NrRosterUiEngine`) → `options.Engine.EngineName == "newrecruit-ui" && options.Headed`.
8. `ReportDiagnosticDumps` references `BsUiDiagnostics.DiagnosticsDirectory` (a driver type!) — replace with the literal path convention it encodes: read the property's value now (`grep -n "DiagnosticsDirectory" src/BattleScribeSpec.BsRosterUiDriver/ -r`) and inline the same path composition with a comment naming the origin.

- [ ] **Step 1:** Write `ProtocolBreakRepl` (full code — model the loop on old `BreakRepl.cs`, commands per the Produces block; `state` renders via `StateDumper.Dump`).
- [ ] **Step 2:** Apply the transformation; build.
- [ ] **Step 3:** `RunProtocolTests`: end-to-end `Program.RunAsync("run", "<spec-id>", "--engine", "battlescribe=dotnet:<reference-adapter-dll>")` for a small embedded/known spec asserting exit code 0 (find a suitable tiny roster spec id via `SpecLoader.DiscoverSpecs` conventions — e.g. the kitchen-sink protocol spec used by CI); `[Trait("Category","Integration")]`. Also a parse-level test that `--break` + `--engine battlescribe` on gamedata errors as before.
- [ ] **Step 4:** Manual sanity: `dotnet run --project src/BattleScribeSpec.Cli -- run <same spec> --engine battlescribe` (spawns the host) — confirm PASS output looks like pre-rewire. Record output in the report.
- [ ] **Step 5:** Offline gate + Cli.Tests; commit `feat(cli): run drives roster engines over the adapter protocol (#271)`.

---

### Task 6: RunCommand — gamedata path over the protocol

**Files:**
- Modify: `src/BattleScribeSpec.Cli/Commands/RunCommand.cs` (`RunGameDataAsync`)
- Test: extend `tests/BattleScribeSpec.Cli.Tests/RunProtocolTests.cs`

Transformation: `EngineFactory.CreateGameDataEngineAsync(engineName, headless)` → `options.Engine.StartProcess()` + `AdapterDescriber` + `new JsonProtocolGameDataEngine(process)`. Applicability: keep `spec.IsApplicableTo(engineName)` using `options.Engine.EngineName` (skip-with-warning when null identity? No — anonymous adapters have no applicability data; run unconditionally). NEW: if `described.Domains` lacks `"gamedata"`, `Ui.Warn` + return 0 (skip, mirroring the applicability skip). `GameDataRunner(engine, engineName)` takes the string name — pass `options.Engine.EngineName ?? "adapter"`.

- [ ] Steps: failing test (gamedata spec via `--engine battlescribe=dotnet:<reference-adapter>`; assert exit 0), implement, offline gate, commit `feat(cli): run drives gamedata engines over the adapter protocol (#271)`.

---

### Task 7: VerifyCommand over the protocol

**Files:**
- Modify: `src/BattleScribeSpec.Cli/Commands/VerifyCommand.cs`
- Test: parse-level in `CommandSurfaceTests` (engine column resolution), plus manual verification

Transformation: `--engines` CSV entries each go through `EngineConnectable.Parse` + `EngineRegistry.LoadDefault().Resolve` (so `verify x --engines battlescribe,wham=dotnet:adapter.dll` works). Per engine: `StartProcess()` (headed per `--headed`) + describe; "engine unavailable" now = spawn/describe failure (same `Outcome.Unavailable` handling); domain check: described `Domains` lacking gamedata → all cells `Skip` with reason. Engine instance reuse across specs is preserved (one process per engine for the whole matrix — `JsonProtocolGameDataEngine` per spec over the same process is WRONG: `gamedataSetup` re-creates server-side, so create ONE `JsonProtocolGameDataEngine` per engine process and let `GameDataRunner.Run(spec)` call Setup per spec exactly as the in-process engines did — verify GameDataRunner drives Setup per run; it does (the runner owns the setup lifecycle)).

- [ ] Steps: implement, run `verify` manually for one spec against `battlescribe` (host) + `battlescribe=dotnet:<reference-adapter>` and compare matrix output sanity, offline gate, commit `feat(cli): verify resolves engines via registry and drives them over the protocol (#271)`.

---

### Task 8: SpecSuiteRunner — gamedata domain support (TestKit)

**Files:**
- Modify: `src/BattleScribeSpec.TestKit/Batch/SpecSuiteOptions.cs`, `SpecSuiteRunner.cs`
- Test: extend `tests/Features/SpecSuiteRunnerTests.cs`

**Interfaces:**
- Produces: `SpecSuiteOptions.Domains` (`IReadOnlyList<string>`, default `["roster"]` for Runner compatibility — the Runner shell keeps passing the default). When `Domains` contains `"gamedata"`, discovery adds `SpecLoader.FindGameDataSpecsDirectory()`/`DiscoverGameDataSpecs` (or gamedata specs under `--specs <dir>` when it contains a `gamedata` subtree — mirror how roster discovery treats the explicit dir); execution per gamedata spec: `new JsonProtocolGameDataEngine(process, timeout)` + `GameDataRunner(engine, options.AssertionEngine ?? options.EngineFilter).Run(spec)`; applicability/xfail via the same `IsApplicableTo`/`IsExpectedToFail` on `GameDataSpecFile`. A pre-flight `AdapterDescriber.DescribeAsync` on the first pooled process gates the gamedata domain: not described → every gamedata spec becomes a skip record `"Skipped: engine does not support gamedata domain"`.
- The worker pool is shared across domains (same processes serve both — the reference adapter and host both do).

- [ ] Steps: failing test (batch over reference adapter with `Domains=["roster","gamedata"]` + a filter matching one known gamedata spec → passes; and legacy default excludes gamedata), implement, verify the Runner still builds/passes untouched (it compiles against the extended options with defaults), offline gate, commit `feat(testkit): SpecSuiteRunner runs gamedata specs over the wire (#271)`.

---

### Task 9: `run --all` and `run --matrix`

**Files:**
- Modify: `src/BattleScribeSpec.Cli/Commands/RunCommand.cs` (mode dispatch), `src/BattleScribeSpec.Cli/CommandFactory.cs` (nothing new — same verb)
- Create: `src/BattleScribeSpec.Cli/Commands/RunBatch.cs`
- Test: `tests/BattleScribeSpec.Cli.Tests/RunBatchSurfaceTests.cs` (parse-level) + integration case in `RunProtocolTests`

**Interfaces:**
- Consumes: `SpecSuiteRunner`/`SpecSuiteOutput` (+ Task 8 domains), `CompatibilityMatrix`, `EngineSelection`, described `maxParallel`.
- Produces the plan-of-record CLI surface:
  - `spec` argument becomes optional (`Arity = ArgumentArity.ZeroOrOne`); exactly one of `<spec>`, `--all`, `--matrix <dir>` (clear `CliInputException` otherwise).
  - New options on `run`: `--all`, `--matrix <dir>`, `--specs <dir>`, `--filter <csv>`, `--tags <expr>`, `--report <path>`, `--expected-failures <engine>`, `--assertion-engine <engine>`, `--workers <int>`.
  - Modal `--output`: single-spec accepts `tree|json`; `--all` accepts `summary|json|github-actions` — implement by widening the option to `Option<string>` with a custom validator per mode; `--json` shortcut stays single-spec-only (error under `--all`).
  - Batch defaults: engine applicability filter = `EngineSelection.EngineName`; `--expected-failures` defaults to nothing (explicit, as today's runner); assertion engine defaults to `AssertionEngineName`.
  - Workers clamping: `effective = entry.MaxParallel is > 0 and var m && workers > m ? m : workers`, warn when clamped; after the first process starts, `AdapterDescriber` result may further clamp (described `MaxParallel > 0 && < effective`) — warn and continue with the described value (pool sizes accordingly: describe BEFORE spawning the rest of the pool; `SpecSuiteRunner` owns the pool, so pass the final worker count in options — do the describe probe in `RunBatch` with a short-lived process, then dispose it).
  - Interactive/artifact flags under `--all`: warn-once-and-ignore (`--screenshots --timeline --record --save-roster --break --all-steps`); `--headed` stays honored.
  - `--matrix <dir>`: port the Runner's 15-line block (`*-conformance.json` → `CompatibilityMatrix.GenerateMarkdown`) verbatim; runs nothing.
  - Exit code: `SpecSuiteResult.ExitCode`.

- [ ] Steps: parse-level tests first (mutual exclusion, modal output validation, workers clamp warning is logic-level — test the clamp function directly by making it an internal static), implement `RunBatch.ExecuteAsync(...)`, integration test `run --all --engine battlescribe=dotnet:<reference-adapter> --filter protocol/protocol-kitchen-sink --output summary` → exit 0 with `Results:` line, offline gate, commit `feat(cli): run --all batch mode and run --matrix (#271)`.

---

### Task 10: probe/discover host verbs + CLI forwarders; drop engine references; AOT flags

**Files:**
- Move: `src/BattleScribeSpec.Cli/Commands/ProbeCommand.cs` → `src/BattleScribeSpec.EngineHost/ProbeCommand.cs`; `src/BattleScribeSpec.Cli/Commands/DiscoverCommand.cs` → `src/BattleScribeSpec.EngineHost/DiscoverCommand.cs` (namespaces → `BattleScribeSpec.EngineHost`; their `EngineOptions` usage replaced by plain `--engine <string>` + `--headed` options local to the host; `SpecLoading` calls satisfied by a host-local copy `HostSpecLoading.cs` of the spec-resolution helpers they need — copy only the methods used; `Ui.*` → `Console.Error` writes)
- Create: `src/BattleScribeSpec.Cli/Commands/ProbeForwardCommand.cs`, `src/BattleScribeSpec.Cli/Commands/DiscoverForwardCommand.cs`
- Modify: `src/BattleScribeSpec.Cli/CommandFactory.cs` (register forwarders), `src/BattleScribeSpec.EngineHost/Program.cs` (register verbs)
- Delete: `src/BattleScribeSpec.Cli/EngineFactory.cs`, `EngineCapabilities.cs`, `BreakRepl.cs`
- Modify: `src/BattleScribeSpec.Cli/BattleScribeSpec.Cli.csproj` — remove all six engine project references; add `BattleScribeSpec.TestKit` + `BattleScribeSpec.XmlGen`; add `<IsAotCompatible>true</IsAotCompatible>`
- Test: `CommandSurfaceTests` still passes; forwarder parse-level tests

**Forwarder contract** (both verbs): parse `spec` + engine options; resolve `EngineSelection` (Task 4); `EngineHostLocator.Resolve(entry, headed: true)` — probe/discover are inherently headed; error for non-builtin launchable entries (`CliInputException("probe/discover require a built-in engine; adapters expose no probe surface.")`); then spawn with **inherited stdio** (NOT redirected — REPLs must reach the console):

```csharp
var launch = EngineHostLocator.Resolve(selection.Entry, headed: true);
// Replace the "serve --engine X" argument tail with "<verb> <spec> --engine X [--gamedata|--roster]".
var psi = new ProcessStartInfo
{
    FileName = launch.Executable,
    Arguments = launch.Arguments.Replace("serve ", $"{verb} {EscapeArg(specInput)} ", StringComparison.Ordinal),
    UseShellExecute = false,
};
using var child = Process.Start(psi)!;
await child.WaitForExitAsync();
return child.ExitCode;
```

(Write a small internal `HostForwarder` helper shared by both forwarders rather than duplicating; `EscapeArg` quotes when the value contains spaces. The `Replace("serve ", …)` hack is brittle — instead have `EngineHostLocator.Resolve` accept the verb: add an optional `string verb = "serve"` parameter composing `{verb} --engine <name> …` and for probe/discover pass the spec as an additional argument the locator appends. Choose the cleaner form and keep `EngineHostLocatorTests` updated.)

- [ ] **Step 1:** Move the two commands into the host; get the host building (host-local spec loading + console output).
- [ ] **Step 2:** Forwarders + CommandFactory registration; `bs-spec probe --help` / `discover --help` list the same user-visible options as before.
- [ ] **Step 3:** Drop the engine references + add `IsAotCompatible` to the Cli csproj; delete the three dead files; `dotnet restore` (lock files!); fix any straggler compile errors (there must be no remaining engine-type usage — `grep -rn "BsUiRosterEngine\|NrRosterUiEngine\|BsGameDataUiEngine\|NrGameDataUiEngine\|NewRecruitRosterEngine\|NewRecruitGameDataEngine\|BattleScribeRosterEngine\|BattleScribeGameDataEngine" src/BattleScribeSpec.Cli/` must return nothing).
- [ ] **Step 4:** AOT analyzer: `dotnet build src/BattleScribeSpec.Cli/BattleScribeSpec.Cli.csproj -warnaserror` — if Spectre.Console trips IL2026/IL3050 warnings, rewrite `Ui.cs`'s implementation to plain ANSI escape codes (same public members: `Info`, `Warn`, `Error`, `Pass`, `Fail`, `FailItem`, `Rule`, `Blank`) and remove the Spectre package reference; if XmlGen trips it (it will only if the analyzer follows project references — it does not for IsAotCompatible), note it.
- [ ] **Step 5:** Manual probe smoke: `dotnet run --project src/BattleScribeSpec.Cli -- probe <tiny spec> --engine battlescribe --ui` — confirm the host launches the BS app or fails with the same jar-resolution error text as before (record which, environment-dependent). Offline gate + Cli.Tests.
- [ ] **Step 6:** Commit `feat(cli): engine-free bs-spec — probe/discover forward to bs-engine-host, AOT-compatible (#271)`.

---

### Task 11: PublishAot smoke + docs

**Files:**
- Modify: `src/BattleScribeSpec.Cli/BattleScribeSpec.Cli.csproj` (only if the outcome is positive: `PublishAot` stays OFF by default; document)
- Modify: `README.md` (architecture diagram + project tree gain EngineHost/XmlGen; CLI usage examples show `--engine` forms), `docs/adapter-protocol.md` (add a short "bs-engine-host" note in the overview naming it as the in-box adapter), `docs/superpowers/specs/…` untouched
- Test: none (verification task)

- [ ] **Step 1:** `dotnet publish src/BattleScribeSpec.Cli -c Release -p:PublishAot=true -r win-x64 2>&1 | tail -20` — record the outcome. Success → try `bs-spec.exe format --help` from the publish dir; failure → capture the first blocking error (expected: XmlSerializer via XmlGen, or Spectre). Either way, write the outcome into README's development section (one short paragraph: AOT status and what blocks it, if anything).
- [ ] **Step 2:** Docs edits; commit `docs: bs-engine-host architecture + AOT status (#271)`.

---

### Task 12: Whole-PR verification + PR

**Files:** none new.

- [ ] **Step 1:** Full clean gate: `dotnet build && dotnet test tests/BattleScribeSpec.Tests.csproj --no-build --filter "Category!=Conformance"` AND `dotnet test tests/BattleScribeSpec.Cli.Tests/BattleScribeSpec.Cli.Tests.csproj --no-build` — both green, counts recorded.
- [ ] **Step 2:** Runner untouched check: `git diff main -- src/BattleScribeSpec.Runner/` must be empty; run the CI runner invocation once locally (same command as PR 1's parity check) — identical results.
- [ ] **Step 3:** End-to-end sanity script (record all outputs):
  - `bs-spec run <spec> --engine battlescribe` (host)
  - `bs-spec run <spec> --engine battlescribe=dotnet:<reference-adapter>` (identity override)
  - `bs-spec run --all --engine battlescribe=dotnet:<ref-adapter> --filter protocol/ --output summary`
  - `bs-spec run --matrix <dir with a conformance json>`
  - `bs-spec verify <gamedata spec> --engines battlescribe`
  - `bs-spec export-xml` and `format` unchanged
- [ ] **Step 4:** Push and open the PR (title `feat: bs-engine-host + protocol-driven CLI (#271, PR 2/3)`); body summarizes the architecture flip, the probe/discover host-verb decision, XmlGen, AOT status, and defers; end with the standard attribution + session link. Watch CI.
