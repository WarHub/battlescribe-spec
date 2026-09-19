using BattleScribeSpec.NewRecruit;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// The NewRecruit store-direct adapter's JS snippets report failure as a string, and the string's
/// tag is the only thing that says whose failure it is. These pin the mapping from tag to exception,
/// because the action classifier reads an untagged <see cref="InvalidOperationException"/> as the
/// ENGINE refusing — and until the tags existed, that is what every lookup miss on this lane was
/// (#25). The lane-level proof is <c>FrozenNrRosterConformanceTests.AnIdTheRosterDoesNotHave_IsAnAddressingFailure</c>;
/// this is the part that runs without a browser.
/// </summary>
[Trait("Category", "Unit")]
public sealed class NewRecruitFailureClassificationTests
{
    [Theory]
    [InlineData("ADDRESS:Selection not found with uid 'x1'", ActionFailureKind.Address, "Selection not found with uid 'x1'")]
    [InlineData("HARNESS:No current roster", ActionFailureKind.Harness, "No current roster")]
    [InlineData("ERROR:SelectEntry error: boom", ActionFailureKind.Engine, "SelectEntry error: boom")]
    [InlineData("DeselectSelection error: boom", ActionFailureKind.Engine, "DeselectSelection error: boom")]
    public void EachTag_BecomesTheFailureItNames(string failure, ActionFailureKind kind, string message)
    {
        var exception = NewRecruitActions.FailureFor(failure);

        Assert.Equal(kind, ActionFailure.Classify(exception));
        Assert.Equal(message, exception.Message);
    }

    /// <summary>
    /// A create action answers a uid on success and a tagged string on failure, through one return
    /// value — so every tag has to be recognised there too, or a failure would come back as a uid.
    /// </summary>
    [Theory]
    [InlineData("ADDRESS:Force not found with uid 'f1'")]
    [InlineData("HARNESS:No army or book")]
    [InlineData("ERROR:addInstance on 'se-1' did not produce a new selection")]
    public void ACreateActionsFailure_IsNeverReturnedAsAUid(string result)
        => Assert.ThrowsAny<Exception>(() => NewRecruitActions.HandleCreateResult(result));

    [Fact]
    public void ACreateActionsUid_IsReturned()
        => Assert.Equal("k3j9x2", NewRecruitActions.HandleCreateResult("k3j9x2"));
}
