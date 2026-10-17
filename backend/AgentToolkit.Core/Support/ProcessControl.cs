using System.Diagnostics;

namespace AgentToolkit.Core.Support;

/// <summary>
/// 客户端进程控制：配置只在启动时读，所以改完盘必须让客户端重启才算生效。
/// 只有显式勾选"重启客户端"时才动手，且失败要如实报告，不许静默降级。
/// </summary>
public static class ProcessControl
{
    public static IReadOnlyList<int> Pids(string processName) => PathProbe.ProcessIds(processName);

    public static bool IsRunning(string processName) => Pids(processName).Count > 0;

    public static string? ExeOf(string processName) => PathProbe.FromProcess(processName);

    public static (bool restarted, string detail) Restart(string processName, string? exeHint, int gracefulWaitMs = 6000)
    {
        var exe = ExeOf(processName) ?? exeHint;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return (false, $"找不到 {processName} 的可执行文件，无法自动重启；请手工完全退出后重新打开。");

        try
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                try { p.CloseMainWindow(); } catch { /* 无窗口时忽略 */ }
                finally { p.Dispose(); }
            }

            var waited = 0;
            while (waited < gracefulWaitMs && IsRunning(processName))
            {
                Thread.Sleep(300);
                waited += 300;
            }

            if (IsRunning(processName))
            {
                foreach (var p in Process.GetProcessesByName(processName))
                {
                    try { p.Kill(entireProcessTree: true); } catch { /* 尽力而为 */ }
                    finally { p.Dispose(); }
                }
                Thread.Sleep(800);
            }

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
            return (true, $"已重启 {processName}：{exe}");
        }
        catch (Exception ex)
        {
            return (false, $"重启 {processName} 失败：{ex.Message}；请手工完全退出后重新打开。");
        }
    }
}
