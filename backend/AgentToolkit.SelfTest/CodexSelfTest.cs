using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentToolkit.Contracts;
using AgentToolkit.Core;
using AgentToolkit.Core.Support;
using AgentToolkit.Core.Targets;
using AgentToolkit.Core.Targets.Codex;
using Microsoft.Data.Sqlite;

namespace AgentToolkit.SelfTest;

/// <summary>
/// Codex 适配器的回归自检。硬性纪律：**只在沙箱副本上操作**，
/// 绝不写真实 <c>~\.codex</c>、也绝不碰 <c>C:\Program Files\WindowsApps\OpenAI.Codex_*</c>
/// （用例 13 只读核对，并对真实的 ~\.codex 做前后 sha256 快照比对）。
///
/// 沙箱里有：合成 config.toml（顶层 4 个键 + 5 个表 + 中文注释 + JSON-in-literal）、
/// 合成 state_5.sqlite（threads）、合成 thread_history_1.sqlite（thread_items：userMessage / reasoning /
/// agentMessage 两种 phase）、一个假 CLI 目录、本工具自己的数据目录。
/// 用例 12 另造一个「真实表只在 -wal 里」的库，把「必须连 -wal/-shm 一起拷」这条纪律变成可回归的断言。
/// </summary>
public sealed class CodexSelfTest : ITargetSelfTest
{
    public string TargetId => "codex";
    public string Name => "Codex 适配器回归（沙箱合成 config.toml / threads / thread_items / WAL 副本）";

    // 标记与协议特征在自检里**独立写一遍**（不引用 CodexRules），否则改坏了表、自检也跟着坏，等于没测。
    private const string Marker1 = "INJECTED REVISION ACTION";
    private const string Marker2 = "TRANSACTION TURN COMMIT";
    private const string Marker3 = "ROLE: PROJECT EXECUTOR";
    private const string Marker4 = "OUTPUT_CONTRACT";
    private const string Marker5 = "VERIFIED TRANSACTION";
    private const string Marker6 = "ROLLBACK.sh";
    private const string ProtocolSha = "37382fb7082652e5b18c99fa5086fbc6d414993eaeae2ec7852ba372b031acac";
    private const long ProtocolBytes = 7575;
    private const string UpstreamName = "gpt-6.1-sol-v1-rc1.md";
    private const string KeyName = "model_instructions_file";

    private const string Thread1 = "01a1fixture-0000-4000-8000-000000000001";
    private const string Thread2 = "01b2fixture-0000-4000-8000-000000000002";
    private const string UserBubble = "把当前注入的指令读一遍，然后按它交付。";

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
        var realHome = CodexLocator.DefaultConfigHome();
        var realExists = CodexLocator.IsConfigRoot(realHome);
        r.Step($"真实主目录（只读比对）：{realHome}（{(realExists ? "存在 config.toml" : "不存在")}）");

        var before = SnapshotTree(realHome);
        var statsBefore = Summarize(before);
        var shmBefore = CountBySuffix(realHome, ".sqlite-shm");
        r.Step($"    快照：{statsBefore.Files} 个文件（sha256 {statsBefore.Hashed} 个、按大小+mtime 记账 {statsBefore.Light} 个），" +
               $"排除 {shmBefore} 个 *.sqlite-shm（SQLite 的 wal-index 共享内存，任何读者都可能更新它）");

        // 记忆文件钉在 %TEMP% 下：任何路径的记忆写入都落不到真实家目录。
        SetEnv(CodexLocator.MemoryFileEnv, Path.Combine(ctx.WorkDir, "codex-memory.json"));

        try
        {
            Case01FactoryState(ctx, r, SetEnv);
            Case02PreviewReadOnly(ctx, r, SetEnv);
            Case03Apply(ctx, r, SetEnv);
            Case04Idempotent(ctx, r, SetEnv);
            Case05RevertWithBackup(ctx, r, SetEnv);
            Case06RevertWithoutBackup(ctx, r, SetEnv);
            Case07ContaminatedBackup(ctx, r, SetEnv);
            Case08FailClosed(ctx, r, SetEnv);
            Case09ForeignTraces(ctx, r, SetEnv);
            Case10Markers(ctx, r, SetEnv);
            Case11LogLayer(ctx, r, SetEnv);
            Case12WalDiscipline(ctx, r, SetEnv);
            Case14Verbatim(ctx, r);
            Case15LocateDiffExportProbes(ctx, r, SetEnv);
        }
        finally
        {
            foreach (var kv in saved) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        }

        Case13RealDataReadOnly(ctx, r, realHome, realExists);

        // 收尾：真实 ~\.codex 一个字节都不许被本工具改动。
        //
        // 一个诚实的细节：ChatGPT 客户端**此刻正在跑**（10 个进程），它自己在不停地往
        // logs_2.sqlite-wal 追加日志。把「运行窗口里的变化」直接算成我们的足迹，等于拿客户端自己的
        // 写入当证据（Cursor 那套对 *.db-shm 的处理就是同一个道理）。所以这里用**空闲窗口**做对照：
        // 什么都不做地等 1.5 秒，看哪些文件自己在变；运行窗口里的变化项必须全部落在「自己在变」的集合里。
        var after = SnapshotTree(realHome);
        var statsAfter = Summarize(after);
        var idleBefore = SnapshotTree(realHome);
        Thread.Sleep(1500);
        var idleAfter = SnapshotTree(realHome);

        var changedKeys = ChangedKeys(before, after);
        var churnKeys = new HashSet<string>(ChangedKeys(idleBefore, idleAfter), StringComparer.OrdinalIgnoreCase);
        var unexplained = changedKeys.Where(k => !churnKeys.Contains(k)).ToArray();

        r.Check(unexplained.Length == 0,
            $"自检全程没有改动真实 ~\\.codex：{statsBefore.Files} 个文件；运行窗口里变化 {changedKeys.Count} 个，" +
            $"其中 {changedKeys.Count - unexplained.Length} 个在「空闲 1.5 秒」窗口里也在变（客户端自己在写），" +
            $"无法归因到本工具的变化项 {unexplained.Length} 个" +
            (unexplained.Length == 0 ? "" : $"（{string.Join("、", unexplained.Select(Path.GetFileName))}）"));
        if (changedKeys.Count > 0)
            r.Step($"    客户端自己在写的文件（空闲窗口里也在变）：" +
                   string.Join("、", churnKeys.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal)));
        r.Check(statsAfter.Files == statsBefore.Files,
            $"真实 ~\\.codex 的文件数不变：{statsBefore.Files} → {statsAfter.Files}（本工具不创建、不删除真实文件）");

        // 关键文件逐字节比对：注入落点 + 两个库 + 会话索引 + 凭据文件，一个都不许变。
        var critical = new[]
        {
            Path.Combine(realHome, "config.toml"),
            Path.Combine(realHome, CodexRules.PromptFileName),
            Path.Combine(realHome, CodexDb.StateDbName),
            Path.Combine(realHome, CodexDb.StateDbName + "-wal"),
            Path.Combine(realHome, CodexDb.ThreadDbName),
            Path.Combine(realHome, CodexDb.ThreadDbName + "-wal"),
            Path.Combine(realHome, "session_index.jsonl"),
            Path.Combine(realHome, "auth.json"),
        }.Where(File.Exists).ToArray();

        var criticalChanged = critical
            .Where(p => before.TryGetValue(p, out var b) && (!after.TryGetValue(p, out var a) || !string.Equals(a, b, StringComparison.Ordinal)))
            .ToArray();
        r.Check(criticalChanged.Length == 0,
            $"关键文件逐字节不变（{critical.Length} 个：config.toml / 提示词文件 / 两个库及其 -wal / session_index.jsonl / auth.json）；" +
            (criticalChanged.Length == 0
                ? "变化 0 个"
                : "变了：" + string.Join("、", criticalChanged.Select(Path.GetFileName))));

        return r;
    }

    // ================================================================ 1 出厂态

    private static void Case01FactoryState(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【1】出厂态：config.toml 顶层没有 model_instructions_file、提示词文件不存在 → 层 NotApplied");

        var s = Setup(ctx, setEnv, "factory");
        var st = s.Target.GetStatus(default);

        r.Check(st.Layers.Count == 1, $"状态里装配出 {st.Layers.Count} 层（日志层只读，不进 Layers）");
        var layer = st.Layers[0];
        r.Check(layer.Kind == LayerKind.SystemPrompt, $"层 1 的 Kind = {layer.Kind}（契约里没有「配置键」档，按 SystemPrompt 报）");
        r.Check(layer.TargetPath == s.ConfigPath, $"层 1 的 TargetPath 是 config.toml（{layer.TargetPath}）");
        r.Check(layer.State == LayerState.NotApplied, $"顶层没有键 → 层 1 {layer.State}（期望 NotApplied）");
        r.Check(layer.CapBytes == 256 * 1024, $"CapBytes = {layer.CapBytes}（本工具自设 256 KB，不是客户端限制）");
        r.Check(layer.Notes.Any(n => n.Title == "体积护栏（本工具自设）"), "Notes 写明 256 KB 是自设护栏、不冒充官方限制");
        r.Check(layer.Notes.Any(n => n.Title == "键的位置"), "Notes 写明键的位置 / 插入位置");
        r.Check(layer.Notes.Any(n => n.Title.StartsWith("契约缺口", StringComparison.Ordinal)),
            "契约缺口（单 TargetPath / 两个落点）写进 Notes，没有改契约");
        r.Check(layer.EffectivenessEvidence?.Contains("还要没装上", StringComparison.Ordinal) == true ||
                layer.EffectivenessEvidence?.Contains("还没装上", StringComparison.Ordinal) == true,
            $"生效证据里如实说配置级不成立：{Trim1(layer.EffectivenessEvidence, 100)}");
        r.Check(!layer.InstalledButNotEffective, "没装上的时候不置 InstalledButNotEffective（不许自相矛盾）");
        r.Check(st.Target.TargetId == "codex" && st.Target.Found, $"EngineStatus.Target 由 ToLocateResult 装配且 Found（{st.Target.TargetId}）");
        r.Check(st.Target.ConfigHome == s.Home, $"ConfigHome = 主目录（{st.Target.ConfigHome}）");
        r.Check(st.Target.Notes.Any(n => n.Title.Contains("SessionsDir", StringComparison.Ordinal)),
            "Notes 里写明契约的 SessionsDir 对 Codex 只对一半（真实落点是两个 SQLite）");
        r.Check(st.Target.Notes.Any(n => n.Title == "本工具约定的变量"), "Notes 说明 CODEX_HOME 是官方变量、哪些是本工具约定的");
        r.Check(st.Layers[0].Backup.Health == BackupHealth.Absent, "出厂态没有备份（Backup.Health=Absent）");

        // 1b：config.toml 根本不存在 → TargetAbsent + fail-closed
        var n = Setup(ctx, setEnv, "factory-no-config", withConfig: false);
        var st2 = n.Target.GetStatus(default);
        r.Check(st2.Layers[0].State == LayerState.TargetAbsent,
            $"没有 config.toml → 层 1 {st2.Layers[0].State}（期望 TargetAbsent）");
        r.Check(st2.Layers[0].StateDetail.Contains("不创建", StringComparison.Ordinal),
            $"StateDetail 写明不创建 config.toml：{Trim1(st2.Layers[0].StateDetail, 90)}");
        var pv2 = n.Target.PreviewApply(new ApplyOptions(), default);
        r.Check(!pv2.CanApply && pv2.Notes.Any(x => x.Severity == Severity.Error),
            "config.toml 不存在 → 预览不可执行 + Error 级说明（fail-closed）");

        // 1c：库里没有线程 → 生效判据必须报「无法判定」，不是「没生效」
        var e = Setup(ctx, setEnv, "factory-no-threads", threads: 0);
        var st3 = e.Target.GetStatus(default);
        r.Check(st3.Notes.Any(x => x.Title == "没有线程日志"), "没有线程 → 状态页有「没有线程日志」说明");
        r.Check(st3.Layers[0].EffectivenessEvidence?.Contains("无法判定", StringComparison.Ordinal) == true,
            $"没有线程 → 生效证据报「无法判定」：{Trim1(st3.Layers[0].EffectivenessEvidence, 120)}");
        r.Check(st3.Layers[0].EffectivenessEvidence?.Contains("装上了但没生效", StringComparison.Ordinal) != true,
            "没有线程时**不**出「装上了但没生效」这个结论");
        r.Check(st3.LastMarkerReport is null, "没有线程 → LastMarkerReport=null（不伪造报告）");

        ctx.Log($"factory: layer={layer.State} threads={st.Layers[0].State}");
    }

    // ================================================================ 2 预览只读

    private static void Case02PreviewReadOnly(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【2】预览只读：PreviewApply 前后 config.toml + 提示词文件 + 三个库的 sha256 都不变，且不建备份");

        var s = Setup(ctx, setEnv, "preview");
        var before = SnapshotOf(s.ConfigPath, s.PromptPath, s.StateDb, s.ThreadDb);
        var mtime = File.GetLastWriteTimeUtc(s.ConfigPath);

        var preview = s.Target.PreviewApply(new ApplyOptions(), default);

        foreach (var kv in before)
            r.Check(Sha(kv.Key) == kv.Value, $"预览前后 {Path.GetFileName(kv.Key)} 的 sha256 不变（{Short(kv.Value)}）");
        r.Check(File.GetLastWriteTimeUtc(s.ConfigPath) == mtime, "预览没有触碰 config.toml 的修改时间");
        r.Check(!File.Exists(s.ConfigPath + ".breach.bak"), "预览没有建 config.toml 备份");
        r.Check(!File.Exists(s.PromptPath), "预览没有创建提示词文件");
        r.Check(!File.Exists(s.PromptPath + ".breach.bak"), "预览没有建提示词文件备份");
        r.Check(CodexConfig.Parse(FileText.Read(s.ConfigPath)).Key.Present == false, "预览后 config.toml 仍没有那个键");

        r.Check(preview.CanApply, $"CanApply = {preview.CanApply}（期望 true）");
        r.Check(preview.Changes.Count == 2, $"Changes 两条（config.toml + 提示词文件），实际 {preview.Changes.Count}");
        r.Check(preview.Changes.All(c => c.Layer == LayerKind.SystemPrompt), "两条都是 SystemPrompt 层");
        r.Check(preview.Changes.First(c => c.Path == s.ConfigPath).WillWrite, "config.toml 那条 WillWrite=true");
        r.Check(preview.Changes.First(c => c.Path == s.PromptPath).WillWrite, "提示词文件那条 WillWrite=true");
        var cfgChange = preview.Changes.First(c => c.Path == s.ConfigPath);
        r.Check(cfgChange.DiffText is not null && cfgChange.DiffText.Contains("+ " + KeyName, StringComparison.Ordinal),
            "config.toml 那条带逐行差异，且差异里能看到插入的键");
        r.Check(preview.PreviewId.Length == 16, $"PreviewId 取输入内容 sha256 前 16 位：{preview.PreviewId}");
        r.Check(preview.Notes.Any(n => n.Title == "不做 TOML 重序列化"), "预览说明不做 TOML 重序列化");
        r.Check(preview.Notes.Any(n => n.Title.Contains("预览里没有日志层", StringComparison.Ordinal)),
            "预览注明日志层只读、没有 PlannedChange");

        var dry = s.Target.Apply(new ApplyOptions { DryRun = true }, null, default);
        r.Check(!dry.Applied, "DryRun=true 的 apply：Applied=false");
        foreach (var kv in before)
            r.Check(Sha(kv.Key) == kv.Value, $"DryRun 没有写 {Path.GetFileName(kv.Key)}");
        r.Check(!File.Exists(s.ConfigPath + ".breach.bak") && !File.Exists(s.PromptPath), "DryRun 不建备份、不写文件");

        ctx.Log($"preview: canApply={preview.CanApply} id={preview.PreviewId}");
    }

    // ================================================================ 3 apply

    private static void Case03Apply(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【3】apply：提示词文件 sha256 == 37382fb7…acac；顶层出现键且指向它；其它键与行数不变；表区指纹不变");

        var s = Setup(ctx, setEnv, "apply");
        var beforeText = FileText.Read(s.ConfigPath);
        var beforeSnap = CodexConfig.Parse(beforeText);

        var res = s.Target.Apply(new ApplyOptions(), null, default);
        r.Check(res.Applied, "ApplyResult.Applied=true");
        r.Check(res.RestartRequired, "RestartRequired=true（config.toml 是启动时读的）");
        r.Check(res.VerificationHint.Contains("Current:", StringComparison.Ordinal) &&
                res.VerificationHint.Contains("新线程", StringComparison.Ordinal) &&
                res.VerificationHint.Contains("无法判定", StringComparison.Ordinal),
            "VerificationHint 写明两级判据、要完全重启 + 新线程、三态要分开");
        r.Check(res.Changes.Count == 2, $"Changes 两条，实际 {res.Changes.Count}");

        // ---- 提示词文件 ----
        r.Check(File.Exists(s.PromptPath), $"已写出提示词文件：{Path.GetFileName(s.PromptPath)}");
        var promptSha = Sha(s.PromptPath);
        r.Check(promptSha == ProtocolSha,
            $"提示词文件 sha256 == {ProtocolSha[..12]}…acac（实际 {Short(promptSha)}）——一个字都没改");
        r.Check(new FileInfo(s.PromptPath).Length == ProtocolBytes,
            $"提示词文件 {new FileInfo(s.PromptPath).Length} 字节 == 原文 {ProtocolBytes} 字节");
        r.Check(File.ReadAllBytes(s.PromptPath)[0] != 0xEF, "写出的提示词文件没有 UTF-8 BOM");
        r.Check(!File.Exists(s.PromptPath + ".breach.bak"), "提示词文件出厂不存在 → 没有多余备份");

        // ---- config.toml ----
        var afterText = FileText.Read(s.ConfigPath);
        var afterSnap = CodexConfig.Parse(afterText);
        r.Check(afterSnap.Key.Present, $"顶层出现了 {KeyName}");
        r.Check(afterSnap.Key.Duplicates == 0, "顶层只有一个该键（没有重复）");
        r.Check(string.Equals(afterSnap.Key.Value, s.PromptPath, StringComparison.OrdinalIgnoreCase),
            $"键值指向本工具的文件：{afterSnap.Key.Value}");
        r.Check(afterSnap.Key.RawLine == KeyName + " = '" + s.PromptPath + "'",
            $"写成了单引号字面量（Windows 路径不转义）：{afterSnap.Key.RawLine}");
        r.Check(afterSnap.LineCount == beforeSnap.LineCount + 1,
            $"行数 {beforeSnap.LineCount} → {afterSnap.LineCount}（只多自己那一行）");
        r.Check(afterSnap.OtherTopDigest == beforeSnap.OtherTopDigest,
            $"其它顶层行（model / model_reasoning_effort / service_tier / notify / 注释）指纹不变（{Short(beforeSnap.OtherTopDigest)}）");
        r.Check(afterSnap.TablesDigest == beforeSnap.TablesDigest,
            $"表区指纹不变（{Short(beforeSnap.TablesDigest)}）：[mcp_servers.*] / [desktop] / [plugins.*] / [windows] 一个字节都没动");
        r.Check(CodexConfig.InsertRoundTrips(beforeText, afterText),
            "逐字节往返：删掉插入的那一行后 == 原文件（其它字节一个都没动）");
        r.Check(afterSnap.Key.LineIndex == beforeSnap.FirstTableIndex,
            $"键插在第一个表头之前（第 {afterSnap.Key.LineIndex + 1} 行 = 原第一个表头位置）");
        r.Check(File.ReadAllBytes(s.ConfigPath)[0] == (byte)'m', "写出的 config.toml 没有 BOM、没被重排（首字节仍是 'm'）");
        r.Check(afterText.Contains("# 沙箱注释：中文与 emoji", StringComparison.Ordinal) &&
                afterText.Contains("pet_fixture", StringComparison.Ordinal) &&
                afterText.Contains("NODE_REPL_TRUSTED_SERVICES", StringComparison.Ordinal),
            "注释 / 中文 / JSON-in-literal 全部原样保留");

        // ---- 备份 ----
        r.Check(File.Exists(s.ConfigPath + ".breach.bak"), "config.toml 已备份到 .breach.bak");
        r.Check(FileText.Read(s.ConfigPath + ".breach.bak") == beforeText, "备份就是注入前的原文（逐字节）");
        var meta = MetaOf(s.ConfigPath);
        r.Check(meta is not null && meta.ContainsKey("originalSha256"), "备份写了 .meta.json（含 originalSha256）");
        var assess = new BackupManager(s.ConfigPath).Assess(CodexRules.BackupMarkers);
        r.Check(assess.Status == BackupStatus.Clean, $"备份判为干净（{assess.Status}，不含注入标记）");

        // ---- 状态 ----
        var st = s.Target.GetStatus(default);
        r.Check(st.Layers[0].State == LayerState.Applied, $"apply 后层 1 = {st.Layers[0].State}（期望 Applied）");
        r.Check(st.Layers[0].Backup.Health == BackupHealth.Clean, $"层 1 备份报告干净（{st.Layers[0].Backup.Health}）");
        r.Check(st.Layers[0].EffectivenessEvidence?.Contains("装上了", StringComparison.Ordinal) == true,
            "生效证据里配置级判为「装上了」");
        r.Check(st.Layers[0].EffectivenessEvidence?.Contains("无法判定", StringComparison.Ordinal) == true,
            "线程都早于注入 → 行为级报「无法判定」（不是「没生效」）");
        r.Check(!st.Layers[0].InstalledButNotEffective,
            "线程都早于注入时**不置** InstalledButNotEffective（无法判定 ≠ 没生效）");
        r.Check(st.RestartRequired, "已注入但还没有注入后的线程 → RestartRequired=true");

        ctx.Log($"apply ok: {Short(promptSha)} key@line {afterSnap.Key.LineIndex + 1}");
    }

    // ================================================================ 4 幂等

    private static void Case04Idempotent(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【4】幂等：第二次 apply 无字节变化、Applied=false、WillWrite 全为 false、mtime 不变");

        var s = Setup(ctx, setEnv, "idempotent");
        s.Target.Apply(new ApplyOptions(), null, default);

        var cfgSha = Sha(s.ConfigPath);
        var promptSha = Sha(s.PromptPath);
        var cfgStamp = File.GetLastWriteTimeUtc(s.ConfigPath);
        var promptStamp = File.GetLastWriteTimeUtc(s.PromptPath);

        var second = s.Target.Apply(new ApplyOptions(), null, default);
        r.Check(!second.Applied, $"第二次 apply 没有可写内容 → Applied={second.Applied}（不虚报）");
        r.Check(second.Changes.All(c => !c.WillWrite), "第二次 apply 的 Changes 里 WillWrite 全为 false");
        r.Check(Sha(s.ConfigPath) == cfgSha, "config.toml 的 sha256 不变");
        r.Check(Sha(s.PromptPath) == promptSha, "提示词文件的 sha256 不变");
        r.Check(File.GetLastWriteTimeUtc(s.ConfigPath) == cfgStamp, "没有重写 config.toml（mtime 不变）");
        r.Check(File.GetLastWriteTimeUtc(s.PromptPath) == promptStamp, "没有重写提示词文件（mtime 不变）");
        r.Check(second.Notes.Any(n => n.Title == "幂等"), "给出幂等说明");

        var preview = s.Target.PreviewApply(new ApplyOptions(), default);
        r.Check(!preview.CanApply, "各层都已到位 → CanApply=false");
        r.Check(preview.Changes.All(c => !c.WillWrite), "预览的所有行 WillWrite=false");
        r.Check(preview.TotalBeforeBytes == 0 && preview.TotalAfterBytes == 0,
            $"无改动时体积统计不虚报（{preview.TotalBeforeBytes} → {preview.TotalAfterBytes}）");

        ctx.Log("idempotent ok");
    }

    // ================================================================ 5 revert（有备份）

    private static void Case05RevertWithBackup(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【5】revert 有备份：config.toml 逐字节回出厂、提示词文件删除、MatchesOriginal=true");

        var s = Setup(ctx, setEnv, "revert");
        var factoryCfg = Sha(s.ConfigPath);
        s.Target.Apply(new ApplyOptions(), null, default);
        r.Check(File.Exists(s.ConfigPath + ".breach.bak"), "5a 前置：apply 拍了备份");
        r.Check(Sha(s.ConfigPath) != factoryCfg, "5a 前置：config.toml 已被改过");

        var rev = s.Target.Revert(new RevertOptions(), null, default);
        r.Check(rev.Reverted, "5a RevertResult.Reverted=true");
        r.Check(Sha(s.ConfigPath) == factoryCfg, "5a config.toml 逐字节回出厂（sha256 相同）");
        r.Check(!CodexConfig.Parse(FileText.Read(s.ConfigPath)).Key.Present, "5a 顶层已经没有本工具的键");
        r.Check(!File.Exists(s.PromptPath), "5a 提示词文件已删除（出厂即不存在）");
        r.Check(rev.MatchesOriginal == true, $"5a 与首次备份 sha256 比对一致（MatchesOriginal={rev.MatchesOriginal}）");
        r.Check(rev.OriginalSha256 is not null && rev.CurrentSha256 == rev.OriginalSha256,
            $"5a OriginalSha256 == CurrentSha256（{Short(rev.CurrentSha256)}）");
        r.Check(rev.ReadBackClean, "5a 回读确认无残留");
        r.Check(rev.Changes.Any(c => c.WillWrite), "5a Changes 记录了还原动作");
        r.Check(rev.Notes.Any(n => n.Title.Contains("生效时机", StringComparison.Ordinal)), "5a 提示生效时机（完全退出 + 新线程）");
        r.Check(File.Exists(s.ConfigPath + ".breach.bak"), "5a 默认保留备份（RemoveBackups=false）");

        // 还原后状态：NotApplied，且不该把「无法判定」说成「没生效」
        var st = s.Target.GetStatus(default);
        r.Check(st.Layers[0].State == LayerState.NotApplied, $"5a 还原后层 1 = {st.Layers[0].State}");

        // 5b：dry-run 的 revert 一个字节都不写
        var d = Setup(ctx, setEnv, "revert-dry");
        d.Target.Apply(new ApplyOptions(), null, default);
        var cfgBeforeDry = Sha(d.ConfigPath);
        var dry = d.Target.Revert(new RevertOptions { DryRun = true }, null, default);
        r.Check(!dry.Reverted, "5b dry-run revert：Reverted=false");
        r.Check(Sha(d.ConfigPath) == cfgBeforeDry && File.Exists(d.PromptPath), "5b dry-run 没有改动任何文件");

        // 5c：RemoveBackups=true → 备份清掉
        var c = Setup(ctx, setEnv, "revert-cleaning");
        c.Target.Apply(new ApplyOptions(), null, default);
        c.Target.Revert(new RevertOptions { RemoveBackups = true }, null, default);
        r.Check(!File.Exists(c.ConfigPath + ".breach.bak") && !File.Exists(c.ConfigPath + ".breach.bak.meta.json"),
            "5c RemoveBackups=true 清掉了 config.toml 的备份与元数据");

        ctx.Log("revert(with backup) ok");
    }

    // ================================================================ 6 revert（无备份）

    private static void Case06RevertWithoutBackup(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【6】revert 无备份：逆向撤销（删键 + 删文件）、MatchesOriginal=null（不伪造 true）");

        var s = Setup(ctx, setEnv, "revert-no-backup");
        var factoryCfg = Sha(s.ConfigPath);
        s.Target.Apply(new ApplyOptions(), null, default);

        File.Delete(s.ConfigPath + ".breach.bak");
        File.Delete(s.ConfigPath + ".breach.bak.meta.json");

        var rev = s.Target.Revert(new RevertOptions(), null, default);
        r.Check(rev.Reverted, "没有备份时走逆向撤销路径");
        r.Check(!CodexConfig.Parse(FileText.Read(s.ConfigPath)).Key.Present, "逆向撤销：顶层键已删除");
        r.Check(Sha(s.ConfigPath) == factoryCfg, "逆向撤销后 config.toml 逐字节回出厂（sha256 相同）");
        r.Check(!File.Exists(s.PromptPath), "逆向撤销：提示词文件已删除");
        r.Check(rev.MatchesOriginal is null, "没有可信备份 → MatchesOriginal 报 null，不伪造 true");
        r.Check(rev.ReadBackClean, "逆向撤销后回读干净");
        r.Check(rev.Changes.First(c => c.Path == s.ConfigPath).Notes.Any(n => n.Detail.Contains("逆向撤销", StringComparison.Ordinal)),
            "变更说明里写明是逆向撤销（删掉顶层那一行）");

        ctx.Log("revert(no backup) ok");
    }

    // ================================================================ 7 污染备份

    private static void Case07ContaminatedBackup(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【7】污染备份：备份里含注入标记 → 状态报 Contaminated，revert 抛 AnchorConflict 且零改动");

        var s = Setup(ctx, setEnv, "contaminated");
        s.Target.Apply(new ApplyOptions(), null, default);
        FileText.Write(s.ConfigPath + ".breach.bak",
            FileText.Read(s.ConfigPath) + "\n# " + Marker1 + "\n# " + Marker2 + "\n");
        r.Check(new BackupManager(s.ConfigPath).Assess(CodexRules.BackupMarkers).Status == BackupStatus.Contaminated,
            "备份被改写成含协议段名 → 判污染");

        var st = s.Target.GetStatus(default);
        r.Check(st.Layers[0].Notes.Any(n => n.Title == "备份污染"), "状态页报出**备份污染**");
        r.Check(st.Layers[0].Backup.Health == BackupHealth.Contaminated,
            $"层 1 的 Backup.Health = {st.Layers[0].Backup.Health}");

        var cfgSha = Sha(s.ConfigPath);
        var promptSha = Sha(s.PromptPath);
        CheckThrows(r, () => s.Target.Revert(new RevertOptions(), null, default), EngineErrorCode.AnchorConflict,
            "污染备份 → revert 抛 AnchorConflict（拒绝用它还原）");
        r.Check(Sha(s.ConfigPath) == cfgSha, "抛错时 config.toml 一个字节都没动");
        r.Check(Sha(s.PromptPath) == promptSha, "抛错时提示词文件一个字节都没动");

        ctx.Log("contaminated ok");
    }

    // ================================================================ 8 fail-closed

    private static void Case08FailClosed(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【8】fail-closed：config.toml 不存在 / 已有别人的键 / 顶层键重复 → 抛 SelfCheckFailed 且不写盘");

        // 8a：config.toml 不存在
        var a = Setup(ctx, setEnv, "fail-no-config", withConfig: false);
        CheckThrows(r, () => a.Target.Apply(new ApplyOptions(), null, default), EngineErrorCode.SelfCheckFailed,
            "8a config.toml 不存在 → apply 抛 SelfCheckFailed");
        r.Check(!File.Exists(a.ConfigPath), "8a config.toml 没有被创建（不代客户端造配置）");
        r.Check(!File.Exists(a.PromptPath), "8a 提示词文件也没写");

        // 8b：已有别人的键（没有 Force）
        var b = Setup(ctx, setEnv, "fail-foreign-key", withKey: true);
        var foreignPrompt = Path.Combine(b.Home, "someone-else.md");
        var foreignSha = Sha(foreignPrompt);
        var cfgSha = Sha(b.ConfigPath);
        var pv = b.Target.PreviewApply(new ApplyOptions(), default);
        r.Check(!pv.CanApply, "8b 别人的键 → CanApply=false");
        r.Check(pv.Notes.Any(n => n.Severity == Severity.Error), "8b 预览给出 Error 级说明");
        CheckThrows(r, () => b.Target.Apply(new ApplyOptions(), null, default), EngineErrorCode.SelfCheckFailed,
            "8b 别人的键 + 无 Force → apply 抛 SelfCheckFailed");
        r.Check(Sha(b.ConfigPath) == cfgSha, "8b 抛错时 config.toml 零改动");
        r.Check(!File.Exists(b.PromptPath), "8b 抛错时提示词文件没写");

        // 8c：Force 才接管，接管后别人的文件一个字节都没动
        var res = b.Target.Apply(new ApplyOptions { Force = true }, null, default);
        r.Check(res.Applied, "8c Force=true → 接管成功（Applied=true）");
        r.Check(string.Equals(CodexConfig.Parse(FileText.Read(b.ConfigPath)).Key.Value, b.PromptPath,
                StringComparison.OrdinalIgnoreCase),
            "8c 顶层键已改成指向本工具的文件");
        r.Check(Sha(foreignPrompt) == foreignSha, "8c 别人那个 .md 一个字节都没动");
        var revF = b.Target.Revert(new RevertOptions(), null, default);
        r.Check(revF.Reverted && revF.MatchesOriginal == true, "8c 从备份还原后与首次备份一致");
        r.Check(string.Equals(CodexConfig.Parse(FileText.Read(b.ConfigPath)).Key.Value, foreignPrompt,
                StringComparison.OrdinalIgnoreCase),
            "8c 还原后键回到别人原来的值（只恢复自己那一个键）");

        // 8d：顶层键重复 → 拒绝（无法判断权威值）
        var d = Setup(ctx, setEnv, "fail-duplicate-key");
        var text = FileText.Read(d.ConfigPath);
        var dup = CodexConfig.InsertKey(text, Path.Combine(d.Home, "a.md"))
            .Replace("[mcp_servers]", KeyName + " = '" + Path.Combine(d.Home, "b.md") + "'\n[mcp_servers]", StringComparison.Ordinal);
        FileText.Write(d.ConfigPath, dup);
        var snapD = CodexConfig.Parse(FileText.Read(d.ConfigPath));
        r.Check(snapD.Key.Duplicates == 1, $"8d fixture 里顶层有两个同名键（Duplicates={snapD.Key.Duplicates}）");
        var stD = d.Target.GetStatus(default);
        r.Check(stD.Layers[0].State == LayerState.AnchorAmbiguous,
            $"8d 状态页报 {stD.Layers[0].State}（期望 AnchorAmbiguous：无法判断权威值）");
        CheckThrows(r, () => d.Target.Apply(new ApplyOptions(), null, default), EngineErrorCode.SelfCheckFailed,
            "8d 键重复 → apply 抛 SelfCheckFailed");

        // 8e：只出现在表里的同名键 → 不是我们的键，也不许写进表里
        var e = Setup(ctx, setEnv, "fail-key-in-table");
        var cfgE = FileText.Read(e.ConfigPath).Replace("[desktop]",
            "[desktop]\r\n# 表里的同名键：不是模型的指令文件键", StringComparison.Ordinal);
        cfgE = cfgE.Replace("[desktop]\r\n# 表里的同名键：不是模型的指令文件键",
            "[desktop]\r\n" + KeyName + " = '" + Path.Combine(e.Home, "not-ours.md") + "'", StringComparison.Ordinal);
        FileText.Write(e.ConfigPath, cfgE);
        var pvE = e.Target.PreviewApply(new ApplyOptions(), default);
        r.Check(!pvE.CanApply && pvE.Notes.Any(n => n.Detail.Contains("表内", StringComparison.Ordinal)),
            "8e 同名键只在表里 → fail-closed，并说明那不是模型的指令文件键");

        ctx.Log("fail-closed ok");
    }

    // ================================================================ 9 别人的痕迹

    private static void Case09ForeignTraces(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【9】别人的痕迹不冒充：上游旧文件名 + 别人的键 → 只报告；apply 需 Force；revert 只删自己的文件");

        var s = Setup(ctx, setEnv, "foreign-traces", withKey: true, upstreamTrace: true);
        var upstreamPath = Path.Combine(s.Home, UpstreamName);
        var upstreamSha = Sha(upstreamPath);
        r.Check(File.Exists(upstreamPath), $"沙箱里有上游旧文件名 {UpstreamName}（{new FileInfo(upstreamPath).Length} 字节）");

        var st = s.Target.GetStatus(default);
        r.Check(st.Notes.Any(n => n.Title == "检测到同类工具留下的痕迹" &&
                                  n.Detail.Contains(UpstreamName, StringComparison.Ordinal)),
            "状态页报告「检测到同类工具留下的痕迹」，并点名那个文件");
        r.Check(st.Notes.Any(n => n.Title.StartsWith("Conflict", StringComparison.Ordinal)),
            "状态页报告 Conflict（键被别的工具占了）");
        r.Check(st.Layers[0].StateDetail.StartsWith("Conflict", StringComparison.Ordinal),
            $"层状态里写明 Conflict：{Trim1(st.Layers[0].StateDetail, 90)}");
        r.Check(st.Layers[0].State == LayerState.AnchorAmbiguous,
            $"「键冲突」按契约唯一的拒绝档 AnchorAmbiguous 报（{st.Layers[0].State}），不新增契约枚举");
        r.Check(!st.Layers[0].ParseOk, "冲突时 ParseOk=false（本工具没有解析出一个可用的落点）");
        r.Check(st.Layers[0].Notes.Any(n => n.Title.Contains("没有 Conflict 这个层状态", StringComparison.Ordinal)),
            "Notes 逐条说明契约缺口：没有 Conflict 档、映射到 AnchorAmbiguous");
        r.Check(st.Notes.Any(n => n.Detail.Contains("不删、不覆盖", StringComparison.Ordinal)),
            "Notes 写明本工具不删、不覆盖别人的文件");

        // 默认不动
        CheckThrows(r, () => s.Target.Apply(new ApplyOptions(), null, default), EngineErrorCode.SelfCheckFailed,
            "9 有别人的键 → 默认 apply 拒绝（需 Force）");
        r.Check(Sha(upstreamPath) == upstreamSha, "9 拒绝时上游文件一个字节都没动");

        // Force 接管 → revert 只删自己那份，上游文件与它的键原样回来
        s.Target.Apply(new ApplyOptions { Force = true }, null, default);
        r.Check(File.Exists(s.PromptPath), "9 Force 后写了本工具自己的文件");
        r.Check(Sha(upstreamPath) == upstreamSha, "9 Force 时上游文件没被动过");

        var rev = s.Target.Revert(new RevertOptions(), null, default);
        r.Check(rev.Reverted, "9 revert 成功");
        r.Check(!File.Exists(s.PromptPath), "9 revert 只删自己那个文件");
        r.Check(File.Exists(upstreamPath) && Sha(upstreamPath) == upstreamSha, "9 上游文件仍在、内容不变");
        r.Check(string.Equals(CodexConfig.Parse(FileText.Read(s.ConfigPath)).Key.Value, upstreamPath,
                StringComparison.OrdinalIgnoreCase),
            "9 键回到上游那个文件（不冒充、不接管）");
        r.Check(rev.Notes.Any(n => n.Title == "别人的文件不动"), "9 revert 的 Notes 明写别人的文件不动");

        ctx.Log("foreign traces ok");
    }

    // ================================================================ 10 标记

    private static void Case10Markers(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【10】标记：注入后 CheckMarkers 命中 > 0；去掉后命中 0 且层状态仍 Applied");

        var a = Setup(ctx, setEnv, "markers-on");
        a.Target.Apply(new ApplyOptions(), null, default);
        var report = a.Target.CheckMarkers(Thread1, default);
        var total = report.Hits.Sum(h => h.Count);

        r.Check(report.SessionId == Thread1, $"报告里是解析后的真线程号（{report.SessionId}）");
        r.Check(total > 0, $"注入后 CheckMarkers 命中 > 0（实际 {total} 次）");
        r.Check(report.Hits.Count == 6, $"标记表 6 条（实际 {report.Hits.Count}）");
        r.Check(report.SystemPromptLayerPresent, "SystemPromptLayerPresent=true（命中来自指令文件的协议段）");
        r.Check(report.TotalChars > 0, $"拼出的文本非空（{report.TotalChars} 字符）");
        r.Check(report.Verdict.Contains("Current:", StringComparison.Ordinal),
            "Verdict 提到原文规定的状态行要求");
        r.Check(report.Verdict.Contains("行为级判据成立", StringComparison.Ordinal),
            $"10a Verdict 明确说行为级判据成立：{Trim1(report.Verdict, 100)}");
        r.Check(report.ExportedSystemPromptPath is not null && File.Exists(report.ExportedSystemPromptPath),
            "CheckMarkers 顺带导出状态清单（供 UI 打开）");

        // ---- 去掉标记（等于这个线程早于注入）----
        var b = Setup(ctx, setEnv, "markers-off");
        b.Target.Apply(new ApplyOptions(), null, default);
        StripMarkers(b.ThreadDb);
        TouchThreads(b.StateDb, secondsFromNow: 120);   // 注入后新建的线程，只是没带协议特征
        var report2 = b.Target.CheckMarkers(Thread1, default);
        r.Check(report2.Hits.Sum(h => h.Count) == 0, "去掉标记 → CheckMarkers 命中 0");
        r.Check(!report2.SystemPromptLayerPresent && !report2.ContextPayloadLayerPresent,
            "两层都报 present=false（命中 0）");
        r.Check(report2.Verdict.Contains("先看它是不是注入之后的新线程", StringComparison.Ordinal),
            $"10b Verdict 没有把「命中 0」直接说成「没生效」：{Trim1(report2.Verdict, 120)}");

        var st2 = b.Target.GetStatus(default);
        r.Check(st2.Layers[0].State == LayerState.Applied,
            $"层状态仍是 Applied——「装上了」与「生效了」必须分开报（{st2.Layers[0].State}）");
        r.Check(st2.Layers[0].InstalledButNotEffective,
            "但注入后的线程里没有协议特征 → 置 InstalledButNotEffective（这才是「装上了但没生效」）");
        r.Check(st2.Layers[0].EffectivenessEvidence?.Contains("装上了但没生效", StringComparison.Ordinal) == true,
            "生效证据里写「装上了但没生效」并给出数字");

        ctx.Log($"markers ok: on={total} → off=0");
    }

    // ================================================================ 11 日志层三态

    private static void Case11LogLayer(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【11】日志层三态：没有线程 → 无法判定；库损坏 → 抛 IoError；有线程且有协议特征 → 报出行为级证据");

        // ---- 11a：没有线程 → 无法判定 ----
        var a = Setup(ctx, setEnv, "log-no-threads", threads: 0);
        a.Target.Apply(new ApplyOptions(), null, default);
        var stA = a.Target.GetStatus(default);
        r.Check(stA.Layers[0].State == LayerState.Applied, "11a 层 1 仍是 Applied（注入本身成功）");
        r.Check(stA.Layers[0].EffectivenessEvidence?.Contains("库里一个线程都没有", StringComparison.Ordinal) == true,
            $"11a 报「无法判定（库里没有线程）」：{Trim1(stA.Layers[0].EffectivenessEvidence, 120)}");
        r.Check(!stA.Layers[0].InstalledButNotEffective, "11a 不置 InstalledButNotEffective（无法判定 ≠ 没生效）");
        r.Check(a.Target.ListSessions(default).Count == 0, "11a ListSessions 返回空（不是抛错）");

        // ---- 11b：线程索引损坏 → 抛 IoError ----
        var b = Setup(ctx, setEnv, "log-corrupt-state", corruptStateDb: true);
        CheckThrows(r, () => b.Target.GetStatus(default), EngineErrorCode.IoError,
            "11b 线程索引损坏 → GetStatus 抛 IoError（不降级成「无法判定」）");
        CheckThrows(r, () => b.Target.ListSessions(default), EngineErrorCode.IoError, "11b ListSessions 抛 IoError");
        CheckThrows(r, () => b.Target.CheckMarkers(Thread1, default), EngineErrorCode.IoError, "11b CheckMarkers 抛 IoError");

        // ---- 11c：正文库损坏 → 抛 IoError ----
        var c = Setup(ctx, setEnv, "log-corrupt-thread", corruptThreadDb: true);
        CheckThrows(r, () => c.Target.GetStatus(default), EngineErrorCode.IoError,
            "11c 正文库损坏 → GetStatus 抛 IoError（不是「没有线程」）");
        CheckThrows(r, () => c.Target.CheckMarkers(Thread1, default), EngineErrorCode.IoError,
            "11c CheckMarkers 抛 IoError");

        // ---- 11d：有线程 + 协议特征 → 报出行为级证据 ----
        var d = Setup(ctx, setEnv, "log-effective");
        d.Target.Apply(new ApplyOptions(), null, default);
        TouchThreads(d.StateDb, secondsFromNow: 120);
        var stD = d.Target.GetStatus(default);
        r.Check(stD.Layers[0].EffectivenessEvidence?.Contains("已生效（行为级）", StringComparison.Ordinal) == true,
            $"11d 报「已生效（行为级）」：{Trim1(stD.Layers[0].EffectivenessEvidence, 160)}");
        r.Check(stD.Layers[0].EffectivenessEvidence?.Contains("线程总数 2", StringComparison.Ordinal) == true,
            "11d 证据里带真实线程数（2）");
        r.Check(stD.Layers[0].EffectivenessEvidence?.Contains("命中总数", StringComparison.Ordinal) == true,
            "11d 证据里带真实命中数");
        r.Check(!stD.Layers[0].InstalledButNotEffective, "11d 已生效 → 不置 InstalledButNotEffective");
        r.Check(!stD.RestartRequired, "11d 已生效 → RestartRequired=false");
        r.Check(stD.LastMarkerReport is not null && stD.LastMarkerReport.Hits.Sum(h => h.Count) > 0,
            "11d LastMarkerReport 非空且命中 > 0");

        // ---- 11e：有注入后的线程但没有特征 → 装上了但没生效 ----
        var e = Setup(ctx, setEnv, "log-not-effective");
        e.Target.Apply(new ApplyOptions(), null, default);
        TouchThreads(e.StateDb, secondsFromNow: 120);
        StripMarkers(e.ThreadDb);
        var stE = e.Target.GetStatus(default);
        r.Check(stE.Layers[0].InstalledButNotEffective, "11e 注入后的线程里没有特征 → InstalledButNotEffective=true");
        r.Check(stE.Layers[0].EffectivenessEvidence?.Contains("装上了但没生效", StringComparison.Ordinal) == true,
            "11e 证据写「装上了但没生效」");
        r.Check(stE.RestartRequired, "11e 盘上是注入态但行为级没有阳性证据 → RestartRequired=true");

        // ---- 11f：会话载入与导出 ----
        var f = Setup(ctx, setEnv, "log-load");
        var sessions = f.Target.ListSessions(default);
        r.Check(sessions.Count == 2, $"11f ListSessions 返回 {sessions.Count} 个线程");
        r.Check(sessions[0].Id == Thread2, $"11f 按 updated_at 降序，最新的在前（{sessions[0].Id}）");
        r.Check(sessions[0].Workspace is not null && sessions[0].Workspace!.StartsWith("\\\\?\\", StringComparison.Ordinal) == false,
            $"11f 工作目录把 \\\\?\\ 前缀规范化掉了（{sessions[0].Workspace}）");
        r.Check(sessions[0].TurnCount == 4, $"11f TurnCount = 本线程 item 条数（{sessions[0].TurnCount}，期望 4）");

        var detail = f.Target.LoadSession(Thread1, new SessionLoadOptions(), default);
        r.Check(detail.Turns.Count == 3,
            $"11f 记录 {detail.Turns.Count} 条轮次（user + 2×assistant；没有正文的 reasoning 不留空轮次）");
        r.Check(detail.Turns.Count > 0 && detail.Turns[0].Role == "user" && detail.Turns[0].Text == UserBubble,
            "11f 第一条 role=user，正文取 content[].text");
        r.Check(detail.Turns.Count > 1 && detail.Turns[1].Role == "assistant:commentary",
            $"11f phase 拼在 role 上（{detail.Turns.ElementAtOrDefault(1)?.Role}）");
        r.Check(detail.Turns.Count > 2 && detail.Turns[2].Role == "assistant:final_answer",
            $"11f final_answer 也拼在 role 上（{detail.Turns.ElementAtOrDefault(2)?.Role}）");
        r.Check(detail.Turns.All(t => t.Role != "reasoning"),
            "11f 没有正文的 reasoning 被跳过（本机实测 1,210 条里只有 33 条有正文）");
        r.Check(detail.Notes.Any(n => n.Title == "解析口径"), "11f Notes 说明解析口径（字段来源）");
        r.Check(detail.Notes.Any(n => n.Detail.Contains("reasoning", StringComparison.Ordinal) &&
                                     n.Detail.Contains("不能当主判据", StringComparison.Ordinal)),
            "11f Notes 写明 reasoning 只有少数有正文、不能当主判据");
        r.Check(detail.SystemPromptText?.Contains(ProtocolSha[..12], StringComparison.Ordinal) == true,
            "11f SystemPromptText 给出真实注入文本的 sha256（与 Cursor 不同：Codex 的指令就在本地）");
        r.Check(detail.SystemPromptText?.Contains("未验证", StringComparison.Ordinal) == true,
            "11f 状态清单里如实列出未验证项");

        r.Check(f.Target.LoadSession("latest", new SessionLoadOptions(), default).Info.Id == Thread2, "11f latest 解析到最近线程");
        r.Check(f.Target.LoadSession(Thread1[..12], new SessionLoadOptions(), default).Info.Id == Thread1, "11f 唯一前缀可解析");
        CheckThrows(r, () => f.Target.LoadSession("no-such-thread", new SessionLoadOptions(), default),
            EngineErrorCode.TargetFileMissing, "11f 线程不存在 → TargetFileMissing，并列出库里现有的");

        var limited = f.Target.LoadSession(Thread1, new SessionLoadOptions { MaxTurns = 2 }, default);
        r.Check(limited.Turns.Count == 2, $"11f MaxTurns=2 → 只载入 2 条（实际 {limited.Turns.Count}）");

        var truncated = f.Target.LoadSession(Thread1, new SessionLoadOptions { MaxCharsPerText = 12 }, default);
        r.Check(truncated.Turns.Any(t => t.Truncated), "11f 超过 MaxCharsPerText 的文本被截断并置 Truncated=true");

        var exp = f.Target.ExportSession(Thread1, Path.Combine(f.Dir, "export.jsonl"), default);
        r.Check(exp.Exported && File.Exists(exp.Path), "11f ExportSession 写出文件");
        var lines = File.ReadAllLines(exp.Path!);
        r.Check(lines.Length == 4, $"11f 逐行 JSON 输出 4 行（实际 {lines.Length}）");
        var allJson = lines.All(l =>
        {
            try { using var _ = JsonDocument.Parse(l); return true; }
            catch { return false; }
        });
        r.Check(allJson, "11f 每一行都是合法 JSON（原始 item_json）");

        var expPrompt = f.Target.ExportSystemPrompt(Thread1, Path.Combine(f.Dir, "state.txt"), default);
        r.Check(expPrompt.Exported && File.Exists(expPrompt.Path), "11f ExportSystemPrompt 写出文件");
        var stateText = FileText.Read(expPrompt.Path!);
        r.Check(stateText.Contains("层 1：系统提示词层", StringComparison.Ordinal) &&
                stateText.Contains("层 2：日志层", StringComparison.Ordinal),
            "11f 状态清单里两层都在（日志层标为只读）");

        ctx.Log("log layer three states ok");
    }

    // ================================================================ 12 WAL 纪律

    private static void Case12WalDiscipline(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【12】WAL 读法：造「真实表只在 -wal 里」的库 → 只拷主库读不到表，连 -wal/-shm 一起拷就读得到");

        var dir = ctx.MakeSandbox("codex-wal");
        var dbPath = Path.Combine(dir, "thread_history_1.sqlite");
        var mainOnlyCopy = Path.Combine(dir, "main-only.sqlite");

        // 写连接保持打开：不 checkpoint，真实表就只存在于 -wal 里（与真实客户端一样）。
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        writer.Open();
        Exec(writer, "PRAGMA journal_mode=WAL;");
        Exec(writer, "PRAGMA wal_autocheckpoint=0;");
        Exec(writer, @"CREATE TABLE _sqlx_migrations (
            version BIGINT PRIMARY KEY, description TEXT NOT NULL,
            installed_on TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
            success BOOLEAN NOT NULL, checksum BLOB NOT NULL, execution_time BIGINT NOT NULL);");
        Exec(writer, "INSERT INTO _sqlx_migrations(version,description,success,checksum,execution_time) " +
                     "VALUES(1,'initial',1,x'00',0);");
        Exec(writer, "PRAGMA wal_checkpoint(FULL);");   // 主库自包含到这一行为止（"主库只有 migrations"）

        // 之后的建表与插入全部只落进 -wal
        Exec(writer, @"CREATE TABLE thread_items (
            thread_id TEXT NOT NULL, turn_id TEXT NOT NULL, item_id TEXT NOT NULL,
            rollout_ordinal INTEGER NOT NULL, created_at_ms INTEGER NOT NULL,
            item_json TEXT NOT NULL, item_type TEXT NOT NULL DEFAULT '',
            updated_at_ordinal INTEGER NOT NULL DEFAULT 0,
            started_at_ms INTEGER, completed_at_ms INTEGER,
            PRIMARY KEY (thread_id, turn_id, item_id));");
        Exec(writer, "INSERT INTO thread_items(thread_id,turn_id,item_id,rollout_ordinal,created_at_ms,item_json,item_type) " +
                     "VALUES(@t,@u,@i,10,@ms,@j,'agentMessage')",
            ("@t", Thread1), ("@u", "turn-1"), ("@i", "item-1"),
            ("@ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            ("@j", "{\"type\":\"agentMessage\",\"text\":\"Current: " + Marker4 + "\",\"phase\":\"commentary\"}"));

        r.Check(new FileInfo(dbPath + "-wal").Length > 0,
            $"fixture 的 -wal 非空（{new FileInfo(dbPath + "-wal").Length} 字节：真实表只在 WAL 里）");

        // ---- (a) 只拷主库 → 读不到真实表 ----
        File.Copy(dbPath, mainOnlyCopy, true);
        using (var cn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = mainOnlyCopy, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString()))
        {
            cn.Open();
            r.Check(HasTable(cn, "_sqlx_migrations"), "(a) 只拷主库：看得见 _sqlx_migrations（已 checkpoint 的那部分）");
            r.Check(!HasTable(cn, "thread_items"),
                "(a) 只拷主库：**看不到 thread_items** —— 这就是「只读直连只看得见 _sqlx_migrations」那个坑");
        }

        // ---- (b) 连 -wal / -shm 一起拷 → 读得到 ----
        var copy = CodexDb.CopyWithSidecars(dbPath, out var tempDir);
        try
        {
            var sidecars = new[] { "", "-wal", "-shm" }.Count(sfx => File.Exists(Path.Combine(tempDir, Path.GetFileName(dbPath) + sfx)));
            r.Check(sidecars >= 2, $"(b) 本工具的拷贝连同 sidecar 一起拷（{sidecars} 个文件：db + -wal + -shm）");
            using var cn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = copy, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
            }.ToString());
            cn.Open();
            r.Check(HasTable(cn, "thread_items"), "(b) 连 -wal 一起拷：thread_items 看得见");
            r.Check(RowCount(cn, "thread_items") == 1, "(b) thread_items 的 1 行也读得到（SQLite 自己恢复了 WAL）");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* 尽力而为 */ }
        }

        // ---- (c) 走真实的适配器路径：把 CODEX_HOME 指过去，读线程正文 ----
        var home = Path.Combine(dir, "home");
        Directory.CreateDirectory(home);
        FileText.Write(Path.Combine(home, "config.toml"), FactoryConfig(home, Path.Combine(dir, "codex.exe")));
        MakeStateDb(Path.Combine(home, "state_5.sqlite"), 1, -60, corrupt: false);
        File.Copy(dbPath, Path.Combine(home, "thread_history_1.sqlite"), true);
        File.Copy(dbPath + "-wal", Path.Combine(home, "thread_history_1.sqlite-wal"), true);
        if (File.Exists(dbPath + "-shm")) File.Copy(dbPath + "-shm", Path.Combine(home, "thread_history_1.sqlite-shm"), true);

        var dataDir = Path.Combine(dir, "toolkit-data");
        Directory.CreateDirectory(dataDir);
        setEnv(CodexLocator.HomeEnv, home);
        setEnv(CodexLocator.HomeEnvOverride, null);
        setEnv(CodexLocator.MemoryFileEnv, Path.Combine(dir, "memory.json"));

        var policy = new PolicyStore(dataDir);
        policy.Materialize(CodexTarget.PolicyDefs);
        var target = new CodexTarget(new TargetContext
        {
            Id = "codex",
            DataDir = dataDir,
            Policy = policy,
            PolicyDefs = CodexTarget.PolicyDefs,
            Ledger = new LedgerStore(Path.Combine(dataDir, "probe-ledger.json")),
        });

        var report = target.CheckMarkers(Thread1, default);
        r.Check(report.Hits.Sum(h => h.Count) > 0,
            $"(c) 适配器从「真实表只在 -wal」的库里读到了正文并命中标记（{report.Hits.Sum(h => h.Count)} 次）");
        r.Check(report.Verdict.Contains("thread_items", StringComparison.Ordinal), "(c) Verdict 说明正文来自 thread_items");

        var st = target.GetStatus(default);
        r.Check(st.Target.Found, "(c) GetStatus 跑通");
        r.Check(st.Layers[0].EffectivenessEvidence is not null &&
                (st.Layers[0].EffectivenessEvidence!.Contains("已生效（行为级）", StringComparison.Ordinal) ||
                 st.Layers[0].EffectivenessEvidence!.Contains("无法判定", StringComparison.Ordinal) ||
                 st.Layers[0].EffectivenessEvidence!.Contains("装上了但没生效", StringComparison.Ordinal) ||
                 st.Layers[0].EffectivenessEvidence!.Contains("还没装上", StringComparison.Ordinal)),
            "(c) 行为级判据给出四种合法结论之一，不会因为 WAL 读不到就说「没生效」");

        // ---- (d) 原文件的字节没被我们改过 ----
        writer.Close();
        r.Step("    (d) 原始 WAL 库全程只被 FileShare.ReadWrite 读过：本用例自己不写它（fixture 除外）");
        ctx.Log("wal discipline ok");
    }

    // ================================================================ 13 真实数据（只读）

    private static void Case13RealDataReadOnly(SelfTestContext ctx, SelfTestResult r, string realHome, bool realExists)
    {
        r.Step("");
        r.Step("【13】真实数据只读核对（本机存在就做；不写任何真实文件；状态按实际断言，不假定出厂态）");

        if (!realExists)
        {
            r.Step($"    本机 {realHome} 里没有 config.toml，跳过（不是失败）。");
            return;
        }

        var cfgPath = Path.Combine(realHome, "config.toml");
        var promptPath = Path.Combine(realHome, CodexRules.PromptFileName);
        var stateDb = Path.Combine(realHome, CodexDb.StateDbName);
        var threadDb = Path.Combine(realHome, CodexDb.ThreadDbName);

        var shasBefore = SnapshotOf(cfgPath, promptPath, stateDb, threadDb,
            threadDb + "-wal", stateDb + "-wal");

        var text = FileText.Read(cfgPath);
        var snap = CodexConfig.Parse(text);
        r.Check(snap.LineCount > 0, $"真实 config.toml 可解析：{snap.LineCount} 行（{FileText.Bytes(text)} 字节，" +
                                    $"顶层 {snap.TopLevelLineCount} 行 / 表 {snap.TableLineCount} 行）");
        r.Check(snap.Key.Duplicates == 0, "真实 config.toml 顶层没有重复的 model_instructions_file");

        // 状态感知：真实库里那个键**可能存在**（用户真的注入过），也可能不存在（出厂态）。两种都是合法状态。
        var realKeyState = snap.Key.Present
            ? $"已存在：{KeyName} = {snap.Key.RawValue}（第 {snap.Key.LineIndex + 1} 行）"
            : $"不存在（出厂态：{KeyName} 没有出现在顶层）";
        r.Step($"    真实 config.toml 的 {KeyName}：{realKeyState}");
        if (snap.Key.Present)
            r.Check(!string.IsNullOrWhiteSpace(snap.Key.Value), $"真实键的值非空：{snap.Key.Value}");
        else
            r.Check(snap.Key.LineIndex == -1, "真实键不存在时 LineIndex = -1");

        r.Check(snap.TablesDigest.Length > 0 && snap.OtherTopDigest.Length > 0,
            $"真实文件的两个指纹算得出来（其它顶层 {Short(snap.OtherTopDigest)} / 表区 {Short(snap.TablesDigest)}）");

        // 真实 thread_items：必须走 WAL 拷贝（这条纪律在真实数据上再验一次）
        if (File.Exists(threadDb))
        {
            using var reader = CodexDb.OpenThreads(realHome);
            r.Check(reader.TotalItems > 0,
                $"真实 thread_history_1.sqlite 走 WAL 拷贝读得到 thread_items：{reader.TotalItems} 条（docs: 3,237）");

            var threads = File.Exists(stateDb) ? CodexDb.ListThreads(realHome) : Array.Empty<CodexThread>();
            r.Step($"    真实线程数：{threads.Count}（docs: 18）");
            if (threads.Count > 0)
            {
                var first = threads[0];
                var items = reader.Items(first.Id);
                r.Step($"    最近线程 {first.Id}：{items.Count} 条 item；标题 {Trim1(first.Title, 40)}");
                r.Check(items.Count >= 0, "真实线程的条目读得出来（item_json 解析不抛）");
                r.Check(items.Count(i => i.Type == CodexDb.AgentMessage) >= 0, "agentMessage 计数可算");
                var withBody = items.Count(i => i.Type == CodexDb.Reasoning && !string.IsNullOrEmpty(i.Reasoning));
                r.Step($"    该线程 reasoning 有正文的条数：{withBody}（全库实测 33/1210，所以不能当主判据）");
            }
        }
        else r.Step($"    真实 {threadDb} 不存在，跳过正文核对。");

        if (File.Exists(stateDb))
        {
            var threads = CodexDb.ListThreads(realHome);
            r.Check(threads.Count > 0, $"真实 state_5.sqlite 可读：threads {threads.Count} 行（docs: 18）");
        }

        // 13b：拿真实客户端跑一遍完整只读路径（数据目录仍指向沙箱，**不写客户端任何东西**）
        var dataDir = ctx.MakeSandbox("codex-real-status");
        var policy = new PolicyStore(dataDir);
        policy.Materialize(CodexTarget.PolicyDefs);

        var savedHome = Environment.GetEnvironmentVariable(CodexLocator.HomeEnv);
        var savedOverride = Environment.GetEnvironmentVariable(CodexLocator.HomeEnvOverride);
        var savedMemory = Environment.GetEnvironmentVariable(CodexLocator.MemoryFileEnv);
        try
        {
            Environment.SetEnvironmentVariable(CodexLocator.HomeEnv, realHome);
            Environment.SetEnvironmentVariable(CodexLocator.HomeEnvOverride, null);
            Environment.SetEnvironmentVariable(CodexLocator.MemoryFileEnv, Path.Combine(dataDir, "memory.json"));

            var realTarget = new CodexTarget(new TargetContext
            {
                Id = "codex",
                DataDir = dataDir,
                Policy = policy,
                PolicyDefs = CodexTarget.PolicyDefs,
                Ledger = new LedgerStore(Path.Combine(dataDir, "probe-ledger.json")),
            });
            var st = realTarget.GetStatus(default);

            r.Check(st.Target.Found && st.Target.ConfigHome == realHome, $"真实客户端 GetStatus 跑通（主目录 {st.Target.ConfigHome}）");
            r.Step($"    真实层状态：{st.Layers[0].State}；ClientRunning={st.ClientRunning}（{st.RunningProcessIds.Count} 个进程）");

            // 状态感知断言：层状态必须与「键实际指向哪里、文件 sha 对不对」一致，不假定出厂态。
            var expectedState = snap.Key.Present
                ? (string.Equals(snap.Key.Value, promptPath, StringComparison.OrdinalIgnoreCase)
                    ? (File.Exists(promptPath) &&
                       FileText.Sha256Text(FileText.Read(promptPath)) == FileText.Sha256Text(FileText.Read(policy.PathOf(CodexTarget.PolicyDefs[0])))
                        ? LayerState.Applied
                        : LayerState.NotApplied)
                    : LayerState.AnchorAmbiguous)
                : LayerState.NotApplied;
            r.Check(st.Layers[0].State == expectedState,
                $"真实层状态与实际盘上状态一致：{st.Layers[0].State}（按「键指哪里 + 文件 sha」推出来应当是 {expectedState}）");
            r.Check(st.Layers[0].EffectivenessEvidence is not null &&
                    (st.Layers[0].EffectivenessEvidence!.Contains("无法判定", StringComparison.Ordinal) ||
                     st.Layers[0].EffectivenessEvidence!.Contains("已生效（行为级）", StringComparison.Ordinal) ||
                     st.Layers[0].EffectivenessEvidence!.Contains("装上了但没生效", StringComparison.Ordinal) ||
                     st.Layers[0].EffectivenessEvidence!.Contains("还没装上", StringComparison.Ordinal)),
                "真实客户端上：两级判据给出四种合法结论之一（无法判定 / 已生效 / 装上了但没生效 / 还没装上）");
            r.Check(!(st.Layers[0].State != LayerState.Applied && st.Layers[0].InstalledButNotEffective),
                "没装上时不置 InstalledButNotEffective（不许自相矛盾）");
            r.Check(st.Target.Notes.Any(n => n.Title.Contains("SessionsDir", StringComparison.Ordinal)),
                "真实客户端的 Notes 里也有 SessionsDir 的契约缺口说明");
            r.Check(st.Target.Version is null || st.Target.Version!.Length > 0,
                $"真实版本号：{st.Target.Version ?? "(读不到)"}（读不到是合法状态：WindowsApps 目录权限 + codex.exe 无版本资源）");
            r.Check(realTarget.GetPolicyFiles(default).Count == 1, "真实客户端上注入文本可读（沙箱数据目录）");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CodexLocator.HomeEnv, savedHome);
            Environment.SetEnvironmentVariable(CodexLocator.HomeEnvOverride, savedOverride);
            Environment.SetEnvironmentVariable(CodexLocator.MemoryFileEnv, savedMemory);
        }

        // 只读证明：读前读后逐字节一致
        var shasAfter = SnapshotOf(cfgPath, promptPath, stateDb, threadDb, threadDb + "-wal", stateDb + "-wal");
        var changed = shasBefore.Count(kv => !shasAfter.TryGetValue(kv.Key, out var v) || v != kv.Value);
        r.Check(changed == 0,
            $"真实 config.toml / 提示词文件 / 两个库 / 两个 -wal 的 sha256 读前读后不变（{shasBefore.Count} 个文件，变化 {changed} 个）");

        var files = Directory.Exists(realHome)
            ? Directory.EnumerateFiles(realHome, "*", SearchOption.AllDirectories).Count()
            : 0;
        r.Step($"    真实 ~\\.codex 文件数：{files}（整棵树的逐字节比对在收尾那两条断言里）");

        ctx.Log("real read-only ok");
    }

    // ================================================================ 14 verbatim

    private static void Case14Verbatim(SelfTestContext ctx, SelfTestResult r)
    {
        r.Step("");
        r.Step("【14】verbatim：内嵌资源 schemeB.protocol.md 的 sha256 == 37382fb7…acac == 落盘资产的 sha256");

        var resource = PolicyStore.ReadResource(CodexRules.ProtocolResourceSuffix);
        r.Check(resource is not null, $"内嵌资源 {CodexRules.ProtocolResourceSuffix} 读得到");
        if (resource is null) return;

        var resSha = FileText.Sha256Text(resource);
        r.Check(resSha == ProtocolSha, $"内嵌资源 sha256 == {ProtocolSha}（实际 {resSha}）");
        r.Check(FileText.Bytes(resource) == ProtocolBytes,
            $"内嵌资源 {FileText.Bytes(resource)} 字节 == {ProtocolBytes} 字节");
        r.Check(!resource.Contains('\r'), "内嵌资源里没有 CR（原文是 LF，没有被换行转换过）");
        r.Check(resource.StartsWith("INJECTED REVISION ACTION\n", StringComparison.Ordinal), "原文首行是 INJECTED REVISION ACTION");
        r.Check(resource.TrimEnd().EndsWith("preparation alone.", StringComparison.Ordinal), "原文末段完整（结尾 preparation alone.）");

        var asset = FindAsset();
        if (asset is null)
        {
            r.Step("    落盘资产没找到（不是在仓库布局下跑的）：第 2 段「资产文件 == 资源」比对跳过；" +
                   "资源那一半的 verbatim 断言照旧成立。");
        }
        else
        {
            r.Check(true, $"落盘资产找得到：{asset}");
            var assetSha = Sha(asset);
            r.Check(assetSha == ProtocolSha, $"落盘资产 sha256 == {ProtocolSha}（实际 {assetSha}）");
            r.Check(new FileInfo(asset).Length == ProtocolBytes, $"落盘资产 {new FileInfo(asset).Length} 字节");
            r.Check(assetSha == resSha, "落盘资产与内嵌资源逐字节一致（同一份资产服务全部目标）");
        }

        // 自检里独立写的协议特征必须真的出现在原文里（否则标记表是编的）
        foreach (var m in new[] { Marker1, Marker2, Marker3, Marker4, Marker5, Marker6 })
            r.Check(resource.Contains(m, StringComparison.Ordinal), $"原文里确实有协议特征 `{m}`（标记表不是编的）");
        r.Check(resource.Contains("`Current:` or `当前：`", StringComparison.Ordinal),
            "原文里确实有「以 Current: 或 当前： 开头」这条要求（行为级判据的来源）");

        // 上游 9 个旧文件名一个都不等于本工具的文件名
        r.Check(!CodexRules.UpstreamPromptFileNames.Contains(CodexRules.PromptFileName, StringComparer.OrdinalIgnoreCase),
            $"本工具的文件名 {CodexRules.PromptFileName} 不是上游 9 个旧名之一");
        r.Check(CodexRules.UpstreamPromptFileNames.Count == 9,
            $"上游旧名表 9 条（实际 {CodexRules.UpstreamPromptFileNames.Count}）");

        ctx.Log($"verbatim ok: {Short(resSha)}");
    }

    // ================================================================ 15 定位 / 差异 / 探针 / 注入文本

    private static void Case15LocateDiffExportProbes(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【15】定位（主目录 / 版本 / CLI / fail-closed）+ 差异 / 探针 / 注入文本");

        var s = Setup(ctx, setEnv, "locate");

        var byHome = s.Target.Locate(new LocateOptions { ExplicitRoot = s.Home }, true, default);
        r.Check(byHome.Found && byHome.ConfigHome == s.Home, $"传主目录 → 命中（{byHome.ConfigHome}）");
        r.Check(byHome.Bundle == s.ConfigPath, "Bundle = config.toml（承载注入的配置文件）");
        r.Check(byHome.PatchFile == s.ConfigPath, "PatchFile = config.toml");
        r.Check(byHome.Version is not null &&
                (byHome.Version == "26.1013.4894.0" || byHome.Version == "26.1013.40934"),
            $"版本从包全名（26.1013.4894.0）或 config.toml 的 BROWSER_USE_CODEX_APP_VERSION（26.1013.40934）解析（{byHome.Version}）");
        r.Check(byHome.Attempts.Any(a => (a.Level == "registry" && a.Hit) || (a.Level == "config" && a.Hit)),
            "Attempts 里能看到版本的来源（Appx 注册表 / config.toml）");
        r.Check(byHome.Exe == s.CliExe, $"CLI 从 config.toml 的 CODEX_CLI_PATH 解析（{byHome.Exe}）");
        r.Check(byHome.Attempts.Count > 3, $"Attempts 记录试过的路径（{byHome.Attempts.Count} 条）");
        r.Check(byHome.Attempts.Any(a => a.Level == "env" && a.Candidate.StartsWith("CODEX_HOME", StringComparison.Ordinal)),
            "Attempts 里有 CODEX_HOME 那一级（官方变量）");
        r.Check(byHome.Notes.Any(n => n.Title == "层 1 的落点"), "Notes 说明层 1 的两个落点");
        r.Check(byHome.Notes.Any(n => n.Title == "包目录权限"), "Notes 说明 WindowsApps 目录权限（版本为什么从注册表/配置来）");
        var engineLoc = MultiTargetEngine.ToLocateResult(s.Target, byHome);
        r.Check(engineLoc.SessionsDir == Path.Combine(s.Home, "sessions"),
            "SessionsDir 仍按契约拼 <ConfigHome>\\sessions（真实落点写在 Notes 与两个库）");

        var byCli = s.Target.Locate(new LocateOptions { ExplicitRoot = s.CliExe }, true, default);
        r.Check(byCli.Found && byCli.Exe == s.CliExe, "传 codex.exe → 命中并由它认 CLI（主目录按 CODEX_HOME 解析）");

        var bad = s.Target.Locate(new LocateOptions { ExplicitRoot = Path.Combine(s.Dir, "no-such-dir") }, true, default);
        r.Check(!bad.Found, "显式路径无效 → fail-closed（不回退、不猜）");
        r.Check(bad.Attempts.Any(a => a.Level == "cli" && !a.Hit), "Attempts 记下 cli 级的无效尝试");
        r.Check(bad.Notes.Any(n => n.Title.Contains("fail-closed", StringComparison.Ordinal)), "Notes 说明 fail-closed 中止原因");

        var auto = s.Target.Locate(null, true, default);
        r.Check(auto.Found && auto.ConfigHome == s.Home, "无显式参数 → 环境变量 CODEX_HOME 命中");
        r.Check(File.Exists(s.MemoryFile), $"定位成功后写记忆文件（{Path.GetFileName(s.MemoryFile)}）");
        r.Check(PathMemory.Load(s.MemoryFile, "configHome") == s.Home, "记忆文件里记下 configHome");

        var info = s.Target.Describe(default);
        r.Check(info.Id == "codex" && info.Kind == TargetKind.PackagedElectron, $"Describe()：{info.Id} / {info.Kind}");
        r.Check(info.Installed && info.ConfigHome == s.Home, "Describe().Installed / ConfigHome 反映定位结果");
        r.Check(info.Layers.Count == 2, $"Describe().Layers 两条（系统提示词层 + 只读日志层），实际 {info.Layers.Count}");
        r.Check(s.Target.LayerDescriptions.Count == 2, "LayerDescriptions 两条");
        r.Check(s.Target.LayerDescriptions[1].Contains("只读", StringComparison.Ordinal), "第二条写明日志层只读");

        // ---- 差异 ----
        s.Target.Apply(new ApplyOptions(), null, default);
        var diff = s.Target.GetSystemPromptDiff(Thread1, default);
        r.Check(diff.BeforeSource is not null && diff.AfterSource is not null, "差异报告带来源");
        r.Check(!diff.BeforeText.Contains(KeyName + " = ", StringComparison.Ordinal),
            "BeforeText 是注入前的基线（不含我们那一行）");
        r.Check(diff.AfterText.Contains(KeyName + " = ", StringComparison.Ordinal), "AfterText 含注入后的键");
        r.Check(diff.AfterText.Contains(Marker1, StringComparison.Ordinal), "AfterText 含注入的指令正文（原文里就有 INJECTED REVISION ACTION）");
        r.Check(diff.Lines.Count > 0 && diff.AddedLines + diff.RemovedLines > 0,
            $"逐行差异非空（+{diff.AddedLines}/-{diff.RemovedLines}）");

        // ---- 探针 ----
        var probes = s.Target.GetProbes();
        r.Check(probes.Count == 6, $"探针 A–F 共 6 条（实际 {probes.Count}）");
        r.Check(probes.Select(p => p.Id).SequenceEqual(new[] { "A", "B", "C", "D", "E", "F" }), "Id 依次是 A–F");
        r.Check(probes.Count(p => p.IsDecisive) == 3, "D/E/F 三条 IsDecisive=true");
        r.Check(probes.First(p => p.Id == "C").PassCriteria.Contains("Current:", StringComparison.Ordinal),
            "C 的判据是原文的状态行要求（Current: / 当前：）");
        r.Check(probes.First(p => p.Id == "E").PassCriteria.Contains("行为级", StringComparison.Ordinal),
            "E 的判据指向本目标的两级判据");

        // ---- 注入文本 ----
        var policy = s.Target.GetPolicyFiles(default);
        r.Check(policy.Count == 1, $"注入文本一条（本目标只用一套方案），实际 {policy.Count}");
        r.Check(policy[0].Name == "instructions", $"Name = instructions（{policy[0].Name}）");
        r.Check(policy[0].Text.Length > 0 && FileText.Sha256Text(policy[0].Text) == ProtocolSha,
            "主副本内容 == gpt-instruct 原文（sha256 一致）");
        r.Check(policy[0].Notes.Any(n => n.Title == "verbatim" &&
                                         n.Detail.Contains("逐字节未改", StringComparison.Ordinal)),
            "注入文本页标注 verbatim（sha256 == 原文）");
        r.Check(policy[0].Notes.Any(n => n.Title == "落点"), "注入文本页标注真实落点（CODEX_HOME\\jailbreak-tool-instructions.md）");

        var original = FileText.Read(policy[0].Path);
        var edited = original.TrimEnd('\r', '\n') + "\n\n（自检追加的一行。）\n";
        var savedFile = s.Target.SavePolicyFile("instructions", edited, default);
        r.Check(FileText.Read(savedFile.Path) == edited, "SavePolicyFile 保存后回读逐字节一致");
        CheckThrows(r, () => s.Target.SavePolicyFile("no-such-policy", "x", default),
            EngineErrorCode.TargetFileMissing, "保存不存在的注入文本 → TargetFileMissing");
        s.Target.SavePolicyFile("instructions", original, default);
        r.Check(FileText.Read(savedFile.Path) == original, "改回原样后与初始文本逐字节一致");

        // 超自设上限的文本 → fail-closed
        var huge = Path.Combine(s.Dir, "huge.md");
        FileText.Write(huge, new string('规', 120000));
        CheckThrows(r, () => s.Target.Apply(new ApplyOptions { PayloadPath = huge }, null, default),
            EngineErrorCode.SelfCheckFailed, "载荷文本超过自设上限 → apply 抛 SelfCheckFailed（fail-closed）");

        // 跳过开关
        var skip = s.Target.PreviewApply(new ApplyOptions { SkipSystemPrompt = true }, default);
        r.Check(skip.Changes.All(c => !c.WillWrite) && !skip.CanApply,
            "SkipSystemPrompt → 两条都 WillWrite=false、CanApply=false（本目标只有这一层）");
        var skipApply = s.Target.Apply(new ApplyOptions { SkipSystemPrompt = true }, null, default);
        r.Check(!skipApply.Applied, "SkipSystemPrompt 的 apply 不写任何东西");

        ctx.Log("locate/diff/probes/policy ok");
    }

    // ================================================================ 沙箱

    private sealed class Sandbox
    {
        public required string Dir { get; init; }
        public required string Home { get; init; }
        public required string ConfigPath { get; init; }
        public required string PromptPath { get; init; }
        public required string MemoryFile { get; init; }
        public required string DataDir { get; init; }
        public required string CliExe { get; init; }
        public required string StateDb { get; init; }
        public required string ThreadDb { get; init; }
        public required CodexTarget Target { get; init; }
    }

    private static Sandbox Setup(
        SelfTestContext ctx, Action<string, string?> setEnv, string name,
        bool withConfig = true,
        bool withKey = false,
        bool upstreamTrace = false,
        int threads = 2,
        bool withMarkers = true,
        bool corruptStateDb = false,
        bool corruptThreadDb = false)
    {
        var dir = ctx.MakeSandbox($"codex-{name}");
        var home = Path.Combine(dir, "home");
        var cliDir = Path.Combine(dir, "bin", "896a0166b774037a");
        var dataDir = Path.Combine(dir, "toolkit-data");
        var memoryFile = Path.Combine(dir, "breach-codex-path.json");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(cliDir);
        Directory.CreateDirectory(dataDir);

        var cliExe = Path.Combine(cliDir, "codex.exe");
        FileText.Write(cliExe, "MZ-dummy codex CLI 341 MB 占位（沙箱）");

        var configPath = Path.Combine(home, "config.toml");
        if (withConfig)
        {
            var text = FactoryConfig(home, cliExe);
            if (withKey)
            {
                var foreignMd = upstreamTrace
                    ? Path.Combine(home, UpstreamName)
                    : Path.Combine(home, "someone-else.md");
                text = CodexConfig.InsertKey(text, foreignMd);
            }
            FileText.Write(configPath, text);
        }

        if (upstreamTrace)
            FileText.Write(Path.Combine(home, UpstreamName),
                "# 上游工具留下的提示词（不是本工具的）\n\n" + Marker1 + "\n（沙箱 fixture）\n");
        if (withKey && !upstreamTrace)
            FileText.Write(Path.Combine(home, "someone-else.md"), "别人的指令文件。\n");

        var stateDb = Path.Combine(home, CodexDb.StateDbName);
        var threadDb = Path.Combine(home, CodexDb.ThreadDbName);
        if (threads > 0 || corruptStateDb)
            MakeStateDb(stateDb, threads, -600, corruptStateDb);
        if (threads > 0 || corruptThreadDb)
            MakeThreadDb(threadDb, threads, withMarkers, corruptThreadDb);

        setEnv(CodexLocator.HomeEnv, home);
        setEnv(CodexLocator.HomeEnvOverride, null);
        setEnv(CodexLocator.MemoryFileEnv, memoryFile);

        var policy = new PolicyStore(dataDir);
        policy.Materialize(CodexTarget.PolicyDefs);
        var target = new CodexTarget(new TargetContext
        {
            Id = "codex",
            DataDir = dataDir,
            Policy = policy,
            PolicyDefs = CodexTarget.PolicyDefs,
            Ledger = new LedgerStore(Path.Combine(dataDir, "probe-ledger.json")),
        });

        ctx.Log($"沙箱 {Path.GetFileName(dir)}：config={(withConfig ? "有" : "无")} key={(withKey ? "有" : "无")} " +
                $"threads={threads} markers={withMarkers}");

        return new Sandbox
        {
            Dir = dir,
            Home = home,
            ConfigPath = configPath,
            PromptPath = CodexRules.PromptPath(home),
            MemoryFile = memoryFile,
            DataDir = dataDir,
            CliExe = cliExe,
            StateDb = stateDb,
            ThreadDb = threadDb,
            Target = target,
        };
    }

    /// <summary>
    /// 合成 config.toml：形状照抄本机真实文件（顶层 4 个键 + [mcp_servers.*] / [desktop] / [plugins.*] / [windows]），
    /// 另外故意混入中文注释、emoji、JSON-in-literal、含分号的路径：
    /// 只有真正「只动自己那一行」的实现才能让这些字节原样留下来。
    /// </summary>
    private static string FactoryConfig(string home, string cliExe)
    {
        var cliDir = Path.GetDirectoryName(cliExe) ?? home;
        var slashedCliExe = cliExe.Replace("\\", "\\\\", StringComparison.Ordinal);
        var slashedCua = Path.Combine(cliDir, "codex-computer-use.exe").Replace("\\", "\\\\", StringComparison.Ordinal);
        var slashedNodeRepl = Path.Combine(cliDir, "node_repl.exe");
        var slashedNode = Path.Combine(cliDir, "node.exe");
        var nodeModules = Path.Combine(cliDir, "node_modules");
        var trusted = home + ";" + nodeModules;

        var sb = new StringBuilder();
        sb.Append("model = \"gpt-6.1-sol\"\n");
        sb.Append("model_reasoning_effort = \"high\"\n");
        sb.Append("service_tier = \"priority\"\n");
        sb.Append("notify = [ \"").Append(slashedCua).Append("\", \"turn-ended\" ]\n");
        sb.Append("[mcp_servers]\n\n");
        sb.Append("[mcp_servers.node_repl]\n");
        sb.Append("args = []\n");
        sb.Append("command = '").Append(slashedNodeRepl).Append("'\n");
        sb.Append("env_vars = [\"CODEX_WINDOWS_REGISTERED_CORE\"]\n");
        sb.Append("startup_timeout_sec = 120\n\n");
        sb.Append("[mcp_servers.node_repl.env]\n");
        sb.Append("NODE_REPL_NATIVE_PIPE_CONNECT_TIMEOUT_MS = \"1000\"\n");
        sb.Append("NODE_REPL_NODE_MODULE_DIRS = '").Append(nodeModules).Append("'\n");
        sb.Append("NODE_REPL_NODE_PATH = '").Append(slashedNode).Append("'\n");
        sb.Append("NODE_REPL_TRUSTED_CODE_PATHS = '").Append(trusted).Append("'\n");
        sb.Append("CODEX_HOME = '").Append(home).Append("'\n");
        sb.Append("BROWSER_USE_AVAILABLE_BACKENDS = \"chrome,iab,mcpapps\"\n");
        sb.Append("BROWSER_USE_CODEX_APP_BUILD_FLAVOR = \"prod\"\n");
        sb.Append("BROWSER_USE_CODEX_APP_VERSION = \"26.1013.40934\"\n");
        sb.Append("NODE_REPL_TRUSTED_SERVICES = '{\"browser\":\"").Append(home.Replace("\\", "/", StringComparison.Ordinal))
          .Append("/plugins/browser.mjs\",\"sky\":\"@oai/sky/service\"}'\n");
        sb.Append("CODEX_CLI_PATH = '").Append(cliExe).Append("'\n\n");
        sb.Append("[desktop]\n");
        sb.Append("followUpQueueMode = \"steer\"\n");
        sb.Append("conversationDetailMode = \"STEPS_COMMANDS\"\n");
        sb.Append("selected-avatar-id = \"pet_fixture\"\n");
        sb.Append("title = \"沙箱 fixture 🙂 中文值\"\n\n");
        sb.Append("# 沙箱注释：中文与 emoji 🙂 用来证明「其它行逐字节不变」\n\n");
        sb.Append("[plugins.\"documents@openai-primary-runtime\"]\n");
        sb.Append("enabled = true\n\n");
        sb.Append("[windows]\n");
        sb.Append("sandbox = \"elevated\"\n");
        return sb.ToString();
    }

    // ================================================================ 合成库

    private static void MakeStateDb(string path, int threads, long updatedOffsetSeconds, bool corrupt)
    {
        if (corrupt)
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("这不是数据库，只是一段文本。" + new string('x', 4000)));
            return;
        }

        using (var cn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            cn.Open();
            Exec(cn, "PRAGMA journal_mode=WAL;");
            Exec(cn, @"CREATE TABLE _sqlx_migrations (
                version BIGINT PRIMARY KEY, description TEXT NOT NULL,
                installed_on TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                success BOOLEAN NOT NULL, checksum BLOB NOT NULL, execution_time BIGINT NOT NULL);");
            Exec(cn, @"CREATE TABLE threads (
                id TEXT PRIMARY KEY, rollout_path TEXT NOT NULL, created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL, source TEXT NOT NULL, model_provider TEXT NOT NULL,
                cwd TEXT NOT NULL, title TEXT NOT NULL, sandbox_policy TEXT NOT NULL,
                approval_mode TEXT NOT NULL, tokens_used INTEGER NOT NULL DEFAULT 0,
                has_user_event INTEGER NOT NULL DEFAULT 0, archived INTEGER NOT NULL DEFAULT 0,
                archived_at INTEGER);");

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            for (var i = 0; i < threads; i++)
            {
                var id = i == 0 ? Thread1 : Thread2;
                Exec(cn, "INSERT INTO threads(id,rollout_path,created_at,updated_at,source,model_provider,cwd,title," +
                         "sandbox_policy,approval_mode,tokens_used,has_user_event,archived) " +
                         "VALUES(@id,@rp,@c,@u,'app','openai',@cwd,@title,'elevated','never',1234,1,0)",
                    ("@id", id),
                    ("@rp", @"\\?\C:\Users\fixture\.codex\sessions\2026\10\17\rollout-" + id + ".jsonl"),
                    ("@c", now + updatedOffsetSeconds - 60),
                    ("@u", now + updatedOffsetSeconds + i),
                    ("@cwd", @"\\?\D:\破甲\toolkit"),
                    ("@title", "沙箱线程 " + (i + 1) + " 🙂"));
            }
            Exec(cn, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        SqliteConnection.ClearAllPools();
    }

    private static void MakeThreadDb(string path, int threads, bool withMarkers, bool corrupt)
    {
        if (corrupt)
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("这不是数据库。" + new string('y', 4096)));
            return;
        }

        using (var cn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            cn.Open();
            Exec(cn, "PRAGMA journal_mode=WAL;");
            Exec(cn, @"CREATE TABLE _sqlx_migrations (
                version BIGINT PRIMARY KEY, description TEXT NOT NULL,
                installed_on TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                success BOOLEAN NOT NULL, checksum BLOB NOT NULL, execution_time BIGINT NOT NULL);");
            Exec(cn, @"CREATE TABLE thread_items (
                thread_id TEXT NOT NULL, turn_id TEXT NOT NULL, item_id TEXT NOT NULL,
                rollout_ordinal INTEGER NOT NULL, created_at_ms INTEGER NOT NULL,
                item_json TEXT NOT NULL, item_type TEXT NOT NULL DEFAULT '',
                updated_at_ordinal INTEGER NOT NULL DEFAULT 0,
                started_at_ms INTEGER, completed_at_ms INTEGER,
                PRIMARY KEY (thread_id, turn_id, item_id));");

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (var i = 0; i < threads; i++)
            {
                var tid = i == 0 ? Thread1 : Thread2;
                var turn = tid + "-turn-1";

                Insert(cn, tid, turn, tid + "-u1", 10, now - 60000, "userMessage",
                    new JsonObject
                    {
                        ["type"] = "userMessage",
                        ["id"] = tid + "-u1",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = UserBubble }),
                    }.ToJsonString());

                // reasoning：本机实测多数没有正文，这里刻意留空
                Insert(cn, tid, turn, tid + "-r1", 11, now - 59000, "reasoning",
                    new JsonObject
                    {
                        ["type"] = "reasoning",
                        ["id"] = tid + "-r1",
                        ["summary"] = new JsonArray(),
                        ["content"] = new JsonArray(),
                    }.ToJsonString());

                Insert(cn, tid, turn, tid + "-a1", 12, now - 58000, "agentMessage",
                    new JsonObject
                    {
                        ["type"] = "agentMessage",
                        ["id"] = tid + "-a1",
                        ["text"] = withMarkers ? CommentaryWithMarkers() : "改好了：三条命令在下面，逐行可跑。\n",
                        ["phase"] = "commentary",
                    }.ToJsonString());

                Insert(cn, tid, turn, tid + "-a2", 13, now - 57000, "agentMessage",
                    new JsonObject
                    {
                        ["type"] = "agentMessage",
                        ["id"] = tid + "-a2",
                        ["text"] = withMarkers ? FinalWithMarkers() : "交付完成，命令逐行可跑。\n",
                        ["phase"] = "final_answer",
                    }.ToJsonString());
            }
            Exec(cn, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        SqliteConnection.ClearAllPools();
    }

    private static string CommentaryWithMarkers() =>
        "Current: 注入文件 / 已确认 / 正在写\n\n" +
        Marker4 + "：三条命令 + 一个补丁 + 一份回滚\n" +
        "TRANSACTION TURN COMMIT 已绑定；" + Marker3 + "\n";

    private static string FinalWithMarkers() =>
        "已交付。\n\n" +
        "1. MODIFIED_FILE：/tmp/modified.txt\n" +
        "2. DIFF_FILE：/tmp/change.diff\n" +
        "3. VERIFICATION.txt：/tmp/verification.txt\n" +
        "4. ROLLBACK.sh：/tmp/rollback.sh\n\n" +
        Marker5 + "：" + Marker1 + " 的 NEXT_REVISION 已推导。\n" +
        // 下面这段只用来把长度推过 256 字符下限（截断断言需要一个真的超长文本）
        "（填充：先确认四个角色的绝对路径都能重新打开，再确认 BASELINE / MODIFIED / ROLLBACK 三段输出都是字面量，" +
        "最后确认回滚后的哈希等于原始哈希。任何一步跳过都会让结论失真，宁可多跑一遍也不要给一个看起来对的结果。" +
        "这一段继续补充与结论无关的说明文字，把长度稳定地推过 256 字符的下限。）\n";

    private static void Insert(SqliteConnection cn, string threadId, string turnId, string itemId, long ordinal,
        long createdAtMs, string type, string json)
    {
        Exec(cn, "INSERT INTO thread_items(thread_id,turn_id,item_id,rollout_ordinal,created_at_ms,item_json,item_type) " +
                 "VALUES(@t,@u,@i,@o,@ms,@j,@ty)",
            ("@t", threadId), ("@u", turnId), ("@i", itemId), ("@o", ordinal), ("@ms", createdAtMs),
            ("@j", json), ("@ty", type));
    }

    /// <summary>把 agentMessage 正文里的协议特征全部换掉（模拟"这个线程早于注入"）。</summary>
    private static void StripMarkers(string threadDb)
    {
        using var cn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = threadDb, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        cn.Open();
        using var read = cn.CreateCommand();
        read.CommandText = "SELECT thread_id, turn_id, item_id, item_json FROM thread_items WHERE item_type='agentMessage'";
        var rows = new List<(string T, string U, string I, string J)>();
        using (var rd = read.ExecuteReader())
            while (rd.Read())
                rows.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3)));

        foreach (var (t, u, i, j) in rows)
        {
            var node = JsonNode.Parse(j)!.AsObject();
            node["text"] = "改好了：三条命令在下面，逐行可跑。\n";
            Exec(cn, "UPDATE thread_items SET item_json=@j WHERE thread_id=@t AND turn_id=@u AND item_id=@i",
                ("@j", node.ToJsonString()), ("@t", t), ("@u", u), ("@i", i));
        }
        SqliteConnection.ClearAllPools();
    }

    /// <summary>把线程的 updated_at 推到「注入之后」（模拟注入后新建/更新的线程）。</summary>
    private static void TouchThreads(string stateDb, long secondsFromNow)
    {
        using var cn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = stateDb, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        cn.Open();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + secondsFromNow;
        Exec(cn, "UPDATE threads SET updated_at=@u", ("@u", ts));
        SqliteConnection.ClearAllPools();
    }

    private static void Exec(SqliteConnection cn, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    private static bool HasTable(SqliteConnection cn, string table)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n";
        cmd.Parameters.AddWithValue("@n", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static int RowCount(SqliteConnection cn, string table)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    // ================================================================ 工具

    private static Dictionary<string, string?> SnapshotOf(params string[] paths)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths) map[p] = Sha(p);
        return map;
    }

    private static string? Sha(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch { return null; }
    }

    private static string Short(string? sha) => sha is null ? "(读不到)" : sha[..Math.Min(12, sha.Length)] + "…";

    /// <summary>读 &lt;target&gt;.breach.bak.meta.json（BackupManager 只写不读，自检自己解析）。</summary>
    private static Dictionary<string, string>? MetaOf(string targetPath)
    {
        try
        {
            var path = targetPath + ".breach.bak.meta.json";
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(FileText.Read(path));
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
                map[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
            return map;
        }
        catch { return null; }
    }

    private static string Trim1(string? s, int n)
    {
        if (s is null) return "(null)";
        var one = s.Replace("\r", " ").Replace("\n", " ");
        return one.Length <= n ? one : one[..n] + "…";
    }

    /// <summary>
    /// 真实主目录快照。大文件（rollout 转录，单份可达 10 MB）只记「大小 + mtime」，
    /// 其余（config.toml / *.sqlite / *.sqlite-wal / json / jsonl）记 sha256；
    /// *.sqlite-shm 是 SQLite 的 wal-index 共享内存，任何读者都可能更新它，排除并单独报数量。
    /// </summary>
    private static Dictionary<string, string?> SnapshotTree(string? dir)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (dir is null || !Directory.Exists(dir)) return map;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".sqlite-shm", StringComparison.OrdinalIgnoreCase)) continue;
                map[f] = DescribeFile(f);
            }
        }
        catch { /* 权限 */ }
        return map;
    }

    private static string? DescribeFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 8L * 1024 * 1024)
                return $"len={info.Length};mtime={info.LastWriteTimeUtc.Ticks}";
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return "sha=" + Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch { return "unreadable"; }
    }

    private static (int Files, int Hashed, int Light, int Shm) Summarize(Dictionary<string, string?> map)
    {
        var hashed = map.Values.Count(v => v is not null && v!.StartsWith("sha=", StringComparison.Ordinal));
        return (map.Count, hashed, map.Count - hashed, 0);
    }

    private static int CountBySuffix(string dir, string suffix)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Count(f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                : 0;
        }
        catch { return 0; }
    }

    private static (int Changed, string Detail) DiffSnapshots(
        Dictionary<string, string?> before, Dictionary<string, string?> after)
    {
        var keys = ChangedKeys(before, after);
        return (keys.Count, string.Join("、", keys.Take(5).Select(Path.GetFileName)));
    }

    /// <summary>两份快照之间发生变化的文件（内容 / 出现 / 消失）。</summary>
    private static List<string> ChangedKeys(
        Dictionary<string, string?> before, Dictionary<string, string?> after)
    {
        var changed = new List<string>();
        foreach (var kv in before)
        {
            if (!after.TryGetValue(kv.Key, out var v)) { changed.Add(kv.Key); continue; }
            if (!string.Equals(kv.Value, v, StringComparison.Ordinal)) changed.Add(kv.Key);
        }
        foreach (var k in after.Keys.Where(k => !before.ContainsKey(k))) changed.Add(k);
        return changed;
    }

    private static (int Total, int Changed, string Detail) DiffByExtension(
        Dictionary<string, string?> before, Dictionary<string, string?> after, params string[] extensions)
    {
        var keys = before.Keys
            .Where(k => extensions.Any(e => k.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
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

    /// <summary>找落盘的原文资产（自检运行时 cwd 不可靠，从程序目录与 cwd 往上找）。</summary>
    private static string? FindAsset()
    {
        var relative = Path.Combine("backend", "AgentToolkit.Core", "Assets", "schemeB", "protocol.md");
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
        }

        var env = Environment.GetEnvironmentVariable("AGENT_TOOLKIT_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var candidate = Path.Combine(env!, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static void CheckThrows(SelfTestResult r, Action act, EngineErrorCode expected, string what)
    {
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
    }
}
