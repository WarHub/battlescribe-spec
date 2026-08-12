using System.Xml.Linq;

namespace BattleScribeSpec.BsRosterUiDriver;

/// <summary>
/// Writes one engine's game data into that engine's BattleScribe data directory, and takes out the
/// game system it put there for the previous spec.
/// </summary>
/// <remarks>
/// An object rather than a static method so the previous id has somewhere to live: a nullable
/// <c>previousGameSystemId</c> parameter would let call sites added later pass <c>null</c> and grow
/// the directory back. It retires only what this instance staged — sweeping the siblings would need
/// <see cref="BsUiOptions.IsolatedHomePath"/> to stay unset forever, and the first time two engines
/// share a home one of them would delete the other's data mid-run.
/// </remarks>
public sealed class BsUiDataStaging
{
    private const string BattleScribeVersion = "2.03";

    private string? _stagedGameSystemId;

    /// <summary>
    /// Writes <paramref name="files"/> into the isolated BattleScribe data directory, under a
    /// subdirectory named for the game system, with the <c>index.bsi</c> BattleScribe needs to see
    /// them at all. Removes the subdirectory this stager wrote for the previous spec.
    /// </summary>
    /// <remarks>
    /// Takes raw XML rather than Protocol objects, because the <c>dataSource</c> path has no
    /// Protocol objects — its files are real BattleScribe data read off disk. The index is built by
    /// READING those files, which is also why the generated path routes through here: an index
    /// describing what was actually staged cannot disagree with it, and one built from the objects
    /// the files were generated from can.
    /// </remarks>
    public async Task StageDataFilesAsync(
        string dataDirectoryPath,
        string gameSystemId,
        IReadOnlyList<(string FileName, string Content)> files)
    {
        Directory.CreateDirectory(dataDirectoryPath);

        if (_stagedGameSystemId is { } previous
            && !string.Equals(previous, gameSystemId, StringComparison.Ordinal))
        {
            RetirePreviouslyStaged(dataDirectoryPath, previous);
        }

        // Claimed before the writes, not after: staging that throws half-way still leaves a
        // directory under this id, and the next call has to be what clears it.
        _stagedGameSystemId = gameSystemId;

        var gameSystemDirectory = Path.Combine(dataDirectoryPath, gameSystemId);
        if (Directory.Exists(gameSystemDirectory))
        {
            // Not best-effort, unlike retirement above: this is where the CURRENT spec's data goes,
            // and the previous run's files left mixed into it is a corrupt setup, not an untidy one.
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
    /// Removes the game system directory this stager wrote last time, best-effort. BattleScribe
    /// refills <c>#cboGameSystem</c> from a walk of this directory each time the New Roster dialog
    /// opens (<c>docs/bs-ui-driver.md</c>, "One game system at a time"), so this reaches a running
    /// app and not only the next cold start — and it holds loaded data files open, so on Windows the
    /// delete can simply fail.
    /// </summary>
    private static void RetirePreviouslyStaged(string dataDirectoryPath, string gameSystemId)
    {
        var directory = Path.Combine(dataDirectoryPath, gameSystemId);
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[bs-ui] Could not remove the previously staged game system '{gameSystemId}'; "
                + $"continuing, since this spec's data is staged either way. {ex.Message}");
        }
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
