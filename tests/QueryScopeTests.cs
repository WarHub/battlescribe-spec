using BattleScribeSpec;
using WarHub.ArmouryModel.Source;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Category 8: Query Scope Resolution
/// Tests that query scopes, fields, and filters can be constructed
/// correctly for conditions and constraints.
/// </summary>
public class QueryScopeTests
{
    [Theory]
    [InlineData("self")]
    [InlineData("parent")]
    [InlineData("force")]
    [InlineData("roster")]
    [InlineData("primary-category")]
    [InlineData("primary-catalogue")]
    public void Condition_AcceptsScopeValue(string scope)
    {
        var condition = NodeFactory.Condition() with
        {
            Type = ConditionKind.AtLeast, Value = 1,
            Field = "selections", Scope = scope,
        };
        Assert.Equal(scope, condition.Scope);
    }

    [Theory]
    [InlineData("selections")]
    [InlineData("forces")]
    [InlineData("pts")]          // cost type ID
    [InlineData("custom-cost")]  // custom cost type ID
    public void Condition_AcceptsFieldValue(string field)
    {
        var condition = NodeFactory.Condition() with
        {
            Type = ConditionKind.EqualTo, Value = 1,
            Field = field, Scope = "force",
        };
        Assert.Equal(field, condition.Field);
    }

    [Theory]
    [InlineData("any")]
    [InlineData("entry-commander")]  // specific entry ID
    [InlineData("cat-hq")]           // category entry ID
    public void Condition_AcceptsChildIdFilter(string childId)
    {
        var condition = NodeFactory.Condition() with
        {
            Type = ConditionKind.AtLeast, Value = 1,
            Field = "selections", Scope = "force", ChildId = childId,
        };
        Assert.Equal(childId, condition.ChildId);
    }

    [Fact]
    public void Condition_IncludeChildSelections_Flag()
    {
        var condition = NodeFactory.Condition() with
        {
            Type = ConditionKind.AtLeast, Value = 1,
            Field = "selections", Scope = "force",
            IncludeChildSelections = true,
        };
        Assert.True(condition.IncludeChildSelections);
    }

    [Fact]
    public void Condition_IncludeChildForces_Flag()
    {
        var condition = NodeFactory.Condition() with
        {
            Type = ConditionKind.AtLeast, Value = 1,
            Field = "selections", Scope = "roster",
            IncludeChildForces = true,
        };
        Assert.True(condition.IncludeChildForces);
    }

    [Fact]
    public void Condition_PercentValue_Flag()
    {
        var condition = NodeFactory.Condition() with
        {
            Type = ConditionKind.AtMost, Value = 50,
            Field = "pts", Scope = "roster",
            PercentValue = true,
        };
        Assert.True(condition.PercentValue);
        Assert.Equal(50m, condition.Value);
    }

    [Fact]
    public void Condition_Shared_Flag()
    {
        var condition = NodeFactory.Condition() with
        {
            Type = ConditionKind.AtLeast, Value = 1,
            Field = "selections", Scope = "force",
            Shared = true,
        };
        Assert.True(condition.Shared);
    }

    [Fact]
    public void Constraint_ScopeAndField_MatchConditionPattern()
    {
        // Constraints use the same scope/field system as conditions (minus childId)
        var constraint = NodeFactory.Constraint() with
        {
            Type = ConstraintKind.Maximum, Value = 3,
            Scope = "force", Field = "selections",
            IncludeChildSelections = false,
        };

        Assert.Equal("force", constraint.Scope);
        Assert.Equal("selections", constraint.Field);
    }

    [Fact]
    public void Repeat_HasQueryFields()
    {
        var repeat = NodeFactory.Repeat() with
        {
            Value = 5m,
            Repeats = 1,
            RoundUp = false,
            Field = "selections",
            Scope = "force",
            ChildId = "entry-soldier-model",
            IncludeChildSelections = true,
        };

        Assert.Equal(5m, repeat.Value);
        Assert.Equal(1, repeat.Repeats);
        Assert.False(repeat.RoundUp);
        Assert.Equal("force", repeat.Scope);
        Assert.Equal("entry-soldier-model", repeat.ChildId);
        Assert.True(repeat.IncludeChildSelections);
    }
}
