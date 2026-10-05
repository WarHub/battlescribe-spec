using System.Reflection;
using System.Text.Json.Nodes;
using BattleScribeSpec.BsGameDataUiDriver;
using BattleScribeSpec.BsRosterUiDriver;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// The two decisions behind handing a warm BattleScribe app to the next spec, checked without an
/// app: a failed call marks it for closing whatever the failure was, and the windows it shows are
/// read as leftovers unless they are its main window.
/// </summary>
/// <remarks>
/// The mark is read by reflection, as <see cref="BsUiSetupFailureTeardownTests"/> reads <c>_app</c>:
/// the engines expose no seam for it, and its effect — the next <c>Cleanup</c> closing the app —
/// needs an app to observe. The failure used is an engine that was never set up, which is the
/// point: it is neither a timeout nor an unexpected-modal message, the only two failures that
/// marked an app before #526.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class BsUiWarmReuseTests
{
    private static readonly BsUiOptions NoApp = new()
    {
        JavaPath = "unused-java",
        RosterEditorJarPath = "unused.jar",
        AgentJarPath = "unused-agent.jar",
    };

    [Fact]
    public void BsUiRosterEngine_AnyFailedCall_MarksTheAppForClosing()
    {
        using var engine = new BsUiRosterEngine(NoApp) { KeepAlive = true };
        engine.SetTestContext("spec-x");

        Assert.Throws<InvalidOperationException>(() => engine.RemoveForce("force-1"));

        Assert.Equal("spec 'spec-x' failed in RemoveForce (InvalidOperationException)", PoisonedBy(engine));
    }

    [Fact]
    public void BsGameDataUiEngine_AnyFailedCall_MarksTheAppForClosing()
    {
        using var engine = new BsGameDataUiEngine(NoApp) { KeepAlive = true };
        engine.SetTestContext("spec-x");

        Assert.Throws<InvalidOperationException>(() => engine.RemoveEntry("entry-1"));

        Assert.Equal("spec 'spec-x' failed in RemoveEntry (InvalidOperationException)", PoisonedBy(engine));
    }

    [Fact]
    public void DescribeWindowsBesides_NamesEveryTitledWindowButTheMainOne()
    {
        var mainOnly = JsonNode.Parse("""[{"title":"Roster Editor 2.03.21 - spec-x (GS v1)","modal":false,"text":null}]""");
        Assert.Null(AgentClient.DescribeWindowsBesides(mainOnly, "Roster Editor"));

        var leftovers = JsonNode.Parse("""
            [
              {"title":"Roster Editor","modal":false,"text":null},
              {"title":"New Roster","modal":true,"text":"Test System — New Roster"},
              {"title":"Loading...","modal":true,"text":null},
              {"title":null,"modal":false,"text":null}
            ]
            """);
        Assert.Equal(
            "[New Roster] (Test System — New Roster), [Loading...]",
            AgentClient.DescribeWindowsBesides(leftovers, "Roster Editor"));
    }

    private static string? PoisonedBy(object engine)
        => (string?)engine.GetType().GetField("_poisonedBy", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine);
}
