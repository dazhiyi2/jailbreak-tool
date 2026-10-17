using System.Security.Cryptography;
using System.Text;
using AgentToolkit.Contracts;

namespace AgentToolkit.Core.Support;

/// <summary>纯文本读写：一律 UTF-8 无 BOM；写入走临时文件 + 替换，避免半截文件。</summary>
public static class FileText
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static bool Exists(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    public static string? ReadOrNull(string? path)
    {
        if (!Exists(path)) return null;
        return File.ReadAllText(path!, Utf8NoBom);
    }

    public static string Read(string path) => File.ReadAllText(path, Utf8NoBom);

    /// <summary>原子写：同目录临时文件 + File.Replace/Move，失败不留半截目标文件。</summary>
    public static void WriteAtomic(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tmp, text, Utf8NoBom);
        if (File.Exists(path)) File.Replace(tmp, path, null, true);
        else File.Move(tmp, path);
    }

    public static void Write(string path, string text) => WriteAtomic(path, text);

    public static long Bytes(string text) => Utf8NoBom.GetByteCount(text);

    public static string Sha256Text(string text) =>
        Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(text))).ToLowerInvariant();

    public static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public static int Count(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle) || haystack.Length == 0) return 0;
        var n = 0;
        var i = -1;
        while ((i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal)) >= 0) n++;
        return n;
    }

    public static string DetectEol(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    public static string FirstNonEmptyLine(string text) =>
        text.Split('\n').Select(l => l.Trim().TrimEnd('\r')).FirstOrDefault(l => l.Length > 0) ?? "";

    public static string Human(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.0") + " KB",
        _ => (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB",
    };

    public static string TrimTrailingNewlines(string text) => text.TrimEnd('\r', '\n');
}

public enum BackupStatus { Absent, Clean, Contaminated, Unreadable }

public sealed class BackupAssessment
{
    public BackupStatus Status { get; init; } = BackupStatus.Absent;
    public string? Path { get; init; }
    public long Bytes { get; init; }
    public string? Sha256 { get; init; }
    public DateTimeOffset? SavedAt { get; init; }
    public string? SupersededPath { get; init; }
    public string? ContaminatedPath { get; init; }
    public string? OriginalSha256FromMeta { get; init; }
    /// <summary>真：备份是参考实现留下的旧命名（.ops.bak），不是本工具的口径。</summary>
    public bool UsesLegacyName { get; init; }
}

/// <summary>
/// 备份纪律（与参考实现一致）：先备份、无备份不写、备份污染另存 .contaminated、
/// 升级覆盖时旧备份另存 .superseded 后重建。污染备份是"补丁删不掉"的真正成因。
/// 另外认参考实现的旧命名 <c>&lt;目标&gt;.ops.bak</c>：载荷层的备份当初用的是这个名字，
/// 不认它就会把"有备份"报成"备份缺失"，进而让还原路径误导用户。
/// </summary>
public sealed class BackupManager
{
    public string TargetPath { get; }
    public string BackupPath => TargetPath + ".breach.bak";
    public string LegacyBackupPath => TargetPath + ".ops.bak";
    public string MetaPath => BackupPath + ".meta.json";

    public BackupManager(string targetPath) => TargetPath = targetPath;

    /// <summary>markers 为该目标全部补丁标记；备份里出现任一即判为污染（它不是原始文件）。</summary>
    public BackupAssessment Assess(IReadOnlyList<string> markers)
    {
        var legacy = false;
        var path = BackupPath;
        if (!File.Exists(path) && File.Exists(LegacyBackupPath)) { path = LegacyBackupPath; legacy = true; }
        if (!File.Exists(path))
            return new BackupAssessment { Status = BackupStatus.Absent, Path = BackupPath };

        string text;
        try { text = FileText.Read(path); }
        catch { return new BackupAssessment { Status = BackupStatus.Unreadable, Path = path, UsesLegacyName = legacy }; }

        var info = new FileInfo(path);
        string? metaSha = null;
        try
        {
            if (File.Exists(MetaPath))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(FileText.Read(MetaPath));
                if (doc.RootElement.TryGetProperty("originalSha256", out var e)) metaSha = e.GetString();
            }
        }
        catch { /* meta 坏了不影响主流程 */ }

        var contaminated = markers.Any(m => !string.IsNullOrEmpty(m) && FileText.Count(text, m) > 0);
        return new BackupAssessment
        {
            Status = contaminated ? BackupStatus.Contaminated : BackupStatus.Clean,
            Path = path,
            Bytes = info.Length,
            Sha256 = FileText.Sha256File(path),
            SavedAt = info.LastWriteTime,
            SupersededPath = File.Exists(BackupPath + ".superseded") ? BackupPath + ".superseded" : null,
            ContaminatedPath = File.Exists(BackupPath + ".contaminated") ? BackupPath + ".contaminated" : null,
            OriginalSha256FromMeta = metaSha,
            UsesLegacyName = legacy,
        };
    }

    public void WriteMeta(string originalText)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            target = TargetPath,
            backup = BackupPath,
            savedAt = DateTimeOffset.Now.ToString("o"),
            originalSha256 = FileText.Sha256Text(originalText),
            originalBytes = FileText.Bytes(originalText),
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        try { FileText.Write(MetaPath, json); } catch { /* 不影响主流程 */ }
    }

    /// <summary>首次备份；已存在则不覆盖。</summary>
    public bool EnsureBackup(string content, out string? note)
    {
        if (File.Exists(BackupPath)) { note = null; return false; }
        FileText.Write(BackupPath, content);
        WriteMeta(content);
        note = null;
        return true;
    }

    public void AgeAsContaminated() => File.Copy(BackupPath, BackupPath + ".contaminated", true);

    public void AgeAsSuperseded() => File.Copy(BackupPath, BackupPath + ".superseded", true);

    public void RemoveAll()
    {
        foreach (var p in new[] { BackupPath, MetaPath, BackupPath + ".superseded", BackupPath + ".contaminated", LegacyBackupPath })
            try { if (File.Exists(p)) File.Delete(p); } catch { /* 尽力而为 */ }
    }

    public BackupInfo ToContract(BackupAssessment a) => new()
    {
        Health = a.Status switch
        {
            BackupStatus.Clean => BackupHealth.Clean,
            BackupStatus.Contaminated => BackupHealth.Contaminated,
            BackupStatus.Unreadable => BackupHealth.Unreadable,
            _ => BackupHealth.Absent,
        },
        Path = a.Path,
        SizeBytes = a.Bytes,
        Sha256 = a.Sha256,
        SavedAt = a.SavedAt,
        SupersededPath = a.SupersededPath,
        ContaminatedCopyPath = a.ContaminatedPath,
    };
}

/// <summary>逐行差异（LCS）。输出契约的 DiffLine 序列，供差异页直接渲染。</summary>
public static class DiffBuilder
{
    public static (List<DiffLine> lines, int added, int removed) Build(string before, string after, int maxLines = 4000)
    {
        var a = before.Replace("\r\n", "\n").Split('\n');
        var b = after.Replace("\r\n", "\n").Split('\n');
        var n = a.Length;
        var m = b.Length;

        // 行数过大时退化为"整段替换"的两块，避免 O(n*m) 爆内存
        if ((long)n * m > 40_000_000L)
        {
            var big = new List<DiffLine>();
            for (var i = 0; i < Math.Min(n, maxLines); i++) big.Add(new DiffLine { Kind = DiffKind.Removed, LeftLineNo = (i + 1).ToString(), Text = a[i] });
            for (var j = 0; j < Math.Min(m, maxLines); j++) big.Add(new DiffLine { Kind = DiffKind.Added, RightLineNo = (j + 1).ToString(), Text = b[j] });
            return (big, m, n);
        }

        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var lines = new List<DiffLine>();
        int x = 0, y = 0, added = 0, removed = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                lines.Add(new DiffLine { Kind = DiffKind.Context, LeftLineNo = (x + 1).ToString(), RightLineNo = (y + 1).ToString(), Text = a[x] });
                x++; y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                lines.Add(new DiffLine { Kind = DiffKind.Removed, LeftLineNo = (x + 1).ToString(), Text = a[x] });
                x++; removed++;
            }
            else
            {
                lines.Add(new DiffLine { Kind = DiffKind.Added, RightLineNo = (y + 1).ToString(), Text = b[y] });
                y++; added++;
            }
            if (lines.Count >= maxLines) break;
        }
        while (x < n && lines.Count < maxLines) { lines.Add(new DiffLine { Kind = DiffKind.Removed, LeftLineNo = (x + 1).ToString(), Text = a[x] }); x++; removed++; }
        while (y < m && lines.Count < maxLines) { lines.Add(new DiffLine { Kind = DiffKind.Added, RightLineNo = (y + 1).ToString(), Text = b[y] }); y++; added++; }
        return (lines, added, removed);
    }

    public static string ToUnifiedText(IEnumerable<DiffLine> lines) =>
        string.Join("\n", lines.Select(l => (l.Kind switch
        {
            DiffKind.Added => "+ ",
            DiffKind.Removed => "- ",
            _ => "  ",
        }) + l.Text));
}

public static class Notes
{
    public static Note Info(string title, string detail, string? source = null) => new() { Severity = Severity.Info, Title = title, Detail = detail, Source = source };
    public static Note Warn(string title, string detail, string? source = null) => new() { Severity = Severity.Warning, Title = title, Detail = detail, Source = source };
    public static Note Error(string title, string detail, string? source = null) => new() { Severity = Severity.Error, Title = title, Detail = detail, Source = source };
}
