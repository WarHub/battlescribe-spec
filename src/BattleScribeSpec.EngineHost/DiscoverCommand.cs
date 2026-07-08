using System.CommandLine;
using System.Text.Json;
using BattleScribeSpec.NrGameDataUiDriver;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.EngineHost;

/// <summary>
/// <c>bs-engine-host discover</c> — automated discovery of the NewRecruit editor's schema
/// surface, used to catalogue NR's additions over the original BattleScribe data format.
///
/// Subcommands:
///   <c>xml &lt;spec&gt;</c>   — capture the real <c>.cat</c>/<c>.gst</c> XML NR emits for a spec's data
///                            (via NR's own serializer), recovering exact element/attribute names.
///   <c>enums &lt;spec&gt;</c> — create one of each selector/type node and dump every dropdown's option
///                            list (modifier/condition/constraint/link/entry-type vocabularies).
///   <c>nodes &lt;spec&gt;</c> — right-click the tree's sections and entries and capture the context-menu
///                            "add" items (and submenus), enumerating every creatable node type.
/// </summary>
internal static class DiscoverCommand
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public static Command Create()
    {
        var command = new Command("discover", "Automated discovery of NewRecruit editor schema additions.");
        command.Subcommands.Add(CreateSubcommand("xml",
            "Capture the real .cat/.gst XML NewRecruit emits for a spec's data.", ExecuteXmlAsync));
        command.Subcommands.Add(CreateSubcommand("enums",
            "Dump every dropdown's option values across the NR editor's node editors.", ExecuteEnumsAsync));
        command.Subcommands.Add(CreateSubcommand("nodes",
            "Enumerate every node type the NR editor can create (context-menu add items).", ExecuteNodesAsync));
        return command;
    }

    private static Command CreateSubcommand(
        string name, string description, Func<string, bool, string?, Task<int>> execute)
    {
        var spec = new Argument<string>("spec")
        {
            Description = "GameData spec whose setup data seeds the NR Editor.",
        };
        var headed = new Option<bool>("--headed") { Description = "Run with a visible browser window." };
        var output = new Option<string?>("--output", "-o")
        {
            Description = "Output directory (default: artifacts/discover/<specId>).",
        };

        var command = new Command(name, description);
        command.Arguments.Add(spec);
        command.Options.Add(headed);
        command.Options.Add(output);
        command.SetAction((parseResult, _) => execute(
            parseResult.GetValue(spec)!, !parseResult.GetValue(headed), parseResult.GetValue(output)));
        return command;
    }

    // ===== Shared setup =====

    private static async Task<(NrGameDataUiEngine? Engine, ProtocolGameSystem Gs, ProtocolCatalogue[] Cats, string Dir)>
        SetUpAsync(string specInput, bool headless, string? outputDir)
    {
        var spec = HostSpecLoading.LoadGameDataSpec(specInput);
        Console.Error.WriteLine($"Loaded GameData spec: {spec.Category}/{spec.Id} — {spec.Description}");
        var (gameSystem, catalogues) = SpecLoader.GetGameDataSetupData(spec.Setup);

        var repoRoot = HostSpecLoading.FindRepoRoot() ?? Directory.GetCurrentDirectory();
        var dir = outputDir ?? Path.Combine(repoRoot, "artifacts", "discover", spec.Id);
        Directory.CreateDirectory(dir);

        var staticDir = NrGameDataUiEngine.FindFrozenStaticDir() ?? throw new InvalidOperationException(
            "NR Editor frozen static dir not found (.testdata/nr-editor) — run setup.ps1.");
        Console.Error.WriteLine($"NR Editor GameData UI (frozen): {staticDir}");

        var engine = await NrGameDataUiEngine.CreateFrozenAsync(staticDir, headless);
        engine.SetTestContext(spec.Id);
        var setupErrors = engine.Setup(gameSystem, catalogues);
        foreach (var err in setupErrors)
        {
            Console.Error.WriteLine($"error:   Setup: {err}");
        }
        if (setupErrors.Count > 0)
        {
            engine.Dispose();
            return (null, gameSystem, catalogues, dir);
        }
        return (engine, gameSystem, catalogues, dir);
    }

    // ===== discover xml =====

    private static async Task<int> ExecuteXmlAsync(string specInput, bool headless, string? outputDir)
    {
        var (engine, gs, cats, dir) = await SetUpAsync(specInput, headless, outputDir);
        if (engine is null)
        { return 1; }
        using var engineScope = engine;

        // Also emit the raw CatXmlGenerator input (what we feed NR) so it can be diffed against NR's
        // re-serialized output below — the diff reveals NR's load-time normalizations.
        await File.WriteAllTextAsync(Path.Combine(dir, $"generated-{gs.Id}.gst"),
            BattleScribeSpec.XmlGen.CatXmlGenerator.GenerateGameSystemXml(gs));
        foreach (var (fileName, xml) in BattleScribeSpec.XmlGen.CatXmlGenerator.GenerateAllCatalogueXml(gs, cats))
        {
            await File.WriteAllTextAsync(Path.Combine(dir, $"generated-{fileName}"), xml);
        }

        var json = await engine.ExportLoadedFilesJsonAsync();
        using var doc = JsonDocument.Parse(json);
        foreach (var line in doc.RootElement.GetProperty("debug").EnumerateArray())
        {
            Console.Error.WriteLine($"  [debug] {line.GetString()}");
        }

        var files = doc.RootElement.GetProperty("files");
        var written = 0;
        foreach (var file in files.EnumerateObject())
        {
            var name = Path.GetFileName(file.Name.Replace('\\', '/'));
            var content = file.Value.GetString() ?? "";
            await File.WriteAllTextAsync(Path.Combine(dir, name), content);
            Console.Error.WriteLine($"  wrote {name} ({content.Length} chars)");
            written++;
        }
        if (written == 0)
        {
            Console.Error.WriteLine("error: No XML captured.");
            return 1;
        }
        Console.Error.WriteLine($"Captured {written} file(s) → {dir}");
        return 0;
    }

    // ===== discover enums =====

    private static async Task<int> ExecuteEnumsAsync(string specInput, bool headless, string? outputDir)
    {
        var (engine, gs, cats, dir) = await SetUpAsync(specInput, headless, outputDir);
        if (engine is null)
        { return 1; }
        using var engineScope = engine;

        var catId = cats.Length > 0 ? cats[0].Id : gs.Id;
        var gsId = gs.Id;
        var results = new Dictionary<string, object>();

        async Task ProbeAsync(string label, Func<Task> setupNode)
        {
            try
            {
                await setupNode();
                var selects = JsonSerializer.Deserialize<object>(await engine.DumpSelectsJsonAsync());
                var widgets = JsonSerializer.Deserialize<object>(await engine.DumpOpenableWidgetsJsonAsync());
                results[label] = new { selects, widgets };
                Console.Error.WriteLine($"  probed {label}");
            }
            catch (Exception ex)
            {
                results[label] = new { error = ex.Message };
                Console.Error.WriteLine($"error:   {label}: {ex.Message}");
            }
        }

        // Selector nodes hang off a selection entry / modifier. Build that scaffold once.
        string? entryId = null, modifierId = null;
        await ProbeAsync("selectionEntry", () =>
            Task.FromResult(entryId = engine.AddEntry(catId, "selectionEntry", "Probe Entry").EntryId));
        if (entryId is not null)
        {
            await ProbeAsync("constraint", () => Task.FromResult(engine.AddEntry(entryId!, "constraint", null)));
            await ProbeAsync("modifier", () =>
            {
                modifierId = engine.AddEntry(entryId!, "modifier", null).EntryId;
                return Task.CompletedTask;
            });
            if (modifierId is not null)
            {
                // The modifier "type" dropdown is gated on the chosen field's data type, so a single
                // dump only shows the types valid for the current field. Set the field to a numeric,
                // string, boolean, and category field in turn and capture the type options for each.
                var byField = new Dictionary<string, object>();
                foreach (var (fieldKind, fieldValue) in new[]
                    { ("numeric", "pts"), ("string", "name"), ("boolean", "hidden"), ("category", "category") })
                {
                    try
                    {
                        engine.SetField(modifierId!, "field", fieldValue);
                        byField[$"{fieldKind} ({fieldValue})"] =
                            JsonSerializer.Deserialize<object>(await engine.DumpSelectsJsonAsync())!;
                    }
                    catch (Exception ex)
                    {
                        byField[$"{fieldKind} ({fieldValue})"] = new { error = ex.Message };
                    }
                }
                results["modifier.typeByField"] = byField;
                Console.Error.WriteLine("  probed modifier.typeByField");

                await ProbeAsync("condition", () => Task.FromResult(engine.AddEntry(modifierId!, "condition", null)));
                await ProbeAsync("conditionGroup", () => Task.FromResult(engine.AddEntry(modifierId!, "conditionGroup", null)));
                await ProbeAsync("repeat", () => Task.FromResult(engine.AddEntry(modifierId!, "repeat", null)));
                // NR-new: localConditionGroup is a child of a modifier.
                await ProbeAsync("localConditionGroup",
                    () => Task.FromResult(engine.AddEntry(modifierId!, "localConditionGroup", null)));
            }
        }

        // Type definitions: the catalogue editor has its own "Profile Types"/"Cost Types" root
        // sections, so add them under the catalogue id (the root-section add path) rather than the
        // game-system id (which the editor isn't currently showing → child-menu miss). The
        // characteristicType is a child of the profileType and carries the `kind`/`defaultValue`/
        // formatRules surface.
        await ProbeAsync("profileType", async () =>
        {
            var pt = engine.AddEntry(catId, "profileType", "Probe Profile Type").EntryId;
            engine.AddEntry(pt!, "characteristicType", "Probe Char Type");
            // NR-new: attributeType is a child of profileType (sibling of characteristicType).
            engine.AddEntry(pt!, "attributeType", "Probe Attr Type");
        });
        await ProbeAsync("characteristicType", async () =>
        {
            var pt = engine.AddEntry(catId, "profileType", "Probe Profile Type 2").EntryId;
            var ct = engine.AddEntry(pt!, "characteristicType", "Probe Char Type 2").EntryId;
            // Re-select the characteristic type so its own panel (kind/defaultValue/formatRules) is shown.
            engine.SetField(ct!, "name", "Probe Char Type 2");
        });
        await ProbeAsync("costType", () => Task.FromResult(engine.AddEntry(catId, "costType", "Probe Cost Type")));
        if (cats.Length > 0)
        {
            await ProbeAsync("entryLink", () =>
            {
                // Link to the probe entry so an entryLink with a Link Type select exists.
                if (entryId is not null)
                { engine.AddLink(catId, "entryLink", entryId); }
                return Task.CompletedTask;
            });
        }

        // Add an NR-specific Association on the probe entry, then export the whole built-up
        // catalogue to XML — this recovers the exact element/attribute names NR emits for the
        // selector nodes (constraint/modifier/condition/conditionGroup/repeat), the conditionGroup's
        // new type values, and the association node.
        if (entryId is not null)
        {
            try
            {
                engine.AddEntry(entryId!, "association", "Probe Association");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error:   association: {ex.Message}");
            }
        }
        try
        {
            var xml = await engine.ExportLoadedFilesJsonAsync();
            using var xdoc = JsonDocument.Parse(xml);
            foreach (var file in xdoc.RootElement.GetProperty("files").EnumerateObject())
            {
                var name = "scaffold-" + Path.GetFileName(file.Name.Replace('\\', '/'));
                await File.WriteAllTextAsync(Path.Combine(dir, name), file.Value.GetString() ?? "");
                Console.Error.WriteLine($"  wrote {name}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error:   xml export: {ex.Message}");
        }

        var path = Path.Combine(dir, "enums.json");
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(results, IndentedJson));
        Console.Error.WriteLine($"Wrote enum dump ({results.Count} node editors) → {path}");
        return 0;
    }

    // ===== discover nodes =====

    private static async Task<int> ExecuteNodesAsync(string specInput, bool headless, string? outputDir)
    {
        var (engine, gs, cats, dir) = await SetUpAsync(specInput, headless, outputDir);
        if (engine is null)
        { return 1; }
        using var engineScope = engine;

        var catId = cats.Length > 0 ? cats[0].Id : gs.Id;
        var results = new Dictionary<string, object>();

        // Create a probe entry so an entry-level context menu (with its child-add submenus) exists,
        // and so the tree has a selected node we can right-click.
        string? probeEntryId = null;
        try
        {
            probeEntryId = engine.AddEntry(catId, "selectionEntry", "Probe Entry").EntryId;
            var entryMenu = await engine.RightClickAndDumpMenuJsonAsync("#editor-entries h3.selected");
            results["selectionEntry (child menu)"] = JsonSerializer.Deserialize<object>(entryMenu)!;
        }
        catch (Exception ex)
        {
            results["selectionEntry (child menu)"] = new { error = ex.Message };
            Console.Error.WriteLine($"error:   entry menu: {ex.Message}");
        }

        // Right-click selected created nodes to discover where NR-new children are added
        // (attributeType under profileType, localConditionGroup/condition under modifier, etc.).
        async Task ChildMenuOfAsync(string label, Func<string?> create)
        {
            try
            {
                create();
                var menu = await engine.RightClickAndDumpMenuJsonAsync("#editor-entries h3.selected");
                results[$"{label} (child menu)"] = JsonSerializer.Deserialize<object>(menu)!;
                Console.Error.WriteLine($"  {label} child menu");
            }
            catch (Exception ex)
            {
                results[$"{label} (child menu)"] = new { error = ex.Message };
                Console.Error.WriteLine($"error:   {label}: {ex.Message}");
            }
        }

        await ChildMenuOfAsync("profileType", () => engine.AddEntry(catId, "profileType", "Probe PT").EntryId);
        if (probeEntryId is not null)
        {
            await ChildMenuOfAsync("modifier", () => engine.AddEntry(probeEntryId, "modifier", null).EntryId);
            await ChildMenuOfAsync("association", () => engine.AddEntry(probeEntryId, "association", "Probe Assoc").EntryId);
        }

        // Right-click each root section header to capture its "add" menu.
        var headerCount = await engine.EvaluateAsync<int>(
            "() => document.querySelectorAll('.collapsible-box.depth-0 > h3').length");
        Console.Error.WriteLine($"  {headerCount} root section header(s)");
        for (var i = 0; i < headerCount; i++)
        {
            try
            {
                var header = $".collapsible-box.depth-0 > h3 >> nth={i}";
                var headerText = await engine.EvaluateAsync<string>(
                    $"() => document.querySelectorAll('.collapsible-box.depth-0 > h3')[{i}]?.innerText?.trim() || ''");
                var menu = await engine.RightClickAndDumpMenuJsonAsync(header);
                results[$"section[{i}]: {headerText}"] = JsonSerializer.Deserialize<object>(menu)!;
                Console.Error.WriteLine($"  section[{i}]: {headerText}");
            }
            catch (Exception ex)
            {
                results[$"section[{i}]"] = new { error = ex.Message };
            }
        }

        var path = Path.Combine(dir, "nodes.json");
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(results, IndentedJson));
        Console.Error.WriteLine($"Wrote creatable-node dump ({results.Count} menus) → {path}");
        return 0;
    }
}
