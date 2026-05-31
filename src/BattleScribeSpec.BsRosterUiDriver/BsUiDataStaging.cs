using System.Xml.Linq;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.BsRosterUiDriver;

public static class BsUiDataStaging
{
    private const string BattleScribeVersion = "2.03";

    public static async Task StageDataFilesAsync(
        string dataDirectoryPath,
        ProtocolGameSystem gameSystem,
        IReadOnlyList<ProtocolCatalogue> catalogues,
        IReadOnlyList<(string FileName, string Content)> files)
    {
        Directory.CreateDirectory(dataDirectoryPath);
        var gameSystemDirectory = Path.Combine(dataDirectoryPath, gameSystem.Id);
        if (Directory.Exists(gameSystemDirectory))
        {
            Directory.Delete(gameSystemDirectory, recursive: true);
        }

        Directory.CreateDirectory(gameSystemDirectory);

        foreach (var (fileName, content) in files)
        {
            var filePath = Path.Combine(gameSystemDirectory, fileName);
            await File.WriteAllTextAsync(filePath, content);
        }

        var indexPath = Path.Combine(gameSystemDirectory, "index.bsi");
        await File.WriteAllTextAsync(indexPath, BuildIndexXml(gameSystem, catalogues, files));
    }

    public static string BuildIndexXml(
        ProtocolGameSystem gameSystem,
        IReadOnlyList<ProtocolCatalogue> catalogues,
        IReadOnlyList<(string FileName, string Content)> files)
    {
        var gstFileName = files.FirstOrDefault(x => x.FileName.EndsWith(".gst", StringComparison.Ordinal)).FileName
            ?? "system.gst";
        var catalogueFiles = files.Where(x => x.FileName.EndsWith(".cat", StringComparison.Ordinal)).ToList();
        XNamespace ns = "http://www.battlescribe.net/schema/dataIndexSchema";
        var entries = new List<XElement>
        {
            new(
                ns + "dataIndexEntry",
                new XAttribute("filePath", gstFileName),
                new XAttribute("dataType", "gamesystem"),
                new XAttribute("dataId", gameSystem.Id),
                new XAttribute("dataName", gameSystem.Name),
                new XAttribute("dataBattleScribeVersion", BattleScribeVersion),
                new XAttribute("dataRevision", 1)),
        };

        for (var i = 0; i < catalogues.Count; i++)
        {
            var fileName = i < catalogueFiles.Count ? catalogueFiles[i].FileName : $"catalogue{i}.cat";
            entries.Add(
                new XElement(
                    ns + "dataIndexEntry",
                    new XAttribute("filePath", fileName),
                    new XAttribute("dataType", "catalogue"),
                    new XAttribute("dataId", catalogues[i].Id),
                    new XAttribute("dataName", catalogues[i].Name),
                    new XAttribute("dataBattleScribeVersion", BattleScribeVersion),
                    new XAttribute("dataRevision", 1)));
        }

        var root = new XElement(
            ns + "dataIndex",
            new XAttribute("battleScribeVersion", BattleScribeVersion),
            new XAttribute("name", gameSystem.Name),
            new XElement(ns + "dataIndexEntries", entries));

        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).ToString();
    }
}
