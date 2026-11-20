using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace WindowsAppUpdates;

internal static class AppWindowCleanup
{
    public static HashSet<int> CaptureProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
                ids.Add(process.Id);
        }
        return ids;
    }

    public static async Task<(string Message, bool NeedsReview)> CloseNewWindowsAsync(
        string? executable, HashSet<int> existing, DateTime started, InstallerProcessGroup installer)
    {
        if (executable is null)
            return ("无法确认应用的可执行文件。如安装器自动打开应用，请手动关闭。", true);

        using var current = Process.GetCurrentProcess();
        int session = current.SessionId;
        string processName = Path.GetFileNameWithoutExtension(executable);
        var requested = new HashSet<int>();
        int openWindows = 0;
        var observation = Stopwatch.StartNew();
        do
        {
            openWindows = 0;
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        if (existing.Contains(process.Id) || process.SessionId != session ||
                            process.StartTime.ToUniversalTime() < started || process.MainWindowHandle == IntPtr.Zero ||
                            !string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                            continue;

                        openWindows++;
                        if (!requested.Contains(process.Id) && installer.Contains(process) && process.CloseMainWindow())
                            requested.Add(process.Id);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                    {
                        // The process exited or is inaccessible from this session.
                    }
                }
            }
            if (observation.Elapsed >= TimeSpan.FromSeconds(3))
                break;
            await Task.Delay(300).ConfigureAwait(false);
        } while (true);

        if (openWindows > 0)
            return ($"还有 {openWindows} 个新打开的应用窗口。无法确认由安装器启动或关闭请求未完成，请手动检查。", true);
        return (requested.Count == 0 ? "未检测到新打开的应用窗口。" : "已关闭安装期间新打开的应用窗口。", false);
    }
}
