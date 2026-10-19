using System.Security.Cryptography;
using System.Text;
using AgentToolkit.Core.Support;

namespace AgentToolkit.SelfTest;

/// <summary>
/// 一个路径的只读快照：文件数、总字节、摘要，外加**逐文件清单**（相对路径 → 大小 + mtime ticks）。
/// 清单用来把「变化」分类：运行中客户端自己写的活状态（会话 / 日志 / 缓存 / WAL）与可疑改动必须分开报，
/// 不能把客户端自己的写入混进「工具没碰它」的结论里。
/// </summary>
public sealed record SnapshotEntry(
    string Path,
    bool Exists,
    int Files,
    long Bytes,
    string Digest,
    string Mode,
    IReadOnlyDictionary<string, (long Size, long Ticks)> Manifest);

/// <summary>
/// 真实客户端的只读快照。
///
/// 纪律：只读、不写、不改 mtime（只做 stat / 读内容）。
/// 摘要有两种口径，都写进结果里，避免把"没扫内容"说成"内容没变"：
///   · tree：递归 stat（相对路径 + 大小 + mtime ticks）的 sha256 —— 覆盖整棵树，适合几十万文件；
///   · content：单个文件的 sha256 内容摘要（注入落点这类关键文件用它，> 64 MB 的退化为 tree）。
/// *.db-shm（SQLite wal-index 共享内存）不参与比较：任何读者（包括自检自己的只读连接）都可能创建 / 更新它。
/// </summary>
public static class RealClientSnapshot
{
    private const int MaxFilesPerRoot = 300_000;
    private const long MaxContentBytes = 64L * 1024 * 1024;

    /// <summary>运行中客户端**自己**会写的活状态：会话、日志、缓存、WAL 等。这些变化不算"工具动过客户端"。</summary>
    private static readonly string[] LiveStatePatterns =
    {
        @"\sessions\", @"\session\", @"\logs\", @"\log\", @"\cache\", @"\caches\", @"\code cache\",
        @"\gpucache\", @"\tmp\", @"\temp\", @"\history\", @"\state\", @"\workspacestorage\",
        @"\blob_storage\", @"\service worker\", @"\crashpad\", @"\dawncache\", @"\dawn_graphitecache\",
        @"\dawnwebgpucache\", @"\lockfile", @"\.log", @"\.jsonl", @"\.ndjson", @"\.db-wal", @"\.db-shm",
        @"\.breach-cursor-path.json", @"\.breach-opencode-path.json",
    };

    public static bool IsLiveState(string relativeOrFullPath)
    {
        var lower = relativeOrFullPath.ToLowerInvariant();
        return LiveStatePatterns.Any(p => lower.Contains(p, StringComparison.Ordinal));
    }

    public static IReadOnlyList<SnapshotEntry> Capture(IEnumerable<string> paths)
    {
        var list = new List<SnapshotEntry>();
        foreach (var path in paths) list.Add(CaptureOne(path));
        return list;
    }

    private static SnapshotEntry CaptureOne(string path)
    {
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            var manifest = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = (info.Length, info.LastWriteTimeUtc.Ticks),
            };
            if (info.Length <= MaxContentBytes)
                return new SnapshotEntry(path, true, 1, info.Length, SafeSha256File(path), "content", manifest);
            return new SnapshotEntry(path, true, 1, info.Length, Digest(manifest), "tree", manifest);
        }

        if (!Directory.Exists(path))
            return new SnapshotEntry(path, false, 0, 0, "", "absent",
                new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase));

        var manifestAll = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        var files = 0;
        long bytes = 0;
        var skippedShm = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                if (file.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)) { skippedShm++; continue; }
                if (files >= MaxFilesPerRoot) break;
                FileInfo info;
                try { info = new FileInfo(file); if (!info.Exists) continue; }
                catch { continue; }
                files++;
                bytes += info.Length;
                var rel = file.Length >= path.Length ? file[path.Length..] : file;
                manifestAll[rel] = (info.Length, info.LastWriteTimeUtc.Ticks);
            }
        }
        catch (Exception ex)
        {
            manifestAll["[error]"] = (0, ex.GetType().Name.GetHashCode());
        }

        var mode = skippedShm == 0 ? "tree" : $"tree(跳过 {skippedShm} 个 *.db-shm)";
        return new SnapshotEntry(path, true, files, bytes, Digest(manifestAll), mode, manifestAll);
    }

    private static string Digest(IReadOnlyDictionary<string, (long Size, long Ticks)> manifest)
    {
        var sb = new StringBuilder();
        foreach (var kv in manifest.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append('|').Append(kv.Value.Size).Append('|').Append(kv.Value.Ticks).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static string SafeSha256File(string path)
    {
        try { return FileText.Sha256File(path); }
        catch (Exception ex)
        {
            return "unreadable:" + ex.GetType().Name;
        }
    }

    /// <summary>
    /// 两次快照之间发生变化的键（全路径形式：<c>&lt;根&gt;&lt;相对路径&gt;</c>）。
    /// 覆盖：新增 / 消失 / 大小或 mtime 变化；单文件 content 口径下内容 sha256 变化也算。
    /// </summary>
    public static IReadOnlySet<string> ChangedKeys(IReadOnlyList<SnapshotEntry> before, IReadOnlyList<SnapshotEntry> after)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < before.Count && i < after.Count; i++)
        {
            var a = before[i];
            var b = after[i];
            if (a.Exists != b.Exists)
            {
                keys.Add(a.Path);
                continue;
            }
            if (a.Mode == "content" && b.Mode == "content")
            {
                if (!string.Equals(a.Digest, b.Digest, StringComparison.Ordinal)) keys.Add(a.Path);
                continue;
            }
            foreach (var kv in a.Manifest)
                if (!b.Manifest.TryGetValue(kv.Key, out var now) || now.Size != kv.Value.Size || now.Ticks != kv.Value.Ticks)
                    keys.Add(a.Path + kv.Key);
            foreach (var kv in b.Manifest)
                if (!a.Manifest.ContainsKey(kv.Key)) keys.Add(a.Path + kv.Key);
        }
        return keys;
    }

    /// <summary>变化键是不是「客户端自己在写」那一类（会话 / 日志 / 缓存 / WAL）。</summary>
    public static bool IsLiveStateKey(string key) => IsLiveState(key);
}
