---
name: nr-adhoc-probing
description: >
  Run ad-hoc probes against the live NewRecruit engine. Use when debugging NR behavior,
  verifying assumptions about NR internals, or testing how NR handles specific data
  configurations. Covers the probe test pattern, environment setup, and JS evaluation
  snippets for inspecting NR's Pinia stores and roster tree.
---

# NR Ad-Hoc Probing

Quickly verify NR behavior by writing a temporary xUnit test, running it against
live NR, reading test output, then deleting the file.

## Workflow

1. **Create** a probe test file at `tests/Integration/NrProbe.cs`
2. **Run** with env vars set:
   ```powershell
   $env:NR_ENGINE_URL = "https://newrecruit.eu"
   $env:NR_HEADLESS = "true"
   dotnet test tests/BattleScribeSpec.Tests.csproj `
       --filter "FullyQualifiedName~NrProbe" `
       --logger "console;verbosity=detailed"
   ```
3. **Read** the `Standard Output Messages` in test output
4. **Delete** the probe file when done

### Gotchas

- **Always** use `--logger "console;verbosity=detailed"` — without it,
  `ITestOutputHelper.WriteLine` output is invisible.
- **Never** use `-p:TestProfile=...` — profile filters exclude ad-hoc test classes.
- **Env vars** must be set on separate lines in PowerShell — `$env:X = "..."` can't
  chain with `&&`.

## Simple probe (using Engine API)

For most probes — checking roster state, testing setup configurations, verifying
action behavior — use `_fixture.Engine` directly. No raw JS needed.

```csharp
using BattleScribeSpec;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace BattleScribeSpec.Tests;

[Collection("NewRecruit")]
[Trait("Category", "Integration")]
public sealed class NrProbe
{
    private readonly ITestOutputHelper _output;
    private readonly NewRecruitFixture _fixture;

    public NrProbe(ITestOutputHelper output, NewRecruitFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    [SkippableFact]
    public void Probe()
    {
        Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

        var gs = new ProtocolGameSystem
        {
            Id = "probe-gs", Name = "Probe System",
            ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Test Force" }]
        };
        var cat = new ProtocolCatalogue
        {
            Id = "probe-cat", Name = "Probe Cat", GameSystemId = "probe-gs",
            SelectionEntries =
            [
                new ProtocolSelectionEntry { Id = "se-1", Name = "Test Unit", Type = "unit" }
            ]
        };

        var errors = _fixture.Engine!.Setup(gs, [cat]);
        _output.WriteLine($"Setup errors: {errors.Count}");

        _fixture.Engine.AddForce(0);
        var state = _fixture.Engine.GetRosterState();
        _output.WriteLine($"Forces: {state.Forces.Count}");
        foreach (var f in state.Forces)
        {
            _output.WriteLine($"  Force: {f.Name}, selections: {f.Selections.Count}");
            foreach (var s in f.Selections)
                _output.WriteLine($"    {s.Name} ({s.Type})");
        }
    }
}
```

## Raw JS probe (for NR internals)

When you need to inspect NR's internal data structures, Pinia stores, or
deobfuscated methods — bypass the Engine API and evaluate JS directly.
For NR internal API reference, see the **newrecruit-adapter** skill.

```csharp
[SkippableFact]
public async Task ProbeJs()
{
    Skip.If(!_fixture.Available, "NR_ENGINE_URL not set");

    var gs = new ProtocolGameSystem
    {
        Id = "probe-gs", Name = "Probe System",
        ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Test Force" }]
    };
    var cat = new ProtocolCatalogue
    {
        Id = "probe-cat", Name = "Probe Cat", GameSystemId = "probe-gs"
    };

    var page = _fixture.Engine!.Browser.Page;
    await _fixture.Engine.Browser.NavigateToAppAsync();
    await _fixture.Engine.Browser.WaitForPiniaAsync();
    var gstXml = CatXmlGenerator.GenerateGameSystemXml(gs);
    var catXml = CatXmlGenerator.GenerateCatalogueXml(gs, cat);

    var result = await page.EvaluateAsync<string?>("""
        async ([gstXml, catXml, systemId]) => {
            try {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const sysStore = pinia._s.get('systemsStore');
                for (const key of Object.keys(sysStore.localLibrary || {}))
                    delete sysStore.localLibrary[key];
                await sysStore.loadSystemFromFs([
                    { name: 'system.gst', path: '/spec/system.gst', data: gstXml },
                    { name: 'cat.cat', path: '/spec/cat.cat', data: catXml },
                ]);
                const localSys = sysStore.localLibrary[systemId];
                sysStore.selectSystem(localSys);
                const sys = sysStore._selectedSystem;
                const pb = sys.books?.array?.filter(b => b.playable)?.[0];
                const bd = await sys.getBook(pb.id);
                bd.catalogue.costIndex = {};
                const gsData = bd.catalogue.gameSystem;
                if (gsData?.costTypes)
                    for (const ct of gsData.costTypes)
                        bd.catalogue.costIndex[ct.id] = ct;

                // === YOUR PROBE LOGIC HERE ===
                let info = '';
                const roster = bd.createRoster(bd.getCosts());
                const forces = roster?.getForces?.() || [];
                info += `Forces: ${forces.length}\n`;
                for (const f of forces)
                    info += `  ${f.getName?.()}\n`;
                return info;
            } catch(e) {
                return 'Error: ' + e.message + '\n' + e.stack;
            }
        }
        """, new object[] { gstXml, catXml, gs.Id });

    _output.WriteLine("=== PROBE RESULT ===");
    _output.WriteLine(result ?? "(null — success)");
}
```

Add this method inside the same `NrProbe` class from the simple template above.

## Tips

- **Multiple probes in one file:** Add multiple `[SkippableFact]` methods —
  they share the browser fixture (one browser for all tests in the collection).
- **Log generated XML:** `_output.WriteLine(gstXml)` to verify CatXmlGenerator output.
- **Fresh state per test:** The JS preamble cleans `localLibrary`, and `Engine.Setup`
  resets the roster — no cross-test contamination.

## Reference files

- [NR-INTERNALS.md](references/NR-INTERNALS.md) — Deobfuscated NR behaviors discovered via probing
