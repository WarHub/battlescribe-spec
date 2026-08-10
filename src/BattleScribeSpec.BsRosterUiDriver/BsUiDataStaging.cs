using System.Xml.Linq;

namespace BattleScribeSpec.BsRosterUiDriver;

public static class BsUiDataStaging
{
    private const string BattleScribeVersion = "2.03";

    /// <summary>
    /// Writes <paramref name="files"/> into the isolated BattleScribe data directory, under a
    /// subdirectory named for the game system, with the <c>index.bsi</c> BattleScribe needs to see
    /// them at all.
    /// </summary>
    /// <remarks>
    /// Takes raw XML rather than Protocol objects, because the <c>dataSource</c> path has no
    /// Protocol objects — its files are real BattleScribe data read off disk. The index is built by
    /// READING those files, which is also why the generated path routes through here: an index
    /// describing what was actually staged cannot disagree with it, and one built from the objects
    /// the files were generated from can.
    /// </remarks>
    public static async Task StageDataFilesAsync(
        string dataDirectoryPath,
        string gameSystemId,
        IReadOnlyList<(string FileName, string Content)> files)
    {
        Directory.CreateDirectory(dataDirectoryPath);
        var gameSystemDirectory = Path.Combine(dataDirectoryPath, gameSystemId);
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
        await File.WriteAllTextAsync(indexPath, BuildIndexXml(files));
    }

    /// <summary>
    /// The <c>index.bsi</c> describing <paramref name="files"/>, with every id and name read out of
    /// the files themselves.
    /// </summary>
    public static string BuildIndexXml(IReadOnlyList<(string FileName, string Content)> files)
    {
        XNamespace ns = "http://www.battlescribe.net/schema/dataIndexSchema";
        var entries = new List<XElement>();
        string? systemName = null;

        foreach (var (fileName, content) in files)
        {
            var isGameSystem = fileName.EndsWith(".gst", StringComparison.OrdinalIgnoreCase);
            if (!isGameSystem && !fileName.EndsWith(".cat", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var root = XDocument.Parse(content).Root;
            if (root is null)
            {
                continue;
            }

            var name = (string?)root.Attribute("name") ?? fileName;
            if (isGameSystem)
            {
                systemName = name;
            }

            entries.Add(
                new XElement(
                    ns + "dataIndexEntry",
                    new XAttribute("filePath", fileName),
                    new XAttribute("dataType", isGameSystem ? "gamesystem" : "catalogue"),
                    new XAttribute("dataId", (string?)root.Attribute("id") ?? fileName),
                    new XAttribute("dataName", name),
                    new XAttribute("dataBattleScribeVersion", (string?)root.Attribute("battleScribeVersion") ?? BattleScribeVersion),
                    new XAttribute("dataRevision", (string?)root.Attribute("revision") ?? "1")));
        }

        // The game system entry first: BattleScribe reads the index in order and a catalogue whose
        // system has not been seen yet is not attached to one.
        entries = [.. entries.OrderByDescending(e => (string?)e.Attribute("dataType") == "gamesystem")];

        var index = new XElement(
            ns + "dataIndex",
            new XAttribute("battleScribeVersion", BattleScribeVersion),
            new XAttribute("name", systemName ?? "Spec Data"),
            new XElement(ns + "dataIndexEntries", entries));

        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), index).ToString();
    }
}
