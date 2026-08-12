[assembly: AssemblyFixture(typeof(BattleScribeSpec.Tests.UiArtifactPathsAssemblyFixture))]

namespace BattleScribeSpec.Tests;

/// <summary>
/// Anchors the NR UI drivers' diagnostics directories at the repo root — once, before any test runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why anchoring is needed at all</b> is <see cref="TestPaths.AnchorDiagnosticsAtRepoRoot"/>'s
/// subject: the drivers' default is relative to the working directory, which VSTest sets to the test
/// assembly's output folder, so every screenshot and DOM dump landed three levels below where CI
/// looks for it.
/// </para>
/// <para>
/// <b>Why here rather than in the four NR UI fixtures</b> is the part worth writing down. The
/// override is an environment variable, so setting it is a process-wide write; done at collection
/// init it would land at an arbitrary moment during a run, and
/// <c>DiagnosticsIsolationTests.NrGameDataUiDiagnostics_DefaultArtifactsDir_DiffersPerWorkerIndex</c>
/// is a test that clears that exact variable and then resolves the default three times. A write
/// arriving between its clear and its reads would fail it — an intermittent introduced by the fix
/// for an intermittent. An assembly fixture initialises before any test case, so the variable is set
/// once, before anything can read it, and never touched again.
/// </para>
/// <para>
/// The BS drivers are not here because they expose a settable directory property instead, which
/// <see cref="BsRosterUiFixture"/> sets without touching the environment.
/// </para>
/// </remarks>
public sealed class UiArtifactPathsAssemblyFixture
{
    public UiArtifactPathsAssemblyFixture()
    {
        TestPaths.AnchorDiagnosticsAtRepoRoot("NR_UI_DIAGNOSTICS_DIR", "nr-ui-diagnostics");
        TestPaths.AnchorDiagnosticsAtRepoRoot(
            "NR_GAMEDATA_UI_DIAGNOSTICS_DIR", "nr-gamedata-ui-diagnostics");
    }
}
