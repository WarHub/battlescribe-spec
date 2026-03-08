using BattleScribeSpec;
using WarHub.ArmouryModel.Source;
using WarHub.ArmouryModel.Source.BattleScribe;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Serialization Round-Trip Tests.
/// Tests that BattleScribe data survives serialize → deserialize cycles.
/// Includes both property-level and XML-level round-trip verification.
/// </summary>
[Trait("Category", "Unit")]
public class SerializationRoundTripTests
{
    private static MemoryStream SerializeToStream(SourceNode node)
    {
        var memStream = new MemoryStream();
        using (var writer = new StreamWriter(memStream, leaveOpen: true))
        {
            BattleScribeXmlSerializer.Instance.Serialize(node, writer);
        }
        memStream.Position = 0;
        return memStream;
    }

    private static string SerializeToString(SourceNode node)
    {
        using var writer = new StringWriter();
        BattleScribeXmlSerializer.Instance.Serialize(node, writer);
        return writer.ToString();
    }

    [Fact]
    public void Gamesystem_CanSerializeAndDeserialize()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();

        using var memStream = SerializeToStream(gs);
        var deserialized = memStream.DeserializeGamesystem();

        Assert.NotNull(deserialized);
        Assert.Equal(gs.Id, deserialized.Id);
        Assert.Equal(gs.Name, deserialized.Name);
        Assert.Equal(gs.BattleScribeVersion, deserialized.BattleScribeVersion);
        Assert.Equal(gs.CostTypes.Count, deserialized.CostTypes.Count);
        Assert.Equal(gs.ProfileTypes.Count, deserialized.ProfileTypes.Count);
        Assert.Equal(gs.CategoryEntries.Count, deserialized.CategoryEntries.Count);
        Assert.Equal(gs.ForceEntries.Count, deserialized.ForceEntries.Count);
    }

    [Fact]
    public void Catalogue_CanSerializeAndDeserialize()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();

        using var memStream = SerializeToStream(cat);
        var deserialized = memStream.DeserializeCatalogue();

        Assert.NotNull(deserialized);
        Assert.Equal(cat.Id, deserialized.Id);
        Assert.Equal(cat.Name, deserialized.Name);
        Assert.Equal(cat.GamesystemId, deserialized.GamesystemId);
        Assert.Equal(cat.SelectionEntries.Count, deserialized.SelectionEntries.Count);
    }

    [Fact]
    public void Catalogue_SelectionEntries_SurviveRoundTrip()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();

        using var memStream = SerializeToStream(cat);
        var deserialized = memStream.DeserializeCatalogue();

        Assert.NotNull(deserialized);
        var commander = deserialized.SelectionEntries.FirstOrDefault(e => e.Name == "Commander");
        Assert.NotNull(commander);
        Assert.Equal(SelectionEntryKind.Unit, commander.Type);
        Assert.Single(commander.Costs);
        Assert.Equal(100m, commander.Costs[0].Value);
        Assert.Equal(2, commander.Constraints.Count);
    }

    [Fact]
    public void Catalogue_Modifiers_SurviveRoundTrip()
    {
        var cat = TestDataFactory.CreateModifierTestCatalogue();

        using var memStream = SerializeToStream(cat);
        var deserialized = memStream.DeserializeCatalogue();

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

        using var memStream = SerializeToStream(cat);
        var deserialized = memStream.DeserializeCatalogue();

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

        using var memStream = SerializeToStream(cat);
        var deserialized = memStream.DeserializeCatalogue();

        Assert.NotNull(deserialized);
        var unit = deserialized.SelectionEntries.First(e => e.Name == "Equipped Unit");
        Assert.Single(unit.SelectionEntryGroups);
        var group = unit.SelectionEntryGroups[0];
        Assert.Equal("Weapon Choice", group.Name);
        Assert.Equal("weapon-a", group.DefaultSelectionEntryId);
        Assert.Equal(3, group.SelectionEntries.Count);
    }

    [Fact]
    public void Roster_CanSerializeAndDeserialize()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();
        var rosterNode = NodeFactory.Roster(gs).Core with
        {
            CostLimits = [new CostLimitCore { TypeId = "pts", Name = "pts", Value = 2000m }],
        };
        var roster = rosterNode.ToNode();

        using var memStream = SerializeToStream(roster);
        var deserialized = memStream.DeserializeRoster();

        Assert.NotNull(deserialized);
        Assert.Equal(roster.GameSystemId, deserialized.GameSystemId);
        Assert.Equal(roster.GameSystemName, deserialized.GameSystemName);
        Assert.Single(deserialized.CostLimits);
        Assert.Equal(2000m, deserialized.CostLimits[0].Value);
    }

    [Fact]
    public void Gamesystem_XmlRoundTrip_ProducesSameOutput()
    {
        var gs = TestDataFactory.CreateMinimalGamesystem();
        var xml1 = SerializeToString(gs);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml1));
        var deserialized = stream.DeserializeGamesystem();
        Assert.NotNull(deserialized);
        var xml2 = SerializeToString(deserialized);
        Assert.Equal(xml1, xml2);
    }

    [Fact]
    public void Catalogue_XmlRoundTrip_ProducesSameOutput()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        var xml1 = SerializeToString(cat);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml1));
        var deserialized = stream.DeserializeCatalogue();
        Assert.NotNull(deserialized);
        var xml2 = SerializeToString(deserialized);
        Assert.Equal(xml1, xml2);
    }
}
