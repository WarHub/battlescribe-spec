using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Cli.Tests;

/// <summary>Tests the human-readable step descriptions and artifact file-name sanitizing.</summary>
[Trait("Category", "Unit")]
public sealed class StepFormatterTests
{
    [Fact]
    public void DescribeStep_FormatsAnActionWithItsParameters()
    {
        var step = new StepDef { Action = "selectEntry", Id = "s1", EntryId = "se-1", ForceId = "f-1", Count = 2 };

        var description = StepFormatter.DescribeStep(step);

        Assert.StartsWith("selectEntry", description);
        Assert.Contains("id=s1", description);
        Assert.Contains("entryId=se-1", description);
        Assert.Contains("forceId=f-1", description);
        Assert.Contains("count=2", description);
    }

    [Fact]
    public void DescribeStep_LabelsAssertions()
    {
        var step = new StepDef { ExpectedState = new ExpectedStateDef() };
        Assert.Equal("expectedState (assertion)", StepFormatter.DescribeStep(step));
    }

    [Fact]
    public void DescribeStep_HandlesEmptyStep()
    {
        Assert.Equal("(unknown)", StepFormatter.DescribeStep(new StepDef()));
    }

    [Fact]
    public void SanitizeFileName_ReplacesPathSeparators()
    {
        // '/' is an invalid file-name char on every platform.
        Assert.Equal("ab_cd", StepFormatter.SanitizeFileName("ab/cd"));
    }

    [Fact]
    public void SanitizeFileName_LeavesPlainNamesUntouched()
    {
        Assert.Equal("addForce", StepFormatter.SanitizeFileName("addForce"));
    }
}
