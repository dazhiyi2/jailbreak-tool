using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;
using AgentToolkit.Core.Targets;
using AgentToolkit.Core.Targets.Opencode;
using Microsoft.Data.Sqlite;

namespace AgentToolkit.SelfTest;

/// <summary>
/// opencode 适配器的回归自检。硬性纪律：**只在沙箱副本上操作**。
///
/// 沙箱里有：假安装根（cli\2.0.32-x64\opencode-cli.exe 占位）、假配置根（opencode.json / AGENTS.md）、
/// 独立的 policy 数据目录，以及一个用 Microsoft.Data.Sqlite **现建的合成 opencode.db**
/// （WAL 模式 + 关掉自动 checkpoint，让数据真的留在 -wal 里——这正好验证「只读连接必须读 WAL」，
/// 也就是不许用 immutable=1 的理由；写连接全程不关，复刻真实客户端占库的情形）。
///
/// 环境变量 OPENCODE_ROOT / OPENCODE_CONFIG / OPENCODE_PATH_MEMORY_FILE / XDG_* 全部指向沙箱，
/// 真实 ~/.config/opencode 与 ~/.local/share/opencode 只做只读核对（用例 10），一个字节都不写。
/// </summary>
public sealed class OpencodeSelfTest : ITargetSelfTest
{
    public string TargetId => "opencode";
    public string Name => "opencode 适配器回归（沙箱合成 opencode.json / AGENTS.md / SQLite 会话库）";

    private const string SessionId = "ses_selftest0001Sandbox0000000001";
    private const string Reasoning1 = "让我先列目录，再排序取前 5。FIXTURE-REASONING-FEATURE\n\nPlatform is win32, PowerShell。先跑 Get-ChildItem。";
    private const string Reasoning2 = "第二次推理 FIXTURE-THINK-2：直接给结果就行。";

    /// <summary>
    /// 一段超过 256 字符的回复正文：用来验证 MaxCharsPerText 真的截断（下限保护 256）。
    /// 内容刻意不含任何注入标记——标记命中数只该来自常驻指令载荷。
    /// </summary>
    private const string LongReply =
        "这一段回复正文刻意写长，用来触发截断路径。目录里最大的五个文件依次是：第一个是体积最大的那个，" +
        "第二个略小一些，第三个和第四个相差不大，第五个刚好卡在边界上。排序的时候要注意单位换算，" +
        "字节和千字节别混着比，否则结论会反过来。为了把长度撑过两百五十六个字符，这里继续补充一些" +
        "与结论无关的说明文字：读取目录、过滤隐藏项、按长度降序、取前五条、逐行输出，每一步都可能出错，" +
        "所以每一步都单独核对一遍。截断判定用的是字符数而不是字节数，中文一个字算一个字符，所以必须真的" +
        "写够长度才能触发，靠堆多字节字符是蒙不过去的。于是这里再补一段：先确认目录存在，再确认权限足够，" +
        "再确认没有符号链接把大小算重，最后才排序取前五。任何一步跳过都会让结论失真，宁可多跑一遍也不要" +
        "给一个看起来对的结果。最后再强调一次：本段不含任何注入标记，标记命中数只该来自常驻指令载荷。";
    private const long TimeCreated = 1792193634693L;
    private const long TimeUpdated = 1792193641081L;

    /// <summary>出厂配置：带其它键与另一条 instructions 条目——apply 之后这些必须一个不少。</summary>
    private const string FactoryConfig =
        """
        {
          "$schema": "https://opencode.ai/config.json",
          "theme": "system",
          "model": "opencode-go/deepseek-v4.1-flash",
          "permission": {
            "edit": "allow",
            "bash": {
              "git push": "ask"
            }
          },
          "instructions": [
            "C:\\Users\\Public\\other-instructions.md"
          ]
        }
        """;

    /// <summary>没有 instructions 键的出厂配置（用例 1 的第二种形态）。</summary>
    private const string FactoryNoInstructions =
        """
        {
          "$schema": "https://opencode.ai/config.json",
          "theme": "dark",
          "autoupdate": false
        }
        """;

    private const string UserAgents = "# 我自己的约定\n\n这是用户自己的 AGENTS.md，工具不得直接覆盖（必须先备份）。\n";

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

        // 真实路径在任何 env 覆盖之前抓下来，供用例 10 的只读核对与「没碰真实目标」的哈希比对用。
        var realHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var realConfig = Path.Combine(realHome, ".config", "opencode");
        var realData = Path.Combine(realHome, ".local", "share", "opencode");
        var realDb = Path.Combine(realData, "opencode.db");
        var realConfigBefore = SnapshotDir(realConfig);
        var realDbShaBefore = ShaShared(realDb);
        var realWalShaBefore = ShaShared(realDb + "-wal");

        r.Step($"真实配置根（只读比对）：{realConfig}");
        r.Step($"真实会话库（只读比对）：{realDb}");

        try
        {
            Case01FactoryState(ctx, r, SetEnv);
            Case02PreviewReadOnly(ctx, r, SetEnv);
            Case03Apply(ctx, r, SetEnv);
            Case04Idempotent(ctx, r, SetEnv);
            Case05Revert(ctx, r, SetEnv);
            Case06ContaminatedBackup(ctx, r, SetEnv);
            Case07FailClosed(ctx, r, SetEnv);
            Case08Logs(ctx, r, SetEnv);
            Case09Effectiveness(ctx, r, SetEnv);
            Case11Locate(ctx, r, SetEnv);
            Case12DiffExportProbes(ctx, r, SetEnv);
        }
        finally
        {
            foreach (var kv in saved) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        }

        Case10RealDataReadOnly(ctx, r, realConfig, realDb);

        // 收尾：真实配置目录与真实数据库必须一个字节都没变（自检全程只读它们）。
        var realConfigAfter = SnapshotDir(realConfig);
        r.Check(SameSnapshot(realConfigBefore, realConfigAfter),
            $"自检全程没有改动真实配置目录（{realConfig}：{realConfigBefore.Count} 个文件，sha256 全部不变）");
        var realDbShaAfter = ShaShared(realDb);
        var realWalShaAfter = ShaShared(realDb + "-wal");
        r.Check(realDbShaBefore == realDbShaAfter, $"真实 opencode.db 的 sha256 不变（{Short(realDbShaAfter)}）");
        r.Check(realWalShaBefore == realWalShaAfter, $"真实 opencode.db-wal 的 sha256 不变（{Short(realWalShaAfter)}）");
        r.Step("    注：-shm 是 SQLite 的 wal-index 共享内存文件，只读连接可能触碰它，故不做 sha 断言。");

        return r;
    }

    // ================================================================ 1 出厂状态

    private static void Case01FactoryState(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【1】出厂状态：opencode.json 不存在 / 没有 instructions、AGENTS.md 不存在 → 两层 NotApplied / TargetAbsent");

        // 1a：opencode.json 不存在
        var a = Setup(ctx, setEnv, "factory-absent", config: null, agents: null);
        var st = a.Target.GetStatus(default);
        r.Check(st.Layers.Count == 2, $"状态里装配出 {st.Layers.Count} 层（期望 2）");
        var l1 = st.Layers[0];
        var l2 = st.Layers[1];
        r.Check(l1.Kind == LayerKind.PolicyText, $"层 1 的 Kind = {l1.Kind}（期望 PolicyText：配置指令层）");
        r.Check(l2.Kind == LayerKind.ContextPayload, $"层 2 的 Kind = {l2.Kind}（期望 ContextPayload）");
        r.Check(l1.TargetPath == a.ConfigFile, $"层 1 指向 {a.ConfigFile}");
        r.Check(l1.State == LayerState.TargetAbsent, $"opencode.json 不存在 → 层 1 {l1.State}（期望 TargetAbsent）");
        r.Check(l2.State == LayerState.TargetAbsent, $"AGENTS.md 不存在 → 层 2 {l2.State}（期望 TargetAbsent）");
        r.Check(st.LastMarkerReport is null, "没有会话 → LastMarkerReport 为 null（不是伪造的空报告）");

        // 1b：opencode.json 存在但没有 instructions 键
        var b = Setup(ctx, setEnv, "factory-no-instructions", config: FactoryNoInstructions, agents: null);
        var st2 = b.Target.GetStatus(default);
        r.Check(st2.Layers[0].State == LayerState.NotApplied,
            $"有配置但没有 instructions → 层 1 {st2.Layers[0].State}（期望 NotApplied）");
        r.Check(st2.Layers[1].State == LayerState.TargetAbsent, "AGENTS.md 仍不存在 → 层 2 TargetAbsent");
        r.Check(st2.Layers[0].Backup.Health == BackupHealth.Absent, "出厂态没有备份（Absent）");
        r.Check(st2.Target.Found && st2.Target.TargetId == "opencode", "EngineStatus.Target 由 ToLocateResult 装配且 Found");
        r.Check(st2.Target.ProfileDir == b.ConfigHome, $"ProfileDir = 配置根（{st2.Target.ProfileDir}）");
        r.Check(st2.Target.AsarPath is null && st2.Target.PatchFilePath is null,
            "没有 bundle / patch 文件（本目标的注入点是配置与指令文件）");
        ctx.Log($"factory: {l1.State}/{l2.State} {st2.Layers[0].State}");
    }

    // ================================================================ 2 预览只读

    private static void Case02PreviewReadOnly(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【2】预览只读：PreviewApply 前后两个文件的 sha256 都不变，Changes 含两层");

        var e = Setup(ctx, setEnv, "preview", config: FactoryConfig, agents: UserAgents);
        var cfgBefore = FileText.Sha256File(e.ConfigFile);
        var agentsBefore = FileText.Sha256File(e.AgentsFile);
        var mtimeBefore = File.GetLastWriteTimeUtc(e.AgentsFile);

        var preview = e.Target.PreviewApply(new ApplyOptions { DryRun = true }, default);

        r.Check(FileText.Sha256File(e.ConfigFile) == cfgBefore, "PreviewApply 前后 opencode.json 的 sha256 不变");
        r.Check(FileText.Sha256File(e.AgentsFile) == agentsBefore, "PreviewApply 前后 AGENTS.md 的 sha256 不变");
        r.Check(File.GetLastWriteTimeUtc(e.AgentsFile) == mtimeBefore, "PreviewApply 没有触碰 AGENTS.md 的修改时间");
        r.Check(!File.Exists(e.ConfigBackup), "预览没有建备份（opencode.json.breach.bak 不存在）");
        r.Check(!File.Exists(e.AgentsBackup), "预览没有建备份（AGENTS.md.breach.bak 不存在）");
        r.Check(preview.CanApply, $"CanApply = {preview.CanApply}（期望 true）");
        r.Check(preview.Changes.Any(c => c.Layer == LayerKind.PolicyText), "Changes 含配置指令层（PolicyText）");
        r.Check(preview.Changes.Any(c => c.Layer == LayerKind.ContextPayload), "Changes 含上下文载荷层（ContextPayload）");
        r.Check(preview.Changes.Where(c => c.Layer == LayerKind.PolicyText).All(c => c.WillWrite),
            "配置指令层标记为将写入（出厂配置里没有本工具的条目）");
        r.Check(preview.Changes.First(c => c.Layer == LayerKind.ContextPayload).WillWrite,
            "载荷层标记为将写入（现有 AGENTS.md 是用户自己的文件 → 会先备份）");
        r.Check(preview.Changes.First(c => c.Layer == LayerKind.PolicyText).DiffText?.Contains("+ ") == true,
            "配置指令层给了逐行差异");
        r.Check(preview.PreviewId.Length == 16, $"PreviewId 取输入内容 sha256 前 16 位：{preview.PreviewId}");
        r.Check(preview.TotalAfterBytes > preview.TotalBeforeBytes,
            $"体积 {preview.TotalBeforeBytes} → {preview.TotalAfterBytes} B");

        // DryRun 的 apply 同样一个字节都不写
        var dry = e.Target.Apply(new ApplyOptions { DryRun = true }, null, default);
        r.Check(!dry.Applied, "DryRun=true 的 apply：Applied=false");
        r.Check(!dry.RestartRequired, "DryRun=true 的 apply：RestartRequired=false");
        r.Check(FileText.Sha256File(e.ConfigFile) == cfgBefore && FileText.Sha256File(e.AgentsFile) == agentsBefore,
            "DryRun=true 的 apply 没有写任何文件（两个 sha256 都不变）");
        r.Check(!File.Exists(e.ConfigBackup) && !File.Exists(e.AgentsBackup), "DryRun=true 的 apply 不建备份");
        ctx.Log($"preview: canApply={preview.CanApply} id={preview.PreviewId}");
    }

    // ================================================================ 3 apply

    private static void Case03Apply(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【3】apply：instructions 出现我们的绝对路径、原有键一个不少、AGENTS.md == 注入文本、写完 JSON 能真实解析");

        var e = Setup(ctx, setEnv, "apply", config: FactoryConfig, agents: UserAgents);
        var payloadText = e.Target.Ctx.Policy.Read(OpencodeTarget.PolicyDefs.First(d => d.Name == "payload")) ?? "";
        var factoryKeys = Keys(FactoryConfig);
        var factoryEntries = OpencodeConfig.EntriesOf(FactoryConfig);

        var res = e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(res.Applied, "ApplyResult.Applied=true");
        r.Check(res.RestartRequired, "RestartRequired=true（常驻指令在会话启动时装配）");
        r.Check(res.VerificationHint.Contains("新会话"), $"VerificationHint 说明「新会话才生效」：{res.VerificationHint[..Math.Min(60, res.VerificationHint.Length)]}…");
        r.Check(res.Changes.Count == 2, $"Changes 两条（配置指令层 + 载荷层），实际 {res.Changes.Count}");

        // --- opencode.json ---
        var text = FileText.Read(e.ConfigFile);
        JsonNode? root = null;
        var parseOk = true;
        try { root = JsonNode.Parse(text); }
        catch (Exception ex) { parseOk = false; r.Fail($"写入后 opencode.json 解析失败：{ex.Message}"); }
        r.Check(parseOk, "写入后 opencode.json 能被 JsonNode 真实解析");
        r.Check(root is JsonObject, "写入后顶层是 JSON 对象");
        r.Check(text.Contains("\n  \"instructions\"", StringComparison.Ordinal),
            "序列化用 2 空格缩进（文本里出现 \\n + 两空格 + \"instructions\"）");

        var entries = OpencodeConfig.EntriesOf(text);
        r.Check(entries.Any(x => string.Equals(x, e.InstructionsPath, StringComparison.OrdinalIgnoreCase)),
            $"instructions 里出现本工具指令文件的绝对路径：{e.InstructionsPath}");
        r.Check(entries.Any(x => string.Equals(x, e.InstructionsPath, StringComparison.OrdinalIgnoreCase) &&
                                 x == e.InstructionsPath),
            "写入的是同一个绝对路径字符串（大小写一致）");
        r.Check(entries.Count(x => string.Equals(x, e.InstructionsPath, StringComparison.OrdinalIgnoreCase)) == 1,
            "只加入一条，没有重复");
        foreach (var fe in factoryEntries)
            r.Check(entries.Contains(fe), $"原有的 instructions 条目仍在：{fe}");

        var nowKeys = Keys(text);
        var missing = factoryKeys.Where(k => !nowKeys.Contains(k)).ToArray();
        r.Check(missing.Length == 0,
            missing.Length == 0
                ? $"原有键集合是结果的子集（{string.Join(", ", factoryKeys)}）"
                : $"原有键丢失：{string.Join(", ", missing)}");
        r.Check(nowKeys.Count == factoryKeys.Count + (factoryKeys.Contains("instructions") ? 0 : 1),
            $"键数量正确（原 {factoryKeys.Count} → 现 {nowKeys.Count}）");

        // --- 备份 ---
        r.Check(File.Exists(e.ConfigBackup), "opencode.json 已建首次备份 .breach.bak");
        r.Check(FileText.Read(e.ConfigBackup) == FactoryConfig, "备份与出厂配置逐字节一致");
        r.Check(!OpencodeMarkers.AllMarkers.Any(m => FileText.Count(FileText.Read(e.ConfigBackup), m) > 0),
            "备份里没有任何本工具标记（干净，可安全还原）");

        // --- AGENTS.md ---
        r.Check(File.Exists(e.AgentsFile), "载荷层 AGENTS.md 已部署");
        r.Check(FileText.Read(e.AgentsFile) == payloadText, "AGENTS.md 内容 == 注入文本（逐字节）");
        r.Check(File.Exists(e.AgentsBackup), "用户自己的 AGENTS.md 已备份为 AGENTS.md.breach.bak");
        r.Check(FileText.Read(e.AgentsBackup) == UserAgents, "用户文件备份逐字节一致");

        // --- 状态 ---
        var st = e.Target.GetStatus(default);
        r.Check(st.Layers[0].State == LayerState.Applied, $"apply 后层 1 = {st.Layers[0].State}（期望 Applied）");
        r.Check(st.Layers[1].State == LayerState.Applied, $"apply 后层 2 = {st.Layers[1].State}（期望 Applied）");
        r.Check(st.Layers[0].Notes.Any(n => n.Title == "管理方式"), "层 1 的 Notes 说明只增删自己那一条");
        r.Check(st.Layers[0].Notes.Any(n => n.Title == "指令文件"), "层 1 的 Notes 报告指令文件路径与体积");
        r.Check(st.Layers[1].Notes.Any(n => n.Title == "项目级同名文件"), "层 2 的 Notes 说明项目级 AGENTS.md 的加载规则");
        r.Check(st.Layers[1].Backup.Path == e.AgentsBackup && st.Layers[1].Backup.Health == BackupHealth.Clean,
            $"层 2 备份报告干净（{st.Layers[1].Backup.Path}）");
        ctx.Log($"apply ok: {e.ConfigFile}");
    }

    // ================================================================ 4 幂等

    private static void Case04Idempotent(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【4】幂等：第二次 apply 两个文件都无字节变化、Applied=false、WillWrite 全为 false");

        var e = Setup(ctx, setEnv, "idempotent", config: FactoryConfig, agents: UserAgents);
        e.Target.Apply(new ApplyOptions(), null, default);

        var cfgSha = FileText.Sha256File(e.ConfigFile);
        var agentsSha = FileText.Sha256File(e.AgentsFile);
        var agentsStamp = File.GetLastWriteTimeUtc(e.AgentsFile);
        var cfgStamp = File.GetLastWriteTimeUtc(e.ConfigFile);

        var second = e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(FileText.Sha256File(e.ConfigFile) == cfgSha, "第二次 apply 后 opencode.json 的 sha256 不变");
        r.Check(FileText.Sha256File(e.AgentsFile) == agentsSha, "第二次 apply 后 AGENTS.md 的 sha256 不变");
        r.Check(File.GetLastWriteTimeUtc(e.ConfigFile) == cfgStamp, "没有重写 opencode.json（修改时间不变）");
        r.Check(File.GetLastWriteTimeUtc(e.AgentsFile) == agentsStamp, "没有重写 AGENTS.md（修改时间不变）");
        r.Check(!second.Applied, $"第二次 apply 没有可写内容 → Applied={second.Applied}（不虚报）");
        r.Check(second.Changes.All(c => !c.WillWrite), "第二次 apply 的 Changes 里 WillWrite 全为 false");
        r.Check(second.Notes.Any(n => n.Title == "幂等"), "Notes 里有「幂等」说明");
        r.Check(OpencodeConfig.EntriesOf(FileText.Read(e.ConfigFile))
                .Count(x => string.Equals(x, e.InstructionsPath, StringComparison.OrdinalIgnoreCase)) == 1,
            "instructions 里我们的路径仍然只有一条（没有重复追加）");

        var preview = e.Target.PreviewApply(new ApplyOptions(), default);
        r.Check(!preview.CanApply, "两层都已到位 → CanApply=false（没有要写的东西）");
        r.Check(preview.Changes.All(c => !c.WillWrite), "预览的所有行 WillWrite=false（含载荷层）");
        r.Check(preview.TotalBeforeBytes == 0 && preview.TotalAfterBytes == 0,
            $"无改动时体积统计不虚报（{preview.TotalBeforeBytes} → {preview.TotalAfterBytes}）");
        r.Check(preview.Changes.SelectMany(c => c.Notes)
                .Any(n => n.Title.Contains("无需写入") || n.Detail.Contains("无需写入")),
            "载荷层给出「已是当前文本，无需写入（幂等）」说明");
        ctx.Log("idempotent ok");
    }

    // ================================================================ 5 revert

    private static void Case05Revert(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【5】revert：出厂态有的文件靠备份逐字节还原；只有「出厂即不存在」才允许删除");
        r.Step("    判据：备份是 apply 那一刻拍下的出厂态。有备份 → 用备份还原；无备份 → 只摘除本工具条目；"
             + "仅当文件是本工具从零创建（只有 $schema + instructions）才删除。");

        // ---- 5a：出厂态有 opencode.json（instructions 里另有别人的条目）+ 用户自己的 AGENTS.md ----
        var e = Setup(ctx, setEnv, "revert", config: FactoryConfig, agents: UserAgents);
        e.Target.Apply(new ApplyOptions(), null, default);
        var appliedCfg = FileText.ReadOrNull(e.ConfigFile);
        r.Check(appliedCfg is not null && OpencodeConfig.ContainsPath(appliedCfg, e.InstructionsPath),
            "5a 前置：apply 后 instructions 里已有本工具路径");
        r.Check(File.Exists(e.ConfigBackup), "5a 前置：apply 时拍了 opencode.json 的首次备份");

        var rev = e.Target.Revert(new RevertOptions(), null, default);
        var backCfg = FileText.ReadOrNull(e.ConfigFile);
        r.Check(rev.Reverted, "5a RevertResult.Reverted=true");
        r.Check(backCfg is not null, "5a 出厂态存在的文件撤销后必须还在（不许被删）");
        r.Check(backCfg == FactoryConfig, "5a opencode.json 与出厂内容逐字节一致（走备份还原，不是重新序列化）");
        r.Check(FileText.Sha256File(e.ConfigFile) == FileText.Sha256Text(FactoryConfig),
            "5a opencode.json 的 sha256 == 出厂文本的 sha256");
        var entriesBack = OpencodeConfig.EntriesOf(backCfg);
        r.Check(entriesBack.Count == 1 && entriesBack[0] == @"C:\Users\Public\other-instructions.md",
            $"5a 出厂那条 instructions 条目原样保留（{entriesBack.Count} 条）");
        r.Check(!OpencodeConfig.ContainsPath(backCfg, e.InstructionsPath), "5a instructions 里已无本工具路径");
        r.Check(FileText.Read(e.AgentsFile) == UserAgents, "5a AGENTS.md 逐字节回到用户自己的原文");
        r.Check(rev.ReadBackClean, "5a 回读确认无残留（ReadBackClean=true）");
        r.Check(rev.MatchesOriginal == true, $"5a 与首次备份 sha256 比对一致（MatchesOriginal={rev.MatchesOriginal}）");
        r.Check(rev.OriginalSha256 is not null && rev.CurrentSha256 == rev.OriginalSha256,
            $"5a OriginalSha256 == CurrentSha256（{Short(rev.CurrentSha256)}）");
        r.Check(File.Exists(e.ConfigBackup) && File.Exists(e.AgentsBackup), "5a 默认保留备份（RemoveBackups=false）");
        r.Check(rev.Changes.Any(c => c.Layer == LayerKind.PolicyText && c.WillWrite), "5a Changes 记录配置指令层还原");
        r.Check(rev.Changes.Any(c => c.Layer == LayerKind.ContextPayload && c.WillWrite), "5a Changes 记录载荷层还原");
        r.Check(rev.Notes.Any(n => n.Title.Contains("生效时机")), "5a 提示生效时机（新会话才回到出厂状态）");

        // ---- 5b：出厂态「有」opencode.json，但里面没有 instructions 键（键是 apply 时加上的） ----
        var f = Setup(ctx, setEnv, "revert-no-key", config: FactoryNoInstructions, agents: UserAgents);
        f.Target.Apply(new ApplyOptions(), null, default);
        r.Check(OpencodeConfig.EntriesOf(FileText.ReadOrNull(f.ConfigFile)).Count == 1,
            "5b 前置：apply 后 instructions 键被建出来且只有本工具一条");
        var revB = f.Target.Revert(new RevertOptions(), null, default);
        var backB = FileText.ReadOrNull(f.ConfigFile);
        r.Check(backB is not null, "5b 出厂态存在的文件撤销后必须还在（不许被删）");
        r.Check(backB == FactoryNoInstructions, "5b opencode.json 与出厂内容逐字节一致（含 instructions 键被去掉）");
        r.Check(backB is not null && !backB.Contains("\"instructions\"", StringComparison.Ordinal),
            "5b 出厂态没有的 instructions 键撤销后不残留");
        r.Check(!OpencodeConfig.ContainsPath(backB, f.InstructionsPath), "5b instructions 里已无本工具路径");
        r.Check(FileText.Read(f.AgentsFile) == UserAgents, "5b AGENTS.md 逐字节回到用户自己的原文");
        r.Check(revB.ReadBackClean, "5b 回读干净");
        r.Check(revB.MatchesOriginal == true, $"5b MatchesOriginal={revB.MatchesOriginal}");

        // ---- 5c：出厂态两个文件都不存在（本工具从零创建）→ 这是唯一允许删除的分支 ----
        var b = Setup(ctx, setEnv, "revert-created", config: null, agents: null);
        b.Target.Apply(new ApplyOptions(), null, default);
        r.Check(File.Exists(b.ConfigFile) && File.Exists(b.AgentsFile), "5c 前置：两个文件被本工具从零创建");
        r.Check(!File.Exists(b.ConfigBackup), "5c 前置：出厂即不存在，所以 apply 没有备份可拍");
        var rev3 = b.Target.Revert(new RevertOptions(), null, default);
        r.Check(rev3.Reverted, "5c revert 执行");
        r.Check(!File.Exists(b.ConfigFile),
            "5c opencode.json 已删除（只有 $schema + instructions 的那一份，恢复出厂的不存在状态）");
        r.Check(!File.Exists(b.AgentsFile), "5c AGENTS.md 已删除（出厂即不存在）");
        r.Check(FileText.ReadOrNull(b.ConfigFile) is null && FileText.ReadOrNull(b.AgentsFile) is null,
            "5c 回读：两个文件都不在，instructions 里自然也没有本工具路径");
        r.Check(rev3.MatchesOriginal is null, "5c 没有备份时 MatchesOriginal 报 null，不伪造 true");
        r.Check(rev3.ReadBackClean, "5c 回读干净");

        // ---- 5d：没有备份，但文件里有别人的键 → 逆向撤销，只摘本工具条目，空数组保留 [] 不删键 ----
        var onlyOurs = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["$schema"] = OpencodeConfig.SchemaUrl,
            ["theme"] = "system",
            ["instructions"] = new[] { e.InstructionsPath },
        }, new JsonSerializerOptions { WriteIndented = true });
        FileText.Write(e.ConfigFile, onlyOurs);
        File.Delete(e.ConfigBackup);
        var rev2 = e.Target.Revert(new RevertOptions { SkipContextPayload = true }, null, default);
        var after = FileText.ReadOrNull(e.ConfigFile);
        r.Check(rev2.Reverted, "5d 没有备份时走逆向撤销路径");
        r.Check(after is not null, "5d 文件还在（有别人的键，不能删）");
        r.Check(after is not null && after.Contains("\"instructions\": []", StringComparison.Ordinal),
            "5d instructions 被清空但键保留为 []（不删键）");
        r.Check(after is not null && after.Contains("\"theme\": \"system\"", StringComparison.Ordinal),
            "5d 别人的键原样保留");
        r.Check(!OpencodeConfig.ContainsPath(after, e.InstructionsPath), "5d 我们的路径已摘除");
        r.Check(rev2.ReadBackClean, "5d 逆向撤销后回读干净");
        r.Check(rev2.MatchesOriginal is null, "5d 没有可信备份时不报 MatchesOriginal=true");

        // ---- 5e：没有备份，且这份文件就是本工具从零创建的（$schema + instructions）→ 删除 ----
        FileText.Write(e.ConfigFile, OpencodeConfig.FactoryJson(e.InstructionsPath));
        r.Check(!File.Exists(e.ConfigBackup), "5e 前置：没有备份");
        var rev4 = e.Target.Revert(new RevertOptions { SkipContextPayload = true }, null, default);
        r.Check(!File.Exists(e.ConfigFile), "5e 出厂即不存在的那一份被删除（不是留下一个空壳配置）");
        r.Check(rev4.ReadBackClean, "5e 回读干净（文件已不在，路径自然没有残留）");
        ctx.Log("revert ok");
    }

    // ================================================================ 6 污染备份

    private static void Case06ContaminatedBackup(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【6】污染备份：把 .breach.bak 改写成含 marker 的内容 → 状态报 Contaminated，revert 抛 AnchorConflict");

        // 6a：opencode.json 的备份被污染
        var a = Setup(ctx, setEnv, "contaminated-cfg", config: FactoryConfig, agents: UserAgents);
        a.Target.Apply(new ApplyOptions(), null, default);
        FileText.Write(a.ConfigBackup,
            "{\n  \"$schema\": \"https://opencode.ai/config.json\",\n  \"instructions\": [\n    \"" +
            OpencodeMarkers.InstructionsMarkers[0] + "\"\n  ]\n}\n");
        r.Check(OpencodeMarkers.AllMarkers.Any(m => FileText.Count(FileText.Read(a.ConfigBackup), m) > 0),
            "6a 备份已被改写成含 marker 的内容");

        var st = a.Target.GetStatus(default);
        r.Check(st.Layers[0].Backup.Health == BackupHealth.Contaminated,
            $"6a 状态页报备份 Contaminated（实际 {st.Layers[0].Backup.Health}）");
        r.Check(st.Layers[0].Notes.Any(n => n.Title.Contains("备份污染")), "6a 状态页给出「备份污染」提示");
        var cfgSha = FileText.Sha256File(a.ConfigFile);
        CheckThrows(r, () => a.Target.Revert(new RevertOptions(), null, default), EngineErrorCode.AnchorConflict,
            "6a 污染备份 → revert 抛 AnchorConflict（拒绝用它还原）");
        r.Check(FileText.Sha256File(a.ConfigFile) == cfgSha, "6a 抛错时没有改动 opencode.json");

        // 6b：AGENTS.md 的备份被污染
        var b = Setup(ctx, setEnv, "contaminated-agents", config: FactoryConfig, agents: UserAgents);
        b.Target.Apply(new ApplyOptions(), null, default);
        FileText.Write(b.AgentsBackup, "# 被改坏的备份\n\n" + OpencodeMarkers.PayloadMarkers[0] + "\n");
        var st2 = b.Target.GetStatus(default);
        r.Check(st2.Layers[1].Backup.Health == BackupHealth.Contaminated,
            $"6b 载荷层备份报 Contaminated（实际 {st2.Layers[1].Backup.Health}）");
        var agentsSha = FileText.Sha256File(b.AgentsFile);
        CheckThrows(r, () => b.Target.Revert(new RevertOptions(), null, default), EngineErrorCode.AnchorConflict,
            "6b 污染备份 → revert 抛 AnchorConflict");
        r.Check(FileText.Sha256File(b.AgentsFile) == agentsSha, "6b 抛错时没有改动 AGENTS.md");
        ctx.Log("contaminated ok");
    }

    // ================================================================ 7 fail-closed

    private static void Case07FailClosed(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【7】fail-closed：opencode.json 是非法 JSON → apply 抛 SelfCheckFailed 且文件未被修改");

        var e = Setup(ctx, setEnv, "failclosed", config: "{ \"instructions\": [ \"C:\\\\x.md\" \n", agents: UserAgents);
        var before = FileText.Sha256File(e.ConfigFile);

        var preview = e.Target.PreviewApply(new ApplyOptions { DryRun = true }, default);
        r.Check(!preview.CanApply, "非法 JSON → CanApply=false（预览如实报不可执行）");
        r.Check(preview.Notes.Any(n => n.Severity == Severity.Error), "预览给出 Error 级说明");

        var threw = false;
        try { e.Target.Apply(new ApplyOptions(), null, default); }
        catch (AgentEngineException ex)
        {
            threw = true;
            r.Check(ex.Code == EngineErrorCode.SelfCheckFailed, $"apply 抛 {ex.Code}（期望 SelfCheckFailed）");
            r.Check(ex.Message.Contains("拒绝写入") || ex.Message.Contains("fail-closed"), "异常说明是 fail-closed 拒写");
        }
        r.Check(threw, "非法 JSON 时拒绝写入（不是先写坏再报错）");
        r.Check(FileText.Sha256File(e.ConfigFile) == before, "opencode.json 未被修改（sha256 不变）");
        r.Check(!File.Exists(e.ConfigBackup), "fail-closed 时连备份都没有建（无备份不写）");
        r.Check(!File.Exists(e.AgentsFile) || FileText.Read(e.AgentsFile) == UserAgents,
            "fail-closed 时载荷层也没有被写（AGENTS.md 保持原样）");

        // instructions 类型不对（对象）同样拒写
        var e2 = Setup(ctx, setEnv, "failclosed-type", config: "{ \"instructions\": { \"a\": 1 } }", agents: null);
        var sha2 = FileText.Sha256File(e2.ConfigFile);
        CheckThrows(r, () => e2.Target.Apply(new ApplyOptions(), null, default), EngineErrorCode.SelfCheckFailed,
            "instructions 是对象 → apply 抛 SelfCheckFailed（硬改会破坏用户配置）");
        r.Check(FileText.Sha256File(e2.ConfigFile) == sha2, "类型不对时文件同样未被修改");
        ctx.Log("fail-closed ok");
    }

    // ================================================================ 8 日志

    private static void Case08Logs(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【8】日志（SQLite 只读）：ListSessions / LoadSession（ReasoningText、Text、SystemPromptText）");

        var e = Setup(ctx, setEnv, "logs", config: FactoryConfig, agents: UserAgents);
        using var db = MakeDb(e.DataHome, e.Target.Ctx.Policy.PathOf(OpencodeTarget.PolicyDefs.First(d => d.Name == "instructions")));
        r.Check(File.Exists(e.DbPath), $"合成会话库已建：{e.DbPath}");
        r.Check(new FileInfo(e.DbPath + "-wal").Length > 0,
            "数据留在 -wal 里（关掉自动 checkpoint）——只读连接必须能读到 WAL，immutable=1 会读成空");
        r.Check(!e.DbPath.StartsWith(Path.Combine(RealUserHome(), ".local"), StringComparison.OrdinalIgnoreCase),
            "会话库在沙箱里，不在真实数据根");

        var sessions = e.Target.ListSessions(default);
        r.Check(sessions.Count == 1, $"ListSessions 返回 {sessions.Count} 个会话（期望 1）");
        var info = sessions[0];
        r.Check(info.Id == SessionId, $"Id 取 session_v2.id：{info.Id}");
        r.Check(info.ModifiedAt == DateTimeOffset.FromUnixTimeMilliseconds(TimeUpdated),
            $"ModifiedAt 取 time_updated（毫秒 epoch）：{info.ModifiedAt:o}");
        r.Check(info.Workspace == db.Worktree, $"Workspace 取 session_v2.directory：{info.Workspace}");
        r.Check(info.SizeBytes == db.ExpectedBytes, $"SizeBytes = 该会话所有 session_message.data 的总字节数（{info.SizeBytes}）");
        r.Check(info.TurnCount == 4, $"TurnCount = user+assistant 消息数（{info.TurnCount}）");
        r.Check(info.Path == e.DbPath, "Path 指向承载它的库文件");

        var detail = e.Target.LoadSession(SessionId, new SessionLoadOptions(), default);
        r.Check(detail.Info.Id == SessionId, "LoadSession 解析到同一会话");
        r.Check(detail.Turns.Count == 4, $"轮次记录 4 条（user / assistant / user / assistant），实际 {detail.Turns.Count}");
        r.Check(detail.Turns[0].Role == "user" && detail.Turns[1].Role == "assistant", "按 seq 升序：user → assistant");
        r.Check(detail.Turns[0].Text == "列出当前目录下最大的 5 个文件，一行一个。", "user 轮的 data.text 进 Text");
        r.Check(detail.Turns[1].ReasoningText?.Contains("FIXTURE-REASONING-FEATURE", StringComparison.Ordinal) == true,
            "assistant 轮的推理原文进 ReasoningText（data.content[] 里 type=reasoning 的 text）");
        r.Check(detail.Turns[1].Text?.Contains("第一条", StringComparison.Ordinal) == true, "assistant 轮的回复文本进 Text");
        r.Check(detail.Turns[3].ReasoningText?.Contains("FIXTURE-THINK-2", StringComparison.Ordinal) == true, "第二轮推理原文解析正确");
        r.Check(detail.Turns[3].Text?.Contains("回复一", StringComparison.Ordinal) == true &&
                detail.Turns[3].Text?.Contains("回复二", StringComparison.Ordinal) == true,
            "多个 type=text 块被拼接");
        r.Check(detail.Turns.All(t => !t.Truncated), "未触发截断（MaxCharsPerText 默认 20000）");
        r.Check(detail.SystemPromptText?.Contains("core/environment", StringComparison.Ordinal) == true,
            "SystemPromptText 是 instruction_state.initial_values → instruction_blob.value 的人话清单（含 core/environment）");
        r.Check(detail.SystemPromptText?.Contains("Working directory", StringComparison.Ordinal) == true,
            "清单里带出了 environment 载荷正文");
        r.Check(detail.SystemPromptText?.Contains(OpencodeMarkers.InstructionsMarkers[0], StringComparison.Ordinal) == true,
            "清单里带出了我们指令文件的正文（它也在 instruction_blob 里）");
        r.Check(detail.Notes.Any(n => n.Title == "常驻指令载荷"), "Notes 说明了常驻指令载荷的来源");

        // latest 别名与空串
        var latest = e.Target.LoadSession("latest", new SessionLoadOptions(), default);
        r.Check(latest.Info.Id == SessionId, "latest 解析到同一个会话");
        r.Check(e.Target.LoadSession("", new SessionLoadOptions(), default).Info.Id == SessionId, "空字符串同样按最新会话处理");
        r.Check(e.Target.LoadSession(SessionId[..12], new SessionLoadOptions(), default).Info.Id == SessionId, "唯一前缀可解析");

        // MaxTurns / MaxCharsPerText
        var limited = e.Target.LoadSession(SessionId, new SessionLoadOptions { MaxTurns = 2 }, default);
        r.Check(limited.Turns.Count == 2, $"MaxTurns=2 → 只载入最近 2 条（实际 {limited.Turns.Count}）");
        r.Check(limited.Notes.Any(n => n.Title.Contains("轮次已限制")), "Notes 说明只载入了最近 N 条");
        var truncated = e.Target.LoadSession(SessionId, new SessionLoadOptions { MaxCharsPerText = 12 }, default);
        r.Check(truncated.Turns.Any(t => t.Truncated), "超过 MaxCharsPerText 的文本被截断并置 Truncated=true");
        r.Check(truncated.Turns.Where(t => t.Truncated).All(t => (t.Text?.Length ?? 0) <= 256),
            "截断带 256 字符下限保护（传 12 这种荒谬值不会把日志切碎）");
        var cutIdx = -1;
        for (var i = 0; i < truncated.Turns.Count; i++)
            if (truncated.Turns[i].Truncated) { cutIdx = i; break; }
        if (cutIdx >= 0)
        {
            var full = detail.Turns[cutIdx].Text ?? detail.Turns[cutIdx].ReasoningText ?? "";
            var cut = truncated.Turns[cutIdx].Text ?? truncated.Turns[cutIdx].ReasoningText ?? "";
            r.Check(!truncated.Turns[cutIdx].Truncated || cut.Length > 0, "被截断的轮次仍留下正文（不是切空）");
            r.Check(full.Length > cut.Length && full.Length > 256,
                $"同一段未截断时更长（{full.Length} 字符 > {cut.Length} 字符），说明截断真的生效了");
            r.Check(cut.Length == 256 && full.StartsWith(cut, StringComparison.Ordinal),
                $"截断点正好是 256 字符，且截断结果是原文前缀（{cut.Length} 字符）");
        }
        r.Check(truncated.Notes.Any(n => n.Title.Contains("截断")), "Notes 明示发生了截断");

        // 只读纪律：库被写连接占着也能读（上面全部读操作都是在写连接没关的情况下做的）；
        // 库文件不存在时 ListSessions 给空列表（与 ZCode 的 List 一致：还没落过会话就是没有会话），
        // 但**读取失败**一律抛 IoError，绝不静默返回空；按会话号的读取在库缺失时必须显式报错。
        var missing = Path.Combine(e.Sandbox, "no-such.db");
        r.Check(!OpencodeDb.Exists(missing), "前置：no-such.db 确实不存在");
        r.Check(OpencodeDb.ListSessions(missing, default).Count == 0,
            "库不存在 → ListSessions 给空列表（不是抛异常，也不是伪造会话）");
        r.Check(OpencodeDb.DescribeMissing(missing, null).Contains(missing, StringComparison.OrdinalIgnoreCase),
            "DescribeMissing 把库路径写进诊断（空列表时要能说清是「库不在」）");
        CheckThrows(r, () => OpencodeDb.InstructionBlobText(missing, default), EngineErrorCode.TargetFileMissing,
            "instruction_blob 读不到时抛 TargetFileMissing（CheckMarkers 的主判据不许静默为空）");
        CheckThrows(r, () => OpencodeDb.LoadSession(missing, SessionId, new SessionLoadOptions(), default),
            EngineErrorCode.TargetFileMissing, "库不存在时 LoadSession 抛 TargetFileMissing");
        CheckThrows(r, () => e.Target.CheckMarkers("no-such-session", default), EngineErrorCode.TargetFileMissing,
            "会话不存在 → TargetFileMissing，并列出库里现有的会话");
        ctx.Log($"logs ok: bytes={info.SizeBytes} turns={detail.Turns.Count}");
    }

    // ================================================================ 9 命中数 / 生效判据

    private static void Case09Effectiveness(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【9】命中数：instruction_blob 含我们文本 → 配置指令层已进上下文；去掉该 blob → 没生效 + InstalledButNotEffective=true");

        var e = Setup(ctx, setEnv, "effectiveness", config: FactoryConfig, agents: UserAgents);
        using var db = MakeDb(e.DataHome, e.Target.Ctx.Policy.PathOf(OpencodeTarget.PolicyDefs.First(d => d.Name == "instructions")));
        e.Target.Apply(new ApplyOptions(), null, default);

        var report = e.Target.CheckMarkers(SessionId, default);
        r.Check(report.SessionId == SessionId, $"报告里是解析后的真会话号（{report.SessionId}）");
        r.Check(report.SystemPromptLayerPresent, "配置指令层的标记在 instruction_blob 里命中 → SystemPromptLayerPresent=true");
        r.Check(report.Verdict.Contains("instruction_blob", StringComparison.Ordinal),
            "Verdict 说明命中来自 instruction_blob");
        var sysHits = report.Hits.Where(h => h.Layer == MarkerLayer.SystemPrompt).Sum(h => h.Count);
        r.Check(sysHits >= 3, $"配置指令层三条标记都命中（合计 {sysHits}）");
        r.Check(report.Hits.First(h => h.Marker == OpencodeMarkers.InstructionsMarkers[0]).Count == 1,
            "「" + OpencodeMarkers.InstructionsMarkers[0] + "」命中 1 次");
        r.Check(report.ExportedSystemPromptPath is not null && File.Exists(report.ExportedSystemPromptPath),
            "CheckMarkers 顺带导出常驻指令清单（供 UI 打开）");

        var st = e.Target.GetStatus(default);
        r.Check(st.Layers[0].InstalledButNotEffective == false,
            "blob 里有我们的文本 → 配置指令层 InstalledButNotEffective=false");
        r.Check(st.Layers[0].EffectivenessEvidence?.Contains("已进模型上下文", StringComparison.Ordinal) == true,
            $"生效证据：{Trim1(st.Layers[0].EffectivenessEvidence, 120)}");
        r.Check(st.RestartRequired == false, "已经进了上下文 → RestartRequired=false");

        // 把含我们文本的那条 blob 删掉（写连接还开着，等于客户端那边这一份载荷没了）
        db.DeleteBlob(db.InstructionsHash);
        r.Check(db.BlobCount() == 1, $"删除后 instruction_blob 只剩 1 条（core/environment）");

        var report2 = e.Target.CheckMarkers(SessionId, default);
        r.Check(report2.SystemPromptLayerPresent == false, "去掉我们的 blob → 配置指令层命中 0");
        r.Check(report2.ContextPayloadLayerPresent == false, "载荷层标记本来就不在 instruction_blob 里 → 也是 0");
        r.Check(report2.Verdict.Contains("没生效", StringComparison.Ordinal),
            $"Verdict 按 MarkerScanner 语义给出「没生效」结论：{Trim1(report2.Verdict, 140)}");

        var st2 = e.Target.GetStatus(default);
        r.Check(st2.Layers[0].State == LayerState.Applied,
            "注意：层状态仍是 Applied——「装上了」与「生效了」必须分开报");
        r.Check(st2.Layers[1].State == LayerState.Applied, "载荷层也是 Applied");
        r.Check(st2.Layers[0].InstalledButNotEffective,
            "配置与 AGENTS.md 都在位、但最近会话的常驻指令载荷里没有我们的文本 → InstalledButNotEffective=true");
        r.Check(st2.Layers[0].EffectivenessEvidence?.Contains("没有这段文本", StringComparison.Ordinal) == true,
            $"生效证据写清了原因：{Trim1(st2.Layers[0].EffectivenessEvidence, 160)}");
        r.Check(st2.Notes.Any(n => n.Title == "装上了但没生效"), "Notes 里有「装上了但没生效」警告");
        r.Check(st2.RestartRequired, "盘上是注入态但最近会话读不到 → RestartRequired=true（开新会话才生效）");
        r.Check(st2.LastMarkerReport?.SessionId == SessionId, $"LastMarkerReport 取最近一次会话（{st2.LastMarkerReport?.SessionId}）");
        ctx.Log($"effectiveness ok: sys={sysHits} → 0");
    }

    // ================================================================ 11 定位

    private static void Case11Locate(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【11】定位：--root 三形态（安装根 / exe / 配置根）+ fail-closed + 环境变量 / 记忆 / XDG / 安装目录");

        var e = Setup(ctx, setEnv, "locate", config: FactoryConfig, agents: UserAgents);

        var byDir = e.Target.Locate(new LocateOptions { ExplicitRoot = e.InstallRoot }, true, default);
        r.Check(byDir.Found && byDir.Root == e.InstallRoot, $"传安装根 → 命中（{byDir.Root}）");
        r.Check(byDir.Exe == e.CliExe, $"Exe 指向 cli 下的 opencode-cli.exe（{byDir.Exe}）");
        r.Check(byDir.Version == "2.0.32-x64", $"Version 取版本目录名（{byDir.Version}）");
        r.Check(byDir.Bundle is null, "Bundle = null（没有单一 bundle，注入点不是它）");
        r.Check(byDir.PatchFile is null, "PatchFile = null（没有可删的条款 → 不做补丁层）");
        r.Check(byDir.ProfileDir == e.ConfigHome, $"ProfileDir = 配置根（{byDir.ProfileDir}）");
        r.Check(byDir.LocatedBy?.StartsWith("cli", StringComparison.Ordinal) == true, $"LocatedBy = {byDir.LocatedBy}");
        r.Check(byDir.Attempts.Count > 3, $"Attempts 记录试过的路径（{byDir.Attempts.Count} 条）");
        r.Check(byDir.Notes.Any(n => n.Title == "注入点不是 bundle"), "Notes 说明注入点是配置与指令文件，不是 bundle");
        r.Check(byDir.Notes.Any(n => n.Title == "本工具约定变量"), "Notes 说明 OPENCODE_ROOT / OPENCODE_CONFIG 是本工具约定，不是官方变量");

        var byExe = e.Target.Locate(new LocateOptions { ExplicitRoot = e.CliExe }, true, default);
        r.Check(byExe.Found && byExe.Exe == e.CliExe && byExe.Root == e.InstallRoot, "传 exe → 命中并由 exe 反推安装根");

        var byCfg = e.Target.Locate(new LocateOptions { ExplicitRoot = e.ConfigHome }, true, default);
        r.Check(byCfg.Found && byCfg.ConfigHome == e.ConfigHome, "传配置根 → 命中（Root 退化为配置根）");
        r.Check(byCfg.Exe is null, "只给配置根时不去别的安装位置猜 CLI（Exe 留空）");

        var bad = e.Target.Locate(new LocateOptions { ExplicitRoot = Path.Combine(e.Sandbox, "no-such-dir") }, true, default);
        r.Check(!bad.Found, "显式路径无效 → fail-closed（不回退、不猜）");
        r.Check(bad.Attempts.Any(a => a.Level == "cli" && !a.Hit), "Attempts 记下 cli 级的无效尝试");
        r.Check(bad.Notes.Any(n => n.Title.Contains("fail-closed")), "Notes 说明 fail-closed 中止原因");

        // 自动探测链第 1 级：环境变量 OPENCODE_ROOT / OPENCODE_CONFIG
        var auto = e.Target.Locate(null, true, default);
        r.Check(auto.Found && auto.Exe == e.CliExe, "无显式参数 → 环境变量 OPENCODE_ROOT 命中（自动探测链）");
        r.Check(auto.ConfigHome == e.ConfigHome, "无显式参数 → 环境变量 OPENCODE_CONFIG 决定配置根");
        r.Check(File.Exists(e.MemoryFile), $"定位成功后写记忆文件（{Path.GetFileName(e.MemoryFile)}）");
        r.Check(PathMemory.Load(e.MemoryFile, "root") == e.InstallRoot, "记忆文件里的 root 是本次命中的安装根");
        r.Check(PathMemory.Load(e.MemoryFile, "configHome") == e.ConfigHome, "记忆文件里的 configHome 一并记下");

        // 自动探测链第 2 级：清掉环境变量后落到记忆文件（顺序就是纪律，不会跳过它直接去 XDG）
        setEnv("OPENCODE_CONFIG", null);
        setEnv("OPENCODE_ROOT", null);
        var byMemory = new OpencodeTarget(e.Target.Ctx).Locate(null, true, default);
        r.Check(byMemory.LocatedBy?.StartsWith("memory", StringComparison.Ordinal) == true,
            $"清掉环境变量后落到记忆级（LocatedBy={byMemory.LocatedBy}）");
        r.Check(byMemory.ConfigHome == e.ConfigHome, "记忆级同时还原配置根");
        r.Check(byMemory.Exe == e.CliExe, "记忆级还原 CLI（记忆里存的是安装根）");

        // 自动探测链第 3 级：IgnoreMemory 跳过记忆 → 配置根落到 XDG_CONFIG_HOME 下的 opencode
        var xdgConfig = Path.Combine(e.XdgConfigHome, "opencode");
        Directory.CreateDirectory(xdgConfig);
        var xdgData = Path.Combine(e.XdgDataHome, "opencode");
        var xdg = new OpencodeTarget(e.Target.Ctx).Locate(new LocateOptions { IgnoreMemory = true }, true, default);
        r.Check(xdg.ConfigHome == xdgConfig, $"IgnoreMemory 后尊重 XDG_CONFIG_HOME：配置根 = {xdg.ConfigHome}");
        var xdgLoc = OpencodeLocator.Resolve(new LocateOptions { IgnoreMemory = true }, false, default);
        r.Check(xdgLoc.DataHome == xdgData, $"尊重 XDG_DATA_HOME：数据根 = {xdgLoc.DataHome}");
        r.Check(xdgLoc.DbPath == Path.Combine(xdgData, "opencode.db"), "会话库路径由数据根拼出（XDG_DATA_HOME/opencode/opencode.db）");
        r.Check(xdg.Attempts.Any(a => a.Level == "xdg"), "Attempts 里有 XDG 级的记录");

        // 自动探测链第 4/5 级：进程 → 安装目录。
        // 本机 opencode-cli 正在跑时进程级会先命中（那也证明这一级真的在工作），
        // 两种情形都要求：用真实安装布局解释出 CLI，且 Attempts 说得清是谁命中的。
        var realAppRoot = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "ai.opencode.desktop");
        var cliUnderRealRoot = OpencodeLocator.CliCandidates(realAppRoot);
        if (cliUnderRealRoot.Count > 0)
        {
            r.Check(xdg.LocatedBy?.Contains("process", StringComparison.OrdinalIgnoreCase) == true ||
                    xdg.LocatedBy?.Contains("install", StringComparison.OrdinalIgnoreCase) == true,
                $"清掉环境变量后从进程 / 安装目录命中（LocatedBy={xdg.LocatedBy}）");
            r.Check(xdg.Exe is not null && File.Exists(xdg.Exe),
                $"解释出的 CLI 真实存在：{xdg.Exe}");
            r.Check(xdg.Exe!.StartsWith(realAppRoot, StringComparison.OrdinalIgnoreCase),
                "CLI 落在 %APPDATA%\\ai.opencode.desktop 下（就是真实安装布局）");
            r.Check(xdg.Attempts.Any(a => a.Level is "process" or "install"),
                "Attempts 记下进程级 / 安装目录级的尝试");
            r.Check(xdg.Attempts.Any(a => a.Hit && a.Level is "process" or "install"),
                "Attempts 标出了真正命中的那一级");
        }
        else
        {
            r.Step("    本机没有 %APPDATA%\\ai.opencode.desktop\\cli，跳过「安装目录」这一级");
        }

        // 安装目录级的实现本身（纯函数）：cli 下有多个版本目录时取 mtime 最新的那个
        var fakeApp = Path.Combine(e.Sandbox, "fake-appdata", "ai.opencode.desktop");
        var oldVer = Path.Combine(fakeApp, "cli", "2.0.31-x64");
        var newVer = Path.Combine(fakeApp, "cli", "2.0.40-x64");
        Directory.CreateDirectory(oldVer);
        Directory.CreateDirectory(newVer);
        FileText.Write(Path.Combine(oldVer, "opencode-cli.exe"), "MZ-old");
        FileText.Write(Path.Combine(newVer, "opencode-cli.exe"), "MZ-new");
        // mtime 取 exe 与目录里较新的那个，所以两边都要往前推（目录要用 Directory 的重载）
        var past = DateTime.UtcNow.AddDays(-30);
        File.SetLastWriteTimeUtc(Path.Combine(oldVer, "opencode-cli.exe"), past);
        Directory.SetLastWriteTimeUtc(oldVer, past);
        File.SetLastWriteTimeUtc(Path.Combine(newVer, "opencode-cli.exe"), DateTime.UtcNow);
        Directory.SetLastWriteTimeUtc(newVer, DateTime.UtcNow);
        var cands = OpencodeLocator.CliCandidates(fakeApp);
        r.Check(cands.Count == 2, $"cli 下的版本候选都被列出来（{cands.Count} 个）");
        r.Check(cands[0].Version == "2.0.40-x64", $"取 mtime 最新的版本目录（{cands[0].Version}）");
        r.Check(cands[0].Exe == Path.Combine(newVer, "opencode-cli.exe"), "候选带出 exe 路径");
        r.Check(cands.Any(c => c.Version == "2.0.31-x64"), "旧版本候选也在列表里（诊断用，不丢）");

        setEnv("OPENCODE_CONFIG", e.ConfigHome);
        setEnv("OPENCODE_ROOT", e.InstallRoot);
        ctx.Log($"locate ok: {e.CliExe}");
    }

    // ================================================================ 12 差异 / 导出 / 探针

    private static void Case12DiffExportProbes(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【12】差异 / 导出 / 探针定义");

        var e = Setup(ctx, setEnv, "diff-export", config: FactoryConfig, agents: UserAgents);
        using var db = MakeDb(e.DataHome, e.Target.Ctx.Policy.PathOf(OpencodeTarget.PolicyDefs.First(d => d.Name == "instructions")));
        e.Target.Apply(new ApplyOptions(), null, default);

        var diff = e.Target.GetSystemPromptDiff(SessionId, default);
        r.Check(diff.BeforeSource is not null && diff.AfterSource is not null, "差异报告带来源（首次备份 / 当前配置）");
        r.Check(diff.BeforeSource!.Contains("备份", StringComparison.Ordinal) &&
                diff.BeforeSource.Contains("AGENTS.md", StringComparison.Ordinal),
            $"BeforeSource 说明基线来自两个文件的首次备份：{Trim1(diff.BeforeSource, 100)}");
        r.Check(diff.BeforeText.Contains("other-instructions.md", StringComparison.Ordinal),
            "BeforeText 取首次备份里的 instructions 条目");
        r.Check(diff.BeforeText.Contains("工具不得直接覆盖", StringComparison.Ordinal),
            "BeforeText 的载荷层是用户自己的 AGENTS.md（出厂态就有这一份）");
        r.Check(!diff.BeforeText.Contains("已是当前文本", StringComparison.Ordinal) &&
                !diff.BeforeText.Contains(OpencodeMarkers.PayloadMarkers[0], StringComparison.Ordinal),
            "BeforeText 不含注入文本（基线是出厂态，不是当前态）");
        r.Check(diff.AfterText.Contains(e.InstructionsPath, StringComparison.Ordinal), "AfterText 含当前 instructions 条目（我们的路径）");
        r.Check(diff.AfterText.Contains(OpencodeMarkers.InstructionsMarkers[0], StringComparison.Ordinal),
            "AfterText 含指令文件正文");
        r.Check(diff.AfterText.Contains(OpencodeMarkers.PayloadMarkers[0], StringComparison.Ordinal), "AfterText 含 AGENTS.md 载荷正文");
        r.Check(diff.Lines.Count > 0 && diff.AddedLines + diff.RemovedLines > 0, $"逐行差异非空（+{diff.AddedLines}/-{diff.RemovedLines}）");
        r.Check(diff.MarkerDelta.Any(h => h.Count > 0), "MarkerDelta 记录注入前后标记变化");

        var expSession = e.Target.ExportSession(SessionId, Path.Combine(e.Sandbox, "export.jsonl"), default);
        r.Check(expSession.Exported && File.Exists(expSession.Path), "ExportSession 写出文件");
        var lines = File.ReadAllLines(expSession.Path!);
        r.Check(lines.Length == 5, $"逐行 JSON 输出 5 条 session_message（实际 {lines.Length}）");
        var firstOk = true;
        foreach (var line in lines)
        {
            try { using var _ = JsonDocument.Parse(line); }
            catch { firstOk = false; }
        }
        r.Check(firstOk, "每一行都是合法 JSON");
        r.Check(lines.Any(l => l.Contains("\"type\":\"user\"", StringComparison.Ordinal)), "导出里含 user 行");
        r.Check(lines.Any(l => l.Contains("FIXTURE-REASONING-FEATURE", StringComparison.Ordinal)), "导出里含推理原文");

        var expPrompt = e.Target.ExportSystemPrompt(SessionId, Path.Combine(e.Sandbox, "prompt.txt"), default);
        r.Check(expPrompt.Exported && File.Exists(expPrompt.Path), "ExportSystemPrompt 写出文件");
        r.Check(FileText.Read(expPrompt.Path!).Contains("core/environment", StringComparison.Ordinal),
            "导出的常驻指令清单含 core/environment");

        var probes = e.Target.GetProbes();
        r.Check(probes.Count == 6, $"探针 A–F 共 6 条（实际 {probes.Count}）");
        r.Check(probes.Select(p => p.Id).SequenceEqual(new[] { "A", "B", "C", "D", "E", "F" }), "Id 依次是 A–F");
        r.Check(probes.Count(p => p.IsDecisive) == 3, "D/E/F 三条 IsDecisive=true");
        r.Check(probes.Where(p => p.Id is "D" or "E" or "F").All(p => p.IsDecisive), "D/E/F 都标了 IsDecisive");
        r.Check(probes.All(p => p.HowTo.Length > 10 && p.PassCriteria.Length > 30), "每条都有 HowTo 与三级判据");
        r.Check(probes.First(p => p.Id == "E").PassCriteria.Contains("reasoning", StringComparison.OrdinalIgnoreCase),
            "E 的判据指向 session_message 里的推理原文");

        var policy = e.Target.GetPolicyFiles(default);
        r.Check(policy.Count == 2, $"注入文本两条：instructions + payload（实际 {policy.Count}）");
        r.Check(policy.All(p => p.Text.Length > 0), "两条注入文本都物化到磁盘且非空");
        r.Check(policy.First(p => p.Name == "instructions").Path == e.InstructionsPath,
            "instructions 文本的路径就是要写进 opencode.json 的那一个");
        r.Check(policy.All(p => p.SizeBytes == FileText.Bytes(p.Text)), "SizeBytes 与文本字节数一致（UTF-8 无 BOM）");
        r.Check(policy.First(p => p.Name == "payload").DisplayName.Length > 0, "payload 有中文显示名（UI 直接展示）");

        // Describe / LayerDescriptions：引擎列表用的元信息
        var info = e.Target.Describe(default);
        r.Check(info.Id == "opencode", $"Describe().Id = {info.Id}");
        r.Check(info.DisplayName.Length > 0 && info.Kind == TargetKind.PackagedElectron,
            $"Describe() 带显示名与 Kind={info.Kind}（桌面版 Electron 外壳 + 独立 CLI）");
        r.Check(info.Installed, "Describe().Installed 反映定位结果（沙箱里装好了）");
        r.Check(info.BundlePath is null, "Describe().BundlePath = null（注入点不是 bundle）");
        r.Check(info.ConfigHome == e.ConfigHome, $"Describe().ConfigHome = 配置根（{info.ConfigHome}）");
        r.Check(info.Layers.Count == 2, $"Describe().Layers 两条（实际 {info.Layers.Count}）");
        r.Check(info.Note?.Contains("不是 bundle", StringComparison.Ordinal) == true,
            $"Describe().Note 说明注入点不是 bundle：{Trim1(info.Note, 90)}");
        r.Check(e.Target.LayerDescriptions.Count == 2, "LayerDescriptions 两条（配置指令层 + 上下文载荷层）");

        // SavePolicyFile：改文本 → 回读一致 → 再改回原样
        var instrDef = OpencodeTarget.PolicyDefs.First(d => d.Name == "instructions");
        var original = FileText.Read(e.InstructionsPath);
        var edited = original.TrimEnd('\r', '\n') + "\n\n（自检追加的一行：保存路径可用。）\n";
        var saved = e.Target.SavePolicyFile("instructions", edited, default);
        r.Check(saved.Path == e.InstructionsPath, $"SavePolicyFile 写回同一条注入文本：{saved.Path}");
        r.Check(FileText.Read(e.InstructionsPath) == edited, "保存后回读逐字节一致");
        r.Check(FileText.Sha256File(e.InstructionsPath) == FileText.Sha256Text(edited), "保存后的 sha256 == 文本 sha256");
        r.Check(e.Target.GetPolicyFiles(default).First(p => p.Name == "instructions").Text == edited,
            "GetPolicyFiles 读到的是刚保存的版本");
        CheckThrows(r, () => e.Target.SavePolicyFile("no-such-policy", "x", default),
            EngineErrorCode.TargetFileMissing, "保存不存在的注入文本 → TargetFileMissing（不新建计划外的文件）");
        CheckThrows(r, () => e.Target.SavePolicyFile("instructions", "   \n", default),
            EngineErrorCode.VerifyProblems, "保存空白文本 → VerifyProblems（拒绝把配置指向空文件）");
        e.Target.SavePolicyFile("instructions", original, default);
        r.Check(FileText.Read(e.InstructionsPath) == original, "改回原样后与初始文本逐字节一致");
        r.Check(File.ReadAllBytes(e.InstructionsPath)[0] != 0xEF,
            $"注入文本没有 BOM（首字节 0x{File.ReadAllBytes(e.InstructionsPath)[0]:X2}）");
        r.Check(e.InstructionsPath.StartsWith(e.Sandbox, StringComparison.OrdinalIgnoreCase),
            "注入文本落在沙箱数据目录里，没碰真实 ~/.config/opencode");
        r.Check(!File.Exists(Path.Combine(e.ConfigHome, instrDef.FileName)),
            "注入文本不在配置根里（不会被当成客户端自己的配置项）");

        // 出厂态 AGENTS.md 不存在时，基线里那一节要明确写「(不存在)」而不是留空
        var f = Setup(ctx, setEnv, "diff-empty-agents", config: FactoryConfig, agents: null);
        f.Target.Apply(new ApplyOptions(), null, default);
        var diff2 = f.Target.GetSystemPromptDiff(null, default);
        r.Check(diff2.BeforeSource!.Contains("空基线", StringComparison.Ordinal) ||
                diff2.BeforeSource.Contains("AGENTS.md 没有干净备份", StringComparison.Ordinal),
            $"出厂没有 AGENTS.md → BeforeSource 说明是空基线：{Trim1(diff2.BeforeSource, 100)}");
        r.Check(diff2.BeforeText.Contains("(不存在)", StringComparison.Ordinal),
            "BeforeText 里 AGENTS.md 一节写「(不存在)」（空基线不是留白）");
        r.Check(diff2.BeforeText.Contains("other-instructions.md", StringComparison.Ordinal),
            "BeforeText 的配置指令层仍是出厂备份里的那一条");
        r.Check(diff2.AfterText.Contains(OpencodeMarkers.PayloadMarkers[0], StringComparison.Ordinal),
            "AfterText 是注入后的完整文本（含载荷正文）");
        ctx.Log("diff/export/probes ok");
    }

    // ================================================================ 10 真实数据（只读）

    private static void Case10RealDataReadOnly(SelfTestContext ctx, SelfTestResult r, string realConfig, string realDb)
    {
        r.Step("");
        r.Step("【10】真实数据只读核对（本机存在就做，不存在就跳过；不写任何真实文件）");

        if (!File.Exists(realDb))
        {
            r.Step($"    本机没有真实会话库（{realDb}），跳过。");
            return;
        }

        var realCfgFile = Path.Combine(realConfig, "opencode.json");
        var realAgents = Path.Combine(realConfig, "AGENTS.md");
        r.Step($"    真实配置根：{realConfig}（opencode.json 存在={File.Exists(realCfgFile)}，AGENTS.md 存在={File.Exists(realAgents)}）");
        r.Step($"    真实会话库：{OpencodeDb.DescribeDb(realDb)}");

        var sessions = OpencodeDb.ListSessions(realDb, default);
        r.Check(sessions.Count > 0, $"真实库里列出 {sessions.Count} 个会话");
        const string realSessionId = "ses_eb8ef608fffeDDKiwgOntuAsAr";
        var target = sessions.FirstOrDefault(s => s.Id == realSessionId);
        if (target is null)
        {
            r.Step($"    真实库里没有样本会话 {realSessionId}（现有：{string.Join("、", sessions.Take(3).Select(s => s.Id))}），跳过细节断言。");
            return;
        }

        r.Check(true, $"真实库里有样本会话 {realSessionId}（model {target.TurnCount} 条轮次，{FileText.Human(target.SizeBytes)}）");
        r.Check(target.Workspace?.Length > 0, $"Workspace 取 session_v2.directory：{target.Workspace}");
        r.Check(target.ModifiedAt > DateTimeOffset.UnixEpoch, $"ModifiedAt 由毫秒 epoch 还原：{target.ModifiedAt:o}");

        var detail = OpencodeDb.LoadSession(realDb, realSessionId, new SessionLoadOptions(), default);
        r.Check(detail.Turns.Count >= 4, $"真实会话解析出 {detail.Turns.Count} 条轮次记录");
        var reasoning = detail.Turns.FirstOrDefault(t => !string.IsNullOrEmpty(t.ReasoningText))?.ReasoningText;
        r.Check(reasoning is { Length: > 0 }, $"真实推理原文非空（{reasoning?.Length ?? 0} 字符）");
        r.Check(reasoning?.Contains("Let me run a command", StringComparison.Ordinal) == true,
            "真实推理原文以「Let me run a command…」开头（与侦察档案一致）");
        r.Check(detail.SystemPromptText?.Contains("core/environment", StringComparison.Ordinal) == true,
            "真实 SystemPromptText 含 core/environment（instruction_blob 权威视图）");
        r.Check(detail.SystemPromptText?.Contains("core/codemode", StringComparison.Ordinal) == true,
            "真实 SystemPromptText 含 core/codemode");
        r.Step($"    真实推理开头：{Trim1(reasoning, 80)}");

        var scoped = OpencodeDb.SessionInstructionText(realDb, realSessionId, default);
        r.Check(scoped.Contains("Working directory", StringComparison.Ordinal),
            "真实会话的常驻指令文本里带 environment 正文（只读核对通过）");
        var blobText = OpencodeDb.InstructionBlobText(realDb, default);
        r.Check(blobText.Blobs >= 4, $"真实 instruction_blob 有 {blobText.Blobs} 条（≥4：codemode / skill-guidance / date / environment）");
        ctx.Log($"real read-only ok: {realSessionId}");
    }

    // ================================================================ 沙箱

    private sealed class Env
    {
        public required string Sandbox { get; init; }
        public required string InstallRoot { get; init; }
        public required string CliExe { get; init; }
        public required string ConfigHome { get; init; }
        public required string XdgConfigHome { get; init; }
        public required string XdgDataHome { get; init; }
        public required string DataHome { get; init; }
        public required string MemoryFile { get; init; }
        public required OpencodeTarget Target { get; init; }

        public string ConfigFile => Path.Combine(ConfigHome, "opencode.json");
        public string AgentsFile => Path.Combine(ConfigHome, "AGENTS.md");
        public string DbPath => Path.Combine(DataHome, "opencode.db");
        public string ConfigBackup => ConfigFile + ".breach.bak";
        public string AgentsBackup => AgentsFile + ".breach.bak";
        public string InstructionsPath => Path.Combine(Target.Ctx.DataDir, "policy", "opencode-instructions.md");
    }

    private static Env Setup(SelfTestContext ctx, Action<string, string?> setEnv, string name, string? config, string? agents)
    {
        var dir = ctx.MakeSandbox($"opencode-{name}");
        var installRoot = Path.Combine(dir, "install");
        var cliDir = Path.Combine(installRoot, "cli", "2.0.32-x64");
        var cliExe = Path.Combine(cliDir, "opencode-cli.exe");
        var configHome = Path.Combine(dir, "config");
        var xdgConfig = Path.Combine(dir, "xdg", "config");
        var xdgData = Path.Combine(dir, "xdg", "data");
        var dataHome = Path.Combine(xdgData, "opencode");
        var memoryFile = Path.Combine(dir, "breach-opencode-path.json");
        var dataDir = Path.Combine(dir, "toolkit-data");

        Directory.CreateDirectory(cliDir);
        FileText.Write(cliExe, "MZ-dummy opencode-cli 203.8MB 占位（沙箱）");
        Directory.CreateDirectory(configHome);
        Directory.CreateDirectory(dataHome);
        Directory.CreateDirectory(dataDir);

        if (config is not null) FileText.Write(Path.Combine(configHome, "opencode.json"), config);
        if (agents is not null) FileText.Write(Path.Combine(configHome, "AGENTS.md"), agents);

        // 环境变量把适配器的自动探测钉在沙箱里；记忆文件也重定向，绝不污染真实 ~/.breach-opencode-path.json
        setEnv("OPENCODE_ROOT", installRoot);
        setEnv("OPENCODE_CONFIG", configHome);
        setEnv("OPENCODE_PATH_MEMORY_FILE", memoryFile);
        setEnv("XDG_CONFIG_HOME", xdgConfig);
        setEnv("XDG_DATA_HOME", xdgData);

        var policy = new PolicyStore(dataDir);
        policy.Materialize(OpencodeTarget.PolicyDefs);
        var targetCtx = new TargetContext
        {
            Id = "opencode",
            DataDir = dataDir,
            Policy = policy,
            PolicyDefs = OpencodeTarget.PolicyDefs,
            Ledger = new LedgerStore(Path.Combine(dataDir, "probe-ledger.json")),
        };

        ctx.Log($"沙箱 {Path.GetFileName(dir)}：config={Path.GetFileName(configHome)} data={Path.GetFileName(dataHome)}");
        return new Env
        {
            Sandbox = dir,
            InstallRoot = installRoot,
            CliExe = cliExe,
            ConfigHome = configHome,
            XdgConfigHome = xdgConfig,
            XdgDataHome = xdgData,
            DataHome = dataHome,
            MemoryFile = memoryFile,
            Target = new OpencodeTarget(targetCtx),
        };
    }

    // ================================================================ 合成会话库

    /// <summary>
    /// 现建一个合成 opencode.db：四个表（session_v2 / session_message / instruction_blob / instruction_state），
    /// 1 个会话 + 5 条消息（user / assistant×2 / idle）+ 两条 instruction_blob（我们的指令文本 + core/environment）。
    /// WAL 模式 + 关掉自动 checkpoint，且**写连接全程不关**：数据真的留在 -wal 里，
    /// 只读连接必须读 WAL 才能看见——这正是不许用 immutable=1 的实测理由。
    /// </summary>
    private sealed class DbFixture : IDisposable
    {
        public required string DbPath { get; init; }
        public required SqliteConnection Writer { get; init; }
        public required string Worktree { get; init; }
        public required long ExpectedBytes { get; init; }
        public required string InstructionsHash { get; init; }
        public required string EnvHash { get; init; }

        public void DeleteBlob(string hash)
        {
            using var cmd = Writer.CreateCommand();
            cmd.CommandText = "DELETE FROM instruction_blob WHERE hash = @h";
            cmd.Parameters.AddWithValue("@h", hash);
            cmd.ExecuteNonQuery();
        }

        public int BlobCount()
        {
            using var cmd = Writer.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM instruction_blob";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public void Dispose()
        {
            // 先清连接池再 Dispose：池里留着连接的话文件句柄不放，沙箱目录删不干净
            // （自检跑完会在 %TEMP% 留下几 MB 的 -wal，正是这个原因）。
            try { SqliteConnection.ClearPool(Writer); } catch { /* 尽力而为 */ }
            try { Writer.Dispose(); } catch { /* 尽力而为 */ }
        }
    }

    private static DbFixture MakeDb(string dataHome, string instructionsFilePath)
    {
        Directory.CreateDirectory(dataHome);
        var dbPath = Path.Combine(dataHome, "opencode.db");
        var worktree = "D:/沙箱/oc-fixture";
        var instructionsText = FileText.Read(instructionsFilePath);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();
        Exec(connection, "PRAGMA journal_mode=WAL;");
        Exec(connection, "PRAGMA wal_autocheckpoint=0;");

        Exec(connection,
            """
            CREATE TABLE project (
              id text PRIMARY KEY, worktree text NOT NULL, vcs text, name text,
              time_created integer NOT NULL, time_updated integer NOT NULL, sandboxes text NOT NULL DEFAULT '[]');
            CREATE TABLE session_v2 (
              id text PRIMARY KEY, project_id text NOT NULL, workspace_id text, parent_id text, slug text NOT NULL,
              directory text NOT NULL, path text, title text, version text NOT NULL, model text, agent text,
              permission text, tokens_reasoning integer DEFAULT 0 NOT NULL,
              time_created integer NOT NULL, time_updated integer NOT NULL, time_idle integer);
            CREATE TABLE session_message (
              id text PRIMARY KEY, session_id text NOT NULL, type text NOT NULL, seq integer NOT NULL,
              time_created integer NOT NULL, time_updated integer NOT NULL, data text NOT NULL);
            CREATE TABLE instruction_blob (hash text PRIMARY KEY, value text);
            CREATE TABLE instruction_state (
              session_id text PRIMARY KEY, epoch_start integer NOT NULL, through_seq integer NOT NULL,
              initial_values text NOT NULL, current_values text NOT NULL);
            """);

        Exec(connection, "INSERT INTO project (id, worktree, vcs, name, time_created, time_updated) VALUES ('prj_selftest', @w, 'git', 'fixture', @t, @t)",
            ("@w", worktree), ("@t", TimeCreated));

        Exec(connection,
            "INSERT INTO session_v2 (id, project_id, slug, directory, title, version, model, agent, tokens_reasoning, time_created, time_updated) " +
            "VALUES (@id, 'prj_selftest', 'cosmic-forest', @dir, '沙箱合成会话', '2.0.32', @model, 'build', 248, @c, @u)",
            ("@id", SessionId), ("@dir", worktree),
            ("@model", "{\"id\":\"deepseek-v4.1-flash\",\"providerID\":\"opencode-go\"}"),
            ("@c", TimeCreated), ("@u", TimeUpdated));

        var messages = new (string Type, int Seq, string Data)[]
        {
            ("user", 1, UserData("列出当前目录下最大的 5 个文件，一行一个。")),
            ("assistant", 2, AssistantData(Reasoning1, "第一条：先看目录内容。")),
            ("user", 3, UserData("再来一次，把结果一行一个输出。")),
            ("assistant", 4, AssistantData(Reasoning2, "回复一：", "回复二：结果在这里。", LongReply)),
            ("idle", 5, "{\"time\":{\"created\":1792193641086},\"outcome\":\"succeeded\"}"),
        };

        long bytes = 0;
        foreach (var (type, seq, data) in messages)
        {
            bytes += FileText.Bytes(data);
            Exec(connection,
                "INSERT INTO session_message (id, session_id, type, seq, time_created, time_updated, data) " +
                "VALUES (@mid, @sid, @type, @seq, @t, @t, @data)",
                ("@mid", $"{SessionId}-{seq}"), ("@sid", SessionId), ("@type", type), ("@seq", seq),
                ("@t", TimeCreated + seq), ("@data", data));
        }

        var instructionsHash = Sha256Text(instructionsText);
        var envValue = JsonSerializer.Serialize(
            $"<env>\n  Working directory: {worktree}\n  Workspace root folder: {worktree}\n  Is directory a git repo: yes\n  Platform: win32\n");
        var envHash = Sha256Text(envValue);

        Exec(connection, "INSERT INTO instruction_blob (hash, value) VALUES (@h, @v)",
            ("@h", instructionsHash), ("@v", JsonSerializer.Serialize(instructionsText)));
        Exec(connection, "INSERT INTO instruction_blob (hash, value) VALUES (@h, @v)",
            ("@h", envHash), ("@v", envValue));

        var mapping = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["file/opencode-instructions.md"] = instructionsHash,
            ["core/environment"] = envHash,
        });
        Exec(connection,
            "INSERT INTO instruction_state (session_id, epoch_start, through_seq, initial_values, current_values) " +
            "VALUES (@sid, 3, 3, @v, @v)",
            ("@sid", SessionId), ("@v", mapping));

        return new DbFixture
        {
            DbPath = dbPath,
            Writer = connection,
            Worktree = worktree,
            ExpectedBytes = bytes,
            InstructionsHash = instructionsHash,
            EnvHash = envHash,
        };
    }

    private static string UserData(string text) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["time"] = new Dictionary<string, object?> { ["created"] = TimeCreated },
        ["text"] = text,
        ["files"] = Array.Empty<string>(),
    });

    private static string AssistantData(string reasoning, params string[] texts)
    {
        var content = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "reasoning",
                ["text"] = reasoning,
                ["state"] = new Dictionary<string, object?> { ["reasoningField"] = "reasoning_content" },
            },
        };
        foreach (var t in texts) content.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = t });
        content.Add(new Dictionary<string, object?>
        {
            ["type"] = "tool",
            ["name"] = "shell",
            ["state"] = new Dictionary<string, object?> { ["status"] = "completed" },
        });

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["time"] = new Dictionary<string, object?> { ["created"] = TimeCreated, ["completed"] = TimeCreated + 42 },
            ["agent"] = "build",
            ["model"] = new Dictionary<string, object?> { ["id"] = "deepseek-v4.1-flash", ["providerID"] = "opencode-go" },
            ["content"] = content,
            ["finish"] = "stop",
            ["tokens"] = new Dictionary<string, object?> { ["reasoning"] = 159 },
        });
    }

    private static void Exec(SqliteConnection cn, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    // ================================================================ 工具

    private static List<string> Keys(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
    }

    private static string Sha256Text(string text) =>
        Convert.ToHexString(SHA256.HashData(new System.Text.UTF8Encoding(false).GetBytes(text))).ToLowerInvariant();

    /// <summary>真实文件可能被正在运行的客户端占着（opencode-cli 持有 db / wal），必须用宽松共享模式读。</summary>
    private static string? ShaShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch { return null; }
    }

    private static Dictionary<string, string?> SnapshotDir(string dir)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(dir)) return map;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                map[f] = ShaShared(f);
        }
        catch { /* 权限 */ }
        return map;
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

    private static string RealUserHome() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Short(string? sha) => sha is null ? "(读不到)" : sha[..Math.Min(12, sha.Length)] + "…";

    private static string Trim1(string? s, int n)
    {
        if (s is null) return "(null)";
        var one = s.Replace("\r", " ").Replace("\n", " ");
        return one.Length <= n ? one : one[..n] + "…";
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
