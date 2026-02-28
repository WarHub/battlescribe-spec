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
        // Verify all ConditionKind enum values are defined
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
        // Verify commonly used scope values work in the data model
        var cat = TestDataFactory.CreateModifierTestCatalogue();

        // Force scope
        var forceScopeCondition = cat.SelectionEntries
            .SelectMany(e => e.Modifiers)
            .SelectMany(m => m.Conditions)
            .FirstOrDefault(c => c.Scope == "force");
        Assert.NotNull(forceScopeCondition);

        // Test that conditions can reference different scope values
        var testCondition = NodeFactory.Condition() with
        {
            Type = ConditionKind.EqualTo, Value = 1,
            Field = "selections", Scope = "roster"
        };
        Assert.Equal("roster", testCondition.Scope);

        testCondition = testCondition with { Scope = "self" };
        Assert.Equal("self", testCondition.Scope);

        testCondition = testCondition with { Scope = "parent" };
        Assert.Equal("parent", testCondition.Scope);
    }

    [Fact]
    public void ConditionGroup_And_CanBeCreated()
    {
        var group = NodeFactory.ConditionGroup() with
        {
            Type = ConditionGroupKind.And,
            Conditions =
            [
                NodeFactory.Condition() with
                {
                    Type = ConditionKind.AtLeast, Value = 1, Field = "selections", Scope = "force",
                },
                NodeFactory.Condition() with
                {
                    Type = ConditionKind.AtMost, Value = 5, Field = "selections", Scope = "force",
                },
            ],
        };

        Assert.Equal(ConditionGroupKind.And, group.Type);
        Assert.Equal(2, group.Conditions.Length);
    }

    [Fact]
    public void ConditionGroup_Or_CanBeCreated()
    {
        var group = NodeFactory.ConditionGroup() with
        {
            Type = ConditionGroupKind.Or,
            Conditions =
            [
                NodeFactory.Condition() with
                {
                    Type = ConditionKind.EqualTo, Value = 0, Field = "selections", Scope = "force",
                },
                NodeFactory.Condition() with
                {
                    Type = ConditionKind.AtLeast, Value = 3, Field = "selections", Scope = "force",
                },
            ],
        };

        Assert.Equal(ConditionGroupKind.Or, group.Type);
        Assert.Equal(2, group.Conditions.Length);
    }

    [Fact]
    public void ConditionGroup_Nested_CanBeCreated()
    {
        var nested = NodeFactory.ConditionGroup() with
        {
            Type = ConditionGroupKind.And,
            ConditionGroups =
            [
                NodeFactory.ConditionGroup() with
                {
                    Type = ConditionGroupKind.Or,
                    Conditions =
                    [
                        NodeFactory.Condition() with
                        {
                            Type = ConditionKind.EqualTo, Value = 1, Field = "selections", Scope = "force",
                        },
                    ],
                },
            ],
            Conditions =
            [
                NodeFactory.Condition() with
                {
                    Type = ConditionKind.AtLeast, Value = 1, Field = "selections", Scope = "roster",
                },
            ],
        };

        Assert.Equal(ConditionGroupKind.And, nested.Type);
        Assert.Single(nested.ConditionGroups);
        Assert.Equal(ConditionGroupKind.Or, nested.ConditionGroups[0].Type);
    }
}
