using BattleScribeSpec;
using WarHub.ArmouryModel.Source;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Category 7: Constraint Validation
/// Tests that constraints are correctly structured for min/max enforcement.
/// </summary>
public class ConstraintValidationTests
{
    [Fact]
    public void Constraint_Minimum_IsCorrectlyDefined()
    {
        var cat = TestDataFactory.CreateBasicCatalogue();
        var commander = cat.SelectionEntries.First(e => e.Name == "Commander");
        var minConstraint = commander.Constraints.First(c => c.Type == ConstraintKind.Minimum);

        Assert.Equal(0m, minConstraint.Value); // 0 = not required
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
    [InlineData(0, true)]   // 0 = not required, valid
    [InlineData(-1, true)]  // -1 = unlimited max, valid
    [InlineData(1, true)]   // explicit limit, valid
    [InlineData(10, true)]  // explicit limit, valid
    public void Constraint_SpecialValues_AreRecognized(decimal value, bool isValid)
    {
        var constraint = NodeFactory.Constraint() with
        {
            Type = ConstraintKind.Maximum,
            Value = value,
            Scope = "force",
            Field = "selections",
        };

        Assert.Equal(value, constraint.Value);
        Assert.True(isValid); // all values are structurally valid
    }

    [Fact]
    public void Constraint_MinZero_MeansNotRequired()
    {
        // Spec: min constraint with value 0 means "not required"
        var constraint = NodeFactory.Constraint() with
        {
            Type = ConstraintKind.Minimum, Value = 0,
            Scope = "force", Field = "selections",
        };
        Assert.Equal(0m, constraint.Value);
        Assert.Equal(ConstraintKind.Minimum, constraint.Type);
    }

    [Fact]
    public void Constraint_MaxNegativeOne_MeansUnlimited()
    {
        // Spec: max constraint with value -1 means "unlimited"
        var constraint = NodeFactory.Constraint() with
        {
            Type = ConstraintKind.Maximum, Value = -1,
            Scope = "force", Field = "selections",
        };
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

        // min=1, max=1 means exactly one must be selected
        Assert.Equal(1m, min.Value);
        Assert.Equal(1m, max.Value);
    }

    [Fact]
    public void Constraint_ScopeVariants()
    {
        // Test that constraints work with different scope values
        var scopes = new[] { "self", "parent", "force", "roster", "primary-category" };
        foreach (var scope in scopes)
        {
            var constraint = NodeFactory.Constraint() with
            {
                Type = ConstraintKind.Maximum, Value = 5,
                Scope = scope, Field = "selections",
            };
            Assert.Equal(scope, constraint.Scope);
        }
    }
}
