using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BattleScribeSpec.Tests.Profiles;

/// <summary>
/// The process that started the test app — <c>dotnet test</c>'s or <c>dotnet run</c>'s <c>dotnet</c>, or the
/// shell that started the executable — whose id <see cref="TestHost"/> hands the platform's
/// <c>--exit-on-process-exit</c>. .NET has no API for a process's parent, so this asks the OS as
/// <c>System.Diagnostics.Process</c> does internally: <c>NtQueryInformationProcess</c> on Windows,
/// <c>getppid</c> elsewhere.
/// </summary>
internal static class Launcher
{
    /// <summary>
    /// The launcher's id, or null when it cannot be read or is no longer running: the platform refuses an id
    /// that names no process, and a launcher that has exited leaves its id to a later, unrelated process.
    /// </summary>
    public static int? Id()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var id = OperatingSystem.IsWindows() ? WindowsParentId(self) : GetParentProcessId();
            using var parent = Process.GetProcessById(id);
            return parent.StartTime <= self.StartTime ? id : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException or TypeLoadException)
        {
            return null;
        }
    }

    private static int WindowsParentId(Process self)
    {
        // PROCESS_BASIC_INFORMATION: six pointer-sized fields, as the native layout pads them; the parent's id is the last.
        var info = new nint[6];
        var status = NtQueryInformationProcess(self.Handle, 0, info, info.Length * nint.Size, out _);
        return status == 0 ? (int)info[5] : throw new InvalidOperationException($"NtQueryInformationProcess returned 0x{status:X8}");
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int informationClass, [Out] nint[] information, int length, out int returnLength);

    [DllImport("libc", EntryPoint = "getppid")]
    private static extern int GetParentProcessId();
}
