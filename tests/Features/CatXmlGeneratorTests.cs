using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Protocol;
using WarHub.ArmouryModel.Source;
using WarHub.ArmouryModel.Source.BattleScribe;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Round-trip tests for CatXmlGenerator: generate XML from spec models,
/// deserialize back with WarHub.ArmouryModel, and verify structural correctness.
/// </summary>
[Trait("Category", "Unit")]
public class CatXmlGeneratorTests
{
    [Fact]
    public void GenerateGameSystemXml_BasicStructure()
    {
        var gameSystem = new ProtocolGameSystem
        {
            Id = "gs-1",
            Name = "Test System",
            CostTypes = [new ProtocolCostType { Id = "pts", Name = "Points" }],
            CategoryEntries = [new ProtocolCategoryEntry { Id = "cat-hq", Name = "HQ" }],
            ForceEntries =
            [
                new ProtocolForceEntry
                {
                    Id = "fe-1",
                    Name = "Battalion",
                    CategoryLinks =
                    [
                        new ProtocolCategoryLink { Id = "cl-1", TargetId = "cat-hq", Name = "HQ", Primary = false }
                    ],
                },
            ],
            ProfileTypes =
            [
                new ProtocolProfileType
                {
                    Id = "pt-1",
                    Name = "Unit Stats",
                    CharacteristicTypes =
                    [
                        new ProtocolCharacteristicType { Id = "ct-m", Name = "M" },
                        new ProtocolCharacteristicType { Id = "ct-ws", Name = "WS" },
                    ],
                },
            ],
        };

        var xml = CatXmlGenerator.GenerateGameSystemXml(gameSystem);

        Assert.NotEmpty(xml);
        Assert.Contains("Test System", xml);

        // Deserialize back
        var node = DeserializeGamesystem(xml);
        Assert.Equal("gs-1", node.Id);
        Assert.Equal("Test System", node.Name);
        Assert.Single(node.CostTypes);
        Assert.Equal("Points", node.CostTypes[0].Name);
        Assert.Single(node.CategoryEntries);
        Assert.Equal("HQ", node.CategoryEntries[0].Name);
        Assert.Single(node.ForceEntries);
        Assert.Equal("Battalion", node.ForceEntries[0].Name);
        Assert.Single(node.ForceEntries[0].CategoryLinks);
        Assert.Single(node.ProfileTypes);
        Assert.Equal(2, node.ProfileTypes[0].CharacteristicTypes.Count);
    }

    [Fact]
    public void GenerateCatalogueXml_BasicEntries()
    {
        var gs = new ProtocolGameSystem
        {
            Id = "gs-1",
            Name = "GS",
            CostTypes = [new ProtocolCostType { Id = "pts", Name = "pts" }],
            CategoryEntries = [new ProtocolCategoryEntry { Id = "cat-1", Name = "Troops" }],
        };

        var cat = new ProtocolCatalogue
        {
            Id = "cat-1",
            Name = "Space Marines",
            GameSystemId = "gs-1",
            SelectionEntries =
            [
                new ProtocolSelectionEntry
                {
                    Id = "se-1",
                    Name = "Intercessors",
                    Type = "unit",
                    Costs = [new ProtocolCostValue { Name = "pts", TypeId = "pts", Value = 100 }],
                    Constraints =
                    [
                        new ProtocolConstraint { Id = "con-1", Type = "min", Value = 1,
                            Field = "selections", Scope = "parent" }
                    ],
                    CategoryLinks =
                    [
                        new ProtocolCategoryLink { Id = "cl-1", TargetId = "cat-1",
                            Name = "Troops", Primary = true }
                    ],
                },
            ],
        };

        var xml = CatXmlGenerator.GenerateCatalogueXml(gs, cat);
        Assert.NotEmpty(xml);

        var node = DeserializeCatalogue(xml);
        Assert.Equal("cat-1", node.Id);
        Assert.Equal("Space Marines", node.Name);
        Assert.Single(node.SelectionEntries);

        var entry = node.SelectionEntries[0];
        Assert.Equal("Intercessors", entry.Name);
        Assert.Equal(SelectionEntryKind.Unit, entry.Type);
        Assert.Single(entry.Costs);
        Assert.Equal(100m, entry.Costs[0].Value);
        Assert.Single(entry.Constraints);
        Assert.Equal(ConstraintKind.Minimum, entry.Constraints[0].Type);
        Assert.Single(entry.CategoryLinks);
        Assert.True(entry.CategoryLinks[0].Primary);
    }

    [Fact]
    public void GenerateCatalogueXml_ModifiersWithConditions()
    {
        var gs = new ProtocolGameSystem
        {
            Id = "gs-1",
            Name = "GS",
            CostTypes = [new ProtocolCostType { Id = "pts", Name = "pts" }],
        };

        var cat = new ProtocolCatalogue
        {
            Id = "cat-1",
            Name = "Cat",
            GameSystemId = "gs-1",
            SelectionEntries =
            [
                new ProtocolSelectionEntry
                {
                    Id = "se-1",
                    Name = "Entry",
                    Type = "upgrade",
                    Modifiers =
                    [
                        new ProtocolModifier
                        {
                            Type = "set",
                            Field = "hidden",
                            Value = "true",
                            Conditions =
                            [
                                new ProtocolCondition
                                {
                                    Type = "atLeast",
                                    Value = 1,
                                    Field = "selections",
                                    Scope = "roster",
                                    ChildId = "se-other",
                                },
                            ],
                            ConditionGroups =
                            [
                                new ProtocolConditionGroup
                                {
                                    Type = "or",
                                    Conditions =
                                    [
                                        new ProtocolCondition
                                        {
                                            Type = "equalTo",
                                            Value = 0,
                                            Field = "selections",
                                            Scope = "parent",
                                            ChildId = "se-x",
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        var xml = CatXmlGenerator.GenerateCatalogueXml(gs, cat);
        var node = DeserializeCatalogue(xml);

        var entry = node.SelectionEntries[0];
        Assert.Single(entry.Modifiers);

        var modifier = entry.Modifiers[0];
        Assert.Equal(ModifierKind.Set, modifier.Type);
        Assert.Equal("hidden", modifier.Field);
        Assert.Equal("true", modifier.Value);
        Assert.Single(modifier.Conditions);
        Assert.Equal(ConditionKind.AtLeast, modifier.Conditions[0].Type);
        Assert.Equal("se-other", modifier.Conditions[0].ChildId);
        Assert.Single(modifier.ConditionGroups);
        Assert.Equal(ConditionGroupKind.Or, modifier.ConditionGroups[0].Type);
    }

    [Fact]
    public void GenerateCatalogueXml_SharedPools()
    {
        var gs = new ProtocolGameSystem { Id = "gs-1", Name = "GS" };

        var cat = new ProtocolCatalogue
        {
            Id = "cat-1",
            Name = "Cat",
            GameSystemId = "gs-1",
            SharedSelectionEntries =
            [
                new ProtocolSelectionEntry { Id = "sse-1", Name = "SharedEntry", Type = "upgrade" }
            ],
            SharedSelectionEntryGroups =
            [
                new ProtocolSelectionEntryGroup { Id = "ssg-1", Name = "SharedGroup" }
            ],
            SharedRules =
            [
                new ProtocolRule { Id = "sr-1", Name = "SharedRule", Description = "Desc" }
            ],
            SharedProfiles =
            [
                new ProtocolProfile { Id = "sp-1", Name = "SharedProfile",
                    TypeId = "pt-1", TypeName = "Stats" }
            ],
            SharedInfoGroups =
            [
                new ProtocolInfoGroup { Id = "sig-1", Name = "SharedIG" }
            ],
        };

        var xml = CatXmlGenerator.GenerateCatalogueXml(gs, cat);
        var node = DeserializeCatalogue(xml);

        Assert.Single(node.SharedSelectionEntries);
        Assert.Equal("SharedEntry", node.SharedSelectionEntries[0].Name);
        Assert.Single(node.SharedSelectionEntryGroups);
        Assert.Equal("SharedGroup", node.SharedSelectionEntryGroups[0].Name);
        Assert.Single(node.SharedRules);
        Assert.Equal("SharedRule", node.SharedRules[0].Name);
        Assert.Single(node.SharedProfiles);
        Assert.Equal("SharedProfile", node.SharedProfiles[0].Name);
        Assert.Single(node.SharedInfoGroups);
        Assert.Equal("SharedIG", node.SharedInfoGroups[0].Name);
    }

    [Fact]
    public void GenerateCatalogueXml_EntryLinks()
    {
        var gs = new ProtocolGameSystem { Id = "gs-1", Name = "GS" };

        var cat = new ProtocolCatalogue
        {
            Id = "cat-1",
            Name = "Cat",
            GameSystemId = "gs-1",
            SharedSelectionEntries =
            [
                new ProtocolSelectionEntry { Id = "sse-1", Name = "Target", Type = "upgrade" }
            ],
            EntryLinks =
            [
                new ProtocolEntryLink { Id = "el-1", Name = "Link", TargetId = "sse-1",
                    Type = "selectionEntry" }
            ],
        };

        var xml = CatXmlGenerator.GenerateCatalogueXml(gs, cat);
        var node = DeserializeCatalogue(xml);

        Assert.Single(node.EntryLinks);
        Assert.Equal("sse-1", node.EntryLinks[0].TargetId);
        Assert.Equal(EntryLinkKind.SelectionEntry, node.EntryLinks[0].Type);
    }

    [Fact]
    public void GenerateCatalogueXml_NestedChildren()
    {
        var gs = new ProtocolGameSystem { Id = "gs-1", Name = "GS" };

        var cat = new ProtocolCatalogue
        {
            Id = "cat-1",
            Name = "Cat",
            GameSystemId = "gs-1",
            SelectionEntries =
            [
                new ProtocolSelectionEntry
                {
                    Id = "se-parent",
                    Name = "Parent",
                    Type = "unit",
                    SelectionEntries =
                    [
                        new ProtocolSelectionEntry
                        {
                            Id = "se-child",
                            Name = "Child",
                            Type = "upgrade",
                            SelectionEntries =
                            [
                                new ProtocolSelectionEntry
                                {
                                    Id = "se-grandchild",
                                    Name = "Grandchild",
                                    Type = "upgrade",
                                },
                            ],
                        },
                    ],
                    SelectionEntryGroups =
                    [
                        new ProtocolSelectionEntryGroup
                        {
                            Id = "seg-1",
                            Name = "Options",
                            SelectionEntries =
                            [
                                new ProtocolSelectionEntry
                                {
                                    Id = "se-opt",
                                    Name = "Option A",
                                    Type = "upgrade",
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        var xml = CatXmlGenerator.GenerateCatalogueXml(gs, cat);
        var node = DeserializeCatalogue(xml);

        var parent = node.SelectionEntries[0];
        Assert.Equal("Parent", parent.Name);
        Assert.Single(parent.SelectionEntries);
        Assert.Equal("Child", parent.SelectionEntries[0].Name);
        Assert.Single(parent.SelectionEntries[0].SelectionEntries);
        Assert.Equal("Grandchild", parent.SelectionEntries[0].SelectionEntries[0].Name);
        Assert.Single(parent.SelectionEntryGroups);
        Assert.Equal("Options", parent.SelectionEntryGroups[0].Name);
        Assert.Single(parent.SelectionEntryGroups[0].SelectionEntries);
    }

    [Fact]
    public void GenerateCatalogueXml_ProfilesAndRules()
    {
        var gs = new ProtocolGameSystem
        {
            Id = "gs-1",
            Name = "GS",
            ProfileTypes =
            [
                new ProtocolProfileType { Id = "pt-1", Name = "Stats",
                    CharacteristicTypes =
                    [
                        new ProtocolCharacteristicType { Id = "ct-1", Name = "M" }
                    ] },
            ],
        };

        var cat = new ProtocolCatalogue
        {
            Id = "cat-1",
            Name = "Cat",
            GameSystemId = "gs-1",
            SelectionEntries =
            [
                new ProtocolSelectionEntry
                {
                    Id = "se-1",
                    Name = "Unit",
                    Type = "unit",
                    Profiles =
                    [
                        new ProtocolProfile
                        {
                            Id = "prof-1",
                            Name = "Unit Stats",
                            TypeId = "pt-1",
                            TypeName = "Stats",
                            Characteristics =
                            [
                                new ProtocolCharacteristic { Name = "M", TypeId = "ct-1", Value = "6\"" }
                            ],
                        },
                    ],
                    Rules =
                    [
                        new ProtocolRule { Id = "rule-1", Name = "Special Rule",
                            Description = "This unit can fly." }
                    ],
                },
            ],
        };

        var xml = CatXmlGenerator.GenerateCatalogueXml(gs, cat);
        var node = DeserializeCatalogue(xml);

        var entry = node.SelectionEntries[0];
        Assert.Single(entry.Profiles);
        Assert.Equal("Unit Stats", entry.Profiles[0].Name);
        Assert.Single(entry.Profiles[0].Characteristics);
        Assert.Equal("6\"", entry.Profiles[0].Characteristics[0].Value);
        Assert.Single(entry.Rules);
        Assert.Equal("This unit can fly.", entry.Rules[0].Description);
    }

    [Fact]
    public void GenerateCatalogueXml_Publications()
    {
        var gs = new ProtocolGameSystem { Id = "gs-1", Name = "GS" };

        var cat = new ProtocolCatalogue
        {
            Id = "cat-1",
            Name = "Cat",
            GameSystemId = "gs-1",
            Publications =
            [
                new ProtocolPublication
                {
                    Id = "pub-1",
                    Name = "Core Rules",
                    ShortName = "CR",
                    Publisher = "GW",
                    PublicationDate = "2024-01-01",
                },
            ],
        };

        var xml = CatXmlGenerator.GenerateCatalogueXml(gs, cat);
        var node = DeserializeCatalogue(xml);

        Assert.Single(node.Publications);
        Assert.Equal("Core Rules", node.Publications[0].Name);
        Assert.Equal("CR", node.Publications[0].ShortName);
        Assert.Equal("GW", node.Publications[0].Publisher);
    }

    [Fact]
    public void GenerateCatalogueXml_InfoGroupsAndInfoLinks()
    {
        var gs = new ProtocolGameSystem { Id = "gs-1", Name = "GS" };

        var cat = new ProtocolCatalogue
        {
            Id = "cat-1",
            Name = "Cat",
            GameSystemId = "gs-1",
            SharedInfoGroups =
            [
                new ProtocolInfoGroup
                {
                    Id = "ig-1",
                    Name = "Weapon Stats",
                    Rules = [new ProtocolRule { Id = "r-1", Name = "Rule", Description = "D" }],
                },
            ],
            InfoLinks =
            [
                new ProtocolInfoLink { Id = "il-1", Name = "Link", TargetId = "ig-1",
                    Type = "infoGroup" }
            ],
        };

        var xml = CatXmlGenerator.GenerateCatalogueXml(gs, cat);
        var node = DeserializeCatalogue(xml);

        Assert.Single(node.SharedInfoGroups);
        Assert.Single(node.SharedInfoGroups[0].Rules);
        Assert.Single(node.InfoLinks);
        Assert.Equal(InfoLinkKind.InfoGroup, node.InfoLinks[0].Type);
    }

    [Fact]
    public void GenerateCatalogueXml_ConstraintProperties()
    {
        var gs = new ProtocolGameSystem { Id = "gs-1", Name = "GS" };

        var cat = new ProtocolCatalogue
        {
            Id = "cat-1",
            Name = "Cat",
            GameSystemId = "gs-1",
            SelectionEntries =
            [
                new ProtocolSelectionEntry
                {
                    Id = "se-1",
                    Name = "Entry",
                    Type = "upgrade",
                    Constraints =
                    [
                        new ProtocolConstraint
                        {
                            Id = "con-1",
                            Type = "max",
                            Value = 3,
                            Field = "selections",
                            Scope = "roster",
                            Shared = true,
                            IncludeChildSelections = true,
                            IncludeChildForces = true,
                            PercentValue = false,
                        },
                    ],
                },
            ],
        };

        var xml = CatXmlGenerator.GenerateCatalogueXml(gs, cat);
        var node = DeserializeCatalogue(xml);

        var constraint = node.SelectionEntries[0].Constraints[0];
        Assert.Equal(ConstraintKind.Maximum, constraint.Type);
        Assert.Equal(3m, constraint.Value);
        Assert.Equal("roster", constraint.Scope);
        Assert.True(constraint.Shared);
        Assert.True(constraint.IncludeChildSelections);
        Assert.True(constraint.IncludeChildForces);
    }

    [Fact]
    public void GenerateGameSystemXml_NestedForceEntries()
    {
        var gs = new ProtocolGameSystem
        {
            Id = "gs-1",
            Name = "GS",
            ForceEntries =
            [
                new ProtocolForceEntry
                {
                    Id = "fe-1",
                    Name = "Primary",
                    ForceEntries =
                    [
                        new ProtocolForceEntry { Id = "fe-2", Name = "Allied" }
                    ],
                },
            ],
        };

        var xml = CatXmlGenerator.GenerateGameSystemXml(gs);
        var node = DeserializeGamesystem(xml);

        Assert.Single(node.ForceEntries);
        Assert.Single(node.ForceEntries[0].ForceEntries);
        Assert.Equal("Allied", node.ForceEntries[0].ForceEntries[0].Name);
    }

    // Helper: deserialize .gst XML back into GamesystemNode
    private static GamesystemNode DeserializeGamesystem(string xml)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        return stream.DeserializeGamesystem()!;
    }

    // Helper: deserialize .cat XML back into CatalogueNode
    private static CatalogueNode DeserializeCatalogue(string xml)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        return stream.DeserializeCatalogue()!;
    }

    [Fact]
    public void GenerateCatalogueXml_EmptyCatalogueArray_Throws()
    {
        var gs = new ProtocolGameSystem { Id = "gs-1", Name = "GS" };
        Assert.Throws<ArgumentException>(() =>
            CatXmlGenerator.GenerateCatalogueXml(gs, []));
    }

    [Fact]
    public void GenerateGameSystemXml_MinimalSpec_ProducesValidXml()
    {
        var gs = new ProtocolGameSystem { Id = "test-gs", Name = "Test Game System" };
        var xml = CatXmlGenerator.GenerateGameSystemXml(gs);
        Assert.NotNull(xml);
        Assert.Contains("<?xml", xml);
        Assert.Contains("gameSystem", xml);
    }

    [Fact]
    public void GenerateCatalogueXml_SpecialXmlCharsInName_ProducesValidXml()
    {
        var gs = new ProtocolGameSystem { Id = "gs-1", Name = "Test & <System>" };
        var cat = new ProtocolCatalogue
        {
            Id = "cat-1",
            Name = "Catalogue with \"quotes\" & <angles>",
            GameSystemId = "gs-1",
        };

        var xml = CatXmlGenerator.GenerateCatalogueXml(gs, cat);
        // XML should be well-formed — deserialize to verify
        var node = DeserializeCatalogue(xml);
        Assert.Contains("quotes", node.Name);
        Assert.Contains("angles", node.Name);
    }

    [Fact]
    public void GenerateCatalogueXml_EmptyCatalogue_ProducesValidXml()
    {
        var gs = new ProtocolGameSystem { Id = "gs-1", Name = "GS" };
        var cat = new ProtocolCatalogue { Id = "cat-1", Name = "Empty Cat", GameSystemId = "gs-1" };

        var xml = CatXmlGenerator.GenerateCatalogueXml(gs, cat);
        var node = DeserializeCatalogue(xml);
        Assert.Equal("Empty Cat", node.Name);
        Assert.Empty(node.SelectionEntries);
    }
}
