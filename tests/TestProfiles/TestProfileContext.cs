namespace BattleScribeSpec.Tests.Profiles;

/// <summary>
/// The profile this run of the test app resolved, for the code that reports on the run as a whole — the
/// trace summary's heading and the telemetry artifact's name (<c>TelemetryAssemblyFixture</c>) — and
/// where that artifact went, for the engine-composition record the host writes beside it.
/// </summary>
/// <remarks>
/// <see cref="TestHost"/> sets <see cref="Current"/> before the platform starts, so it is fixed by the time
/// any fixture reads it. The host already knows the lane; a second channel (an environment variable, or
/// parsing the command line again) would be a second record of it.
/// </remarks>
internal static class TestProfileContext
{
    /// <summary>What <see cref="Current"/> holds for a run that named no profile.</summary>
    public const string Unprofiled = "unprofiled";

    /// <summary>The resolved profile's name, or <see cref="Unprofiled"/>.</summary>
    public static string Current { get; set; } = Unprofiled;

    /// <summary>
    /// The base path of this run's telemetry artifact set (<c>artifacts/telemetry/xunit-&lt;profile&gt;-&lt;timestamp&gt;</c>),
    /// set by <c>TelemetryAssemblyFixture</c> as soon as it names it; <see langword="null"/> in an assembly
    /// with no such fixture, or before any test ran. <see cref="TestHost"/> writes the run's
    /// <see cref="LaneComposition.JsonSuffix"/> record at this path, so the record sits in the set it describes
    /// and <c>TelemetryRetention</c> sweeps the two together.
    /// </summary>
    public static string? TelemetryArtifactBase { get; set; }
}
