using System.Diagnostics;

namespace AgentToolkit.Ui.Services;

// 所有外部程序打开动作集中于此；不承担文件读取、写入或存在性检查。
public static class ShellOpen
{
    public static bool IsLocalPath(string? path) => path is { Length: >= 3 } && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
    public static void OpenFolder(string path) => Open("explorer.exe", path);
    public static void OpenText(string path) => Open("notepad.exe", path);

    private static void Open(string executable, string path)
    {
        if (!IsLocalPath(path))
            throw new ArgumentException("只允许打开引擎提供的本机绝对文件路径。", nameof(path));
        var start = new ProcessStartInfo(executable) { UseShellExecute = true };
        start.ArgumentList.Add(path);
        Process.Start(start);
    }
}
