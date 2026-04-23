using BattleScribeSpec;
using WarHub.ArmouryModel.Source;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Tests that constraint data structures are correctly constructed for min/max enforcement
/// via TestDataFactory.
/// Note: These verify structure only, NOT runtime constraint enforcement.
/// See ConstraintBattleScribeTests for runtime enforcement tests.
/// </summary>
[Trait("Category", "Unit")]
public class ConstraintStructureTests
{
    [Fact]
    public void Constraint_Minimum_IsCorrectlyDefined()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        var commander = cat.SelectionEntries.First(e => e.Name == "Commander");
        var minConstraint = commander.Constraints.First(c => c.Type == ConstraintKind.Minimum);

        Assert.Equal(0m, minConstraint.Value);
        Assert.Equal("force", minConstraint.Scope);
        Assert.Equal("selections", minConstraint.Field);
    }

    [Fact]
    public void Constraint_Maximum_IsCorrectlyDefined()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        var commander = cat.SelectionEntries.First(e => e.Name == "Commander");
        var maxConstraint = commander.Constraints.First(c => c.Type == ConstraintKind.Maximum);

        Assert.Equal(3m, maxConstraint.Value);
        Assert.Equal("force", maxConstraint.Scope);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(10)]
    public void Constraint_SpecialValues_AreRecognized(decimal value)
    {
        var constraint = new ConstraintCore
        {
            Id = "test",
            Type = ConstraintKind.Maximum,
            Value = value,
            Scope = "force",
            Field = "selections",
        }.ToNode();

        Assert.Equal(value, constraint.Value);
        Assert.Equal(ConstraintKind.Maximum, constraint.Type);
        Assert.Equal("force", constraint.Scope);
        Assert.Equal("selections", constraint.Field);
    }

    [Fact]
    public void Constraint_MinZero_MeansNotRequired()
    {
        var constraint = new ConstraintCore
        {
            Id = "test",
            Type = ConstraintKind.Minimum, Value = 0,
            Scope = "force", Field = "selections",
        }.ToNode();
        Assert.Equal(0m, constraint.Value);
        Assert.Equal(ConstraintKind.Minimum, constraint.Type);
    }

    [Fact]
    public void Constraint_MaxNegativeOne_MeansUnlimited()
    {
        var constraint = new ConstraintCore
        {
            Id = "test",
            Type = ConstraintKind.Maximum, Value = -1,
            Scope = "force", Field = "selections",
        }.ToNode();
        Assert.Equal(-1m, constraint.Value);
        Assert.Equal(ConstraintKind.Maximum, constraint.Type);
    }

    [Fact]
    public void ModelEntry_HasMinMaxRange()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        var squad = cat.SelectionEntries.First(e => e.Name == "Soldier Squad");
        var model = squad.SelectionEntries.First(e => e.Name == "Soldier");

        var min = model.Constraints.First(c => c.Type == ConstraintKind.Minimum);
        var max = model.Constraints.First(c => c.Type == ConstraintKind.Maximum);

        Assert.Equal(5m, min.Value);
        Assert.Equal(10m, max.Value);
        Assert.True(min.Value <= max.Value, "Min must be <= Max");
    }

    [Fact]
    public void SelectionGroup_HasExactlyOneConstraint()
    {
        var cat = TestDataFactory.CreateSelectionGroupTestCatalogue();
        var unit = cat.SelectionEntries.First(e => e.Name == "Equipped Unit");
        var group = unit.SelectionEntryGroups[0];

        var min = group.Constraints.First(c => c.Type == ConstraintKind.Minimum);
        var max = group.Constraints.First(c => c.Type == ConstraintKind.Maximum);

        Assert.Equal(1m, min.Value);
        Assert.Equal(1m, max.Value);
    }

    [Fact]
    public void Constraint_ScopeVariants()
    {
        var scopes = new[] { "self", "parent", "force", "roster", "primary-category" };
        foreach (var scope in scopes)
        {
            var constraint = new ConstraintCore
            {
                Id = "test",
                Type = ConstraintKind.Maximum, Value = 5,
                Scope = scope, Field = "selections",
            }.ToNode();
            Assert.Equal(scope, constraint.Scope);
        }
    }
}
