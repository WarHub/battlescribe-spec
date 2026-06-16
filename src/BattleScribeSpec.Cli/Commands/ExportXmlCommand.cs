using System.CommandLine;
using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Cli;

/// <summary>
/// <c>bs-spec export-xml &lt;spec&gt; &lt;dir&gt;</c> — generate BattleScribe <c>.gst</c>/<c>.cat</c>
/// XML from a spec's setup data. Needs no engine.
/// </summary>
internal static class ExportXmlCommand
{
    public static Command Create()
    {
        var spec = new Argument<string>("spec")
        {
            Description = "Spec file path, spec ID, or \"-\" for stdin.",
        };
        var dir = new Argument<string>("dir")
        {
            Description = "Output directory (files are written under <dir>/<specId>/).",
        };

        var command = new Command("export-xml", "Generate BattleScribe .gst/.cat XML from a spec's setup data.");
        command.Arguments.Add(spec);
        command.Arguments.Add(dir);

        command.SetAction(parseResult => Execute(parseResult.GetValue(spec)!, parseResult.GetValue(dir)!));
        return command;
    }

    private static int Execute(string specInput, string outputDir)
    {
        SpecFile spec;
        try
        {
            spec = SpecLoading.LoadSpec(specInput);
        }
        catch (Exception ex)
        {
            Ui.Error($"Error loading spec: {ex.Message}");
            return 1;
        }

        if (spec.Setup.DataSource is { Length: > 0 })
        {
            Ui.Error("export-xml is not supported for dataSource specs.");
            return 1;
        }

        var (gameSystem, catalogues) = SpecLoader.GetSetupData(spec.Setup, spec.Id);
        var specExportDir = Path.Combine(outputDir, spec.Id);
        Directory.CreateDirectory(specExportDir);

        var gstOut = Path.Combine(specExportDir, $"{StepFormatter.SanitizeFileName(gameSystem.Name)}.gst");
        File.WriteAllText(gstOut, CatXmlGenerator.GenerateGameSystemXml(gameSystem));
        Ui.Info($"Wrote {gstOut}");

        for (var catIdx = 0; catIdx < catalogues.Length; catIdx++)
        {
            var catName = StepFormatter.SanitizeFileName(catalogues[catIdx].Name);
            // Deduplicate filename if two catalogues share a sanitized name.
            var catFileName = catIdx == 0 || Enumerable.Range(0, catIdx).All(j => StepFormatter.SanitizeFileName(catalogues[j].Name) != catName)
                ? catName
                : $"{catName}-{catIdx + 1}";
            var catOut = Path.Combine(specExportDir, $"{catFileName}.cat");
            File.WriteAllText(catOut, CatXmlGenerator.GenerateCatalogueXml(gameSystem, catalogues[catIdx]));
            Ui.Info($"Wrote {catOut}");
        }

        Ui.Pass($"Exported {1 + catalogues.Length} file(s) to {specExportDir}");
        return 0;
    }
}
