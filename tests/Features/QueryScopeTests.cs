using WarHub.ArmouryModel.Source;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Tests that query scope, field, and filter properties round-trip correctly
/// through Core → Node conversion (via .ToNode()).
/// Note: These test the WarHub.ArmouryModel data model layer, not runtime query resolution.
/// </summary>
[Trait("Category", "Unit")]
public class QueryScopeStructureTests
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
        var condition = new ConditionCore
        {
            Type = ConditionKind.AtLeast,
            Value = 1,
            Field = "selections",
            Scope = scope,
        }.ToNode();
        Assert.Equal(scope, condition.Scope);
    }

    [Theory]
    [InlineData("selections")]
    [InlineData("forces")]
    [InlineData("pts")]
    [InlineData("custom-cost")]
    public void Condition_AcceptsFieldValue(string field)
    {
        var condition = new ConditionCore
        {
            Type = ConditionKind.EqualTo,
            Value = 1,
            Field = field,
            Scope = "force",
        }.ToNode();
        Assert.Equal(field, condition.Field);
    }

    [Theory]
    [InlineData("any")]
    [InlineData("entry-commander")]
    [InlineData("cat-hq")]
    public void Condition_AcceptsChildIdFilter(string childId)
    {
        var condition = new ConditionCore
        {
            Type = ConditionKind.AtLeast,
            Value = 1,
            Field = "selections",
            Scope = "force",
            ChildId = childId,
        }.ToNode();
        Assert.Equal(childId, condition.ChildId);
    }

    [Fact]
    public void Condition_IncludeChildSelections_Flag()
    {
        var condition = new ConditionCore
        {
            Type = ConditionKind.AtLeast,
            Value = 1,
            Field = "selections",
            Scope = "force",
            IncludeChildSelections = true,
        }.ToNode();
        Assert.True(condition.IncludeChildSelections);
    }

    [Fact]
    public void Condition_IncludeChildForces_Flag()
    {
        var condition = new ConditionCore
        {
            Type = ConditionKind.AtLeast,
            Value = 1,
            Field = "selections",
            Scope = "roster",
            IncludeChildForces = true,
        }.ToNode();
        Assert.True(condition.IncludeChildForces);
    }

    [Fact]
    public void Condition_IsValuePercentage_Flag()
    {
        var condition = new ConditionCore
        {
            Type = ConditionKind.AtMost,
            Value = 50,
            Field = "pts",
            Scope = "roster",
            IsValuePercentage = true,
        }.ToNode();
        Assert.True(condition.IsValuePercentage);
        Assert.Equal(50m, condition.Value);
    }

    [Fact]
    public void Condition_Shared_Flag()
    {
        var condition = new ConditionCore
        {
            Type = ConditionKind.AtLeast,
            Value = 1,
            Field = "selections",
            Scope = "force",
            Shared = true,
        }.ToNode();
        Assert.True(condition.Shared);
    }

    [Fact]
    public void Constraint_ScopeAndField_MatchConditionPattern()
    {
        var constraint = new ConstraintCore
        {
            Id = "test",
            Type = ConstraintKind.Maximum,
            Value = 3,
            Scope = "force",
            Field = "selections",
            IncludeChildSelections = false,
        }.ToNode();

        Assert.Equal("force", constraint.Scope);
        Assert.Equal("selections", constraint.Field);
    }

    [Fact]
    public void Repeat_HasQueryFields()
    {
        var repeat = new RepeatCore
        {
            Value = 5m,
            RepeatCount = 1,
            RoundUp = false,
            Field = "selections",
            Scope = "force",
            ChildId = "entry-soldier-model",
            IncludeChildSelections = true,
        }.ToNode();

        Assert.Equal(5m, repeat.Value);
        Assert.Equal(1, repeat.RepeatCount);
        Assert.False(repeat.RoundUp);
        Assert.Equal("force", repeat.Scope);
        Assert.Equal("entry-soldier-model", repeat.ChildId);
        Assert.True(repeat.IncludeChildSelections);
    }
}
