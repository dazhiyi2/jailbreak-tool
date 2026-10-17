using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Zcode;

/// <summary>补丁表里一条补丁的单次判定结果（apply / revert / restore 共用）。</summary>
public sealed class ZcodePatchStep
{
    public required string Id { get; init; }
    public required string Layer { get; init; }
    public required string Desc { get; init; }
    public required string Action { get; set; }
    public ZcodePatchState State { get; set; }
    public int AnchorHits { get; set; }
    public int MarkerHits { get; set; }
    public string? PatchHits { get; set; }
}

/// <summary>一次 bundle 层操作的完整结论。ErrorCode 非空表示调用方应抛 AgentEngineException。</summary>
public sealed class ZcodeBundleResult
{
    public bool Wrote { get; set; }
    public bool NothingToDo { get; set; }
    public EngineErrorCode? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public List<Note> Notes { get; } = new();
    public List<ZcodePatchStep> Plan { get; } = new();
    public long BeforeBytes { get; set; }
    public long AfterBytes { get; set; }
    public string BackupPath { get; set; } = "";
    public BackupStatus BackupStatus { get; set; } = BackupStatus.Absent;
    public string? SupersededPath { get; set; }
    public string? ContaminatedPath { get; set; }
    public bool ReadBackClean { get; set; }
    public bool? MatchesOriginal { get; set; }
    public string? OriginalSha256 { get; set; }
    public string? CurrentSha256 { get; set; }
    public string? SelfCheckDetail { get; set; }
    public bool UsedRestoreFallback { get; set; }
    public string? SafetyBackupPath { get; set; }
    public int WritesPlanned { get; set; }

    public string PlanText() => string.Join("\n", Plan.Select(p => $"  {p.Id}: {p.Action}"));
}

/// <summary>
/// bundle 层的读写操作。逐条移植 tools/zcode-patch.mjs 的 cmdApply / cmdRevert / cmdRestore。
///
/// 纪律（源实现踩过的坑都在这里）：
///   - anchor 命中 &gt;1 → 整次 apply 拒绝写入（AnchonConflict），绝不猜位置。
///   - marker 命中 → 幂等跳过。
///   - 无备份不写；无备份但文件已含补丁 → 逆序还原出原始内容建**干净**备份（逆序还残留 marker 就拒绝建备份）。
///   - 备份含 marker = 污染 → 另存 .contaminated，绝不覆盖备份。
///   - 备份干净而当前文件一个 marker 都没有 = 升级覆盖 → 旧备份另存 .superseded 后重建。
///   - DryRun 绝不写盘（连备份也不写）。
///   - 写盘前先自检（词法配平 + 体积区间 + node --check），失败拒绝写入。
/// </summary>
public static class ZcodePatchOps
{
    public static string BackupPathOf(string bundlePath) => bundlePath + ".breach.bak";

    /// <summary>照 cmdApply：在**累积文本**上逐条判定（P1 打完 P4 的锚点才出现）。</summary>
    public static ZcodeBundleResult Apply(string bundlePath, bool dryRun, CancellationToken ct)
    {
        var res = new ZcodeBundleResult { BackupPath = BackupPathOf(bundlePath) };
        if (!File.Exists(bundlePath))
            throw new AgentEngineException(EngineErrorCode.TargetFileMissing, $"bundle 不存在：{bundlePath}");

        var text = FileText.Read(bundlePath);
        res.BeforeBytes = FileText.Bytes(text);
        var next = text;
        string? ambiguous = null;

        foreach (var p in ZcodePatchTable.Patches)
        {
            ct.ThrowIfCancellationRequested();
            var (state, hits) = ZcodePatchTable.StateOf(next, p);
            var step = new ZcodePatchStep
            {
                Id = p.Id, Layer = p.Layer, Desc = p.Desc, State = state,
                AnchorHits = hits, MarkerHits = FileText.Count(next, p.Marker), Action = "",
            };
            switch (state)
            {
                case ZcodePatchState.Applied:
                    step.Action = "skip (already applied)";
                    break;
                case ZcodePatchState.Ready:
                    // 非正则替换：避免 $& / $1 被当捕获引用（源码用 () => p.patch 的函数形式）
                    next = ZcodePatchTable.ReplaceOnce(next, p.Anchor, p.Patch);
                    step.Action = "apply";
                    break;
                case ZcodePatchState.Missing:
                    step.Action = "SKIP (missing)";
                    break;
                default:
                    step.Action = $"SKIP (ambiguous x{hits})";
                    ambiguous ??= $"{p.Id}（锚点命中 {hits} 次）";
                    break;
            }
            res.Plan.Add(step);
        }

        res.AfterBytes = FileText.Bytes(next);
        res.WritesPlanned = res.Plan.Count(x => x.Action == "apply");

        if (ambiguous is not null)
        {
            res.ErrorCode = EngineErrorCode.AnchorConflict;
            res.ErrorMessage = $"锚点不唯一，整次 apply 拒绝写入：{ambiguous}。先手工缩小锚点范围再打。";
            res.Notes.Add(Notes.Error("锚点不唯一", res.ErrorMessage, "zcode/apply"));
            return res;
        }

        if (res.WritesPlanned == 0)
        {
            res.NothingToDo = true;
            res.Notes.Add(Notes.Info("没有可打的补丁", res.PlanText(), "zcode/apply"));

            // 即使无需写入，也要体检备份：否则一个污染备份会一直潜伏，
            // 等到用户日后跑 revert 时才引爆（那时工具会报告"还原成功"，实则毫无变化）。
            var bm0 = new BackupManager(bundlePath);
            if (File.Exists(bm0.BackupPath))
            {
                var a0 = bm0.Assess(ZcodePatchTable.Markers);
                res.BackupStatus = a0.Status;
                res.SupersededPath = a0.SupersededPath;
                res.ContaminatedPath = a0.ContaminatedPath;
                res.OriginalSha256 = a0.OriginalSha256FromMeta;
                if (a0.Status == BackupStatus.Contaminated)
                {
                    bm0.AgeAsContaminated();
                    res.ContaminatedPath = bm0.BackupPath + ".contaminated";
                    res.ErrorCode = EngineErrorCode.AnchorConflict;   // 对应源实现 exit 6
                    res.ErrorMessage =
                        "备份已污染：备份文件本身含有补丁标记，它不是原始文件。用这种备份还原会把补丁「还原」回来——" +
                        "这正是「删不掉」的成因。证据已另存 .contaminated，请先走逆向还原（restore），别用这份备份。";
                    res.Notes.Add(Notes.Error("备份污染", res.ErrorMessage, "zcode/apply"));
                    return res;
                }
                res.Notes.Add(Notes.Info("备份体检", $"备份存在且状态为 {a0.Status}：{bm0.BackupPath}", "zcode/apply"));
            }
            return res;
        }

        if (dryRun)
        {
            res.Notes.Add(Notes.Info("[dry-run] 未写入任何文件（含备份）",
                $"将要执行：\n{res.PlanText()}\n预计体积：{FileText.Human(res.BeforeBytes)} → {FileText.Human(res.AfterBytes)}",
                "zcode/apply"));
            return res;
        }

        // ---------------------------------------------------------------- 备份三层
        var bm = new BackupManager(bundlePath);
        if (!File.Exists(bm.BackupPath))
        {
            var patchedAlready = ZcodePatchTable.AnyMarker(text);
            if (patchedAlready)
            {
                // 当前文件已经带着补丁，却没有任何备份。直接复制当前文件当备份，就是把"半打补丁的版本"
                // 存成"原始文件"——日后 revert 会把补丁装回来，而备份已被删除，从此再也还原不了。
                // 这是本工具踩过的死锁：先逆向撤销得到原始内容，用它建干净备份，再继续往上打。
                var orig = ZcodePatchTable.UndoAllPatches(text);
                if (ZcodePatchTable.AnyMarker(orig))
                {
                    res.ErrorCode = EngineErrorCode.SelfCheckFailed;   // 对应源实现 exit 3
                    res.ErrorMessage =
                        "当前文件含补丁，但无法逆向还原出干净内容（补丁文本失配）。拒绝建立可能被污染的备份，" +
                        "请先手工核对 bundle 现状。";
                    res.Notes.Add(Notes.Error("拒绝建立可能被污染的备份", res.ErrorMessage, "zcode/apply"));
                    return res;
                }
                FileText.Write(bm.BackupPath, orig);
                bm.WriteMeta(orig);
                res.BackupStatus = BackupStatus.Clean;
                res.Notes.Add(Notes.Warn("无备份且文件已含补丁",
                    $"已按补丁表逆序还原出原始内容并建立干净备份：{bm.BackupPath}（{FileText.Human(FileText.Bytes(orig))}）",
                    "zcode/apply"));
            }
            else
            {
                FileText.Write(bm.BackupPath, text);
                bm.WriteMeta(text);
                res.BackupStatus = BackupStatus.Clean;
                res.Notes.Add(Notes.Info("已备份原文件", $"{bm.BackupPath}", "zcode/apply"));
            }
        }
        else
        {
            var anyMarker = ZcodePatchTable.AnyMarker(text);
            var backupText = FileText.ReadOrNull(bm.BackupPath) ?? "";
            var backupPatched = ZcodePatchTable.AnyMarker(backupText);

            if (backupPatched)
            {
                // 备份本身带补丁标记 —— 它不是原始文件。用这种备份 revert 会把补丁"还原"回来。
                bm.AgeAsContaminated();
                res.BackupStatus = BackupStatus.Contaminated;
                res.ContaminatedPath = bm.BackupPath + ".contaminated";
                res.Notes.Add(Notes.Warn("备份已污染",
                    $"备份含补丁标记，已另存为 {res.ContaminatedPath}；本次 apply 不覆盖它。当前没有可用的原始文件，" +
                    "建议打完补丁后跑一次 restore 重建干净备份。", "zcode/apply"));
            }
            else if (!anyMarker)
            {
                // 当前文件是全新版本（被升级覆盖），旧备份是上一版 —— 直接 revert 会把旧版装回去。
                bm.AgeAsSuperseded();
                res.SupersededPath = bm.BackupPath + ".superseded";
                FileText.Write(bm.BackupPath, text);
                bm.WriteMeta(text);
                res.BackupStatus = BackupStatus.Clean;
                res.Notes.Add(Notes.Warn("检测到升级覆盖",
                    $"旧备份另存为 {res.SupersededPath}；已用当前版本重建备份 → {bm.BackupPath}", "zcode/apply"));
            }
            else
            {
                var a = bm.Assess(ZcodePatchTable.Markers);
                res.BackupStatus = a.Status;
                res.SupersededPath = a.SupersededPath;
                res.ContaminatedPath = a.ContaminatedPath;
                res.OriginalSha256 = a.OriginalSha256FromMeta;
                res.Notes.Add(Notes.Info("备份已存在且干净，保留", bm.BackupPath, "zcode/apply"));
            }
        }

        // ---------------------------------------------------------------- 写盘前自检
        var gate = JsSyntaxGate.VerifyAfterPatch(text, next);
        res.SelfCheckDetail = gate.Detail;
        res.Notes.AddRange(gate.Notes);
        if (!gate.Ok)
        {
            res.ErrorCode = EngineErrorCode.SelfCheckFailed;
            res.ErrorMessage = $"改完自检失败，拒绝写入 {bundlePath}：{gate.Detail}";
            res.Notes.Add(Notes.Error("自检失败，拒绝写入", res.ErrorMessage, "zcode/selfcheck"));
            return res;
        }

        FileText.Write(bundlePath, next);
        res.Wrote = true;
        res.CurrentSha256 = FileText.Sha256File(bundlePath);

        // 回读自证：本次标记为 apply 的每道补丁，marker 必须能在盘上读到（"写进去了"不等于"写对了"）。
        var readback = FileText.Read(bundlePath);
        res.ReadBackClean = res.Plan
            .Where(x => x.Action == "apply")
            .All(x => FileText.Count(readback, ZcodePatchTable.Patches.First(p => p.Id == x.Id).Marker) > 0);
        res.Notes.Add(Notes.Info("已写入", $"{bundlePath}（{FileText.Human(res.BeforeBytes)} → {FileText.Human(FileText.Bytes(next))}）", "zcode/apply"));
        return res;
    }

    /// <summary>照 cmdProbes 的 seg(start, len)：取某段起点后固定长度的文本；找不到时给"(未找到)"。</summary>
    public static string Segment(string text, string start, int len)
    {
        var i = text.IndexOf(start, StringComparison.Ordinal);
        return i < 0 ? "(未找到)" : text.Substring(i, Math.Min(len, text.Length - i));
    }

    /// <summary>照 cmdRevert：从备份还原 + SHA-256 比对 + 回读确认无残留；备份缺失/不可信时改走逆向还原。</summary>
    public static ZcodeBundleResult Revert(string bundlePath, bool dryRun, bool removeBackups, CancellationToken ct)
    {
        var res = new ZcodeBundleResult { BackupPath = BackupPathOf(bundlePath) };
        if (!File.Exists(bundlePath))
            throw new AgentEngineException(EngineErrorCode.TargetFileMissing, $"bundle 不存在：{bundlePath}");

        var bm = new BackupManager(bundlePath);
        if (!File.Exists(bm.BackupPath))
        {
            var r = Restore(bundlePath, dryRun, ct);
            r.UsedRestoreFallback = true;
            r.Notes.Insert(0, Notes.Warn("没有备份，改用逆向还原",
                $"没有备份：{bm.BackupPath}（无法从备份还原）。已改走 restore：按补丁表逆序做文本级撤销。", "zcode/revert"));
            return r;
        }

        var assessment = bm.Assess(ZcodePatchTable.Markers);
        res.BackupStatus = assessment.Status;
        res.OriginalSha256 = assessment.OriginalSha256FromMeta;
        res.SupersededPath = assessment.SupersededPath;
        res.ContaminatedPath = assessment.ContaminatedPath;

        if (assessment.Status == BackupStatus.Contaminated)
        {
            // 前置健康检查：污染备份绝不能被用来"还原"。
            bm.AgeAsContaminated();
            res.ContaminatedPath = bm.BackupPath + ".contaminated";
            res.ErrorCode = EngineErrorCode.AnchorConflict;   // 对应源实现 exit 6
            res.ErrorMessage =
                "备份已污染：备份文件本身含有补丁标记，它不是原始文件。用这种备份还原会把补丁「还原」回来，" +
                $"而工具还会报告成功。证据已另存 {res.ContaminatedPath}，请改用逆向还原（restore）。";
            res.Notes.Add(Notes.Error("备份污染，拒绝用它还原", res.ErrorMessage, "zcode/revert"));
            return res;
        }

        if (assessment.Status == BackupStatus.Unreadable)
        {
            var r = Restore(bundlePath, dryRun, ct);
            r.UsedRestoreFallback = true;
            r.Notes.Insert(0, Notes.Warn("备份无法读取，改用逆向还原",
                $"备份无法读取：{bm.BackupPath}。已改走 restore。", "zcode/revert"));
            return r;
        }

        var backupBytes = File.ReadAllBytes(bm.BackupPath);
        var expected = assessment.OriginalSha256FromMeta;

        if (dryRun)
        {
            res.CurrentSha256 = FileText.Sha256File(bundlePath);
            res.ReadBackClean = !ZcodePatchTable.AnyMarker(FileText.Read(bm.BackupPath));
            res.MatchesOriginal = expected is null
                ? null
                : string.Equals(expected, assessment.Sha256, StringComparison.OrdinalIgnoreCase);
            res.Notes.Add(Notes.Info("[dry-run] 未写入任何文件",
                $"将从备份还原：{bm.BackupPath}（{FileText.Human(backupBytes.Length)}）→ {bundlePath}。" +
                "预览阶段没有回读证据，ReadBackClean 只反映备份内容本身。", "zcode/revert"));
            return res;
        }

        // 字节级还原（同 copyFileSync）：保证"写入结果 == 备份内容"可证明。
        WriteBytesAtomic(bundlePath, backupBytes);

        var got = FileText.Sha256File(bundlePath);
        var want = FileText.Sha256File(bm.BackupPath);
        res.CurrentSha256 = got;
        if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase))
        {
            res.ErrorCode = EngineErrorCode.IoError;   // 对应源实现 exit 5
            res.ErrorMessage = $"还原校验失败：SHA-256 不一致！备份 {want}，当前 {got}。";
            res.Notes.Add(Notes.Error("还原校验失败", res.ErrorMessage, "zcode/revert"));
            return res;
        }
        res.Wrote = true;
        res.Notes.Add(Notes.Info("已从备份还原", $"{bundlePath}；SHA-256 一致（{got[..16]}…）{FileText.Human(backupBytes.Length)}", "zcode/revert"));

        // 还原后必须自证：bundle 里不应再残留任何补丁标记。
        var readback = FileText.Read(bundlePath);
        var leftover = ZcodePatchTable.MarkersFound(readback);
        if (leftover.Count > 0)
        {
            res.ReadBackClean = false;
            res.ErrorCode = EngineErrorCode.AnchorConflict;   // 对应源实现 exit 6
            res.ErrorMessage = $"还原后仍检测到补丁标记：{string.Join(", ", leftover)}。这份备份并非干净的原始文件，请改用逆向还原（restore）。";
            res.Notes.Add(Notes.Error("还原后仍有残留", res.ErrorMessage, "zcode/revert"));
            return res;
        }
        res.ReadBackClean = true;
        res.Notes.Add(Notes.Info("内容校验", "无补丁残留（干净）", "zcode/revert"));

        res.MatchesOriginal = expected is null ? null : string.Equals(expected, got, StringComparison.OrdinalIgnoreCase);
        if (res.MatchesOriginal == false)
            res.Notes.Add(Notes.Warn("与首次备份指纹不同",
                $"本次还原结果与 meta 里记录的 originalSha256 不同（备份在建立后可能被改动过）。首次：{expected}", "zcode/revert"));

        if (removeBackups)
        {
            bm.RemoveAll();
            res.Notes.Add(Notes.Info("已清除备份", $"删除 {bm.BackupPath} / {bm.MetaPath} / .superseded / .contaminated", "zcode/revert"));
        }
        return res;
    }

    /// <summary>照 cmdRestore：按补丁表**逆序**做文本级撤销；某条 patch 文本命中 &gt;1 次 → 中止（不猜位置）。</summary>
    public static ZcodeBundleResult Restore(string bundlePath, bool dryRun, CancellationToken ct)
    {
        var res = new ZcodeBundleResult { BackupPath = BackupPathOf(bundlePath) };
        if (!File.Exists(bundlePath))
            throw new AgentEngineException(EngineErrorCode.TargetFileMissing, $"bundle 不存在：{bundlePath}");

        var before = FileText.Read(bundlePath);
        res.BeforeBytes = FileText.Bytes(before);
        var next = before;

        // 逆序撤销。P5 的 anchor 是 P4 patch 的尾句，P4 的 anchor 是 P1 patch 的尾句——正序会打断后续匹配。
        foreach (var p in ZcodePatchTable.Patches.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            var patched = FileText.Count(next, p.Patch);
            var marked = FileText.Count(next, p.Marker);
            var step = new ZcodePatchStep
            {
                Id = p.Id, Layer = p.Layer, Desc = p.Desc, State = ZcodePatchState.Applied,
                AnchorHits = FileText.Count(next, p.Anchor), MarkerHits = marked, PatchHits = $"x{patched}", Action = "",
            };

            if (patched == 1)
            {
                next = ZcodePatchTable.ReplaceOnce(next, p.Patch, p.Anchor);
                step.Action = "reverted";
            }
            else if (patched > 1)
            {
                step.Action = $"AMBIGUOUS (patch text x{patched})";
                res.Plan.Add(step);
                res.AfterBytes = FileText.Bytes(next);
                res.ErrorCode = EngineErrorCode.AnchorConflict;
                res.ErrorMessage = $"补丁文本出现多次（{p.Id}: x{patched}），中止以免改错位置。";
                res.Notes.Add(Notes.Error("补丁文本不唯一，中止", res.ErrorMessage, "zcode/restore"));
                return res;
            }
            else if (marked > 0)
            {
                step.Action = "MARKER PRESENT but patch text missing — manual check";
                res.Notes.Add(Notes.Warn("标记在但补丁文本不在",
                    $"{p.Id}：marker 命中 {marked} 次，但补丁文本找不到，无法自动撤销，请人工核对。", "zcode/restore"));
            }
            else
            {
                step.State = ZcodePatchState.Missing;
                step.Action = "not applied, skipped";
            }
            res.Plan.Add(step);
        }

        res.AfterBytes = FileText.Bytes(next);
        res.WritesPlanned = res.Plan.Count(x => x.Action == "reverted");

        if (next == before)
        {
            res.NothingToDo = true;
            res.Notes.Add(Notes.Info("没有可撤销的补丁", $"文件已是原始状态，或全部失配。\n{res.PlanText()}", "zcode/restore"));
            return res;
        }

        if (dryRun)
        {
            res.Notes.Add(Notes.Info("[dry-run] 未写入任何文件",
                $"将要执行：\n{res.PlanText()}\n预计体积：{FileText.Human(res.BeforeBytes)} → {FileText.Human(res.AfterBytes)}", "zcode/restore"));
            return res;
        }

        var safety = bundlePath + ".pre-restore.bak";
        if (!File.Exists(safety))
        {
            WriteBytesAtomic(safety, File.ReadAllBytes(bundlePath));
            res.SafetyBackupPath = safety;
            res.Notes.Add(Notes.Info("安全网", $"已另存当前文件 → {safety}", "zcode/restore"));
        }
        else
        {
            res.SafetyBackupPath = safety;
            res.Notes.Add(Notes.Info("安全网已存在，保留", safety, "zcode/restore"));
        }

        var gate = JsSyntaxGate.VerifyAfterPatch(before, next);
        res.SelfCheckDetail = gate.Detail;
        res.Notes.AddRange(gate.Notes);
        if (!gate.Ok)
        {
            res.ErrorCode = EngineErrorCode.SelfCheckFailed;
            res.ErrorMessage = $"撤销后自检失败，拒绝写入 {bundlePath}：{gate.Detail}";
            res.Notes.Add(Notes.Error("自检失败，拒绝写入", res.ErrorMessage, "zcode/selfcheck"));
            return res;
        }

        FileText.Write(bundlePath, next);
        res.Wrote = true;
        res.CurrentSha256 = FileText.Sha256File(bundlePath);
        res.ReadBackClean = !ZcodePatchTable.AnyMarker(FileText.Read(bundlePath));
        res.Notes.Add(Notes.Info("已写入", $"{bundlePath}（{FileText.Human(res.BeforeBytes)} → {FileText.Human(FileText.Bytes(next))}）\n{res.PlanText()}", "zcode/restore"));
        return res;
    }

    /// <summary>照 breach.ps1 的 Restart-ZCode：关主窗口 → 等 3 秒 → 强制结束残留 → 重新启动。</summary>
    public static bool TryRestart(string? exe, out string detail)
    {
        detail = "";
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            detail = "ZCode 可执行文件不可用，未重启；请自行退出并重新启动。";
            return false;
        }
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("ZCode");
            foreach (var p in procs)
            {
                try { p.CloseMainWindow(); } catch { /* 忽略 */ }
            }
            if (procs.Length > 0) Thread.Sleep(3000);
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("ZCode"))
            {
                try { p.Kill(true); } catch { /* 忽略 */ }
                finally { p.Dispose(); }
            }
            Thread.Sleep(1000);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe, UseShellExecute = true,
            });
            detail = $"已重启 ZCode：{exe}";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"重启失败：{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>字节级原子写：临时文件 + 替换，保证"写入结果 == 备份内容"逐字节成立。</summary>
    public static void WriteBytesAtomic(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(path)) File.Replace(tmp, path, null, true);
        else File.Move(tmp, path);
    }
}
