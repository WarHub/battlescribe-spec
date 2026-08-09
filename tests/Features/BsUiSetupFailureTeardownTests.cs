using System.Reflection;
using BattleScribeSpec.BsGameDataUiDriver;
using BattleScribeSpec.BsRosterUiDriver;
using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests.Features;

/// <summary>
/// Regression test for a real product bug found in code review: <c>SetupAsync</c>'s failure path in
/// both <see cref="BsUiRosterEngine"/> and <see cref="BsGameDataUiEngine"/> called the unforced
/// <c>CleanupAsync()</c>. Since <c>KeepAlive</c> defaults to true for these engines (see
/// <c>HostEngineFactory</c>) and <c>_poisoned</c>/force are only ever set by an <b>action</b>-phase
/// failure — never a <b>setup</b>-phase one — a cold-start failure (e.g. the JavaFX window never
/// appearing) left <c>CleanupAsync()</c> a no-op: <c>_app</c> was never disposed, then silently
/// overwritten by the next cold-start attempt. That orphans the underlying JVM process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is hermetic (no real JVM/Java agent involved)</b>: both engines'
/// <c>SetupAsync</c> constructs the <c>BsRosterApp</c> and immediately calls <c>StartAsync</c>,
/// which calls <c>Process.Start</c> with the configured Java path. Pointing <c>JavaPath</c> at a
/// nonexistent executable makes <c>Process.Start</c> throw synchronously, before any OS process
/// exists — so this test exercises the exact catch block the fix lives in (SetupAsync's
/// <c>catch (Exception ex) { await CleanupAsync(force: true); ... }</c>) without spawning
/// anything real. The engine's private <c>_app</c>/<c>_client</c> fields are inspected via
/// reflection (there's no public seam to observe them, and adding one purely for this test would
/// be a bigger change than the bug warrants) to confirm the failed setup's app was actually torn
/// down instead of being left dangling under <c>KeepAlive = true</c>.
/// </para>
/// <para>
/// <b>What this does NOT cover</b>: the literal "a live JVM process gets orphaned" scenario, which
/// requires <c>Process.Start</c> to succeed and a <i>later</i> step (e.g. the 30s
/// <c>WaitForWindowAsync</c> window-wait) to fail — that needs a real JVM + JavaFX + display and
/// can't be simulated hermetically. The fix under test is in the shared catch handler that runs
/// for ANY setup-phase exception regardless of how far setup got, so this earlier-failure test
/// exercises the identical code path (and the identical bug, had it not been fixed) as the later,
/// JVM-requiring failure described in review.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class BsUiSetupFailureTeardownTests
{
    private const string NonexistentJavaPath = "no-such-java-executable-bsspec-test.exe";

    private static ProtocolCatalogue MinimalCatalogue
        => new() { Id = "cat", Name = "Cat", GameSystemId = "gs" };

    [Fact]
    public void BsUiRosterEngine_SetupFailure_TearsDownAppEvenWhenKeepAliveIsTrue()
    {
        var options = new BsUiOptions
        {
            JavaPath = NonexistentJavaPath,
            RosterEditorJarPath = "unused-roster-editor.jar",
            AgentJarPath = "unused-agent.jar",
        };

        using var engine = new BsUiRosterEngine(options) { KeepAlive = true };
        var gameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" };

        // A catalogue, so setup gets as far as Process.Start and fails on the bogus Java path —
        // which is the scenario this test documents. With none, `BuildXmlFiles` threw first and the
        // test passed on a data-generation failure instead, never reaching the JVM at all.
        // `RosterEngine_DataGenerationFailure_...` below covers that path deliberately.
        var errors = engine.Setup(gameSystem, [MinimalCatalogue]);

        Assert.NotEmpty(errors); // the bogus Java path must actually fail setup

        // ...and fail for THAT reason. Without this the test passes on any setup failure at all,
        // which is how it went green on a data-generation error for however long the guard sat in
        // the wrong place — asserting teardown after a failure that never created an app to tear
        // down. The Java path appears in what Process.Start throws on every platform.
        Assert.Contains(errors, e => e.Contains(NonexistentJavaPath, StringComparison.Ordinal));

        var appField = typeof(BsUiRosterEngine).GetField("_app", BindingFlags.NonPublic | BindingFlags.Instance);
        var clientField = typeof(BsUiRosterEngine).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(appField);
        Assert.NotNull(clientField);

        // Before the fix: CleanupAsync() (unforced) no-ops under KeepAlive=true, leaving _app
        // pointing at the broken cold-start instance — the orphaned-JVM bug. After the fix:
        // CleanupAsync(force: true) tears it down and nulls it out regardless of KeepAlive.
        Assert.Null(appField!.GetValue(engine));
        Assert.Null(clientField!.GetValue(engine));
    }

    /// <summary>
    /// A setup-phase failure BEFORE the app starts is still a setup failure, and
    /// <c>Setup</c> reports it the way it reports every other one.
    /// </summary>
    /// <remarks>
    /// The roster engine's <c>BuildXmlFiles</c> rejects an empty catalogue array, and that
    /// generation used to run OUTSIDE the handler that converts setup failures into returned
    /// errors — so this threw out of <c>Setup</c>, past <c>RosterRunner</c>'s "Setup error:"
    /// reporting and past the forced teardown. The gamedata twin generates inside its handler and
    /// never had the gap; the two were given the same fix and drifted on where the boundary sat.
    /// <para>
    /// This asserts the CONTRACT, not the guard's position: whatever rejects the data, and wherever
    /// it sits, the caller sees errors rather than an exception.
    /// </para>
    /// </remarks>
    [Fact]
    public void BsUiRosterEngine_DataGenerationFailure_ReturnsErrorsInsteadOfThrowing()
    {
        var options = new BsUiOptions
        {
            JavaPath = NonexistentJavaPath,
            RosterEditorJarPath = "unused-roster-editor.jar",
            AgentJarPath = "unused-agent.jar",
        };

        using var engine = new BsUiRosterEngine(options) { KeepAlive = true };
        var gameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" };

        // No catalogues — the generator's own precondition, and the closest hermetic stand-in for
        // any spec whose data it refuses.
        var errors = engine.Setup(gameSystem, []);

        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("catalogue", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BsGameDataUiEngine_SetupFailure_TearsDownAppEvenWhenKeepAliveIsTrue()
    {
        var options = new BsUiOptions
        {
            JavaPath = NonexistentJavaPath,
            RosterEditorJarPath = "unused-data-editor.jar",
            AgentJarPath = "unused-agent.jar",
        };

        using var engine = new BsGameDataUiEngine(options) { KeepAlive = true };
        var gameSystem = new ProtocolGameSystem { Id = "gs", Name = "GS" };

        // No catalogue needed: this engine's BuildXmlFiles tolerates an empty array, so setup
        // reaches Process.Start either way. That difference from the roster engine is why only one
        // of these two ever exercised the failure it documents.
        var errors = engine.Setup(gameSystem, []);

        Assert.NotEmpty(errors); // the bogus Java path must actually fail setup
        Assert.Contains(errors, e => e.Contains(NonexistentJavaPath, StringComparison.Ordinal));

        var appField = typeof(BsGameDataUiEngine).GetField("_app", BindingFlags.NonPublic | BindingFlags.Instance);
        var clientField = typeof(BsGameDataUiEngine).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(appField);
        Assert.NotNull(clientField);

        Assert.Null(appField!.GetValue(engine));
        Assert.Null(clientField!.GetValue(engine));
    }
}
