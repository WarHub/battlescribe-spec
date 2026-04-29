using WarHub.ArmouryModel.Source;

namespace BattleScribeSpec;

/// <summary>
/// Creates minimal synthetic BattleScribe data for targeted testing.
/// Each factory method produces a self-contained game system + catalogue
/// pair designed to test a specific specification area.
/// Uses Core records with .ToNode() for immutable construction.
/// </summary>
/// <remarks>
/// TODO: Consider moving to the test project (BattleScribeSpec.Tests) since this is
/// test infrastructure that doesn't need to ship with the main library. Currently in
/// src/ because both BattleScribeTestFixture and test project depend on it.
/// </remarks>
public static class TestDataFactory
{
    private static string NewId() => Guid.NewGuid().ToString();

    private static CostCore Pts(decimal value) => new() { TypeId = "pts", Name = "pts", Value = value };

    /// <summary>
    /// Creates a minimal game system with one cost type (pts) and one profile type (Stats).
    /// </summary>
    public static GamesystemNode CreateMinimalGamesystem()
    {
        return new GamesystemCore
        {
            Id = "test-gs-1",
            Name = "Test Game",
            BattleScribeVersion = "2.03",
            Revision = 1,
            CostTypes = [new CostTypeCore { Id = "pts", Name = "pts" }],
            ProfileTypes =
            [
                new ProfileTypeCore
                {
                    Id = "unit-stats",
                    Name = "Unit",
                    CharacteristicTypes =
                    [
                        new CharacteristicTypeCore { Id = "char-m", Name = "M" },
                        new CharacteristicTypeCore { Id = "char-ws", Name = "WS" },
                        new CharacteristicTypeCore { Id = "char-bs", Name = "BS" },
                        new CharacteristicTypeCore { Id = "char-s", Name = "S" },
                        new CharacteristicTypeCore { Id = "char-t", Name = "T" },
                        new CharacteristicTypeCore { Id = "char-w", Name = "W" },
                    ]
                }
            ],
            CategoryEntries =
            [
                new CategoryEntryCore { Id = "cat-hq", Name = "HQ" },
                new CategoryEntryCore { Id = "cat-troops", Name = "Troops" },
                new CategoryEntryCore { Id = "cat-faction", Name = "Faction" },
            ],
            ForceEntries =
            [
                new ForceEntryCore
                {
                    Id = "force-det",
                    Name = "Detachment",
                    CategoryLinks =
                    [
                        new CategoryLinkCore { Id = NewId(), TargetId = "cat-hq", Name = "HQ" },
                        new CategoryLinkCore { Id = NewId(), TargetId = "cat-troops", Name = "Troops" },
                    ]
                }
            ],
        }.ToNode();
    }

    /// <summary>
    /// Creates a catalogue with basic selection entries for testing
    /// selection add/remove and cost calculation.
    /// </summary>
    public static CatalogueNode CreateBasicCatalogue()
    {
        return new CatalogueCore
        {
            Id = "test-cat-1",
            Name = "Test Catalogue",
            GamesystemId = "test-gs-1",
            BattleScribeVersion = "2.03",
            Revision = 1,
            SelectionEntries =
            [
                new SelectionEntryCore
                {
                    Id = "entry-commander",
                    Name = "Commander",
                    Type = SelectionEntryKind.Unit,
                    Costs = [Pts(100)],
                    CategoryLinks =
                    [
                        new CategoryLinkCore { Id = NewId(), TargetId = "cat-hq", Name = "HQ" },
                    ],
                    Constraints =
                    [
                        new ConstraintCore
                        {
                            Id = "con-cmd-min", Type = ConstraintKind.Minimum, Value = 0,
                            Scope = "force", Field = "selections",
                        },
                        new ConstraintCore
                        {
                            Id = "con-cmd-max", Type = ConstraintKind.Maximum, Value = 3,
                            Scope = "force", Field = "selections",
                        },
                    ],
                    SelectionEntries =
                    [
                        new SelectionEntryCore
                        {
                            Id = "entry-power-sword",
                            Name = "Power Sword",
                            Type = SelectionEntryKind.Upgrade,
                            Costs = [Pts(5)],
                            Constraints =
                            [
                                new ConstraintCore
                                {
                                    Id = "con-sword-max", Type = ConstraintKind.Maximum, Value = 1,
                                    Scope = "parent", Field = "selections",
                                },
                            ],
                        },
                    ],
                },
                new SelectionEntryCore
                {
                    Id = "entry-soldiers",
                    Name = "Soldier Squad",
                    Type = SelectionEntryKind.Unit,
                    Costs = [Pts(10)],
                    CategoryLinks =
                    [
                        new CategoryLinkCore { Id = NewId(), TargetId = "cat-troops", Name = "Troops" },
                    ],
                    SelectionEntries =
                    [
                        new SelectionEntryCore
                        {
                            Id = "entry-soldier-model",
                            Name = "Soldier",
                            Type = SelectionEntryKind.Model,
                            Costs = [Pts(10)],
                            Constraints =
                            [
                                new ConstraintCore
                                {
                                    Id = "con-soldier-min", Type = ConstraintKind.Minimum, Value = 5,
                                    Scope = "parent", Field = "selections",
                                },
                                new ConstraintCore
                                {
                                    Id = "con-soldier-max", Type = ConstraintKind.Maximum, Value = 10,
                                    Scope = "parent", Field = "selections",
                                },
                            ],
                        },
                    ],
                },
            ],
        }.ToNode();
    }

    /// <summary>
    /// Creates a catalogue with modifiers for testing modifier evaluation.
    /// </summary>
    public static CatalogueNode CreateModifierTestCatalogue()
    {
        return new CatalogueCore
        {
            Id = "test-cat-modifiers",
            Name = "Modifier Test Catalogue",
            GamesystemId = "test-gs-1",
            BattleScribeVersion = "2.03",
            Revision = 1,
            SelectionEntries =
            [
                new SelectionEntryCore
                {
                    Id = "entry-name-mod",
                    Name = "Base Name",
                    Type = SelectionEntryKind.Upgrade,
                    Modifiers =
                    [
                        new ModifierCore
                        {
                            Type = ModifierKind.Append, Field = "name", Value = "(Modified)",
                        },
                    ],
                },
                new SelectionEntryCore
                {
                    Id = "entry-var-cost",
                    Name = "Variable Cost Unit",
                    Type = SelectionEntryKind.Unit,
                    Costs = [Pts(50)],
                    Modifiers =
                    [
                        new ModifierCore
                        {
                            Type = ModifierKind.Increment, Field = "pts", Value = "10",
                            Conditions =
                            [
                                new ConditionCore
                                {
                                    Type = ConditionKind.AtLeast, Value = 3,
                                    Field = "selections", Scope = "force", ChildId = "entry-var-cost",
                                },
                            ],
                        },
                    ],
                },
                new SelectionEntryCore
                {
                    Id = "entry-conditional",
                    Name = "Conditional Entry",
                    Type = SelectionEntryKind.Upgrade,
                    Hidden = true,
                    Modifiers =
                    [
                        new ModifierCore
                        {
                            Type = ModifierKind.Set, Field = "hidden", Value = "false",
                            Conditions =
                            [
                                new ConditionCore
                                {
                                    Type = ConditionKind.AtLeast, Value = 1,
                                    Field = "selections", Scope = "force", ChildId = "entry-commander",
                                },
                            ],
                        },
                    ],
                },
                new SelectionEntryCore
                {
                    Id = "entry-faction-swap",
                    Name = "Faction Swap Unit",
                    Type = SelectionEntryKind.Unit,
                    Costs = [Pts(75)],
                    CategoryLinks =
                    [
                        new CategoryLinkCore
                        {
                            Id = "catlink-hq", TargetId = "cat-hq", Name = "HQ", Primary = true,
                        },
                    ],
                    Modifiers =
                    [
                        new ModifierCore
                        {
                            Type = ModifierKind.SetPrimary, Field = "category",
                            Value = "cat-troops",
                        },
                    ],
                },
            ],
        }.ToNode();
    }

    /// <summary>
    /// Creates a catalogue with shared entries and entry links for testing reference resolution.
    /// </summary>
    public static CatalogueNode CreateLinkTestCatalogue()
    {
        return new CatalogueCore
        {
            Id = "test-cat-links",
            Name = "Link Test Catalogue",
            GamesystemId = "test-gs-1",
            BattleScribeVersion = "2.03",
            Revision = 1,
            SharedSelectionEntries =
            [
                new SelectionEntryCore
                {
                    Id = "shared-weapon-1",
                    Name = "Shared Weapon",
                    Type = SelectionEntryKind.Upgrade,
                    Costs = [Pts(15)],
                },
            ],
            SelectionEntries =
            [
                new SelectionEntryCore
                {
                    Id = "entry-linked-unit",
                    Name = "Linked Unit",
                    Type = SelectionEntryKind.Unit,
                    Costs = [Pts(80)],
                    EntryLinks =
                    [
                        new EntryLinkCore
                        {
                            Id = "link-to-shared",
                            TargetId = "shared-weapon-1",
                            Type = EntryLinkKind.SelectionEntry,
                        },
                    ],
                },
            ],
        }.ToNode();
    }

    /// <summary>
    /// Creates a catalogue with selection entry groups for testing default selection and group constraints.
    /// </summary>
    public static CatalogueNode CreateSelectionGroupTestCatalogue()
    {
        return new CatalogueCore
        {
            Id = "test-cat-groups",
            Name = "SelectionGroup Test Catalogue",
            GamesystemId = "test-gs-1",
            BattleScribeVersion = "2.03",
            Revision = 1,
            SelectionEntries =
            [
                new SelectionEntryCore
                {
                    Id = "entry-equipped",
                    Name = "Equipped Unit",
                    Type = SelectionEntryKind.Unit,
                    Costs = [Pts(50)],
                    SelectionEntryGroups =
                    [
                        new SelectionEntryGroupCore
                        {
                            Id = "group-weapon-choice",
                            Name = "Weapon Choice",
                            DefaultSelectionEntryId = "weapon-a",
                            Constraints =
                            [
                                new ConstraintCore
                                {
                                    Id = "con-weapon-min", Type = ConstraintKind.Minimum, Value = 1,
                                    Scope = "parent", Field = "selections",
                                },
                                new ConstraintCore
                                {
                                    Id = "con-weapon-max", Type = ConstraintKind.Maximum, Value = 1,
                                    Scope = "parent", Field = "selections",
                                },
                            ],
                            SelectionEntries =
                            [
                                new SelectionEntryCore
                                {
                                    Id = "weapon-a", Name = "Weapon A",
                                    Type = SelectionEntryKind.Upgrade, Costs = [Pts(0)],
                                },
                                new SelectionEntryCore
                                {
                                    Id = "weapon-b", Name = "Weapon B",
                                    Type = SelectionEntryKind.Upgrade, Costs = [Pts(10)],
                                },
                                new SelectionEntryCore
                                {
                                    Id = "weapon-c", Name = "Weapon C",
                                    Type = SelectionEntryKind.Upgrade, Costs = [Pts(25)],
                                },
                            ],
                        },
                    ],
                },
            ],
        }.ToNode();
    }
}
