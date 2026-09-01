using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace QwenPlayground.Core.Crash;

/// <summary>
/// Exit code завершённого процесса прямым OpenProcess/GetExitCodeProcess.
/// Почему не Process.ExitCode: в .NET 10 он бросает InvalidOperationException
/// для процессов, не запущенных этим объектом («Process was not started by this
/// object»), а GetProcessById мёртвый процесс уже не возвращает. Для watchdog'а
/// код выхода — ключевая диагностика: нативный краш оставляет в нём исключение
/// (0xC0000005 access violation и т.п.), kill — свой код.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NativeExitCode
{
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>Код выхода завершённого процесса; null — неизвестен (процесс не найден, жив, нет прав).</summary>
    public static int? TryGet(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        var handle = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            if (GetExitCodeProcess(handle, out var code))
            {
                return (int)code;
            }
        }
        finally
        {
            CloseHandle(handle);
        }
        return null;
    }
}
