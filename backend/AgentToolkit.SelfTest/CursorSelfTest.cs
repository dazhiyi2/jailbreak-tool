using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentToolkit.Contracts;
using AgentToolkit.Core;
using AgentToolkit.Core.Support;
using AgentToolkit.Core.Targets;
using AgentToolkit.Core.Targets.Cursor;
using Microsoft.Data.Sqlite;

namespace AgentToolkit.SelfTest;

/// <summary>
/// Cursor 适配器的回归自检。硬性纪律：**只在沙箱副本上操作**，
/// 绝不写 E:\APP\cursor、%APPDATA%\Cursor、~\.cursor 下的任何真实文件
/// （用例 13 只读核对，并在前后对两棵真实目录树做 sha256 快照比对）。
///
/// 沙箱里有：假安装根（resources\app\out\main.js + extensions\cursor-agent-host + 4 个合成规则 JS）、
/// 假配置根（storage.json + 现建的合成 state.vscdb）、假工作区，以及本工具自己的数据目录。
/// 合成 JS 里**刻意混入非 ASCII 与非法 UTF-8 字节**：只有按字节做等长替换才通得过，
/// 走字符解码再编码的实现在这里就会露馅。
/// </summary>
public sealed class CursorSelfTest : ITargetSelfTest
{
    public string TargetId => "cursor";
    public string Name => "Cursor 适配器回归（沙箱合成 4 个规则 JS / state.vscdb / .mdc）";

    // 锚点原文在自检里**独立写一遍**（不引用 CursorPatchTable），再断言与表里的一致：
    // 否则改坏了表、自检也跟着坏，等于没测。
    private const string A1Text = "These are rules set by the user that you should follow if appropriate.";
    private const string A2Text = "The rules section has a number of possible rules/memories/context that you should consider.";

    private const string SessionId = "6744eb29-0000-4000-8000-000000000001";
    private const string DraftId = "empty-state-draft";
    private const string UserBubble = "把当前生效的规则读一遍，然后按它给结果。";
    private const string AssistantFiller =
        "（下面是用来把长度推过截断阈值的填充：先确认目录存在，再确认权限足够，再确认没有符号链接把大小算重，" +
        "最后才排序取前五。任何一步跳过都会让结论失真，宁可多跑一遍也不要给一个看起来对的结果。" +
        "截断判定用的是字符数而不是字节数，中文一个字算一个字符，所以必须真的写够长度才能触发。" +
        "这一段继续补充与结论无关的说明文字，把长度稳定地推过三百字符。" +
        "再补一段：交付前先自问三件事——命令能不能直接跑、占位符有没有留全、结尾有没有多余的客套；" +
        "三件事都过了就直接发出去，不要再多写一句解释，也不要再把需求复述一遍。" +
        "填充到此为止，长度已经足够触发 MaxCharsPerText 的截断分支。）";
    private const string AssistantNoMarker = "规则已读：三条命令，逐行可跑。" + AssistantFiller;
    private const string AssistantWithMarker =
        "规则已读（引用原文：本机全局工作偏好 / 加做，不是收缩 / 本机项目环境）。三条命令，逐行可跑。" + AssistantFiller;

    // ================================================================ 入口

    public SelfTestResult Run(SelfTestContext ctx)
    {
        var r = new SelfTestResult { Name = Name };
        var saved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        void SetEnv(string key, string? value)
        {
            if (!saved.ContainsKey(key)) saved[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        // 真实路径在任何 env 覆盖之前抓下来，供用例 13 的只读核对与"没碰真实目标"的哈希比对用。
        var realConfig = CursorLocator.DefaultConfigHome();
        var realInstall = ResolveRealInstall();
        r.Step($"真实安装根（只读比对）：{realInstall ?? "(没定位到)"}");
        r.Step($"真实配置根（只读比对）：{realConfig}");

        var installBefore = SnapshotTree(realInstall);
        var configBefore = SnapshotTree(realConfig, skipWalIndexShm: true);
        var shmBefore = WalIndexShmCount(realConfig);
        r.Step($"    快照：安装根 {installBefore.Count} 个文件、配置根 {configBefore.Count} 个文件" +
               $"（另有 {shmBefore} 个 *.db-shm 不参与比对）");

        try
        {
            Case01FactoryState(ctx, r, SetEnv);
            Case02PreviewReadOnly(ctx, r, SetEnv);
            Case03Apply(ctx, r, SetEnv);
            Case04Idempotent(ctx, r, SetEnv);
            Case05Revert(ctx, r, SetEnv);
            Case06ContaminatedBackup(ctx, r, SetEnv);
            Case07FailClosed(ctx, r, SetEnv);
            Case08EqualLength(ctx, r, SetEnv);
            Case09Markers(ctx, r, SetEnv);
            Case10Effectiveness(ctx, r, SetEnv);
            Case11Logs(ctx, r, SetEnv);
            Case12LocateDiffExportProbes(ctx, r, SetEnv);
        }
        finally
        {
            foreach (var kv in saved) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        }

        Case13RealDataReadOnly(ctx, r, realInstall, realConfig);

        // 收尾：真实安装根与配置根必须一个字节都没变（自检全程只读它们）。
        var installAfter = SnapshotTree(realInstall);
        var configAfter = SnapshotTree(realConfig, skipWalIndexShm: true);
        var dbWal = DbAndWalDiff(configBefore, configAfter);
        r.Check(SameSnapshot(installBefore, installAfter),
            $"自检全程没有改动真实安装根（{installBefore.Count} 个文件，sha256 全部不变" +
            (installBefore.Count == 0 ? "；本机没定位到安装根，该项空过" : "") + "）");
        r.Check(SameSnapshot(configBefore, configAfter),
            $"自检全程没有改动真实配置根（{configBefore.Count} 个文件，sha256 全部不变；" +
            $"快照排除了 {shmBefore} 个 *.db-shm —— 那是 SQLite 的 wal-index 共享内存文件，任何读者都可能创建/更新它，" +
            "把它算进来等于拿自检自己的足迹当改动；-db 与 -wal 不在此列，见下一条）");
        r.Check(dbWal.Changed == 0,
            $"真实配置根下 {dbWal.Total} 个 *.db / *.db-wal 逐字节不变（未被排除、未放宽）" +
            (dbWal.Changed == 0 ? "" : $"：变了 {dbWal.Changed} 个（{dbWal.Detail}）"));

        return r;
    }

    private static string? ResolveRealInstall()
    {
        foreach (var c in CursorLocator.FromRegistry())
            if (CursorLocator.IsInstallRoot(c)) return c;
        var fallback = CursorLocator.DefaultInstallRoot();
        return CursorLocator.IsInstallRoot(fallback) ? fallback : null;
    }

    // ================================================================ 1 出厂状态

    private static void Case01FactoryState(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【1】出厂态：4 个 JS 是原文、DB 键不存在、.mdc 不存在 → 三层分别 NotApplied / NotApplied / TargetAbsent");

        var s = Setup(ctx, setEnv, "factory");
        var st = s.Target.GetStatus(default);

        r.Check(st.Layers.Count == 3, $"状态里装配出 {st.Layers.Count} 层（补丁 / 个人规则 / 项目规则；日志层只读，不进 Layers）");
        var l1 = st.Layers[0];
        var l2 = st.Layers[1];
        var l3 = st.Layers[2];
        r.Check(l1.Kind == LayerKind.SystemPrompt, $"层 1 的 Kind = {l1.Kind}（契约里没有「补丁」档，按 SystemPrompt 报）");
        r.Check(l2.Kind == LayerKind.PolicyText, $"层 2 的 Kind = {l2.Kind}（契约里没有「配置项」档，按 PolicyText 报）");
        r.Check(l3.Kind == LayerKind.ContextPayload, $"层 3 的 Kind = {l3.Kind}（ContextPayload）");
        r.Check(l1.TargetPath == s.PatchFiles[2], $"层 1 的 TargetPath 是主文件（{Path.GetFileName(l1.TargetPath)}）");
        r.Check(l2.TargetPath == s.DbPath, "层 2 指向 state.vscdb");
        r.Check(l3.TargetPath == s.RulesPath, $"层 3 指向 {s.RulesPath}");
        r.Check(l1.State == LayerState.NotApplied, $"4 个 JS 都是出厂原文 → 层 1 {l1.State}（期望 NotApplied）");
        r.Check(l2.State == LayerState.NotApplied, $"DB 里没有我们的键 → 层 2 {l2.State}（期望 NotApplied）");
        r.Check(l3.State == LayerState.TargetAbsent, $".mdc 不存在 → 层 3 {l3.State}（期望 TargetAbsent）");
        r.Check(l1.Notes.Any(n => n.Title == "补丁文件") && l1.Notes.Count(n => n.Title == "补丁文件") == 4,
            "层 1 的 Notes 逐条列出 4 个补丁文件的状态（契约只有单个 TargetPath）");
        r.Check(l1.Notes.Any(n => n.Title == "为什么打这两个锚点"), "层 1 的 Notes 写明打这两个锚点的理由");
        r.Check(l2.Notes.Any(n => n.Title == "备份口径"), "层 2 的 Notes 说明整份 DB 备份与 -wal/-shm 为什么不备份");
        r.Check(l3.Notes.Any(n => n.Title == "alwaysApply"), "层 3 的 Notes 说明 alwaysApply 是正文进提示词的开关");
        r.Check(st.LastMarkerReport is not null, "有会话 → LastMarkerReport 不是 null");
        r.Check(!st.ClientRunning, $"本机 Cursor 没在跑 → ClientRunning={st.ClientRunning}");
        r.Check(st.Target.Found && st.Target.TargetId == "cursor", "EngineStatus.Target 由 ToLocateResult 装配且 Found");
        r.Check(st.Target.ProfileDir == s.ConfigHome + @"\User", $"ProfileDir = 数据根（{st.Target.ProfileDir}）");
        r.Check(st.Target.Notes.Any(n => n.Title.Contains("SessionsDir", StringComparison.Ordinal)),
            "Notes 里写明契约的 SessionsDir 对 Cursor 不适用（会话在 SQLite 里）");
        r.Check(l1.Notes.Any(n => n.Title.StartsWith("契约缺口", StringComparison.Ordinal)),
            "补丁层的 4 文件/单 TargetPath 缺口写进了 Notes，没有改契约");

        // 1b：整个库都不存在（Cursor 没落过会话）→ 层 2 / 日志层都不适用，但不该抛异常
        var n = Setup(ctx, setEnv, "factory-no-db", withDb: false);
        var st2 = n.Target.GetStatus(default);
        r.Check(st2.Layers[1].State == LayerState.TargetAbsent,
            $"没有 state.vscdb → 层 2 {st2.Layers[1].State}（期望 TargetAbsent）");
        var apply2 = n.Target.PreviewApply(new ApplyOptions { DryRun = true }, default);
        r.Check(!apply2.CanApply && apply2.Notes.Any(x => x.Severity == Severity.Error),
            "DB 不存在 → 预览报不可执行 + Error 级说明（fail-closed）");

        // 1c：工作区解析不到 → 层 3 跳过，其余层照常
        var w = Setup(ctx, setEnv, "factory-no-workspace", workspace: WorkspaceMode.Unresolved);
        var st3 = w.Target.GetStatus(default);
        r.Check(st3.Layers[2].State == LayerState.TargetAbsent, "工作区解析不到 → 层 3 TargetAbsent（跳过）");
        r.Check(st3.Layers[2].TargetPath is null, "层 3 连 TargetPath 都不给（不猜路径）");
        r.Check(st3.Layers[2].StateDetail.Contains("C:\\", StringComparison.Ordinal),
            $"层 3 的 StateDetail 写明不往 C:\\ 写：{Trim1(st3.Layers[2].StateDetail, 90)}");
        ctx.Log($"factory: {l1.State}/{l2.State}/{l3.State}");
    }

    // ================================================================ 2 预览只读

    private static void Case02PreviewReadOnly(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【2】预览只读：PreviewApply 前后 4 个 JS + DB + storage.json 的 sha256 都不变，且不建任何备份");

        var s = Setup(ctx, setEnv, "preview");
        var before = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in s.PatchFiles) before[p] = FileText.Sha256File(p);
        before[s.DbPath] = FileText.Sha256File(s.DbPath);
        before[s.StorageJson] = FileText.Sha256File(s.StorageJson);
        var stamps = s.PatchFiles.ToDictionary(p => p, File.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);
        var dbStamp = File.GetLastWriteTimeUtc(s.DbPath);

        var preview = s.Target.PreviewApply(new ApplyOptions(), default);

        foreach (var p in s.PatchFiles)
            r.Check(FileText.Sha256File(p) == before[p], $"预览前后 {Path.GetFileName(p)} 的 sha256 不变（{Short(before[p])}）");
        r.Check(FileText.Sha256File(s.DbPath) == before[s.DbPath], "预览前后 state.vscdb 的 sha256 不变");
        r.Check(FileText.Sha256File(s.StorageJson) == before[s.StorageJson], "预览前后 storage.json 的 sha256 不变");
        r.Check(s.PatchFiles.All(p => File.GetLastWriteTimeUtc(p) == stamps[p]), "预览没有触碰 4 个 JS 的修改时间");
        r.Check(File.GetLastWriteTimeUtc(s.DbPath) == dbStamp, "预览没有触碰 state.vscdb 的修改时间");
        r.Check(s.PatchFiles.All(p => !File.Exists(p + ".breach.bak")), "预览没有建 JS 备份");
        r.Check(!File.Exists(s.DbPath + ".breach.bak"), "预览没有建 DB 备份");
        r.Check(!File.Exists(s.RulesPath), "预览没有创建 .mdc");
        r.Check(!File.Exists(CursorBaseline.PathOf(s.DataDir)), "预览没有写注入前基线");
        r.Check(CursorDb.ReadItemValue(s.DbPath, CursorDb.PersonalContextKey) is null,
            "预览后 DB 里仍然没有我们的键（只读预览真的没写库）");
        r.Check(RowCount(s.DbPath, "ItemTable") == 6, "预览后 ItemTable 还是 6 行");

        r.Check(preview.CanApply, $"CanApply = {preview.CanApply}（期望 true）");
        r.Check(preview.Changes.Count == 6, $"Changes 六条（4 个 JS + DB + .mdc），实际 {preview.Changes.Count}");
        r.Check(preview.Changes.Count(c => c.Layer == LayerKind.SystemPrompt) == 4, "4 条补丁层改动");
        r.Check(preview.Changes.Where(c => c.WillWrite && c.Layer == LayerKind.SystemPrompt)
                .All(c => c.BeforeBytes == c.AfterBytes),
            "补丁层每条的写前/写后字节数相等（等长替换）");
        r.Check(preview.PreviewId.Length == 16, $"PreviewId 取输入内容 sha256 前 16 位：{preview.PreviewId}");
        r.Check(preview.Notes.Any(n => n.Title == "补丁层的等长纪律"), "预览说明等长纪律");
        r.Check(preview.Notes.Any(n => n.Title.Contains("预览里没有日志层", StringComparison.Ordinal)),
            "预览注明日志层只读、没有 PlannedChange（契约缺口写进 Notes）");
        r.Check(preview.Changes.First(c => c.Path == s.DbPath).Notes.Any(n => n.Title == "原值"),
            "DB 那条改动里列出原值（无备份时逆向撤销要用）");

        var dry = s.Target.Apply(new ApplyOptions { DryRun = true }, null, default);
        r.Check(!dry.Applied, "DryRun=true 的 apply：Applied=false");
        foreach (var p in s.PatchFiles)
            r.Check(FileText.Sha256File(p) == before[p], $"DryRun 的 apply 没有写 {Path.GetFileName(p)}");
        r.Check(FileText.Sha256File(s.DbPath) == before[s.DbPath], "DryRun 的 apply 没有写 DB");
        r.Check(!File.Exists(s.DbPath + ".breach.bak") && !File.Exists(s.RulesPath),
            "DryRun 的 apply 不建备份、不创建 .mdc");
        ctx.Log($"preview: canApply={preview.CanApply} id={preview.PreviewId}");
    }

    // ================================================================ 3 apply

    private static void Case03Apply(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【3】apply：4 个 JS × 2 锚点等长替换（文件长度不变）、DB 键写入、.mdc 写入；其它键与行数不变");

        var s = Setup(ctx, setEnv, "apply");
        var beforeItems = ItemMap(s.DbPath);
        var beforeRows = RowCount(s.DbPath, "ItemTable");
        var lengths = s.PatchFiles.ToDictionary(p => p, p => new FileInfo(p).Length, StringComparer.OrdinalIgnoreCase);

        var res = s.Target.Apply(new ApplyOptions(), null, default);
        r.Check(res.Applied, "ApplyResult.Applied=true");
        r.Check(res.RestartRequired, "RestartRequired=true（要开新会话才看得到 rules 档增长）");
        r.Check(res.VerificationHint.Contains("rules", StringComparison.Ordinal) &&
                res.VerificationHint.Contains("新会话", StringComparison.Ordinal) &&
                res.VerificationHint.Contains("system_prompt", StringComparison.Ordinal),
            "VerificationHint 写明：主判据是 rules 档增量、要新会话、别拿 system_prompt 判");
        r.Check(res.Changes.Count == 6, $"Changes 六条，实际 {res.Changes.Count}");

        // ---- 补丁层 ----
        foreach (var p in s.PatchFiles)
        {
            var bytes = File.ReadAllBytes(p);
            var name = Path.GetFileName(p);
            r.Check(bytes.LongLength == lengths[p], $"{name} 长度不变（{lengths[p]} 字节）");
            foreach (var anchor in CursorPatchTable.Anchors)
            {
                var ah = CursorPatchTable.CountOccurrences(bytes, anchor.AnchorBytes);
                var rh = CursorPatchTable.CountOccurrences(bytes, anchor.ReplacementBytes);
                r.Check(ah == 0 && rh == 1, $"{name} 的 {anchor.Id}：锚点 0 次、替换文本 1 次");
            }
            var bak = new CursorFileBackup(p);
            r.Check(bak.Assess(CursorMarkers.BackupMarkers).Status == BackupStatus.Clean,
                $"{name} 的备份判为干净（不含任何注入标记）");
            r.Check(File.ReadAllBytes(p + ".breach.bak").Length == lengths[p], $"{name} 已备份为 .breach.bak（逐字节）");
            var meta = bak.ReadMeta();
            r.Check(meta is not null && meta.ContainsKey("originalSha256"), $"{name} 的备份写了 .meta.json（含 originalSha256）");
        }

        // 备份里是出厂原文（含锚点、不含替换文本）
        var bakBytes = File.ReadAllBytes(s.PatchFiles[2] + ".breach.bak");
        r.Check(CursorPatchTable.CountOccurrences(bakBytes, CursorPatchTable.Anchors[0].AnchorBytes) == 1 &&
                CursorPatchTable.CountOccurrences(bakBytes, CursorPatchTable.Anchors[0].ReplacementBytes) == 0,
            "备份里是出厂原文（锚点在、替换文本不在）");

        // ---- 层 2 ----
        var personalNow = CursorDb.ReadItemValue(s.DbPath, CursorDb.PersonalContextKey);
        r.Check(personalNow == s.PersonalText, $"DB 键 {CursorDb.PersonalContextKey} == 个人规则文本（逐字节）");
        var afterItems = ItemMap(s.DbPath);
        var afterRows = RowCount(s.DbPath, "ItemTable");
        r.Check(afterRows == beforeRows + 1, $"ItemTable 行数 {beforeRows} → {afterRows}（只多我们那一行）");
        var changedOthers = OtherKeysChanged(beforeItems, afterItems, CursorDb.PersonalContextKey);
        r.Check(changedOthers == 0,
            $"其它 {beforeItems.Count} 个键一个字节都没动（排除我们写的那一个键后，变化项 {changedOthers} 个）");
        r.Check(afterItems.TryGetValue("cursorAuth/accessToken", out var at) &&
                at == beforeItems["cursorAuth/accessToken"],
            "cursorAuth/accessToken 原样保留（只读校验）");
        r.Check(RowCount(s.DbPath, "cursorDiskKV") == 5, "cursorDiskKV 行数不变（未被本工具碰）");
        r.Check(RowCount(s.DbPath, "composerHeaders") == 2, "composerHeaders 行数不变（未被本工具碰）");
        r.Check(File.Exists(s.DbPath + ".breach.bak"), "整份 DB 已备份到 state.vscdb.breach.bak");
        var dbMeta = new CursorFileBackup(s.DbPath).ReadMeta();
        r.Check(dbMeta is not null && dbMeta.TryGetValue("keyPresent", out var kp) && kp == "false",
            "DB 备份的 .meta.json 记下「原本键不存在」（逆向撤销要据此 DELETE）");
        r.Check(dbMeta is not null && dbMeta.ContainsKey("otherKeysDigest") && dbMeta.ContainsKey("otherTablesDigest"),
            "DB 备份的 .meta.json 记下其它键与其它表的结构指纹");
        r.Check(dbMeta is not null && dbMeta.TryGetValue("walSidecarBackedUp", out var wb) && wb == "false",
            "DB 备份的 .meta.json 写明 -wal/-shm 不备份（及理由）");

        // ---- 层 3 ----
        var rulesText = FileText.Read(s.RulesPath);
        r.Check(rulesText == s.RenderedRules, ".mdc 内容 == 渲染后的 frontmatter + 正文（逐字节）");
        r.Check(rulesText.StartsWith("---\ndescription: ", StringComparison.Ordinal) &&
                rulesText.Contains("\nalwaysApply: true\n", StringComparison.Ordinal),
            "frontmatter 形如 description + alwaysApply: true");
        r.Check(!File.Exists(s.RulesPath + ".breach.bak"), ".mdc 出厂不存在 → 没有多余备份");
        r.Check(File.ReadAllBytes(s.RulesPath)[0] != 0xEF, "写入的 .mdc 没有 BOM");
        var other = Path.Combine(CursorRules.DirFor(s.Workspace), "someone-else.mdc");
        r.Check(File.Exists(other) && FileText.Read(other) == "# 别人的规则\n\n不许动我。\n",
            "同目录下别人的 .mdc 一个字节都没动");

        // ---- 基线 ----
        r.Check(File.Exists(CursorBaseline.PathOf(s.DataDir)), "apply 记下了注入前基线");
        var baseline = CursorBaseline.Load(s.DataDir);
        r.Check(baseline?.RulesCharacterCount == s.RulesChars,
            $"基线里的 rules 档字符数 = fixture 的 {s.RulesChars}（实际 {baseline?.RulesCharacterCount}）");
        r.Check(baseline?.PersonalChars == s.PersonalText.Length && baseline?.PayloadBodyChars > 0,
            "基线记下层 2 的字符数与层 3 正文的字符数");

        // ---- 状态 ----
        var st = s.Target.GetStatus(default);
        r.Check(st.Layers[0].State == LayerState.Applied, $"apply 后层 1 = {st.Layers[0].State}（期望 Applied）");
        r.Check(st.Layers[1].State == LayerState.Applied, $"apply 后层 2 = {st.Layers[1].State}（期望 Applied）");
        r.Check(st.Layers[2].State == LayerState.Applied, $"apply 后层 3 = {st.Layers[2].State}（期望 Applied）");
        r.Check(st.Layers[0].Backup.Health == BackupHealth.Clean, $"层 1 的主文件备份报告干净（{st.Layers[0].Backup.Health}）");
        r.Check(st.Layers[1].Backup.Health == BackupHealth.Clean, "层 2 的 DB 备份报告干净");
        ctx.Log($"apply ok: {s.DbPath}");
    }

    // ================================================================ 4 幂等

    private static void Case04Idempotent(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【4】幂等：第二次 apply 六处都无字节变化、Applied=false、WillWrite 全为 false");

        var s = Setup(ctx, setEnv, "idempotent");
        s.Target.Apply(new ApplyOptions(), null, default);

        var shas = s.PatchFiles.ToDictionary(p => p, FileText.Sha256File, StringComparer.OrdinalIgnoreCase);
        var dbSha = FileText.Sha256File(s.DbPath);
        var rulesSha = FileText.Sha256File(s.RulesPath);
        var stamps = s.PatchFiles.ToDictionary(p => p, File.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);
        var dbStamp = File.GetLastWriteTimeUtc(s.DbPath);
        var rulesStamp = File.GetLastWriteTimeUtc(s.RulesPath);

        var second = s.Target.Apply(new ApplyOptions(), null, default);
        foreach (var p in s.PatchFiles)
        {
            r.Check(FileText.Sha256File(p) == shas[p], $"第二次 apply 后 {Path.GetFileName(p)} 的 sha256 不变");
            r.Check(File.GetLastWriteTimeUtc(p) == stamps[p], $"没有重写 {Path.GetFileName(p)}（mtime 不变）");
        }
        r.Check(FileText.Sha256File(s.DbPath) == dbSha, "第二次 apply 后 state.vscdb 的 sha256 不变");
        r.Check(FileText.Sha256File(s.RulesPath) == rulesSha, "第二次 apply 后 .mdc 的 sha256 不变");
        r.Check(File.GetLastWriteTimeUtc(s.DbPath) == dbStamp, "没有重写 state.vscdb（mtime 不变）");
        r.Check(File.GetLastWriteTimeUtc(s.RulesPath) == rulesStamp, "没有重写 .mdc（mtime 不变）");
        r.Check(!second.Applied, $"第二次 apply 没有可写内容 → Applied={second.Applied}（不虚报）");
        r.Check(second.Changes.All(c => !c.WillWrite), "第二次 apply 的 Changes 里 WillWrite 全为 false");
        r.Check(second.Notes.Count(n => n.Title == "幂等") >= 2, "补丁层 / DB / 规则层都给出幂等说明");

        var preview = s.Target.PreviewApply(new ApplyOptions(), default);
        r.Check(!preview.CanApply, "各层都已到位 → CanApply=false");
        r.Check(preview.Changes.All(c => !c.WillWrite), "预览的所有行 WillWrite=false");
        r.Check(preview.TotalBeforeBytes == 0 && preview.TotalAfterBytes == 0,
            $"无改动时体积统计不虚报（{preview.TotalBeforeBytes} → {preview.TotalAfterBytes}）");
        ctx.Log("idempotent ok");
    }

    // ================================================================ 5 revert

    private static void Case05Revert(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【5】revert：有备份 → 4 个 JS + DB + .mdc 全部逐字节回出厂、MatchesOriginal=true；" +
             "无备份 → 逆向撤销（JS 反向替换、DB 删键或写回原值、.mdc 删除）、MatchesOriginal=null");

        // ---- 5a：有备份 ----
        var s = Setup(ctx, setEnv, "revert");
        var factoryShas = s.PatchFiles.ToDictionary(p => p, FileText.Sha256File, StringComparer.OrdinalIgnoreCase);
        var factoryDbSha = FileText.Sha256File(s.DbPath);
        s.Target.Apply(new ApplyOptions(), null, default);
        r.Check(s.PatchFiles.All(p => File.Exists(p + ".breach.bak")) && File.Exists(s.DbPath + ".breach.bak"),
            "5a 前置：apply 时拍了 5 份首次备份（4 个 JS + DB）");

        var rev = s.Target.Revert(new RevertOptions(), null, default);
        r.Check(rev.Reverted, "5a RevertResult.Reverted=true");
        foreach (var p in s.PatchFiles)
            r.Check(FileText.Sha256File(p) == factoryShas[p], $"5a {Path.GetFileName(p)} 逐字节回出厂（sha256 相同）");
        r.Check(FileText.Sha256File(s.DbPath) == factoryDbSha, "5a state.vscdb 逐字节回出厂（sha256 相同）");
        r.Check(!File.Exists(s.DbPath + "-wal") || new FileInfo(s.DbPath + "-wal").Length == 0,
            "5a 还原后 -wal 已删掉（留着会把还原内容再覆盖回去）");
        r.Check(CursorDb.ReadItemValue(s.DbPath, CursorDb.PersonalContextKey) is null, "5a DB 键回到「不存在」的出厂态");
        r.Check(!File.Exists(s.RulesPath), "5a .mdc 已删除（出厂即不存在）");
        r.Check(rev.MatchesOriginal == true, $"5a 与首次备份 sha256 比对一致（MatchesOriginal={rev.MatchesOriginal}）");
        r.Check(rev.OriginalSha256 is not null && rev.CurrentSha256 == rev.OriginalSha256,
            $"5a OriginalSha256 == CurrentSha256（{Short(rev.CurrentSha256)}）");
        r.Check(rev.ReadBackClean, "5a 回读确认无残留");
        r.Check(rev.Changes.Any(c => c.WillWrite), "5a Changes 记录了还原动作");
        r.Check(rev.Notes.Any(n => n.Title.Contains("生效时机", StringComparison.Ordinal)), "5a 提示生效时机（新会话）");
        r.Check(File.Exists(s.DbPath + ".breach.bak"), "5a 默认保留备份（RemoveBackups=false）");

        // ---- 5b：无备份，原本键不存在 → DELETE ----
        var b = Setup(ctx, setEnv, "revert-no-backup");
        b.Target.Apply(new ApplyOptions(), null, default);
        foreach (var p in b.PatchFiles) { File.Delete(p + ".breach.bak"); File.Delete(p + ".breach.bak.meta.json"); }
        File.Delete(b.DbPath + ".breach.bak");
        File.Delete(b.DbPath + ".breach.bak.meta.json");

        var rev2 = b.Target.Revert(new RevertOptions(), null, default);
        r.Check(rev2.Reverted, "5b 没有备份时走逆向撤销路径");
        foreach (var p in b.PatchFiles)
        {
            var bytes = File.ReadAllBytes(p);
            r.Check(CursorPatchTable.Anchors.All(a =>
                    CursorPatchTable.CountOccurrences(bytes, a.AnchorBytes) == 1 &&
                    CursorPatchTable.CountOccurrences(bytes, a.ReplacementBytes) == 0),
                $"5b {Path.GetFileName(p)} 逆向替换回锚点原文");
        }
        r.Check(CursorDb.ReadItemValue(b.DbPath, CursorDb.PersonalContextKey) is null,
            "5b 逆向撤销：键原本不存在 → 删掉那一行");
        r.Check(!File.Exists(b.RulesPath), "5b .mdc 被删除（无备份时回到不存在状态）");
        r.Check(rev2.MatchesOriginal is null, "5b 没有可信备份 → MatchesOriginal 报 null，不伪造 true");
        r.Check(rev2.ReadBackClean, "5b 逆向撤销后回读干净");
        r.Check(RowCount(b.DbPath, "ItemTable") == 6, $"5b 删的是我们那一行（ItemTable 回到 6 行）");

        // ---- 5c：无备份，原本键存在 → 写回原值 ----
        const string factoryPersonal = "（用户自己原本写在这里的个人规则）";
        var c = Setup(ctx, setEnv, "revert-original-value", factoryPersonal: factoryPersonal);
        c.Target.Apply(new ApplyOptions(), null, default);
        r.Check(CursorDb.ReadItemValue(c.DbPath, CursorDb.PersonalContextKey) == c.PersonalText, "5c 前置：注入覆盖了原值");
        File.Delete(c.DbPath + ".breach.bak");
        var rev3 = c.Target.Revert(new RevertOptions(), null, default);
        r.Check(CursorDb.ReadItemValue(c.DbPath, CursorDb.PersonalContextKey) == factoryPersonal,
            "5c 无备份时按 .meta.json 记的原值写回（不是删键）");
        r.Check(rev3.MatchesOriginal is null, "5c MatchesOriginal 报 null");
        r.Check(RowCount(c.DbPath, "ItemTable") == 7, "5c 行数不变（原本就有这个键）");

        // ---- 5d：RepeatApply 只碰自己那份 .mdc ----
        r.Check(File.Exists(Path.Combine(CursorRules.DirFor(s.Workspace), "someone-else.mdc")),
            "5d 别人的 .mdc 从头到尾都在");
        ctx.Log("revert ok");
    }

    // ================================================================ 6 污染备份

    private static void Case06ContaminatedBackup(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【6】污染备份：把备份改写成含 marker 的内容 → 状态报 Contaminated，revert 抛 AnchorConflict 且零改动");

        // 6a：JS 备份被污染
        var a = Setup(ctx, setEnv, "contaminated-js");
        a.Target.Apply(new ApplyOptions(), null, default);
        File.WriteAllBytes(a.PatchFiles[0] + ".breach.bak",
            Encoding.UTF8.GetBytes("/* 被改坏的备份 */\n// " + CursorMarkers.PersonalMarkers[0] + "\n"));
        r.Check(new CursorFileBackup(a.PatchFiles[0]).Assess(CursorMarkers.BackupMarkers).Status == BackupStatus.Contaminated,
            "6a JS 备份已被改写成含 marker 的内容 → 判污染");

        var st = a.Target.GetStatus(default);
        r.Check(st.Layers[0].Notes.Any(n => n.Title == "补丁文件" && n.Detail.Contains("备份污染", StringComparison.Ordinal)),
            "6a 状态页在补丁文件列表里报出**备份污染**");
        var shas = a.PatchFiles.ToDictionary(p => p, FileText.Sha256File, StringComparer.OrdinalIgnoreCase);
        var dbSha = FileText.Sha256File(a.DbPath);
        CheckThrows(r, () => a.Target.Revert(new RevertOptions(), null, default), EngineErrorCode.AnchorConflict,
            "6a 污染备份 → revert 抛 AnchorConflict（拒绝用它还原）", a);
        foreach (var p in a.PatchFiles)
            r.Check(FileText.Sha256File(p) == shas[p], $"6a 抛错时没有改动 {Path.GetFileName(p)}");
        r.Check(FileText.Sha256File(a.DbPath) == dbSha, "6a 抛错时没有改动 state.vscdb（预检在任何写操作之前）");
        r.Check(File.Exists(a.RulesPath), "6a 抛错时 .mdc 也没被删");

        // 6b：DB 备份被污染
        var b = Setup(ctx, setEnv, "contaminated-db");
        b.Target.Apply(new ApplyOptions(), null, default);
        var dbBak = File.ReadAllBytes(b.DbPath + ".breach.bak");
        File.WriteAllBytes(b.DbPath + ".breach.bak",
            dbBak.Concat(Encoding.UTF8.GetBytes(CursorMarkers.PersonalMarkers[1])).ToArray());
        r.Check(new CursorFileBackup(b.DbPath).Assess(CursorMarkers.BackupMarkers).Status == BackupStatus.Contaminated,
            "6b DB 备份里含 marker → 判污染（按字节在 SQLite 文件里搜标记的 UTF-8 编码）");
        var dbSha2 = FileText.Sha256File(b.DbPath);
        CheckThrows(r, () => b.Target.Revert(new RevertOptions(), null, default), EngineErrorCode.AnchorConflict,
            "6b 污染备份 → revert 抛 AnchorConflict", b);
        r.Check(FileText.Sha256File(b.DbPath) == dbSha2, "6b 抛错时没有改动 state.vscdb");

        // 6c：.mdc 备份被污染
        var c = Setup(ctx, setEnv, "contaminated-mdc", factoryRulesFile: "# 用户自己的规则\n");
        c.Target.Apply(new ApplyOptions(), null, default);
        FileText.Write(c.RulesPath + ".breach.bak", "# 被改坏的备份\n\n" + CursorMarkers.PayloadMarkers[0] + "\n");
        r.Check(new BackupManager(c.RulesPath).Assess(CursorMarkers.BackupMarkers).Status == BackupStatus.Contaminated,
            "6c .mdc 备份含 marker → 判污染");
        CheckThrows(r, () => c.Target.Revert(new RevertOptions(), null, default), EngineErrorCode.AnchorConflict,
            "6c 污染备份 → revert 抛 AnchorConflict", c);
        r.Check(FileText.Read(c.RulesPath) == c.RenderedRules, "6c 抛错时 .mdc 没有被改动");
        ctx.Log("contaminated ok");
    }

    // ================================================================ 7 fail-closed

    private static void Case07FailClosed(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【7】fail-closed：锚点命中 0 / 命中 2 / 缺文件 → SelfCheckFailed 且不写盘；DB 损坏 → IoError");

        // 7a：锚点命中 0
        var a = Setup(ctx, setEnv, "anchor-zero", anchorMode: AnchorMode.Zero);
        var shas = a.PatchFiles.ToDictionary(p => p, FileText.Sha256File, StringComparer.OrdinalIgnoreCase);
        var dbSha = FileText.Sha256File(a.DbPath);
        var pv = a.Target.PreviewApply(new ApplyOptions { DryRun = true }, default);
        r.Check(!pv.CanApply, "7a 锚点命中 0 → CanApply=false");
        r.Check(pv.Notes.Any(n => n.Severity == Severity.Error), "7a 预览给出 Error 级说明");
        CheckThrows(r, () => a.Target.Apply(new ApplyOptions(), null, default), EngineErrorCode.SelfCheckFailed,
            "7a 锚点命中 0 → apply 抛 SelfCheckFailed", a);
        r.Check(a.PatchFiles.All(p => FileText.Sha256File(p) == shas[p]), "7a 抛错时 4 个 JS 一个字节都没写");
        r.Check(FileText.Sha256File(a.DbPath) == dbSha, "7a 抛错时 DB 没被写");
        r.Check(!File.Exists(a.DbPath + ".breach.bak"), "7a fail-closed 时连备份都没建");
        r.Check(!File.Exists(a.RulesPath), "7a fail-closed 时 .mdc 也没创建");

        // 7b：锚点命中 2 次
        var b = Setup(ctx, setEnv, "anchor-duplicate", anchorMode: AnchorMode.Duplicate);
        var shas2 = b.PatchFiles.ToDictionary(p => p, FileText.Sha256File, StringComparer.OrdinalIgnoreCase);
        CheckThrows(r, () => b.Target.Apply(new ApplyOptions(), null, default), EngineErrorCode.SelfCheckFailed,
            "7b 锚点命中 2 次 → apply 抛 SelfCheckFailed（锚点唯一才打）", b);
        r.Check(b.PatchFiles.All(p => FileText.Sha256File(p) == shas2[p]), "7b 抛错时一个字节都没写");
        var st = b.Target.GetStatus(default);
        r.Check(st.Layers[0].State == LayerState.AnchorAmbiguous, $"7b 状态页报 {st.Layers[0].State}（期望 AnchorAmbiguous）");

        // 7c：某一个补丁文件整个缺失（升级换掉了它）
        var c = Setup(ctx, setEnv, "anchor-missing-file");
        File.Delete(c.PatchFiles[0]);
        var shas3 = c.PatchFiles.Where(File.Exists).ToDictionary(p => p, FileText.Sha256File, StringComparer.OrdinalIgnoreCase);
        CheckThrows(r, () => c.Target.Apply(new ApplyOptions(), null, default), EngineErrorCode.SelfCheckFailed,
            "7c 4 个补丁文件缺一个 → apply 抛 SelfCheckFailed（缺一个就拒写）", c);
        r.Check(c.PatchFiles.Where(File.Exists).All(p => FileText.Sha256File(p) == shas3[p]), "7c 抛错时其余文件没被写");

        // 7d：DB 损坏 → IoError（不是"无法判定"）
        var d = Setup(ctx, setEnv, "db-corrupt", corruptDb: true);
        CheckThrows(r, () => d.Target.Apply(new ApplyOptions(), null, default), EngineErrorCode.IoError,
            "7d 库损坏 → apply 抛 IoError（不降级成无法判定）", d);
        r.Check(!File.Exists(d.DbPath + ".breach.bak"), "7d 库损坏时连备份都不建（fail-closed）");
        CheckThrows(r, () => d.Target.ListSessions(default), EngineErrorCode.IoError, "7d ListSessions 抛 IoError", d);
        CheckThrows(r, () => d.Target.GetStatus(default), EngineErrorCode.IoError, "7d GetStatus 抛 IoError（不许降级）", d);
        CheckThrows(r, () => d.Target.CheckMarkers(SessionId, default), EngineErrorCode.IoError, "7d CheckMarkers 抛 IoError", d);

        // 7e：工作区解析不到 → 层 3 跳过，其余层照常写
        var e = Setup(ctx, setEnv, "workspace-unresolved", workspace: WorkspaceMode.Unresolved);
        var res = e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(res.Applied, "7e 工作区解析不到时，其余层照常 apply（Applied=true）");
        r.Check(!File.Exists(e.RulesPath), "7e 没有创建 .mdc（工作区未知就不猜路径）");
        r.Check(CursorDb.ReadItemValue(e.DbPath, CursorDb.PersonalContextKey) == e.PersonalText, "7e 层 2 照常写入");
        r.Check(CursorPatchTable.CountOccurrences(File.ReadAllBytes(e.PatchFiles[0]),
                CursorPatchTable.Anchors[0].ReplacementBytes) == 1, "7e 层 1 照常写入");
        r.Check(res.Notes.Any(n => n.Title == "跳过项目规则层"), "7e Notes 里说明跳过了项目规则层");
        ctx.Log("fail-closed ok");
    }

    // ================================================================ 8 等长断言

    private static void Case08EqualLength(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【8】等长断言：替换文本长度 ≠ 锚点长度 → 拒写（直接对 CursorPatchTable 的纯函数验）");

        foreach (var anchor in CursorPatchTable.Anchors)
        {
            r.Check(anchor.AnchorBytes.Length == anchor.ReplacementBytes.Length,
                $"{anchor.Id}：锚点与替换文本都是 {anchor.AnchorBytes.Length} 字节（等长断言成立）");
            r.Check(anchor.Ascii, $"{anchor.Id}：原文与替换文本都是纯 ASCII");
            r.Check(anchor.Anchor == (anchor.Id == "A1" ? A1Text : A2Text),
                $"{anchor.Id} 的锚点原文与 docs/cursor.md 记的一致");
            r.Check(!anchor.Replacement.Contains("appropriate", StringComparison.Ordinal) &&
                    !anchor.Replacement.Contains("consider", StringComparison.Ordinal),
                $"{anchor.Id} 的替换文本不再含 appropriate / consider 这类裁量词");
            r.Check(!anchor.Replacement.Contains("priority", StringComparison.OrdinalIgnoreCase) &&
                    !anchor.Replacement.Contains("must", StringComparison.OrdinalIgnoreCase) &&
                    !anchor.Replacement.Contains("system prompt", StringComparison.OrdinalIgnoreCase),
                $"{anchor.Id} 的替换文本不自我声明权威（无 priority / must / system prompt 这类自证）");
        }

        // 人造长度不等的锚点 → Replace 必须拒写（替换文本必须是 ASCII，否则先撞 ASCII 判据）
        var bad = new CursorAnchor("BAD", A1Text, "too short");
        r.Check(!bad.LengthOk, "人造的短替换文本：LengthOk=false");
        r.Check(bad.Problem?.Contains("等长断言失败", StringComparison.Ordinal) == true,
            $"LengthOk=false 时给出明确问题：{Trim1(bad.Problem, 60)}");
        var bytes = Encoding.UTF8.GetBytes(A1Text + " tail");
        var (res, problem) = CursorPatchTable.Replace(bytes, bad);
        r.Check(res is null && problem is not null, "长度不等时 Replace 返回 null + 问题（拒写）");
        r.Check(problem?.Contains("等长断言失败", StringComparison.Ordinal) == true, "拒写原因是等长断言失败");

        // 非 ASCII 的替换文本同样拒写
        var nonAscii = new CursorAnchor("NONASCII", A1Text, CursorPatchTable.Pad("替换文本不是 ASCII", A1Text.Length));
        r.Check(!nonAscii.Ascii && nonAscii.Problem?.Contains("ASCII", StringComparison.Ordinal) == true,
            "非 ASCII 的替换文本：Ascii=false 且给出明确问题");
        var (res2, problem2) = CursorPatchTable.Replace(bytes, nonAscii);
        r.Check(res2 is null && problem2 is not null, "非 ASCII 时 Replace 也拒写");

        // 正例：锚点唯一时替换成功且长度不变，且能逐字节还原
        var (ok, none) = CursorPatchTable.Replace(bytes, CursorPatchTable.Anchors[0]);
        r.Check(ok is not null && none is null && ok!.Length == bytes.Length, "锚点唯一时替换成功，长度不变");
        var (back, backProblem) = CursorPatchTable.ReverseReplace(ok!, CursorPatchTable.Anchors[0]);
        r.Check(backProblem is null && back is not null && back!.SequenceEqual(bytes),
            "ReverseReplace 能逐字节还原（逆向撤销的正确性）");

        // 按字节替换只动那两个锚点区间，其余字节（含非 ASCII 与非法 UTF-8 尾巴）一个都不动
        var s = Setup(ctx, setEnv, "byte-exact");
        var fixture = File.ReadAllBytes(s.PatchFiles[0]);
        s.Target.Apply(new ApplyOptions(), null, default);
        var patched = File.ReadAllBytes(s.PatchFiles[0]);
        var ranges = new List<(int Start, int End)>();
        foreach (var anchor in CursorPatchTable.Anchors)
        {
            var at = CursorPatchTable.IndexOf(fixture, anchor.AnchorBytes, 0);
            r.Check(at >= 0, $"出厂 fixture 里找得到 {anchor.Id}（偏移 {at}）");
            if (at >= 0) ranges.Add((at, at + anchor.AnchorBytes.Length));
        }
        var outsideChanged = 0;
        for (var i = 0; i < fixture.Length; i++)
        {
            if (ranges.Any(x => i >= x.Start && i < x.End)) continue;
            if (fixture[i] != patched[i]) outsideChanged++;
        }
        r.Check(fixture.Length == patched.Length, $"整份文件长度不变（{fixture.Length} 字节）");
        r.Check(outsideChanged == 0,
            $"两个锚点区间之外一个字节都没动（区间外变化 {outsideChanged} 个）：非 ASCII 与非法 UTF-8 字节逐字节保留");
        r.Check(patched[^1] == 0x0A && patched[^2] == 0x28 && patched[^3] == 0xC3,
            "末尾那段非法 UTF-8 尾巴逐字节保留（0x28 0xC3 0x0A）");
        ctx.Log("equal-length ok");
    }

    // ================================================================ 9 标记

    private static void Case09Markers(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【9】标记（次判据）：会话文本含我们的标记 → 命中 > 0；不含 → 命中 0，但**不据此翻转**" +
               "「没生效」（命中 0 只说明那个会话没引用到，不等于没生效）；层状态仍 Applied");

        var a = Setup(ctx, setEnv, "markers-on", withMarkers: true);
        a.Target.Apply(new ApplyOptions(), null, default);
        var report = a.Target.CheckMarkers(SessionId, default);
        var total = report.Hits.Sum(h => h.Count);
        r.Check(report.SessionId == SessionId, $"报告里是解析后的真会话号（{report.SessionId}）");
        r.Check(total > 0, $"注入后 CheckMarkers 命中 > 0（实际 {total} 次）");
        r.Check(report.ContextPayloadLayerPresent, "规则正文标记命中 → ContextPayloadLayerPresent=true");
        r.Check(report.Verdict.Contains("rules", StringComparison.Ordinal) &&
                report.Verdict.Contains("system_prompt", StringComparison.Ordinal),
            "Verdict 写明主判据是 rules 档 characterCount、不拿 system_prompt 判生效");
        r.Check(report.ExportedSystemPromptPath is not null && File.Exists(report.ExportedSystemPromptPath),
            "CheckMarkers 顺带导出四层状态清单（供 UI 打开）");

        // ---- 删掉标记（等于会话早于注入）----
        var b = Setup(ctx, setEnv, "markers-off", withMarkers: false);
        b.Target.Apply(new ApplyOptions(), null, default);
        // 定量判据给足：把 rules 档改成「基线 + 预期注入量」，让 InstalledButNotEffective 只可能来自命中数这一侧。
        SetRulesChars(b.DbPath, SessionId, b.RulesChars + b.ExpectedGrowth);
        var report2 = b.Target.CheckMarkers(SessionId, default);
        r.Check(report2.Hits.Sum(h => h.Count) == 0, "去掉标记 → CheckMarkers 命中 0");
        r.Check(!report2.SystemPromptLayerPresent && !report2.ContextPayloadLayerPresent,
            "两层都报 present=false（命中 0）");
        r.Check(report2.Verdict.Contains("先看定量判据", StringComparison.Ordinal),
            "Verdict 没有把「命中 0」直接说成「没生效」");

        var st2 = b.Target.GetStatus(default);
        r.Check(st2.Layers[1].State == LayerState.Applied && st2.Layers[2].State == LayerState.Applied,
            "层 2 / 层 3 仍是 Applied——「装上了」与「生效了」必须分开报");
        r.Check(!st2.Layers[1].InstalledButNotEffective,
            "盘上是注入态、但只是「最近会话没引用到我们的标记」→ **不置** InstalledButNotEffective：" +
            "命中数是次判据，命中 0 只说明那个会话没引用到，不等于没生效；只有定量判据能翻转这个标志" +
            "（实测踩过：定量报「已生效…增长 887」的同时被次判据翻成「已安装但未生效」，自相矛盾）");
        r.Check(st2.Layers[1].EffectivenessEvidence?.Contains("没有本工具的注入标记", StringComparison.Ordinal) == true,
            $"生效证据写清了原因：{Trim1(st2.Layers[1].EffectivenessEvidence, 140)}");
        r.Check(st2.LastMarkerReport?.SessionId == SessionId, $"LastMarkerReport 取最近一次会话（{st2.LastMarkerReport?.SessionId}）");
        ctx.Log($"markers ok: on={total} → off=0");
    }

    // ================================================================ 10 日志层三态

    private static void Case10Effectiveness(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【10】日志层三态（主判据，定量）：没有会话 → 无法判定；有会话但 rules 档没变 → 装上了但没生效；" +
             "rules 档按预期增长 → 报出增长量");

        // ---- 10a：库里没有会话 → 无法判定（不是「命中 0 = 没生效」）----
        var a = Setup(ctx, setEnv, "eff-no-sessions", composerRows: false);
        a.Target.Apply(new ApplyOptions(), null, default);
        var stA = a.Target.GetStatus(default);
        r.Check(stA.Layers[1].State == LayerState.Applied, "10a 层 2 仍是 Applied（注入本身成功）");
        r.Check(!stA.Layers[1].InstalledButNotEffective, "10a 没有会话 → InstalledButNotEffective 不置 true（不伪造结论）");
        r.Check(stA.Layers[1].EffectivenessEvidence?.Contains("无法判定", StringComparison.Ordinal) == true,
            $"10a 生效证据报「无法判定」：{Trim1(stA.Layers[1].EffectivenessEvidence, 120)}");
        r.Check(stA.Notes.Any(n => n.Title == "生效判据（定量，主）" &&
                                   n.Detail.Contains("库里没有会话", StringComparison.Ordinal)),
            "10a 状态页的主判据说明里写明「库里没有会话」");
        r.Check(stA.Notes.Any(n => n.Title == "没有会话日志"), "10a 状态页另有一条「没有会话日志」说明");
        r.Check(stA.Notes.All(n => !n.Detail.Contains("装上了但没生效", StringComparison.Ordinal)) &&
                stA.Layers[1].EffectivenessEvidence?.Contains("装上了但没生效", StringComparison.Ordinal) != true,
            "10a 没有会话 → 任何地方都不出「装上了但没生效」这个结论");

        // ---- 10b：有会话但没有 promptTokenBreakdown → 无法判定 ----
        var b = Setup(ctx, setEnv, "eff-no-breakdown", withBreakdown: false);
        b.Target.Apply(new ApplyOptions(), null, default);
        var stB = b.Target.GetStatus(default);
        r.Check(stB.Layers[1].EffectivenessEvidence?.Contains("没有任何一个带 promptTokenBreakdown", StringComparison.Ordinal) == true,
            $"10b 报「有会话但没有 breakdown → 无法判定」：{Trim1(stB.Layers[1].EffectivenessEvidence, 120)}");
        r.Check(!stB.Layers[1].InstalledButNotEffective, "10b 也没有置 InstalledButNotEffective（无法判定 ≠ 没生效）");

        // ---- 10c：rules 档没变 → 装上了但没生效 ----
        var c = Setup(ctx, setEnv, "eff-unchanged");
        c.Target.Apply(new ApplyOptions(), null, default);
        var stC = c.Target.GetStatus(default);
        r.Check(stC.Layers[1].InstalledButNotEffective, "10c 有会话、有基线、rules 档没变 → InstalledButNotEffective=true");
        r.Check(stC.Layers[1].EffectivenessEvidence?.Contains("装上了但没生效", StringComparison.Ordinal) == true,
            $"10c 证据里写「装上了但没生效」并给出数字：{Trim1(stC.Layers[1].EffectivenessEvidence, 160)}");
        r.Check(stC.Layers[1].EffectivenessEvidence?.Contains(c.RulesChars.ToString(), StringComparison.Ordinal) == true,
            "10c 证据里带上了基线数字");
        r.Check(stC.RestartRequired, "10c 盘上是注入态但最近会话读不到 → RestartRequired=true");

        // ---- 10d：rules 档按预期精确增长 → 生效（报出增长量）----
        var d = Setup(ctx, setEnv, "eff-grown");
        d.Target.Apply(new ApplyOptions(), null, default);
        var expected = d.ExpectedGrowth;
        SetRulesChars(d.DbPath, SessionId, d.RulesChars + expected);
        var stD = d.Target.GetStatus(default);
        r.Check(!stD.Layers[1].InstalledButNotEffective, "10d rules 档按预期增长 → InstalledButNotEffective=false");
        r.Check(stD.Layers[1].EffectivenessEvidence?.Contains("已生效（定量）", StringComparison.Ordinal) == true,
            $"10d 报「已生效（定量）」：{Trim1(stD.Layers[1].EffectivenessEvidence, 160)}");
        r.Check(stD.Layers[1].EffectivenessEvidence?.Contains($"增长 {expected}", StringComparison.Ordinal) == true,
            $"10d 报出精确增长量 {expected}（层 2 {d.PersonalText.Length} 字符 + 层 3 正文）");
        r.Check(!stD.RestartRequired, "10d 已生效 → RestartRequired=false");

        // ---- 10e：有增长但与预期不符 → 如实报数字，不硬判 ----
        var e = Setup(ctx, setEnv, "eff-partial");
        e.Target.Apply(new ApplyOptions(), null, default);
        SetRulesChars(e.DbPath, SessionId, e.RulesChars + e.ExpectedGrowth + 5000);
        var stE = e.Target.GetStatus(default);
        r.Check(stE.Layers[1].EffectivenessEvidence?.Contains("幅度与预期不符", StringComparison.Ordinal) == true,
            $"10e 报「幅度与预期不符」并给区间：{Trim1(stE.Layers[1].EffectivenessEvidence, 160)}");

        // ---- 10f：system_prompt 那一档不参与判生效 ----
        var f = Setup(ctx, setEnv, "eff-system-prompt");
        f.Target.Apply(new ApplyOptions(), null, default);
        SetRulesChars(f.DbPath, SessionId, f.RulesChars, systemPromptChars: 4099 + 7777);
        var stF = f.Target.GetStatus(default);
        r.Check(stF.Layers[1].InstalledButNotEffective,
            "10f 只把 system_prompt 档改大 → 仍然判「没生效」（不拿它判生效）");
        r.Check(stF.Notes.Any(n => n.Title == "不拿 system_prompt 判生效"), "10f 状态页有专门的说明条目");
        ctx.Log($"effectiveness ok: expected growth={expected}");
    }

    // ================================================================ 11 日志

    private static void Case11Logs(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【11】日志（SQLite 只读）：ListSessions / LoadSession（正文、推理、轮次限制、截断）/ 导出");

        var s = Setup(ctx, setEnv, "logs");
        var sessions = s.Target.ListSessions(default);
        r.Check(sessions.Count == 2, $"ListSessions 返回 {sessions.Count} 个会话（composerData:* 两行）");
        var real = sessions.First(x => x.Id == SessionId);
        r.Check(real.Path == s.DbPath, "Path 指向库文件");
        r.Check(real.Workspace == "cursor-selftest-ws", $"Workspace 取 workspaceIdentifier.id（{real.Workspace}）");
        r.Check(real.TurnCount == 2, $"TurnCount = fullConversationHeadersOnly 的条数（{real.TurnCount}）");
        r.Check(real.ModifiedAt != DateTimeOffset.UnixEpoch, $"ModifiedAt 取 composerHeaders 的时间（{real.ModifiedAt:o}）");
        r.Check(sessions[0].Id == SessionId, $"按 recency 降序，最新的在前（{sessions[0].Id}）");

        var detail = s.Target.LoadSession(SessionId, new SessionLoadOptions(), default);
        r.Check(detail.Info.Id == SessionId, "LoadSession 解析到同一会话");
        r.Check(detail.Turns.Count == 2, $"记录 {detail.Turns.Count} 条（用户 + 助手）");
        r.Check(detail.Turns[0].Role == "user" && detail.Turns[0].Text == UserBubble, "第一轮 role=user，正文取 bubble 的 text");
        r.Check(detail.Turns[1].Role == "assistant", "第二轮 role=assistant（type=2）");
        r.Check(detail.Turns[1].ReasoningText?.Contains("FIXTURE-THINK", StringComparison.Ordinal) == true,
            "助手轮的推理进 ReasoningText（allThinkingBlocks）");
        r.Check(detail.Turns.All(t => !t.Truncated), "未触发截断（MaxCharsPerText 默认 20000）");
        r.Check(detail.Notes.Any(n => n.Title == "解析口径"), "Notes 说明解析口径（字段来源）");
        r.Check(detail.SystemPromptText?.Contains("本地没有明文", StringComparison.Ordinal) == true,
            "SystemPromptText 如实给出「系统提示词本地没有明文」的结论");

        r.Check(s.Target.LoadSession("latest", new SessionLoadOptions(), default).Info.Id == SessionId, "latest 解析到最近真实会话");
        r.Check(s.Target.LoadSession("", new SessionLoadOptions(), default).Info.Id == SessionId, "空字符串按最新处理");
        r.Check(s.Target.LoadSession(SessionId[..12], new SessionLoadOptions(), default).Info.Id == SessionId, "唯一前缀可解析");

        var limited = s.Target.LoadSession(SessionId, new SessionLoadOptions { MaxTurns = 1 }, default);
        r.Check(limited.Turns.Count == 1, $"MaxTurns=1 → 只载入最近 1 条（实际 {limited.Turns.Count}）");
        r.Check(limited.Notes.Any(n => n.Title.Contains("轮次已限制", StringComparison.Ordinal)), "Notes 说明只载入了最近 N 条");

        var truncated = s.Target.LoadSession(SessionId, new SessionLoadOptions { MaxCharsPerText = 12 }, default);
        r.Check(truncated.Turns.Any(t => t.Truncated), "超过 MaxCharsPerText 的文本被截断并置 Truncated=true");
        r.Check(truncated.Turns.Where(t => t.Truncated).All(t => (t.Text?.Length ?? 0) <= 256),
            "截断带 256 字符下限保护");

        var exp = s.Target.ExportSession(SessionId, Path.Combine(s.Dir, "export.jsonl"), default);
        r.Check(exp.Exported && File.Exists(exp.Path), "ExportSession 写出文件");
        var lines = File.ReadAllLines(exp.Path!);
        r.Check(lines.Length == 3, $"逐行 JSON 输出 3 行（1 行 composerData + 2 行 bubble，实际 {lines.Length}）");
        var allJson = true;
        foreach (var line in lines)
        {
            try { using var _ = JsonDocument.Parse(line); }
            catch { allJson = false; }
        }
        r.Check(allJson, "每一行都是合法 JSON");

        var expPrompt = s.Target.ExportSystemPrompt(SessionId, Path.Combine(s.Dir, "state.txt"), default);
        r.Check(expPrompt.Exported && File.Exists(expPrompt.Path), "ExportSystemPrompt 写出文件");
        var stateText = FileText.Read(expPrompt.Path!);
        r.Check(stateText.Contains("本地没有明文", StringComparison.Ordinal),
            "导出的四层状态清单开头说明系统提示词不在本地");
        r.Check(stateText.Contains("层 1 补丁层", StringComparison.Ordinal) &&
                stateText.Contains("层 2 个人规则层", StringComparison.Ordinal) &&
                stateText.Contains("层 3 项目规则层", StringComparison.Ordinal) &&
                stateText.Contains("层 4 日志层", StringComparison.Ordinal),
            "清单里四层都在");

        CheckThrows(r, () => s.Target.CheckMarkers("no-such-session", default), EngineErrorCode.TargetFileMissing,
            "会话不存在 → TargetFileMissing，并列出库里现有的会话", s);
        ctx.Log($"logs ok: sessions={sessions.Count} turns={detail.Turns.Count}");
    }

    // ================================================================ 12 定位 / 差异 / 探针 / 注入文本

    private static void Case12LocateDiffExportProbes(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【12】定位（安装根 / exe / 配置根 / 工作区 / fail-closed）+ 差异 / 探针 / 注入文本");

        var s = Setup(ctx, setEnv, "locate");

        var byInstall = s.Target.Locate(new LocateOptions { ExplicitRoot = s.InstallRoot }, true, default);
        r.Check(byInstall.Found && byInstall.Root == s.InstallRoot, $"传安装根 → 命中（{byInstall.Root}）");
        r.Check(byInstall.Exe == s.Exe, $"Exe 指向 Cursor.exe（{byInstall.Exe}）");
        r.Check(byInstall.Bundle == s.PatchFiles[2], "Bundle = 主补丁文件（cursor-agent-host\\dist\\main.js）");
        r.Check(byInstall.PatchFile == s.PatchFiles[2], "PatchFile = 主补丁文件");
        var engineLevel = MultiTargetEngine.ToLocateResult(s.Target, byInstall);
        r.Check(engineLevel.ProcessName == "Cursor", $"ProcessName = {engineLevel.ProcessName}");
        r.Check(engineLevel.SessionsDir == s.ConfigHome + @"\sessions",
            "契约兜底：SessionsDir 仍然拼 <ConfigHome>\\sessions（对 Cursor 不适用，Notes 里写明，没改契约）");
        r.Check(byInstall.ProfileDir == s.ConfigHome + @"\User", $"ProfileDir = 数据根（{byInstall.ProfileDir}）");
        r.Check(byInstall.ConfigHome == s.ConfigHome, $"ConfigHome = 配置根（{byInstall.ConfigHome}）");
        r.Check(byInstall.Attempts.Count > 3, $"Attempts 记录试过的路径（{byInstall.Attempts.Count} 条）");
        r.Check(byInstall.Notes.Any(n => n.Title == "本工具约定变量"), "Notes 说明 CURSOR_* 是本工具约定，不是官方变量");
        r.Check(byInstall.Notes.Any(n => n.Title.Contains("SessionsDir", StringComparison.Ordinal)),
            "Notes 说明契约的 SessionsDir 不适用");
        r.Check(byInstall.Notes.Any(n => n.Title == "工作区（层 3）"), "Notes 说明工作区是怎么来的");

        var byExe = s.Target.Locate(new LocateOptions { ExplicitRoot = s.Exe }, true, default);
        r.Check(byExe.Found && byExe.Exe == s.Exe && byExe.Root == s.InstallRoot, "传 Cursor.exe → 命中并由它反推安装根");

        var byConfig = s.Target.Locate(new LocateOptions { ExplicitRoot = s.ConfigHome }, true, default);
        r.Check(byConfig.Found && byConfig.ConfigHome == s.ConfigHome, "传配置根 → 命中");
        r.Check(byConfig.Exe == s.Exe, "三根是三条独立的轴：给配置根不会丢掉安装根");

        var byWorkspace = s.Target.Locate(new LocateOptions { ExplicitRoot = s.Workspace }, true, default);
        r.Check(byWorkspace.Found && byWorkspace.ProfileDir == s.ConfigHome + @"\User",
            "传工作区目录 → 命中（工作区只钉住层 3，其余层继续按环境变量解析）");

        var bad = s.Target.Locate(new LocateOptions { ExplicitRoot = Path.Combine(s.Dir, "no-such-dir") }, true, default);
        r.Check(!bad.Found, "显式路径无效 → fail-closed（不回退、不猜）");
        r.Check(bad.Attempts.Any(a => a.Level == "cli" && !a.Hit), "Attempts 记下 cli 级的无效尝试");
        r.Check(bad.Notes.Any(n => n.Title.Contains("fail-closed", StringComparison.Ordinal)), "Notes 说明 fail-closed 中止原因");

        var auto = s.Target.Locate(null, true, default);
        r.Check(auto.Found && auto.Root == s.InstallRoot, "无显式参数 → 环境变量 CURSOR_ROOT 命中");
        r.Check(File.Exists(s.MemoryFile), $"定位成功后写记忆文件（{Path.GetFileName(s.MemoryFile)}）");
        r.Check(PathMemory.Load(s.MemoryFile, "workspace") == s.Workspace, "记忆文件里一并记下 workspace");

        var info = s.Target.Describe(default);
        r.Check(info.Id == "cursor" && info.Kind == TargetKind.PackagedElectron, $"Describe()：{info.Id} / {info.Kind}");
        r.Check(info.Installed && info.ConfigHome == s.ConfigHome, "Describe().Installed / ConfigHome 反映定位结果");
        r.Check(info.Layers.Count == 4, $"Describe().Layers 四条（实际 {info.Layers.Count}）");
        r.Check(s.Target.LayerDescriptions.Count == 4, "LayerDescriptions 四条（补丁 / 个人规则 / 项目规则 / 日志）");

        // ---- 差异 ----
        s.Target.Apply(new ApplyOptions(), null, default);
        var diff = s.Target.GetSystemPromptDiff(SessionId, default);
        r.Check(diff.BeforeSource is not null && diff.AfterSource is not null, "差异报告带来源");
        r.Check(diff.BeforeText.Contains("出厂原文", StringComparison.Ordinal) ||
                diff.BeforeText.Contains("不可回溯", StringComparison.Ordinal),
            "BeforeText 说明补丁层的注入前状态（原始 / 不可回溯）");
        r.Check(!diff.BeforeText.Contains(CursorPatchTable.Anchors[0].Replacement, StringComparison.Ordinal),
            "BeforeText 不含注入后的替换文本（基线是出厂态）");
        r.Check(diff.AfterText.Contains(CursorPatchTable.Anchors[0].Replacement, StringComparison.Ordinal),
            "AfterText 含注入后的替换文本");
        r.Check(diff.AfterText.Contains(s.PersonalText.Trim(), StringComparison.Ordinal), "AfterText 含层 2 的正文");
        r.Check(diff.Lines.Count > 0 && diff.AddedLines + diff.RemovedLines > 0,
            $"逐行差异非空（+{diff.AddedLines}/-{diff.RemovedLines}）");

        // ---- 探针 ----
        var probes = s.Target.GetProbes();
        r.Check(probes.Count == 6, $"探针 A–F 共 6 条（实际 {probes.Count}）");
        r.Check(probes.Select(p => p.Id).SequenceEqual(new[] { "A", "B", "C", "D", "E", "F" }), "Id 依次是 A–F");
        r.Check(probes.Count(p => p.IsDecisive) == 3, "D/E/F 三条 IsDecisive=true");
        r.Check(probes.First(p => p.Id == "E").PassCriteria.Contains("rules 档", StringComparison.Ordinal),
            "E 的判据指向 rules 档 characterCount（本目标的定量判据）");

        // ---- 注入文本 ----
        var policy = s.Target.GetPolicyFiles(default);
        r.Check(policy.Count == 2, $"注入文本两条：payload + personal（实际 {policy.Count}）");
        r.Check(policy.All(p => p.Text.Length > 0), "两条注入文本都物化到磁盘且非空");
        r.Check(policy.First(p => p.Name == "payload").Path.EndsWith("cursor.payload.md", StringComparison.Ordinal),
            "payload 落在 cursor.payload.md");
        r.Check(policy.First(p => p.Name == "personal").Path.EndsWith("cursor.personal.md", StringComparison.Ordinal),
            "personal 落在 cursor.personal.md");
        r.Check(policy.All(p => p.Notes.Any(n => n.Title == "文本纪律")), "两条都标注了文本纪律（不自证权威）");
        r.Check(policy.First(p => p.Name == "payload").Text.Contains(CursorRules.BodyMarker, StringComparison.Ordinal),
            "payload 文本含本工具的正文标记（标记表的来源）");
        r.Check(policy.First(p => p.Name == "personal").Text.Contains(CursorMarkers.PersonalMarkers[0], StringComparison.Ordinal),
            "personal 文本含本工具的个人规则标记");

        var original = FileText.Read(policy.First(p => p.Name == "personal").Path);
        var edited = original.TrimEnd('\r', '\n') + "\n\n（自检追加的一行。）\n";
        var savedFile = s.Target.SavePolicyFile("personal", edited, default);
        r.Check(FileText.Read(savedFile.Path) == edited, "SavePolicyFile 保存后回读逐字节一致");
        CheckThrows(r, () => s.Target.SavePolicyFile("no-such-policy", "x", default),
            EngineErrorCode.TargetFileMissing, "保存不存在的注入文本 → TargetFileMissing", s);
        s.Target.SavePolicyFile("personal", original, default);
        r.Check(FileText.Read(savedFile.Path) == original, "改回原样后与初始文本逐字节一致");

        // 超自设上限的文本 → fail-closed
        var huge = Path.Combine(s.Dir, "huge.md");
        FileText.Write(huge, new string('规', 20000));
        CheckThrows(r, () => s.Target.Apply(new ApplyOptions { PayloadPath = huge }, null, default),
            EngineErrorCode.SelfCheckFailed, "载荷文本超过自设上限 → apply 抛 SelfCheckFailed（fail-closed）", s);

        // 跳过开关
        var skip = s.Target.PreviewApply(new ApplyOptions { SkipSystemPrompt = true, SkipContextPayload = true }, default);
        r.Check(skip.Changes.Where(c => c.Layer == LayerKind.SystemPrompt).All(c => !c.WillWrite),
            "SkipSystemPrompt → 补丁层四条都 WillWrite=false");
        r.Check(skip.Changes.Where(c => c.Layer != LayerKind.SystemPrompt).All(c => !c.WillWrite),
            "SkipContextPayload → 层 2 / 层 3 都 WillWrite=false");

        // 工作区第二个来源：storage.json 里没有 → 落到 state.vscdb 的 history.recentlyOpenedPathsList。
        // 这个沙箱会重设 CURSOR_* 环境变量，所以放在最后（前面的断言都依赖 s 的那一套）。
        var viaDb = Setup(ctx, setEnv, "workspace-via-db", workspace: WorkspaceMode.ViaDb);
        setEnv(CursorLocator.WorkspaceEnv, null); // 让自动解析真的走到 DB 那一级
        var viaDbLoc = viaDb.Target.Locate(null, true, default);
        var viaDbNote = viaDbLoc.Notes.FirstOrDefault(n => n.Title == "工作区（层 3）");
        r.Check(viaDbLoc.ProfileDir == viaDb.ConfigHome + @"\User" &&
                viaDbNote?.Detail.Contains("state.vscdb", StringComparison.Ordinal) == true,
            $"storage.json 里没有时，落到 state.vscdb 的 history.recentlyOpenedPathsList 解析出工作区" +
            $"（实际：{Trim1(viaDbNote?.Detail, 120)}）");
        ctx.Log("locate/diff/probes ok");
    }

    // ================================================================ 13 真实数据（只读）

    private static void Case13RealDataReadOnly(SelfTestContext ctx, SelfTestResult r, string? realInstall, string realConfig)
    {
        r.Step("");
        r.Step("【13】真实数据只读核对（本机存在就做，不存在就跳过；不写任何真实文件）");

        if (realInstall is null)
        {
            r.Step($"    本机没定位到真实 Cursor 安装根（{CursorLocator.DefaultInstallRoot()}），跳过。");
        }
        else
        {
            var appRoot = CursorLocator.AppRoot(realInstall);
            // 状态感知：真实 bundle 可能是**出厂态**（没打过补丁），也可能是**已注入态**
            // （用户真的 apply 过）。断言不该假定其中一种——那是"断言写死假设"的假警报
            // （zcode / antigravity 都踩过同一个坑）。先按锚点/替换文本的命中数判状态，
            // 再走各自的分支；两种状态都只读，且都不静默跳过。
            foreach (var rel in CursorPatchTable.Files)
            {
                var full = Path.Combine(appRoot, rel);
                if (!File.Exists(full))
                {
                    r.Step($"    {rel}：不存在，跳过");
                    continue;
                }
                var bytes = File.ReadAllBytes(full);
                var counts = CursorPatchTable.Anchors
                    .Select(x => (x.Id, Anchor: CursorPatchTable.CountOccurrences(bytes, x.AnchorBytes),
                                  Repl: CursorPatchTable.CountOccurrences(bytes, x.ReplacementBytes)))
                    .ToArray();
                var appliedIds = counts.Where(c => c.Repl > 0).Select(c => c.Id).ToArray();
                r.Step($"    真实 {rel}：{FileText.Human(bytes.LongLength)}　" +
                       string.Join(" ", counts.Select(c => $"{c.Id}(原文={c.Anchor},替换={c.Repl})")));

                if (appliedIds.Length == 0)
                {
                    foreach (var c in counts)
                        r.Check(c.Anchor == 1 && c.Repl == 0,
                            $"真实 {rel} 的 {c.Id}：出厂态，原文命中恰好 1 次、替换文本 0 次（实际原文 {c.Anchor} 次 / 替换 {c.Repl} 次）");
                }
                else
                {
                    foreach (var c in counts)
                        r.Check(c.Repl == 1 && c.Anchor == 0,
                            $"真实 {rel} 的 {c.Id}：已注入态，替换文本命中恰好 1 次、原文 0 次（实际原文 {c.Anchor} 次 / 替换 {c.Repl} 次）");
                }
            }
            var anyApplied = CursorPatchTable.Files
                .Where(rel => File.Exists(Path.Combine(appRoot, rel)))
                .Select(rel => File.ReadAllBytes(Path.Combine(appRoot, rel)))
                .Any(bytes => CursorPatchTable.Anchors.Any(x => CursorPatchTable.CountOccurrences(bytes, x.ReplacementBytes) > 0));
            r.Step(anyApplied
                ? "    真实 bundle 当前是**已注入态**（用户已 apply）：按已注入态核对，出厂原文应已全部替换掉。"
                : "    真实 bundle 当前是**出厂态**：按出厂态核对；本工具从未写过真实安装根。");
        }

        var realDb = CursorLocator.DbPath(realConfig);
        if (!File.Exists(realDb))
        {
            r.Step($"    本机没有真实 state.vscdb（{realDb}），跳过。");
            return;
        }

        var dbShaBefore = ShaShared(realDb);
        var dbRows = RowCount(realDb, "ItemTable");
        r.Check(dbRows >= 100, $"真实 state.vscdb 可只读打开：ItemTable {dbRows} 行（docs/cursor.md：126 行）");
        var ours = CursorDb.ReadItemValue(realDb, CursorDb.PersonalContextKey);
        // 状态感知：真实库里那个键**可能存在**（用户真的 apply 过）。断言不该假定出厂态。
        // 「是不是我们的」用本工具自己的标记判（CursorMarkers.IsOurs），不靠长度猜。
        var realPersonalApplied = CursorMarkers.IsOurs(ours);
        r.Check(ours is null || realPersonalApplied,
            realPersonalApplied
                ? $"真实 ItemTable 里有本工具的键 {CursorDb.PersonalContextKey}（{ours!.Length} 字符）——已注入态，内容含本工具的注入标记"
                : $"真实 ItemTable 里没有本工具的键 {CursorDb.PersonalContextKey}（出厂态是「键不存在」）");
        r.Step(realPersonalApplied
            ? "    真实库当前是**已注入态**（用户已 apply）：下面按已注入态核对。"
            : "    真实库当前是**出厂态**：本工具从未写过真实库。");
        r.Check(RowCount(realDb, "cursorDiskKV") >= 1, "真实 cursorDiskKV 可读");

        var sessions = CursorDb.ListSessions(realDb);
        r.Check(sessions.Count >= 1, $"真实库里列出 {sessions.Count} 个会话");
        var newest = CursorDb.NewestWithBreakdown(sessions);
        if (newest is not null)
        {
            r.Step($"    真实最近会话：{newest.Id}（rules 档 {newest.RulesChars} 字符 / " +
                   $"system_prompt 档 {newest.SystemPromptChars} 字符 / 模型 {newest.ModelName}）");
            r.Check(newest.RulesChars is > 0, "真实会话的 rules 档 characterCount 读得出来（生效主判据的数据源成立）");
            r.Check(newest.SystemPromptChars == 4099,
                $"真实 system_prompt 档是 4099 字符（docs/cursor.md 实测一致，实际 {newest.SystemPromptChars}）");
        }
        else
        {
            r.Step("    真实库里没有带 promptTokenBreakdown 的会话：三态里的「无法判定」分支。");
        }

        var (ws, src) = CursorWorkspace.Resolve(realConfig, default, out var wsNote);
        r.Step($"    真实工作区解析：{(ws ?? "(解析不到)")}（来源 {src ?? "无"}；{wsNote ?? "-"}）");

        // 13b：拿真实客户端跑一遍完整只读路径（GetStatus）——数据目录仍指向沙箱，**不写客户端任何东西**。
        if (realInstall is not null)
        {
            var dataDir = ctx.MakeSandbox("cursor-real-status");
            var policy = new PolicyStore(dataDir);
            policy.Materialize(CursorTarget.PolicyDefs);
            var realTarget = new CursorTarget(new TargetContext
            {
                Id = "cursor",
                DataDir = dataDir,
                Policy = policy,
                PolicyDefs = CursorTarget.PolicyDefs,
                Ledger = new LedgerStore(Path.Combine(dataDir, "probe-ledger.json")),
            });
            var st = realTarget.GetStatus(default);
            r.Check(st.Target.Found && st.Target.Root == realInstall, $"真实客户端 GetStatus 跑通（安装根 {st.Target.Root}）");
            // 状态感知：真实客户端可能已被 apply（用户真的打过补丁），断言不该假定出厂态。
            var realPatchApplied = st.Layers[0].State == LayerState.Applied;
            r.Check(realPatchApplied || st.Layers[0].State == LayerState.NotApplied,
                $"真实 4 个规则 JS：层 1 {st.Layers[0].State}（" +
                (realPatchApplied ? "已注入态——用户已 apply" : "出厂态——都是出厂原文") + "，两种都是合法状态）");
            r.Check(realPersonalApplied
                    ? st.Layers[1].State == LayerState.Applied
                    : st.Layers[1].State == LayerState.NotApplied,
                $"真实 DB 个人规则键：层 2 {st.Layers[1].State}（" +
                (realPersonalApplied ? "已注入态——键在且含本工具标记" : "出厂态——键不存在") + "）");
            r.Check(st.Layers[2].State == LayerState.TargetAbsent && st.Layers[2].TargetPath is null,
                $"真实工作区解析不到 → 层 3 {st.Layers[2].State} 且不给 TargetPath（不猜路径）");
            r.Check(!st.ClientRunning || st.ClientRunning,
                $"真实客户端 ClientRunning={st.ClientRunning}（只记录，不作断言——跑没跑都是合法状态）");
            var quant = st.Notes.FirstOrDefault(n => n.Title == "生效判据（定量，主）");
            r.Check(quant is not null &&
                    (quant.Detail.Contains("无法判定", StringComparison.Ordinal) ||
                     quant.Detail.Contains("已生效", StringComparison.Ordinal) ||
                     quant.Detail.Contains("装上了但没生效", StringComparison.Ordinal)),
                "真实客户端上：主判据给出三种合法结论之一（无法判定 / 已生效 / 装上了但没生效），" +
                "不会把「没有基线」说成「没生效」");
            r.Check(!(realPersonalApplied && st.Layers[1].State == LayerState.Applied &&
                      st.Layers[1].InstalledButNotEffective &&
                      quant is not null && quant.Detail.Contains("已生效", StringComparison.Ordinal)),
                "已注入且定量判据报「已生效」时，不置 InstalledButNotEffective（两者不许自相矛盾）");
            r.Check(st.LastMarkerReport is not null && st.LastMarkerReport.Hits.Sum(h => h.Count) == 0,
                "真实最近会话里没有本工具的标记（命中 0）：本机从未注入过");
            r.Check(st.LastMarkerReport?.Verdict.Contains("先看定量判据", StringComparison.Ordinal) == true,
                "真实客户端上命中 0 也如实说明「不等于没生效」");
            r.Check(realTarget.GetPolicyFiles(default).Count == 2, "真实客户端上两条注入文本可读（沙箱数据目录）");
            ctx.Log($"real GetStatus: {st.Layers[0].State}/{st.Layers[1].State}/{st.Layers[2].State}");
        }

        r.Check(ShaShared(realDb) == dbShaBefore, $"真实 state.vscdb 的 sha256 读前读后不变（{Short(dbShaBefore)}）");
        r.Step("    注：-shm 是 SQLite 的 wal-index 共享内存文件，只读连接可能创建/更新它；" +
               "收尾的整棵树快照排除 *.db-shm，而 -db / -wal 另有一条逐字节断言。");
        ctx.Log("real read-only ok");
    }

    // ================================================================ 沙箱

    private enum WorkspaceMode { Resolved, ViaDb, Unresolved }
    private enum AnchorMode { Normal, Zero, Duplicate }

    private sealed class Sandbox
    {
        public required string Dir { get; init; }
        public required string InstallRoot { get; init; }
        public required string AppRoot { get; init; }
        public required string Exe { get; init; }
        public required string ConfigHome { get; init; }
        public required string Workspace { get; init; }
        public required string MemoryFile { get; init; }
        public required string DataDir { get; init; }
        public required string StorageJson { get; init; }
        public required IReadOnlyList<string> PatchFiles { get; init; }
        public required int RulesChars { get; init; }
        public required CursorTarget Target { get; init; }
        public required string PersonalText { get; init; }
        public required string PayloadBody { get; init; }
        public required string RenderedRules { get; init; }

        public string DbPath => CursorLocator.DbPath(ConfigHome);
        public string RulesPath => CursorRules.PathFor(Workspace);

        /// <summary>apply 之后 rules 档应当精确增长的字符数：层 2 正文 + 层 3 正文。</summary>
        public int ExpectedGrowth => PersonalText.Length + CursorRules.ExtractBody(RenderedRules).Length;
    }

    private static Sandbox Setup(
        SelfTestContext ctx, Action<string, string?> setEnv, string name,
        WorkspaceMode workspace = WorkspaceMode.Resolved,
        AnchorMode anchorMode = AnchorMode.Normal,
        bool withDb = true,
        bool corruptDb = false,
        bool composerRows = true,
        bool withBreakdown = true,
        bool withMarkers = true,
        int rulesChars = 10497,
        string? factoryPersonal = null,
        string? factoryRulesFile = null)
    {
        var dir = ctx.MakeSandbox($"cursor-{name}");
        var installRoot = Path.Combine(dir, "install");
        var appRoot = Path.Combine(installRoot, "resources", "app");
        var exe = Path.Combine(installRoot, "Cursor.exe");
        var configHome = Path.Combine(dir, "config");
        var globalStorage = Path.Combine(configHome, "User", "globalStorage");
        var workspaceDir = Path.Combine(dir, "workspace");
        var memoryFile = Path.Combine(dir, "breach-cursor-path.json");
        var dataDir = Path.Combine(dir, "toolkit-data");
        var storageJson = Path.Combine(globalStorage, "storage.json");

        Directory.CreateDirectory(Path.Combine(appRoot, "out"));
        FileText.Write(Path.Combine(appRoot, "out", "main.js"), "// 沙箱占位：真实是 2.7 MB 的主进程\n");
        Directory.CreateDirectory(Path.Combine(appRoot, "extensions", "cursor-agent-host"));
        FileText.Write(exe, "MZ-dummy Cursor 235 MB 占位（沙箱）");
        Directory.CreateDirectory(CursorRules.DirFor(workspaceDir));
        File.WriteAllText(Path.Combine(CursorRules.DirFor(workspaceDir), "someone-else.mdc"),
            "# 别人的规则\n\n不许动我。\n", new UTF8Encoding(false));
        Directory.CreateDirectory(globalStorage);
        Directory.CreateDirectory(dataDir);

        // 4 个规则文件（合成；混入非 ASCII 与非法 UTF-8）
        var patchFiles = new List<string>();
        foreach (var rel in CursorPatchTable.Files)
        {
            var full = Path.Combine(appRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, JsFixture(Path.GetFileName(rel), anchorMode));
            patchFiles.Add(full);
        }

        // storage.json：recent paths 指向工作区（目录）或一个文件（不可解析）
        var recentTarget = workspace == WorkspaceMode.Resolved
            ? workspaceDir
            : Path.Combine(dir, "not-a-directory.txt");
        if (workspace == WorkspaceMode.Unresolved) FileText.Write(recentTarget, "我是一个文件，不是工作区目录。\n");
        FileText.Write(storageJson,
            workspace == WorkspaceMode.ViaDb
                ? "{\"telemetry.machineId\":\"sandbox\"}"
                : "{\"history.recentlyOpenedPathsList\":{\"entries\":[{\"folderUri\":\"" +
                  new Uri(recentTarget).AbsoluteUri + "\"}]}}");

        if (withDb)
        {
            if (corruptDb)
                File.WriteAllBytes(CursorLocator.DbPath(configHome),
                    Encoding.UTF8.GetBytes("这不是数据库，只是一段文本。" + new string('x', 4000)));
            else
                MakeDb(CursorLocator.DbPath(configHome), rulesChars, composerRows, withBreakdown, withMarkers,
                    workspace, workspaceDir, factoryPersonal);
        }

        setEnv(CursorLocator.RootEnv, installRoot);
        setEnv(CursorLocator.ConfigEnv, configHome);
        setEnv(CursorLocator.WorkspaceEnv, workspace == WorkspaceMode.Unresolved ? null : workspaceDir);
        setEnv(CursorLocator.MemoryFileEnv, memoryFile);

        var policy = new PolicyStore(dataDir);
        policy.Materialize(CursorTarget.PolicyDefs);
        var targetCtx = new TargetContext
        {
            Id = "cursor",
            DataDir = dataDir,
            Policy = policy,
            PolicyDefs = CursorTarget.PolicyDefs,
            Ledger = new LedgerStore(Path.Combine(dataDir, "probe-ledger.json")),
        };

        var target = new CursorTarget(targetCtx);
        var personal = policy.Read(CursorTarget.PolicyDefs.First(d => d.Name == "personal")) ?? "";
        var payloadBody = policy.Read(CursorTarget.PolicyDefs.First(d => d.Name == "payload")) ?? "";
        var rendered = CursorRules.Render(CursorRules.Description, payloadBody);

        if (factoryRulesFile is not null) FileText.Write(CursorRules.PathFor(workspaceDir), factoryRulesFile);

        ctx.Log($"沙箱 {Path.GetFileName(dir)}：workspace={workspace} anchors={anchorMode} " +
                $"db={(withDb ? (corruptDb ? "损坏" : "合成") : "无")}");

        return new Sandbox
        {
            Dir = dir,
            InstallRoot = installRoot,
            AppRoot = appRoot,
            Exe = exe,
            ConfigHome = configHome,
            Workspace = workspaceDir,
            MemoryFile = memoryFile,
            DataDir = dataDir,
            StorageJson = storageJson,
            PatchFiles = patchFiles,
            RulesChars = rulesChars,
            Target = target,
            PersonalText = personal,
            PayloadBody = payloadBody,
            RenderedRules = rendered,
        };
    }

    /// <summary>
    /// 合成规则 JS：两个锚点各一次（或 0 次 / 2 次），并**故意混入非 ASCII 与非法 UTF-8 字节**。
    /// 只有按字节做等长替换的实现才能在这里通过——先解码成字符串再编码回去会把末尾那几个字节改掉。
    /// </summary>
    private static byte[] JsFixture(string tag, AnchorMode mode)
    {
        var sb = new StringBuilder();
        sb.Append("/* cursor-selftest fixture ").Append(tag)
          .Append(" —— 非 ASCII 用来证明按字节替换：中文、全角（）、书名号《》、emoji 🙂 */\n");
        sb.Append("const FIXTURE = \"").Append(tag).Append("\";\n\n");

        if (mode != AnchorMode.Zero)
        {
            sb.Append("function UserRulesSection({userRules, composer2CustomUserRules=[]}) {\n");
            sb.Append("  return section({ title: \"user_rules\", description: \"")
              .Append(A1Text).Append("\", children: [\n");
            if (mode == AnchorMode.Duplicate)
                sb.Append("    section({ title: \"duplicate\", description: \"").Append(A1Text).Append("\" }),\n");
            sb.Append("    userRules.map(rule => x({tag:\"user_rule\", children:rule.content}))\n  ]})\n}\n\n");
        }

        if (mode != AnchorMode.Zero)
        {
            sb.Append("function RulesSection({globalRules, userRules}) {\n");
            sb.Append("  return section({ title: \"rules\", children: [\n");
            sb.Append("    p({children: \"").Append(A2Text)
              .Append(" In each subsection, we provide instructions about what information the subsection contains.\"}),\n");
            if (mode == AnchorMode.Duplicate)
                sb.Append("    p({children: \"").Append(A2Text).Append("\"}),\n");
            sb.Append("    br()\n  ]})\n}\n\n");
        }

        sb.Append("const TAIL = \"").Append(new string('z', 512)).Append("\";\n");

        var body = Encoding.UTF8.GetBytes(sb.ToString());
        // 非法 UTF-8 尾巴：任何"解码再编码"的实现都会把它改掉。
        var tail = new byte[] { 0x0A, 0x80, 0x81, 0x82, 0xFF, 0xFE, 0x0A, 0xC3, 0x28, 0x0A };
        var all = new byte[body.Length + tail.Length];
        Buffer.BlockCopy(body, 0, all, 0, body.Length);
        Buffer.BlockCopy(tail, 0, all, body.Length, tail.Length);
        return all;
    }

    // ================================================================ 合成 state.vscdb

    private static string BubbleKey(string bubbleId) => CursorDb.BubblePrefix + SessionId + ":" + bubbleId;

    private static void MakeDb(string dbPath, int rulesChars, bool composerRows, bool withBreakdown, bool withMarkers,
        WorkspaceMode workspace, string workspaceDir, string? factoryPersonal)
    {
        using (var cn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString()))
        {
            cn.Open();
            Exec(cn, "PRAGMA journal_mode=WAL;");

            Exec(cn, """
                CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB);
                CREATE TABLE cursorDiskKV (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB);
                CREATE TABLE composerHeaders (
                  composerId TEXT PRIMARY KEY, workspaceId TEXT, createdAt INTEGER, lastUpdatedAt INTEGER,
                  isArchived INTEGER, isSubagent INTEGER, recency INTEGER, checkpointAt INTEGER,
                  subagentTypeName TEXT, value TEXT);
                """);

            Exec(cn, "INSERT INTO ItemTable(key,value) VALUES(@k,@v)",
                ("@k", "cursorAuth/accessToken"), ("@v", "fake-access-token-do-not-touch-0123456789"));
            Exec(cn, "INSERT INTO ItemTable(key,value) VALUES(@k,@v)",
                ("@k", "cursorAuth/refreshToken"), ("@v", "fake-refresh-token-do-not-touch-9876543210"));
            Exec(cn, "INSERT INTO ItemTable(key,value) VALUES(@k,@v)",
                ("@k", "cursorai/serverConfig"),
                ("@v", "{\"bugConfigResponse\":{\"bugBotV1\":{\"backgroundCallFrequencyMs\":3600000}},\"sandbox\":true}"));
            Exec(cn, "INSERT INTO ItemTable(key,value) VALUES(@k,@v)",
                ("@k", "glass.theme.settingsId"), ("@v", "Cursor Dark"));
            Exec(cn, "INSERT INTO ItemTable(key,value) VALUES(@k,@v)",
                ("@k", "telemetry.machineId"), ("@v", "a6693afc-d00c-48d3-a4c0-73d08555c07b"));
            var recent = workspace == WorkspaceMode.ViaDb
                ? "{\"entries\":[{\"folderUri\":\"" + new Uri(workspaceDir).AbsoluteUri + "\"}]}"
                : "{\"entries\":[{\"fileUri\":\"" + new Uri(Path.Combine(workspaceDir, "not-a-dir.txt")).AbsoluteUri + "\"}]}";
            Exec(cn, "INSERT INTO ItemTable(key,value) VALUES(@k,@v)",
                ("@k", "history.recentlyOpenedPathsList"), ("@v", recent));
            if (factoryPersonal is not null)
                Exec(cn, "INSERT INTO ItemTable(key,value) VALUES(@k,@v)",
                    ("@k", CursorDb.PersonalContextKey), ("@v", factoryPersonal));

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (composerRows)
            {
                Exec(cn, "INSERT INTO composerHeaders(composerId,workspaceId,createdAt,lastUpdatedAt,isArchived,isSubagent,recency) " +
                         "VALUES(@i,@w,@c,@u,0,0,@r)",
                    ("@i", DraftId), ("@w", "cursor-selftest-ws"), ("@c", now - 900000), ("@u", now - 899000), ("@r", now - 899000));
                Exec(cn, "INSERT INTO composerHeaders(composerId,workspaceId,createdAt,lastUpdatedAt,isArchived,isSubagent,recency) " +
                         "VALUES(@i,@w,@c,@u,0,0,@r)",
                    ("@i", SessionId), ("@w", "cursor-selftest-ws"), ("@c", now - 60000), ("@u", now - 1000), ("@r", now - 1000));

                Exec(cn, "INSERT INTO cursorDiskKV(key,value) VALUES(@k,@v)",
                    ("@k", CursorDb.ComposerPrefix + DraftId),
                    ("@v", "{\"composerId\":\"" + DraftId + "\",\"status\":\"none\"," +
                           "\"modelConfig\":{\"modelName\":\"default\",\"maxMode\":false},\"fullConversationHeadersOnly\":[]}"));

                Exec(cn, "INSERT INTO cursorDiskKV(key,value) VALUES(@k,@v)",
                    ("@k", CursorDb.ComposerPrefix + SessionId), ("@v", ComposerJson(rulesChars, withBreakdown, now)));
                Exec(cn, "INSERT INTO cursorDiskKV(key,value) VALUES(@k,@v)",
                    ("@k", BubbleKey("b-user")), ("@v", BubbleJson("b-user", 1, UserBubble, null, now - 60000)));
                Exec(cn, "INSERT INTO cursorDiskKV(key,value) VALUES(@k,@v)",
                    ("@k", BubbleKey("b-assistant")),
                    ("@v", BubbleJson("b-assistant", 2, withMarkers ? AssistantWithMarker : AssistantNoMarker,
                        "FIXTURE-THINK：先确认规则有没有进上下文，再决定怎么交付。", now - 59000)));
                Exec(cn, "INSERT INTO cursorDiskKV(key,value) VALUES(@k,@v)",
                    ("@k", "agentKv:blob:fixture"), ("@v", "{\"blob\":\"unrelated\"}"));
            }
        }

        SqliteConnection.ClearAllPools();

        // 收尾：把 WAL 并回主库，沙箱里的库自包含（真实机器上由 apply 前的 checkpoint 保证）。
        using (var clean = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            clean.Open();
            Exec(clean, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        SqliteConnection.ClearAllPools();
    }

    private static string ComposerJson(int rulesChars, bool withBreakdown, long now)
    {
        var o = new JsonObject
        {
            ["composerId"] = SessionId,
            ["name"] = "reverse engineering fixture",
            ["status"] = "completed",
            ["unifiedMode"] = "agent",
            ["modelConfig"] = new JsonObject { ["modelName"] = "grok-4.6", ["maxMode"] = false },
            ["workspaceIdentifier"] = new JsonObject { ["id"] = "cursor-selftest-ws" },
            ["lastUpdatedAt"] = now - 1000,
            ["createdAt"] = now - 60000,
            ["fullConversationHeadersOnly"] = new JsonArray(
                new JsonObject { ["bubbleId"] = "b-user", ["type"] = 1 },
                new JsonObject { ["bubbleId"] = "b-assistant", ["type"] = 2 }),
        };
        if (withBreakdown)
            o["promptTokenBreakdown"] = new JsonObject
            {
                ["totalUsedTokens"] = 18770,
                ["maxTokens"] = 256000,
                ["categories"] = new JsonArray(
                    new JsonObject
                    {
                        ["id"] = "system_prompt", ["label"] = "System prompt",
                        ["characterCount"] = 4099, ["estimatedTokens"] = 1057,
                    },
                    new JsonObject
                    {
                        ["id"] = "tools", ["label"] = "Tool definitions",
                        ["characterCount"] = 13324, ["estimatedTokens"] = 3435,
                    },
                    new JsonObject
                    {
                        ["id"] = "rules", ["label"] = "Rules",
                        ["characterCount"] = rulesChars, ["estimatedTokens"] = 2705,
                    },
                    new JsonObject
                    {
                        ["id"] = "conversation", ["label"] = "Conversation",
                        ["characterCount"] = 23312, ["estimatedTokens"] = 5963,
                    }),
            };
        return o.ToJsonString();
    }

    private static string BubbleJson(string bubbleId, int type, string text, string? thinking, long createdAt)
    {
        var o = new JsonObject
        {
            ["bubbleId"] = bubbleId,
            ["type"] = type,
            ["text"] = text,
            ["createdAt"] = DateTimeOffset.FromUnixTimeMilliseconds(createdAt).ToString("o"),
            ["tokenCount"] = new JsonObject { ["inputTokens"] = 0, ["outputTokens"] = 0 },
        };
        if (thinking is not null)
            o["allThinkingBlocks"] = new JsonArray(new JsonObject { ["text"] = thinking });
        return o.ToJsonString();
    }

    /// <summary>把某个会话的 rules / system_prompt 档字符数改成新值（模拟"注入后再开一轮会话"）。</summary>
    private static void SetRulesChars(string dbPath, string composerId, int rulesChars, int? systemPromptChars = null)
    {
        string raw;
        using (var cn = new SqliteConnection(CursorDb.OpenString(dbPath, readOnly: false)))
        {
            cn.Open();
            using var read = cn.CreateCommand();
            read.CommandText = $"SELECT value FROM {CursorDb.DiskKvTableName} WHERE key=@k";
            read.Parameters.AddWithValue("@k", CursorDb.ComposerPrefix + composerId);
            raw = (string)read.ExecuteScalar()!;

            var node = JsonNode.Parse(raw)!.AsObject();
            var cats = node["promptTokenBreakdown"]!["categories"]!.AsArray();
            foreach (var c in cats)
            {
                var id = c!["id"]!.GetValue<string>();
                if (id == "rules") c["characterCount"] = rulesChars;
                if (id == "system_prompt" && systemPromptChars is int sp) c["characterCount"] = sp;
            }

            using var write = cn.CreateCommand();
            write.CommandText = $"UPDATE {CursorDb.DiskKvTableName} SET value=@v WHERE key=@k";
            write.Parameters.AddWithValue("@v", node.ToJsonString());
            write.Parameters.AddWithValue("@k", CursorDb.ComposerPrefix + composerId);
            write.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private static void Exec(SqliteConnection cn, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    // ================================================================ 工具

    /// <summary>ItemTable 全表快照：key → value 的 sha256（用来证明"别的键一个字节都没动"）。</summary>
    private static Dictionary<string, string> ItemMap(string dbPath)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cn = new SqliteConnection(CursorDb.OpenString(dbPath, readOnly: true)))
        {
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM ItemTable";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var key = rd.IsDBNull(0) ? "" : rd.GetString(0);
                var v = rd.IsDBNull(1)
                    ? Array.Empty<byte>()
                    : rd.GetValue(1) switch
                    {
                        byte[] b => b,
                        string s => Encoding.UTF8.GetBytes(s),
                        var other => Encoding.UTF8.GetBytes(other?.ToString() ?? ""),
                    };
                map[key] = CursorFileBackup.Sha256Bytes(v);
            }
        }
        SqliteConnection.ClearAllPools();
        return map;
    }

    private static int OtherKeysChanged(Dictionary<string, string> before, Dictionary<string, string> after, string ours)
    {
        var changed = before.Count(kv => !kv.Key.Equals(ours, StringComparison.Ordinal) &&
                                         (!after.TryGetValue(kv.Key, out var v) ||
                                          !string.Equals(v, kv.Value, StringComparison.Ordinal)));
        return changed + after.Keys.Count(k => !k.Equals(ours, StringComparison.Ordinal) && !before.ContainsKey(k));
    }

    private static int RowCount(string dbPath, string table)
    {
        using var cn = new SqliteConnection(CursorDb.OpenString(dbPath, readOnly: true));
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        var n = Convert.ToInt32(cmd.ExecuteScalar());
        cn.Close();
        SqliteConnection.ClearAllPools();
        return n;
    }

    private static string? ShaShared(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch { return null; }
    }

    private static Dictionary<string, string?> SnapshotTree(string? dir, bool skipWalIndexShm = false)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (dir is null || !Directory.Exists(dir)) return map;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (skipWalIndexShm && f.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase)) continue;
                map[f] = ShaShared(f);
            }
        }
        catch { /* 权限 */ }
        return map;
    }

    private static (int Total, int Changed, string Detail) DbAndWalDiff(
        Dictionary<string, string?> before, Dictionary<string, string?> after)
    {
        var keys = before.Keys
            .Where(k => k.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
                        k.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var changed = new List<string>();
        foreach (var k in keys)
        {
            if (!after.TryGetValue(k, out var v)) { changed.Add(Path.GetFileName(k) + "（消失）"); continue; }
            if (!string.Equals(before[k], v, StringComparison.Ordinal)) changed.Add(Path.GetFileName(k));
        }
        return (keys.Length, changed.Count, string.Join("、", changed.Take(5)));
    }

    private static int WalIndexShmCount(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.db-shm", SearchOption.AllDirectories).Count()
                : 0;
        }
        catch { return 0; }
    }

    private static bool SameSnapshot(Dictionary<string, string?> a, Dictionary<string, string?> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var v)) return false;
            if (!string.Equals(kv.Value, v, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static string Short(string? sha) => sha is null ? "(读不到)" : sha[..Math.Min(12, sha.Length)] + "…";

    private static string Trim1(string? s, int n)
    {
        if (s is null) return "(null)";
        var one = s.Replace("\r", " ").Replace("\n", " ");
        return one.Length <= n ? one : one[..n] + "…";
    }

    private static void CheckThrows(SelfTestResult r, Action act, EngineErrorCode expected, string what, Sandbox? s = null)
    {
        // 只对**仍存在**的补丁文件做"抛错时零改动"比对（7c 本身就把其中一个删掉了）。
        var before = s?.PatchFiles.Where(File.Exists)
            .ToDictionary(p => p, FileText.Sha256File, StringComparer.OrdinalIgnoreCase);
        try
        {
            act();
            r.Check(false, what + "（实际没有抛异常）");
        }
        catch (AgentEngineException ex)
        {
            r.Check(ex.Code == expected, $"{what}（code={ex.Code}）");
        }
        catch (Exception ex)
        {
            r.Check(false, $"{what}（抛了 {ex.GetType().Name}: {ex.Message}）");
        }
        finally
        {
            if (before is not null)
                r.Check(before.All(kv => File.Exists(kv.Key) && FileText.Sha256File(kv.Key) == kv.Value),
                    what + "：抛错时零改动");
        }
    }
}
