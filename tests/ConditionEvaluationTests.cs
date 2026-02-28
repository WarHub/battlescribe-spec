using BattleScribeSpec;
using WarHub.ArmouryModel.Source;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Category 6: Condition Evaluation
/// Tests that conditions are correctly structured with proper types,
/// scopes, fields, and comparison values.
/// </summary>
public class ConditionEvaluationTests
{
    [Fact]
    public void Condition_AtLeast_HasCorrectStructure()
    {
        var cat = TestDataFactory.CreateModifierTestCatalogue();
        var entry = cat.SelectionEntries.First(e => e.Id == "entry-var-cost");
        var condition = entry.Modifiers[0].Conditions[0];

        Assert.Equal(ConditionKind.AtLeast, condition.Type);
        Assert.Equal(3m, condition.Value);
        Assert.Equal("selections", condition.Field);
        Assert.Equal("force", condition.Scope);
    }

    [Fact]
    public void Condition_HasChildIdFilter()
    {
        var cat = TestDataFactory.CreateModifierTestCatalogue();
        var entry = cat.SelectionEntries.First(e => e.Id == "entry-var-cost");
        var condition = entry.Modifiers[0].Conditions[0];

        Assert.Equal("entry-var-cost", condition.ChildId);
    }

    [Fact]
    public void AllConditionKinds_AreValid()
    {
        var kinds = Enum.GetValues<ConditionKind>();
        Assert.Contains(ConditionKind.LessThan, kinds);
        Assert.Contains(ConditionKind.GreaterThan, kinds);
        Assert.Contains(ConditionKind.EqualTo, kinds);
        Assert.Contains(ConditionKind.NotEqualTo, kinds);
        Assert.Contains(ConditionKind.AtLeast, kinds);
        Assert.Contains(ConditionKind.AtMost, kinds);
        Assert.Contains(ConditionKind.InstanceOf, kinds);
        Assert.Contains(ConditionKind.NotInstanceOf, kinds);
    }

    [Fact]
    public void Condition_Scope_Values()
    {
        var cat = TestDataFactory.CreateModifierTestCatalogue();

        // Force scope
        var forceScopeCondition = cat.SelectionEntries
            .SelectMany(e => e.Modifiers)
            .SelectMany(m => m.Conditions)
            .FirstOrDefault(c => c.Scope == "force");
        Assert.NotNull(forceScopeCondition);

        // Test conditions with different scope values using Core records
        var rosterScope = new ConditionCore
        {
            Type = ConditionKind.EqualTo, Value = 1,
            Field = "selections", Scope = "roster"
        }.ToNode();
        Assert.Equal("roster", rosterScope.Scope);

        var selfScope = new ConditionCore
        {
            Type = ConditionKind.EqualTo, Value = 1,
            Field = "selections", Scope = "self"
        }.ToNode();
        Assert.Equal("self", selfScope.Scope);

        var parentScope = new ConditionCore
        {
            Type = ConditionKind.EqualTo, Value = 1,
            Field = "selections", Scope = "parent"
        }.ToNode();
        Assert.Equal("parent", parentScope.Scope);
    }

    [Fact]
    public void ConditionGroup_And_CanBeCreated()
    {
        var group = new ConditionGroupCore
        {
            Type = ConditionGroupKind.And,
            Conditions =
            [
                new ConditionCore
                {
                    Type = ConditionKind.AtLeast, Value = 1, Field = "selections", Scope = "force",
                },
                new ConditionCore
                {
                    Type = ConditionKind.AtMost, Value = 5, Field = "selections", Scope = "force",
                },
            ],
        }.ToNode();

        Assert.Equal(ConditionGroupKind.And, group.Type);
        Assert.Equal(2, group.Conditions.Count);
    }

    [Fact]
    public void ConditionGroup_Or_CanBeCreated()
    {
        var group = new ConditionGroupCore
        {
            Type = ConditionGroupKind.Or,
            Conditions =
            [
                new ConditionCore
                {
                    Type = ConditionKind.EqualTo, Value = 0, Field = "selections", Scope = "force",
                },
                new ConditionCore
                {
                    Type = ConditionKind.AtLeast, Value = 3, Field = "selections", Scope = "force",
                },
            ],
        }.ToNode();

        Assert.Equal(ConditionGroupKind.Or, group.Type);
        Assert.Equal(2, group.Conditions.Count);
    }

    [Fact]
    public void ConditionGroup_Nested_CanBeCreated()
    {
        var nested = new ConditionGroupCore
        {
            Type = ConditionGroupKind.And,
            ConditionGroups =
            [
                new ConditionGroupCore
                {
                    Type = ConditionGroupKind.Or,
                    Conditions =
                    [
                        new ConditionCore
                        {
                            Type = ConditionKind.EqualTo, Value = 1, Field = "selections", Scope = "force",
                        },
                    ],
                },
            ],
            Conditions =
            [
                new ConditionCore
                {
                    Type = ConditionKind.AtLeast, Value = 1, Field = "selections", Scope = "roster",
                },
            ],
        }.ToNode();

        Assert.Equal(ConditionGroupKind.And, nested.Type);
        Assert.Single(nested.ConditionGroups);
        Assert.Equal(ConditionGroupKind.Or, nested.ConditionGroups[0].Type);
    }
}
