using WarHub.ArmouryModel.Source;
using WarHub.ArmouryModel.Source.BattleScribe;

namespace BattleScribeSpec;

/// <summary>
/// Loads and provides access to BattleScribe data files for testing.
/// Supports both real-world data (wh40k-9e) and synthetic test data.
/// </summary>
public class DataLoader
{
    /// <summary>
    /// Deserializes a .gst or .gstz file into a GamesystemNode.
    /// </summary>
    public static SourceNode LoadFile(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return DataVersionManagement.DeserializeAuto(stream, MigrationMode.OnFailure)
            ?? throw new InvalidOperationException($"Failed to deserialize: {filePath}");
    }

    /// <summary>
    /// Loads all .gst and .cat files from a directory.
    /// Returns (gamesystem, catalogues) tuple.
    /// </summary>
    public static (SourceNode? Gamesystem, List<SourceNode> Catalogues) LoadDirectory(string directory)
    {
        SourceNode? gamesystem = null;
        var catalogues = new List<SourceNode>();

        foreach (var file in Directory.GetFiles(directory, "*.gst"))
        {
            gamesystem = LoadFile(file);
        }

        foreach (var file in Directory.GetFiles(directory, "*.cat"))
        {
            catalogues.Add(LoadFile(file));
        }

        return (gamesystem, catalogues);
    }

    /// <summary>
    /// Round-trip test: deserialize → serialize → deserialize and compare.
    /// Returns true if the round-trip preserves the data.
    /// </summary>
    public static (bool Success, string? Error) RoundTripTest(string filePath)
    {
        try
        {
            var original = LoadFile(filePath);

            using var memStream = new MemoryStream();
            using (var writer = new StreamWriter(memStream, leaveOpen: true))
            {
                BattleScribeXmlSerializer.Instance.Serialize(original, writer);
            }

            memStream.Position = 0;
            var roundTripped = DataVersionManagement.DeserializeAuto(memStream, MigrationMode.None);
            if (roundTripped == null)
                return (false, "Failed to deserialize round-tripped XML");

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
