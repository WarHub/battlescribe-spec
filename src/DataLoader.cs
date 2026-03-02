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

        var gamesystemFiles = Directory.GetFiles(directory, "*.gst");
        if (gamesystemFiles.Length > 1)
            throw new InvalidOperationException(
                $"Expected at most one .gst file in '{directory}', found {gamesystemFiles.Length}: {string.Join(", ", gamesystemFiles.Select(Path.GetFileName))}");
        if (gamesystemFiles.Length == 1)
            gamesystem = LoadFile(gamesystemFiles[0]);

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

            if (!HasEquivalentCoreData(original, roundTripped, out var mismatch))
                return (false, mismatch);

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static bool HasEquivalentCoreData(SourceNode original, SourceNode roundTripped, out string? mismatch)
    {
        mismatch = null;
        if (original.GetType() != roundTripped.GetType())
        {
            mismatch = $"Type changed from {original.GetType().Name} to {roundTripped.GetType().Name}";
            return false;
        }

        switch (original, roundTripped)
        {
            case (GamesystemNode o, GamesystemNode r):
                if (o.Id != r.Id || o.Name != r.Name || o.BattleScribeVersion != r.BattleScribeVersion
                    || o.ForceEntries.Count != r.ForceEntries.Count || o.CostTypes.Count != r.CostTypes.Count)
                {
                    mismatch = "Gamesystem core metadata changed during round-trip.";
                    return false;
                }
                return true;

            case (CatalogueNode o, CatalogueNode r):
                if (o.Id != r.Id || o.Name != r.Name || o.GamesystemId != r.GamesystemId
                    || o.BattleScribeVersion != r.BattleScribeVersion
                    || o.SelectionEntries.Count != r.SelectionEntries.Count
                    || o.EntryLinks.Count != r.EntryLinks.Count)
                {
                    mismatch = "Catalogue core metadata changed during round-trip.";
                    return false;
                }
                return true;

            case (RosterNode o, RosterNode r):
                if (o.GameSystemId != r.GameSystemId || o.GameSystemName != r.GameSystemName
                    || o.Forces.Count != r.Forces.Count || o.Costs.Count != r.Costs.Count)
                {
                    mismatch = "Roster core metadata changed during round-trip.";
                    return false;
                }
                return true;

            default:
                return true;
        }
    }
}
