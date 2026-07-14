using BattleScribeSpec.BsGameDataUiDriver;
using BattleScribeSpec.Concurrency;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Shared fixture for the BS GameData UI driver conformance tests.
/// Discovers BS binary artifacts, launches the BattleScribe process with the Java agent,
/// and provides a single <see cref="BsGameDataUiEngine"/> instance for all tests.
///
/// <para>
/// <b>Sequential by design</b> — a single BS desktop app instance handles one spec at a time.
/// Tests in the <c>BsGameDataUi</c> collection run sequentially (no xUnit parallelism).
/// </para>
///
/// <para>
/// <b>Skipped when</b>:
/// <list type="bullet">
///   <item><c>BS_UI_SKIP=true</c> environment variable is set</item>
///   <item>BattleScribe binary artifacts not found (run <c>setup.ps1</c>)</item>
///   <item>Java agent JAR not found (run <c>pwsh -File src/bs-ui-java-agent/build.ps1</c>)</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Reuse (warm start) is not a knob here.</b> Whether the JVM survives between specs is
/// <see cref="ConcurrencyPlan.ReuseGameData"/>, resolved from <see cref="FixtureConcurrency"/> like
/// every other concurrency/reuse decision in the harness. It used to be the <c>BS_UI_KEEP_ALIVE</c>
/// environment variable, which answered the same question and <em>disagreed</em> with the policy by
/// default — unset (any local <c>dotnet test</c>) ran cold while the policy said warm, and CI set
/// the variable in two jobs to paper over the gap. That is exactly the "two mechanisms that can
/// disagree, one silently" defect the concurrency model exists to remove.
/// </para>
///
/// <para>
/// <b>Environment variables</b>:
/// <list type="bullet">
///   <item><c>BS_UI_SKIP</c> — set to "true" to skip all BS UI tests</item>
/// </list>
/// </para>
/// </summary>
public sealed class BsGameDataUiFixture : IAsyncLifetime
{
    public BsGameDataUiEngine? Engine { get; private set; }
    public bool Available => Engine is not null;

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("BS_UI_SKIP") == "true")
        {
            return;
        }

        var options = BsGameDataUiEngine.FindOptions();
        if (options is null)
        {
            return;
        }

        // KeepAlive means exactly "the plan says reuse this engine" — the same single decision the
        // CLI path takes (HostEngineFactory.CreateBsUiGameDataEngine). No environment variable.
        var keepAlive = FixtureConcurrency.Resolve("battlescribe-ui").ReuseGameData;

        using var span = FixtureTelemetry.StartInit(nameof(BsGameDataUiFixture));
        var engine = new BsGameDataUiEngine(options) { KeepAlive = keepAlive };

        // Verify agent is connectable with an empty setup — fail gracefully if not.
        // This avoids hard test failures when BS artifacts are present but the agent JAR isn't built.
        try
        {
            // Don't actually launch the app here — just validate options.
            // The app is launched on first Setup() call during a test.
            Engine = engine;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bs-gamedata-ui-fixture] Failed to initialize: {ex.Message}");
            engine.Dispose();
        }

        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Engine?.Dispose();
        Engine = null;
        await Task.CompletedTask;
    }
}

[CollectionDefinition("BsGameDataUi")]
public class BsGameDataUiCollection : ICollectionFixture<BsGameDataUiFixture>
{
}
