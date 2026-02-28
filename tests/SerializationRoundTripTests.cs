using BattleScribeSpec;
using WarHub.ArmouryModel.Source;
using WarHub.ArmouryModel.Source.BattleScribe;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Category 11: Serialization Round-Trip
/// Tests that BattleScribe data survives serialize → deserialize cycles.
/// </summary>
public class SerializationRoundTripTests
{
    [Fact]
    public void Gamesystem_CanSerializeAndDeserialize()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();

        using var memStream = new MemoryStream();
        using (var writer = new StreamWriter(memStream, leaveOpen: true))
        {
            BattleScribeXmlSerializer.Instance.Serialize(gs, writer);
        }

        memStream.Position = 0;
        var deserialized = BattleScribeXmlSerializer.Instance.Deserialize(
            ser => ser.DeserializeGamesystem(),
            memStream) as GamesystemNode;

        Assert.NotNull(deserialized);
        Assert.Equal(gs.Id, deserialized.Id);
        Assert.Equal(gs.Name, deserialized.Name);
        Assert.Equal(gs.BattleScribeVersion, deserialized.BattleScribeVersion);
        Assert.Equal(gs.CostTypes.Length, deserialized.CostTypes.Length);
        Assert.Equal(gs.ProfileTypes.Length, deserialized.ProfileTypes.Length);
        Assert.Equal(gs.CategoryEntries.Length, deserialized.CategoryEntries.Length);
        Assert.Equal(gs.ForceEntries.Length, deserialized.ForceEntries.Length);
    }

    [Fact]
    public void Catalogue_CanSerializeAndDeserialize()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();

        using var memStream = new MemoryStream();
        using (var writer = new StreamWriter(memStream, leaveOpen: true))
        {
            BattleScribeXmlSerializer.Instance.Serialize(cat, writer);
        }

        memStream.Position = 0;
        var deserialized = BattleScribeXmlSerializer.Instance.Deserialize(
            ser => ser.DeserializeCatalogue(),
            memStream) as CatalogueNode;

        Assert.NotNull(deserialized);
        Assert.Equal(cat.Id, deserialized.Id);
        Assert.Equal(cat.Name, deserialized.Name);
        Assert.Equal(cat.GamesystemId, deserialized.GamesystemId);
        Assert.Equal(cat.SelectionEntries.Length, deserialized.SelectionEntries.Length);
    }

    [Fact]
    public void Catalogue_SelectionEntries_SurviveRoundTrip()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();

        using var memStream = new MemoryStream();
        using (var writer = new StreamWriter(memStream, leaveOpen: true))
        {
            BattleScribeXmlSerializer.Instance.Serialize(cat, writer);
        }

        memStream.Position = 0;
        var deserialized = BattleScribeXmlSerializer.Instance.Deserialize(
            ser => ser.DeserializeCatalogue(),
            memStream) as CatalogueNode;

        Assert.NotNull(deserialized);
        var commander = deserialized.SelectionEntries.FirstOrDefault(e => e.Name == "Commander");
        Assert.NotNull(commander);
        Assert.Equal(SelectionEntryKind.Unit, commander.Type);
        Assert.Single(commander.Costs);
        Assert.Equal(100m, commander.Costs[0].Value);
        Assert.Equal(2, commander.Constraints.Length);
    }

    [Fact]
    public void Catalogue_Modifiers_SurviveRoundTrip()
    {
        var cat = TestDataFactory.CreateModifierTestCatalogue();

        using var memStream = new MemoryStream();
        using (var writer = new StreamWriter(memStream, leaveOpen: true))
        {
            BattleScribeXmlSerializer.Instance.Serialize(cat, writer);
        }

        memStream.Position = 0;
        var deserialized = BattleScribeXmlSerializer.Instance.Deserialize(
            ser => ser.DeserializeCatalogue(),
            memStream) as CatalogueNode;

        Assert.NotNull(deserialized);
        var entry = deserialized.SelectionEntries.FirstOrDefault(e => e.Id == "entry-name-mod");
        Assert.NotNull(entry);
        Assert.Single(entry.Modifiers);
        Assert.Equal(ModifierKind.Append, entry.Modifiers[0].Type);
        Assert.Equal("name", entry.Modifiers[0].Field);
        Assert.Equal("(Modified)", entry.Modifiers[0].Value);
    }

    [Fact]
    public void Catalogue_EntryLinks_SurviveRoundTrip()
    {
        var cat = TestDataFactory.CreateLinkTestCatalogue();

        using var memStream = new MemoryStream();
        using (var writer = new StreamWriter(memStream, leaveOpen: true))
        {
            BattleScribeXmlSerializer.Instance.Serialize(cat, writer);
        }

        memStream.Position = 0;
        var deserialized = BattleScribeXmlSerializer.Instance.Deserialize(
            ser => ser.DeserializeCatalogue(),
            memStream) as CatalogueNode;

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.SharedSelectionEntries);
        Assert.Equal("shared-weapon-1", deserialized.SharedSelectionEntries[0].Id);

        var unit = deserialized.SelectionEntries.First(e => e.Name == "Linked Unit");
        Assert.Single(unit.EntryLinks);
        Assert.Equal("shared-weapon-1", unit.EntryLinks[0].TargetId);
    }

    [Fact]
    public void Catalogue_SelectionGroups_SurviveRoundTrip()
    {
        var cat = TestDataFactory.CreateSelectionGroupTestCatalogue();

        using var memStream = new MemoryStream();
        using (var writer = new StreamWriter(memStream, leaveOpen: true))
        {
            BattleScribeXmlSerializer.Instance.Serialize(cat, writer);
        }

        memStream.Position = 0;
        var deserialized = BattleScribeXmlSerializer.Instance.Deserialize(
            ser => ser.DeserializeCatalogue(),
            memStream) as CatalogueNode;

        Assert.NotNull(deserialized);
        var unit = deserialized.SelectionEntries.First(e => e.Name == "Equipped Unit");
        Assert.Single(unit.SelectionEntryGroups);
        var group = unit.SelectionEntryGroups[0];
        Assert.Equal("Weapon Choice", group.Name);
        Assert.Equal("weapon-a", group.DefaultSelectionEntryId);
        Assert.Equal(3, group.SelectionEntries.Length);
    }

    [Fact]
    public void Roster_CanSerializeAndDeserialize()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();
        var roster = NodeFactory.Roster(gs) with
        {
            CostLimits = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = 2000m }],
        };

        using var memStream = new MemoryStream();
        using (var writer = new StreamWriter(memStream, leaveOpen: true))
        {
            BattleScribeXmlSerializer.Instance.Serialize(roster, writer);
        }

        memStream.Position = 0;
        var deserialized = BattleScribeXmlSerializer.Instance.Deserialize(
            ser => ser.DeserializeRoster(),
            memStream) as RosterNode;

        Assert.NotNull(deserialized);
        Assert.Equal(roster.GamesystemId, deserialized.GamesystemId);
        Assert.Equal(roster.GamesystemName, deserialized.GamesystemName);
        Assert.Single(deserialized.CostLimits);
        Assert.Equal(2000m, deserialized.CostLimits[0].Value);
    }
}
