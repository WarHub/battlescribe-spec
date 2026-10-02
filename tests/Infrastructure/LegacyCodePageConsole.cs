using System.Diagnostics;

namespace BattleScribeSpec.TestSupport;

/// <summary>
/// Starts a child process on a console of its own set to a legacy code page, so a test sees what a
/// user on such a console sees. Shared by both test projects (Cli.Tests links this file).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a test needs this at all.</b> The defect it exposes — the two ends of a redirected pipe
/// encoding text differently (see <c>Utf8Stdio</c>) — is invisible on a console whose code page is
/// UTF-8, and that is the only console most of our runs ever see: Linux CI is UTF-8, and this
/// repository's Windows development box runs with the system-wide UTF-8 code page (ACP = OEMCP =
/// 65001). A round-trip test without this passes before the fix as well as after it.
/// </para>
/// <para>
/// <b>How.</b> On Windows the child is started through <c>cmd /d /s /c "chcp 437 &gt;nul &amp;&amp; …"</c>
/// with <see cref="ProcessStartInfo.CreateNoWindow"/> set, which gives <c>cmd</c> — and the child it
/// starts — a fresh hidden console; <c>chcp</c> changes that console and nothing else. Never run
/// <c>chcp</c> on an inherited console: it would change the test runner's console, which can be the
/// developer's terminal, and the change outlives the process.
/// </para>
/// <para>
/// <b>Off Windows it does nothing, and a test that relies on it cannot fail there.</b> .NET takes the
/// console encoding from the locale, which is UTF-8 on every runner we use, and there is no console to
/// re-page, so the child starts unchanged — and the defect cannot occur. Every CI job runs on Linux, so
/// <b>only a Windows run catches a regression here</b>: a developer's pre-push on Windows, or a test run
/// by hand there. A green Linux run of these tests is not evidence either way.
/// </para>
/// </remarks>
internal static class LegacyCodePageConsole
{
    /// <summary>
    /// 437, the US OEM code page. It has no em dash: on the way out .NET best-fits U+2014 to
    /// <c>-</c>, and on the way in it reads UTF-8's <c>E2 80 94</c> as three characters.
    /// </summary>
    public const int CodePage = 437;

    /// <summary>
    /// Rewrites <paramref name="psi"/> so its child starts on a console of its own whose code page is
    /// <see cref="CodePage"/>. Exit code, standard streams and environment pass through <c>cmd</c>
    /// unchanged. A no-op off Windows.
    /// </summary>
    /// <exception cref="ArgumentException">An argument carries a character cmd would interpret inside quotes.</exception>
    public static void Apply(ProcessStartInfo psi)
    {
        ArgumentNullException.ThrowIfNull(psi);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var arguments = psi.ArgumentList.Count > 0
            ? string.Join(' ', psi.ArgumentList.Select(Quote))
            : psi.Arguments;
        var command = $"{Quote(psi.FileName)} {arguments}";

        psi.ArgumentList.Clear();
        psi.FileName = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe";
        psi.Arguments = $"/d /s /c \"chcp {CodePage} >nul && {command}\"";
        psi.CreateNoWindow = true;
    }

    /// <summary>
    /// Quotes one argument for a cmd command line. Inside quotes cmd still expands <c>%</c> and ends
    /// the quote at <c>"</c>, so those are refused rather than escaped — every caller passes paths
    /// and flags. A trailing backslash would escape the closing quote for the child's argv parser,
    /// so it is refused too.
    /// </summary>
    private static string Quote(string argument)
    {
        if (argument.IndexOfAny(['"', '%']) >= 0 || argument.EndsWith('\\'))
        {
            throw new ArgumentException(
                $"LegacyCodePageConsole cannot pass this argument through cmd unchanged: {argument}", nameof(argument));
        }

        return $"\"{argument}\"";
    }
}
