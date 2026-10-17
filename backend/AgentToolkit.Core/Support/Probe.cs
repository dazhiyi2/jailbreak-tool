using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace AgentToolkit.Core.Support;

/// <summary>
/// 多级路径探测的公共件。纪律：CLI 显式指定 → 环境变量 → 记忆文件 → 运行中进程 →
/// 注册表 → 快捷方式 → 全盘嗅探；显式指定失效时 fail-closed，绝不静默回退到别的安装。
/// </summary>
public static class PathProbe
{
    public static IReadOnlyList<string> Drives()
    {
        var list = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.IsReady) list.Add(d.RootDirectory.FullName); } catch { /* 跳过 */ }
        }
        return list;
    }

    public static string? FromProcess(string processName)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
                catch { /* 权限或 32/64 位差异 */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* 忽略 */ }
        return null;
    }

    public static IReadOnlyList<int> ProcessIds(string processName)
    {
        try { return Process.GetProcessesByName(processName).Select(p => { var id = p.Id; p.Dispose(); return id; }).ToArray(); }
        catch { return Array.Empty<int>(); }
    }

    /// <summary>注册表卸载项：按 DisplayName 匹配，取 InstallLocation / DisplayIcon 目录。</summary>
    public static IReadOnlyList<string> FromRegistry(string displayNamePattern)
    {
        var found = new List<string>();
        var subKeys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var sub in subKeys)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                    using var key = baseKey.OpenSubKey(sub);
                    if (key is null) continue;
                    foreach (var name in key.GetSubKeyNames())
                    {
                        using var app = key.OpenSubKey(name);
                        var display = app?.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(display) ||
                            display.IndexOf(displayNamePattern, StringComparison.OrdinalIgnoreCase) < 0) continue;

                        var loc = app?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(loc)) found.Add(loc.Trim('"'));
                        var icon = app?.GetValue("DisplayIcon") as string;
                        if (!string.IsNullOrWhiteSpace(icon))
                        {
                            var p = icon.Trim('"').Split(',')[0];
                            var dir = Path.GetDirectoryName(p);
                            if (!string.IsNullOrEmpty(dir)) found.Add(dir);
                        }
                        var uninstall = app?.GetValue("UninstallString") as string;
                        if (!string.IsNullOrWhiteSpace(uninstall))
                        {
                            var p = uninstall.Trim('"').Split('"')[0].Trim();
                            var dir = Path.GetDirectoryName(p);
                            if (!string.IsNullOrEmpty(dir)) found.Add(dir);
                        }
                    }
                }
                catch { /* 权限或键不存在 */ }
            }
        }
        return found.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>快捷方式解析：优先用 WScript.Shell，失败则退回对 .lnk 做字符串尾部提取。</summary>
    public static IReadOnlyList<string> FromShortcuts(IEnumerable<string> lnkPaths)
    {
        var targets = new List<string>();
        object? shell = null;
        try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); } catch { shell = null; }

        foreach (var lnk in lnkPaths)
        {
            if (!File.Exists(lnk)) continue;
            if (shell is not null)
            {
                try
                {
                    dynamic sh = shell;
                    dynamic sc = sh.CreateShortcut(lnk);
                    string t = sc.TargetPath;
                    if (!string.IsNullOrWhiteSpace(t)) { targets.Add(t); continue; }
                }
                catch { /* 落到字符串提取 */ }
            }
            var guess = LnkStringGuess(lnk);
            if (guess is not null) targets.Add(guess);
        }
        return targets;
    }

    private static string? LnkStringGuess(string lnk)
    {
        try
        {
            var bytes = File.ReadAllBytes(lnk);
            var text = System.Text.Encoding.ASCII.GetString(bytes);
            var i = text.LastIndexOf(":\\", StringComparison.Ordinal);
            if (i < 1) return null;
            var start = i - 1;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is ':' or '\\' or ' ' or '_' or '-' or '.')) start--;
            var end = i + 2;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '\\' or ' ' or '_' or '-' or '.')) end++;
            var candidate = text[start..end].Trim();
            return candidate.Contains(".exe", StringComparison.OrdinalIgnoreCase) ? candidate : null;
        }
        catch { return null; }
    }

    /// <summary>把用户给的 --root 归一化成安装根：允许传 exe、resources 目录或根目录。</summary>
    public static string NormalizeRoot(string input, string exeName)
    {
        var p = input.Trim().Trim('"');
        if (File.Exists(p))
        {
            var name = Path.GetFileName(p);
            if (name.Equals(exeName, StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(p)!;
        }
        if (Directory.Exists(p) && Path.GetFileName(p).Equals("resources", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(p)!;
        return p;
    }

    public static string? WalkUpFor(string start, string relative, int maxDepth = 4)
    {
        var dir = new DirectoryInfo(start);
        for (var i = 0; i <= maxDepth && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public static IReadOnlyList<string> FilesUnder(string dir, string pattern, int maxDepth, int budget)
    {
        var results = new List<string>();
        var queue = new Queue<(string path, int depth)>();
        queue.Enqueue((dir, 0));
        while (queue.Count > 0 && budget-- > 0)
        {
            var (path, depth) = queue.Dequeue();
            try
            {
                foreach (var f in Directory.EnumerateFiles(path, pattern))
                {
                    results.Add(f);
                    if (results.Count >= 20) return results;
                }
                if (depth >= maxDepth) continue;
                foreach (var d in Directory.EnumerateDirectories(path))
                {
                    var name = Path.GetFileName(d);
                    if (name.StartsWith('.') || name is "node_modules" or "Cache" or "cache") continue;
                    queue.Enqueue((d, depth + 1));
                }
            }
            catch { /* 权限 */ }
        }
        return results;
    }
}

/// <summary>定位记忆文件（如 ~/.breach-dsh-path.json），首轮成功后直接命中。</summary>
public static class PathMemory
{
    public static string? Load(string file, string property = "root")
    {
        try
        {
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(FileText.Read(file));
            return doc.RootElement.TryGetProperty(property, out var e) ? e.GetString() : null;
        }
        catch { return null; }
    }

    public static void Save(string file, string value, string property = "root")
    {
        try
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [property] = value,
                ["savedAt"] = DateTimeOffset.Now.ToString("o"),
            }, new JsonSerializerOptions { WriteIndented = true });
            FileText.Write(file, json);
        }
        catch { /* 记忆失败不致命 */ }
    }
}
