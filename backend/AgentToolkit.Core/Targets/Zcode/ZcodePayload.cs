using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Zcode;

/// <summary>载荷层（~/.zcode/AGENTS.md）一次操作的结论。</summary>
public sealed class ZcodePayloadResult
{
    public bool Wrote { get; set; }
    public bool Deleted { get; set; }
    public bool NothingToDo { get; set; }
    /// <summary>磁盘现有内容与将要写入的内容不同（= 需要写入）；dry-run 下也为 true。</summary>
    public bool Changed { get; set; }
    public string TargetPath { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public string? Action { get; set; }
    public long BeforeBytes { get; set; }
    public long AfterBytes { get; set; }
    public List<Note> Notes { get; } = new();
    public List<string> InertDirsRemoved { get; } = new();
    public bool BackedUpUserFile { get; set; }
}

/// <summary>载荷层备份的体检结论（路径约定是 &lt;AGENTS.md&gt;.ops.bak，与补丁层的 .breach.bak 不同）。</summary>
public sealed class ZcodePayloadBackup
{
    public required BackupInfo Info { get; init; }
    public IReadOnlyList<Note> Notes { get; init; } = Array.Empty<Note>();
}

/// <summary>
/// 上下文载荷层：~/.zcode/AGENTS.md（客户端加载上限 100 KB）。
/// 逐条移植 tools/install.ps1 的 Invoke-Install / Invoke-Revert：
///   - 归属判定看**载荷首行**（feature string）：首行相同的现存文件是自家旧版，不备份；
///     否则把用户原文件备份成 &lt;目标&gt;.ops.bak。
///   - 还原时：备份是自家旧载荷 → 删目标 + 删备份；备份是用户原文件 → 还原；目标是自家文件且无备份 → 删目标；
///     目标不是自家文件 → 不动并说明。
///   - install/revert 都清掉 plugin-workspace 下的惰性目录（源码实测：plugin-workspace 不是 ZCode 的加载路径）。
/// </summary>
public static class ZcodePayload
{
    public const string FileName = "AGENTS.md";
    public const long CapBytes = 102400;

    /// <summary>惰性目录：源实现确认它们不在 ZCode 的加载路径上，装上只会留垃圾。</summary>
    public static readonly string[] InertRelativeDirs =
    {
        Path.Combine("plugin-workspace", "ops-conventions"),
        Path.Combine("plugin-workspace", "legacy-ops"),
    };

    public static string TargetOf(string home) => Path.Combine(home, FileName);
    public static string BackupOf(string home) => TargetOf(home) + ".ops.bak";

    /// <summary>载荷首行（feature string）：归属判定的唯一依据。</summary>
    public static string FeatureOf(string payloadText) =>
        payloadText.Split('\n')[0].Trim().TrimEnd('\r');

    /// <summary>磁盘现有内容与将要写入的内容是否逐字符相同。</summary>
    private static bool ContentEqual(string? existing, string payloadText) =>
        existing is not null && string.Equals(existing, payloadText, StringComparison.Ordinal);

    /// <summary>
    /// 载荷层是否需要写入：<c>磁盘现有内容 != 将要写入的内容</c>。
    /// 预览（WillWrite）、注入（Applied）与总体积都用这一个判据，避免"内容一样却报要写"。
    /// </summary>
    public static bool NeedsWrite(string home, string payloadText) =>
        !ContentEqual(FileText.ReadOrNull(TargetOf(home)), payloadText);

    public static ZcodePayloadResult Install(string home, string payloadText, bool dryRun)
    {
        var res = new ZcodePayloadResult
        {
            TargetPath = TargetOf(home),
            BackupPath = BackupOf(home),
        };

        if (string.IsNullOrEmpty(payloadText))
            throw new AgentEngineException(EngineErrorCode.TargetFileMissing,
                "载荷文本为空：先在「注入文本」页确认 zcode-agents.md 的内容。");

        var bytes = FileText.Bytes(payloadText);
        if (bytes >= CapBytes)
            throw new AgentEngineException(EngineErrorCode.VerifyProblems,
                $"载荷超出客户端上限：{FileText.Human(bytes)} / {FileText.Human(CapBytes)}，拒绝部署（客户端会截断或拒绝加载）。");

        var feature = FeatureOf(payloadText);
        var existing = FileText.ReadOrNull(res.TargetPath);
        res.BeforeBytes = existing is null ? 0 : FileText.Bytes(existing);
        res.AfterBytes = bytes;
        res.Changed = !ContentEqual(existing, payloadText);

        if (!res.Changed)
        {
            // 内容逐字符相同：跳过写入（幂等），也不动目标的修改时间。
            res.NothingToDo = true;
            res.Action = "install(already-current)";
            res.Notes.Add(Notes.Info("载荷层已是当前文本，无需写入（幂等）",
                $"{res.TargetPath} 与将要写入的内容逐字符相同，跳过写入；备份与归属判定不受影响。" +
                $"（首行归属判定仍以「{feature}」为依据。）", "zcode/payload"));
        }
        else
        {
            if (existing is not null && existing.Trim() != payloadText.Trim())
            {
                if (feature.Length > 0 && existing.Contains(feature, StringComparison.Ordinal))
                {
                    res.Notes.Add(Notes.Info("已存在的 AGENTS.md 是自家旧版载荷，不为它建备份",
                        "按载荷首行判定归属：首行相同即视为本工具此前部署的版本，不覆盖备份。", "zcode/payload"));
                }
                else
                {
                    if (!dryRun) FileText.Write(res.BackupPath, existing);
                    res.BackedUpUserFile = true;
                    res.Notes.Add(Notes.Warn("已备份用户自己的 AGENTS.md",
                        (dryRun ? "将要备份 → " : "备份 → ") + res.BackupPath, "zcode/payload"));
                }
            }

            if (!dryRun) FileText.Write(res.TargetPath, payloadText);
            res.Wrote = !dryRun;
            res.Action = dryRun ? "install(dry-run)" : "install";
            res.Notes.Add(Notes.Info(dryRun ? "[dry-run] 将写入载荷" : "已写入载荷",
                $"{res.TargetPath}（{FileText.Human(bytes)}；上限 {FileText.Human(CapBytes)}）", "zcode/payload"));
        }

        RemoveInertDirs(home, dryRun, res);
        return res;
    }

    public static ZcodePayloadResult Revert(string home, string? payloadText, bool dryRun)
    {
        var res = new ZcodePayloadResult
        {
            TargetPath = TargetOf(home),
            BackupPath = BackupOf(home),
        };

        var body = payloadText ?? "";
        var feature = body.Length > 0 ? FeatureOf(body) : "";
        var existing = FileText.ReadOrNull(res.TargetPath);
        var isOurs = existing is not null && feature.Length > 0 && existing.Contains(feature, StringComparison.Ordinal);
        res.BeforeBytes = existing is null ? 0 : FileText.Bytes(existing);

        if (File.Exists(res.BackupPath))
        {
            var saved = FileText.ReadOrNull(res.BackupPath) ?? "";
            var savedIsOurs = feature.Length > 0 && saved.Length > 0 && saved.Contains(feature, StringComparison.Ordinal);
            if (savedIsOurs)
            {
                // 备份是自家旧载荷，不是用户文件。
                res.Changed = true;
                if (isOurs)
                {
                    if (!dryRun) File.Delete(res.TargetPath);
                    res.Deleted = !dryRun;
                    res.Notes.Add(Notes.Info(dryRun ? "[dry-run] 将删除已部署的 AGENTS.md" : "已删除部署的 AGENTS.md",
                        "备份是自家旧版载荷，所以目标是删掉而不是还原。", "zcode/payload"));
                }
                if (!dryRun) File.Delete(res.BackupPath);
                res.Action = "revert(discard-stale-payload-backup)";
                res.Notes.Add(Notes.Info(dryRun ? "[dry-run] 将丢弃自家旧载荷备份" : "已丢弃自家旧载荷备份",
                    res.BackupPath, "zcode/payload"));
            }
            else
            {
                if (!dryRun)
                {
                    FileText.Write(res.TargetPath, saved);
                    File.Delete(res.BackupPath);
                }
                res.Changed = true;
                res.Wrote = !dryRun;
                res.AfterBytes = FileText.Bytes(saved);
                res.Action = "revert(restore-user-file)";
                res.Notes.Add(Notes.Info(dryRun ? "[dry-run] 将还原用户原始 AGENTS.md" : "已还原用户原始 AGENTS.md",
                    $"{res.BackupPath} → {res.TargetPath}", "zcode/payload"));
            }
        }
        else if (isOurs)
        {
            res.Changed = true;
            if (!dryRun) File.Delete(res.TargetPath);
            res.Deleted = !dryRun;
            res.Action = "revert(delete-no-backup)";
            res.Notes.Add(Notes.Info(dryRun ? "[dry-run] 将删除部署的 AGENTS.md（无备份）" : "已删除部署的 AGENTS.md（无备份）",
                res.TargetPath, "zcode/payload"));
        }
        else if (existing is not null)
        {
            res.NothingToDo = true;
            res.Action = "revert(leave-foreign-file)";
            res.Notes.Add(Notes.Warn("AGENTS.md 不是本工具的文件，未改动",
                $"首行与载荷首行不一致，判定为用户自己的文件：{res.TargetPath}", "zcode/payload"));
        }
        else
        {
            res.NothingToDo = true;
            res.Action = "revert(nothing)";
            res.Notes.Add(Notes.Info("没有可还原的载荷", $"目标不存在：{res.TargetPath}", "zcode/payload"));
        }

        RemoveInertDirs(home, dryRun, res);
        return res;
    }

    /// <summary>
    /// 载荷层备份体检。注意路径约定：install.ps1 用的是 <c>&lt;目标&gt;.ops.bak</c>，
    /// 不是补丁层的 <c>&lt;目标&gt;.breach.bak</c>——状态页必须按这条约定报告，否则会误报"没有备份"。
    /// 备份里含载荷首行 = 它是自家旧载荷（不是在用户原文件基础上拍的）→ 按本工具的词汇记为污染，
    /// 因为拿它"还原"不会得到用户文件（revert 走的是删目标 + 丢弃备份的分支）。
    /// </summary>
    public static ZcodePayloadBackup AssessBackup(string home, string? payloadText)
    {
        var path = BackupOf(home);
        if (!File.Exists(path))
        {
            return new ZcodePayloadBackup
            {
                Info = new BackupInfo { Health = BackupHealth.Absent, Path = path },
                Notes = new[]
                {
                    Notes.Warn("没有载荷备份",
                        $"没有 {path}（载荷层的备份约定是 <目标>.ops.bak，不是补丁层的 .breach.bak）。" +
                        "还原时目标会被直接删除，不会还原出用户文件——这正是 install.ps1 的语义。", "zcode/payload"),
                },
            };
        }

        string text;
        FileInfo fi;
        try
        {
            text = FileText.Read(path);
            fi = new FileInfo(path);
        }
        catch (Exception ex)
        {
            return new ZcodePayloadBackup
            {
                Info = new BackupInfo { Health = BackupHealth.Unreadable, Path = path },
                Notes = new[] { Notes.Warn("载荷备份不可读", $"{path}：{ex.Message}", "zcode/payload") },
            };
        }

        var feature = string.IsNullOrEmpty(payloadText) ? "" : FeatureOf(payloadText);
        var savedIsOurs = feature.Length > 0 && text.Contains(feature, StringComparison.Ordinal);
        var info = new BackupInfo
        {
            Health = savedIsOurs ? BackupHealth.Contaminated : BackupHealth.Clean,
            Path = path,
            SizeBytes = fi.Length,
            Sha256 = FileText.Sha256File(path),
            SavedAt = fi.LastWriteTime,
        };
        var notes = new List<Note>();
        if (savedIsOurs)
        {
            notes.Add(Notes.Warn("载荷备份是自家旧载荷",
                "这份备份不是在用户原文件基础上拍的，拿它「还原」得不到用户文件；revert 会删除目标并丢弃它。" +
                "若目标是本工具部署的载荷，这是期望行为；若目标其实是用户自己的文件，请人工核对后再动手。", "zcode/payload"));
        }
        else
        {
            notes.Add(Notes.Info("载荷备份是用户原文件",
                $"revert 会把它逐字节还原回 {TargetOf(home)}，并删除这份备份。", "zcode/payload"));
        }

        return new ZcodePayloadBackup { Info = info, Notes = notes };
    }

    private static void RemoveInertDirs(string home, bool dryRun, ZcodePayloadResult res)
    {
        foreach (var rel in InertRelativeDirs)
        {
            var dir = Path.Combine(home, rel);
            if (!Directory.Exists(dir)) continue;
            if (!dryRun)
            {
                try { Directory.Delete(dir, true); }
                catch (Exception ex)
                {
                    res.Notes.Add(Notes.Warn("惰性目录删除失败", $"{dir}：{ex.Message}", "zcode/payload"));
                    continue;
                }
            }
            res.InertDirsRemoved.Add(dir);
            res.Notes.Add(Notes.Warn(dryRun ? "[dry-run] 将清除惰性目录" : "已清除惰性目录",
                $"{dir}（plugin-workspace 不是 ZCode 的加载路径，源码已实测：放那里的内容不会进模型上下文）", "zcode/payload"));
        }
    }
}
