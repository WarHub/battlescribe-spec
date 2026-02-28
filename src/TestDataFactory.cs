using WarHub.ArmouryModel.Source;

namespace BattleScribeSpec;

/// <summary>
/// Creates minimal synthetic BattleScribe data for targeted testing.
/// Each factory method produces a self-contained game system + catalogue
/// pair designed to test a specific specification area.
/// </summary>
public static class TestDataFactory
{
    private static string NewId() => Guid.NewGuid().ToString();

    /// <summary>
    /// Creates a minimal game system with one cost type (pts) and one profile type (Stats).
    /// </summary>
    public static GamesystemNode CreateMinimalGamesystem()
    {
        return NodeFactory.Gamesystem("Test Game") with
        {
            Id = "test-gs-1",
            BattleScribeVersion = "2.03",
            Revision = 1,
            CostTypes = [NodeFactory.CostType("pts") with { Id = "pts" }],
            ProfileTypes =
            [
                NodeFactory.ProfileType("Unit") with
                {
                    Id = "unit-stats",
                    CharacteristicTypes =
                    [
                        NodeFactory.CharacteristicType("M") with { Id = "char-m" },
                        NodeFactory.CharacteristicType("WS") with { Id = "char-ws" },
                        NodeFactory.CharacteristicType("BS") with { Id = "char-bs" },
                        NodeFactory.CharacteristicType("S") with { Id = "char-s" },
                        NodeFactory.CharacteristicType("T") with { Id = "char-t" },
                        NodeFactory.CharacteristicType("W") with { Id = "char-w" },
                    ]
                }
            ],
            CategoryEntries =
            [
                NodeFactory.CategoryEntry("HQ") with { Id = "cat-hq" },
                NodeFactory.CategoryEntry("Troops") with { Id = "cat-troops" },
                NodeFactory.CategoryEntry("Faction") with { Id = "cat-faction" },
            ],
            ForceEntries =
            [
                NodeFactory.ForceEntry("Detachment") with
                {
                    Id = "force-det",
                    CategoryLinks =
                    [
                        NodeFactory.CategoryLink() with { Id = NewId(), TargetId = "cat-hq", Name = "HQ" },
                        NodeFactory.CategoryLink() with { Id = NewId(), TargetId = "cat-troops", Name = "Troops" },
                    ]
                }
            ],
        };
    }

    /// <summary>
    /// Creates a catalogue with basic selection entries for testing
    /// selection add/remove and cost calculation.
    /// </summary>
    public static CatalogueNode CreateBasicCatalogue()
    {
        return NodeFactory.Catalogue("test-gs-1", "Test Catalogue") with
        {
            Id = "test-cat-1",
            BattleScribeVersion = "2.03",
            Revision = 1,
            SelectionEntries =
            [
                // A simple HQ unit with a cost
                NodeFactory.SelectionEntry("Commander") with
                {
                    Id = "entry-commander",
                    Type = SelectionEntryKind.Unit,
                    Costs = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = 100m }],
                    CategoryLinks =
                    [
                        NodeFactory.CategoryLink() with { Id = NewId(), TargetId = "cat-hq", Name = "HQ" },
                    ],
                    Constraints =
                    [
                        NodeFactory.Constraint() with
                        {
                            Id = "con-cmd-min", Type = ConstraintKind.Minimum, Value = 0,
                            Scope = "force", Field = "selections",
                        },
                        NodeFactory.Constraint() with
                        {
                            Id = "con-cmd-max", Type = ConstraintKind.Maximum, Value = 3,
                            Scope = "force", Field = "selections",
                        },
                    ],
                    SelectionEntries =
                    [
                        // Nested upgrade entry
                        NodeFactory.SelectionEntry("Power Sword") with
                        {
                            Id = "entry-power-sword",
                            Type = SelectionEntryKind.Upgrade,
                            Costs = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = 5m }],
                            Constraints =
                            [
                                NodeFactory.Constraint() with
                                {
                                    Id = "con-sword-max", Type = ConstraintKind.Maximum, Value = 1,
                                    Scope = "parent", Field = "selections",
                                },
                            ],
                        },
                    ],
                },

                // A troops unit with model count
                NodeFactory.SelectionEntry("Soldier Squad") with
                {
                    Id = "entry-soldiers",
                    Type = SelectionEntryKind.Unit,
                    Costs = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = 10m }],
                    CategoryLinks =
                    [
                        NodeFactory.CategoryLink() with { Id = NewId(), TargetId = "cat-troops", Name = "Troops" },
                    ],
                    SelectionEntries =
                    [
                        NodeFactory.SelectionEntry("Soldier") with
                        {
                            Id = "entry-soldier-model",
                            Type = SelectionEntryKind.Model,
                            Costs = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = 10m }],
                            Constraints =
                            [
                                NodeFactory.Constraint() with
                                {
                                    Id = "con-soldier-min", Type = ConstraintKind.Minimum, Value = 5,
                                    Scope = "parent", Field = "selections",
                                },
                                NodeFactory.Constraint() with
                                {
                                    Id = "con-soldier-max", Type = ConstraintKind.Maximum, Value = 10,
                                    Scope = "parent", Field = "selections",
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>
    /// Creates a catalogue with modifiers for testing modifier evaluation.
    /// </summary>
    public static CatalogueNode CreateModifierTestCatalogue()
    {
        return NodeFactory.Catalogue("test-gs-1", "Modifier Test Catalogue") with
        {
            Id = "test-cat-modifiers",
            BattleScribeVersion = "2.03",
            Revision = 1,
            SelectionEntries =
            [
                // Entry with unconditional name modifier
                NodeFactory.SelectionEntry("Base Name") with
                {
                    Id = "entry-name-mod",
                    Type = SelectionEntryKind.Upgrade,
                    Modifiers =
                    [
                        // Append " (Modified)" to name unconditionally
                        NodeFactory.Modifier() with
                        {
                            Type = ModifierKind.Append, Field = "name", Value = "(Modified)",
                        },
                    ],
                },

                // Entry with conditional cost modifier
                NodeFactory.SelectionEntry("Variable Cost Unit") with
                {
                    Id = "entry-var-cost",
                    Type = SelectionEntryKind.Unit,
                    Costs = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = 50m }],
                    Modifiers =
                    [
                        // Increment cost by 10 when at least 3 selections in force
                        NodeFactory.Modifier() with
                        {
                            Type = ModifierKind.Increment, Field = "pts", Value = "10",
                            Conditions =
                            [
                                NodeFactory.Condition() with
                                {
                                    Type = ConditionKind.AtLeast, Value = 3,
                                    Field = "selections", Scope = "force", ChildId = "entry-var-cost",
                                },
                            ],
                        },
                    ],
                },

                // Entry with hidden modifier (conditional visibility)
                NodeFactory.SelectionEntry("Conditional Entry") with
                {
                    Id = "entry-conditional",
                    Type = SelectionEntryKind.Upgrade,
                    Hidden = true,
                    Modifiers =
                    [
                        // Un-hide when a specific other entry is selected
                        NodeFactory.Modifier() with
                        {
                            Type = ModifierKind.Set, Field = "hidden", Value = "false",
                            Conditions =
                            [
                                NodeFactory.Condition() with
                                {
                                    Type = ConditionKind.AtLeast, Value = 1,
                                    Field = "selections", Scope = "force", ChildId = "entry-commander",
                                },
                            ],
                        },
                    ],
                },

                // Entry with set-primary category modifier
                NodeFactory.SelectionEntry("Faction Swap Unit") with
                {
                    Id = "entry-faction-swap",
                    Type = SelectionEntryKind.Unit,
                    Costs = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = 75m }],
                    CategoryLinks =
                    [
                        NodeFactory.CategoryLink() with
                        {
                            Id = "catlink-hq", TargetId = "cat-hq", Name = "HQ", Primary = true,
                        },
                    ],
                    Modifiers =
                    [
                        // Change primary category to Troops
                        NodeFactory.Modifier() with
                        {
                            Type = ModifierKind.SetPrimary, Field = "category",
                            Value = "cat-troops",
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>
    /// Creates a catalogue with shared entries and entry links for testing reference resolution.
    /// </summary>
    public static CatalogueNode CreateLinkTestCatalogue()
    {
        return NodeFactory.Catalogue("test-gs-1", "Link Test Catalogue") with
        {
            Id = "test-cat-links",
            BattleScribeVersion = "2.03",
            Revision = 1,
            SharedSelectionEntries =
            [
                NodeFactory.SelectionEntry("Shared Weapon") with
                {
                    Id = "shared-weapon-1",
                    Type = SelectionEntryKind.Upgrade,
                    Costs = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = 15m }],
                },
            ],
            SelectionEntries =
            [
                NodeFactory.SelectionEntry("Linked Unit") with
                {
                    Id = "entry-linked-unit",
                    Type = SelectionEntryKind.Unit,
                    Costs = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = 80m }],
                    EntryLinks =
                    [
                        NodeFactory.EntryLink() with
                        {
                            Id = "link-to-shared",
                            TargetId = "shared-weapon-1",
                            Type = EntryLinkKind.SelectionEntry,
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>
    /// Creates a catalogue with selection entry groups for testing default selection and group constraints.
    /// </summary>
    public static CatalogueNode CreateSelectionGroupTestCatalogue()
    {
        return NodeFactory.Catalogue("test-gs-1", "SelectionGroup Test Catalogue") with
        {
            Id = "test-cat-groups",
            BattleScribeVersion = "2.03",
            Revision = 1,
            SelectionEntries =
            [
                NodeFactory.SelectionEntry("Equipped Unit") with
                {
                    Id = "entry-equipped",
                    Type = SelectionEntryKind.Unit,
                    Costs = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = 50m }],
                    SelectionEntryGroups =
                    [
                        NodeFactory.SelectionEntryGroup("Weapon Choice") with
                        {
                            Id = "group-weapon-choice",
                            DefaultSelectionEntryId = "weapon-a",
                            Constraints =
                            [
                                NodeFactory.Constraint() with
                                {
                                    Id = "con-weapon-min", Type = ConstraintKind.Minimum, Value = 1,
                                    Scope = "parent", Field = "selections",
                                },
                                NodeFactory.Constraint() with
                                {
                                    Id = "con-weapon-max", Type = ConstraintKind.Maximum, Value = 1,
                                    Scope = "parent", Field = "selections",
                                },
                            ],
                            SelectionEntries =
                            [
                                NodeFactory.SelectionEntry("Weapon A") with
                                {
                                    Id = "weapon-a",
                                    Type = SelectionEntryKind.Upgrade,
                                    Costs = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = 0m }],
                                },
                                NodeFactory.SelectionEntry("Weapon B") with
                                {
                                    Id = "weapon-b",
                                    Type = SelectionEntryKind.Upgrade,
                                    Costs = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = 10m }],
                                },
                                NodeFactory.SelectionEntry("Weapon C") with
                                {
                                    Id = "weapon-c",
                                    Type = SelectionEntryKind.Upgrade,
                                    Costs = [NodeFactory.Cost("pts") with { TypeId = "pts", Value = 25m }],
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
