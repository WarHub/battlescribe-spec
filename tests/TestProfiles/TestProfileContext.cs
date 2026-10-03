namespace BattleScribeSpec.Tests.Profiles;

/// <summary>
/// The profile this run of the test app resolved, for the code that reports on the run as a whole — the
/// trace summary's heading and the telemetry artifact's name (<c>TelemetryAssemblyFixture</c>).
/// </summary>
/// <remarks>
/// <see cref="TestHost"/> sets it before the platform starts, so it is fixed by the time any fixture
/// reads it. The host already knows the lane; a second channel (an environment variable, or parsing the
/// command line again) would be a second record of it.
/// </remarks>
internal static class TestProfileContext
{
    /// <summary>What <see cref="Current"/> holds for a run that named no profile.</summary>
    public const string Unprofiled = "unprofiled";

    /// <summary>The resolved profile's name, or <see cref="Unprofiled"/>.</summary>
    public static string Current { get; set; } = Unprofiled;
}
