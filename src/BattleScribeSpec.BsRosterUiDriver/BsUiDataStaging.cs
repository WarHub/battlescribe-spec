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
/// <para>
/// <b>A running app sees a game system appear or disappear whole, never in between.</b> Files are
/// written into a directory beside the data directory — outside the walk BattleScribe makes of it —
/// and renamed into place; a directory leaving goes the same way, renamed out before it is deleted.
/// Writing straight into the data directory left a window in which the app could read a half-written
/// file, or a directory whose catalogues were already gone, and BattleScribe deletes a file it cannot
/// read.
/// </para>
/// </remarks>
public sealed class BsUiDataStaging
{
    private const string BattleScribeVersion = "2.03";

    private string? _stagedGameSystemId;

    /// <summary>
    /// Writes <paramref name="files"/> into the isolated BattleScribe data directory, under a
    /// subdirectory named for the game system, with the <c>index.bsi</c> BattleScribe needs to see
    /// them at all. Removes the subdirectory this stager wrote for the previous spec. Writes nothing
    /// when the subdirectory already holds exactly these files.
    /// </summary>
    /// <remarks>
    /// Takes raw XML rather than Protocol objects, because the <c>dataSource</c> path has no
    /// Protocol objects — its files are real BattleScribe data read off disk. The index is built by
    /// READING those files, which is also why the generated path routes through here: an index
    /// describing what was actually staged cannot disagree with it, and one built from the objects
    /// the files were generated from can.
    /// <para>
    /// Writing nothing for an identical staging is not an optimisation. Rewriting the files of a game
    /// system a running app has a roster open on — even with the same bytes — makes BattleScribe ask
    /// "A file was modified outside of BattleScribe. Would you like to reload your roster?" at a
    /// moment of its choosing, and a prompt landing inside the next spec's action failed it (#526).
    /// The comparison is against the disk, not a memory of what was staged: the app deletes a file it
    /// finds corrupt, and the Data Editor saves over the ones it edits.
    /// </para>
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

        // Claimed before the writes, not after: a directory may already stand under this id, and
        // the next call has to be what clears it whether or not this one gets as far as replacing it.
        _stagedGameSystemId = gameSystemId;

        var staged = WithIndex(files);
        var gameSystemDirectory = Path.Combine(dataDirectoryPath, gameSystemId);
        if (await HoldsExactlyAsync(gameSystemDirectory, staged))
        {
            return;
        }

        var incoming = BesideDataDirectory(dataDirectoryPath, $"{gameSystemId}.staging");
        Directory.CreateDirectory(incoming);
        try
        {
            foreach (var (fileName, content) in staged)
            {
                await File.WriteAllTextAsync(Path.Combine(incoming, fileName), content);
            }

            if (Directory.Exists(gameSystemDirectory))
            {
                // Not best-effort, unlike retirement: this is where the CURRENT spec's data goes, and
                // the previous run's files left in its place is a corrupt setup, not an untidy one.
                Remove(gameSystemDirectory, dataDirectoryPath);
            }

            Move(incoming, gameSystemDirectory);
        }
        catch
        {
            TryDelete(incoming);
            throw;
        }
    }

    /// <summary>
    /// True when staging <paramref name="files"/> would replace a directory already standing under
    /// <paramref name="gameSystemId"/> with different content — files a running app may have loaded.
    /// </summary>
    public static async Task<bool> WouldReplaceAsync(
        string dataDirectoryPath,
        string gameSystemId,
        IReadOnlyList<(string FileName, string Content)> files)
    {
        var gameSystemDirectory = Path.Combine(dataDirectoryPath, gameSystemId);
        return Directory.Exists(gameSystemDirectory)
            && !await HoldsExactlyAsync(gameSystemDirectory, WithIndex(files));
    }

    private static List<(string FileName, string Content)> WithIndex(
        IReadOnlyList<(string FileName, string Content)> files)
        => [.. files, ("index.bsi", BuildIndexXml(files))];

    private static async Task<bool> HoldsExactlyAsync(
        string directory, IReadOnlyList<(string FileName, string Content)> files)
    {
        if (!Directory.Exists(directory)
            || Directory.EnumerateFileSystemEntries(directory).Count() != files.Count)
        {
            return false;
        }

        foreach (var (fileName, content) in files)
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path)
                || !string.Equals(await File.ReadAllTextAsync(path), content, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Removes the game system directory this stager wrote last time, best-effort. BattleScribe
    /// refills <c>#cboGameSystem</c> from a walk of this directory each time the New Roster dialog
    /// opens (<c>docs/bs-ui-driver.md</c>, "One game system at a time"), so this reaches a running
    /// app and not only the next cold start — and it holds loaded data files open, so on Windows the
    /// move can simply fail, which leaves the directory whole rather than half-deleted.
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
            Remove(directory, dataDirectoryPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[bs-ui] Could not remove the previously staged game system '{gameSystemId}'; "
                + $"continuing, since this spec's data is staged either way. {ex.Message}");
        }
    }

    /// <summary>
    /// Takes <paramref name="directory"/> out of the data directory in one rename, then deletes it
    /// where the app does not look. Throws only if the rename fails.
    /// </summary>
    private static void Remove(string directory, string dataDirectoryPath)
    {
        var outgoing = BesideDataDirectory(dataDirectoryPath, $"{Path.GetFileName(directory)}.retired");
        Move(directory, outgoing);
        TryDelete(outgoing);
    }

    /// <summary>
    /// A fresh path next to the data directory: on the same volume, so a rename into it is one
    /// operation, and outside the directory BattleScribe walks for game systems.
    /// </summary>
    private static string BesideDataDirectory(string dataDirectoryPath, string name)
        => Path.Combine(
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectoryPath)))!,
            $".{name}-{Guid.NewGuid():N}");

    /// <summary>
    /// A directory rename, retried briefly: on Windows a scanner reading a file it just saw written
    /// refuses the rename for a moment. A handle the app holds open outlasts the retries and throws.
    /// </summary>
    private static void Move(string source, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bs-ui] Could not delete '{directory}', outside the data directory: {ex.Message}");
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
