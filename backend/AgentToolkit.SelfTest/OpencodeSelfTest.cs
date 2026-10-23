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

    /// <summary>
    /// 带 agent 键的出厂配置（系统提示词层用）：里面已经有别人的 agent、别人的 prompt、别人才有的字段。
    /// apply 之后这些一个不少；撤销之后本工具写进去的 prompt 要被摘掉，别人的 prompt 不许动。
    /// </summary>
    private const string FactoryConfigWithAgent =
        """
        {
          "$schema": "https://opencode.ai/config.json",
          "theme": "system",
          "model": "opencode-go/deepseek-v4.1-flash",
          "instructions": [
            "C:\\Users\\Public\\other-instructions.md"
          ],
          "agent": {
            "build": {
              "model": "opencode-go/deepseek-v4.1-flash",
              "description": "用户的 build 配置"
            },
            "plan": {
              "mode": "primary",
              "description": "用户的 plan 配置"
            },
            "title": {
              "prompt": "给会话起个短标题",
              "temperature": 0.2
            },
            "review": {
              "prompt": "这是用户自己写的 review 提示词"
            }
          }
        }
        """;

    /// <summary>系统提示词层要写的四个 agent（与 OpencodeConfig.TargetAgents 同口径，但断言里写死更抗改）。</summary>
    private static readonly string[] AgentNames = { "build", "plan", "general", "explore" };

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

        // 真实客户端的只读快照（双方案用例也要求全程只读：不写、不 touch）
        var realSnapshot = DualScheme.CaptureReal("opencode");

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
            Case13AgentPromptLayer(ctx, r, SetEnv);
            Case13SwitchOff(ctx, r, SetEnv);
            Case14SystemMarkers(ctx, r, SetEnv);
            Case15NoSessionUndecidable(ctx, r, SetEnv);
            Case16DualScheme(ctx, r, SetEnv);
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

        DualScheme.CheckRealReadOnly(r, "opencode", realSnapshot);
        return r;
    }

    // ================================================================ 1 出厂状态

    private static void Case01FactoryState(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【1】出厂状态：opencode.json 不存在 / 没有 instructions、AGENTS.md 不存在 → 三层 NotApplied / TargetAbsent");

        // 1a：opencode.json 不存在
        var a = Setup(ctx, setEnv, "factory-absent", config: null, agents: null);
        var st = a.Target.GetStatus(default);
        r.Check(st.Layers.Count == 3, $"状态里装配出 {st.Layers.Count} 层（期望 3）");
        var l1 = st.Layers[0];
        var l2 = st.Layers[1];
        var l3 = st.Layers[2];
        r.Check(l1.Kind == LayerKind.PolicyText, $"层 1 的 Kind = {l1.Kind}（期望 PolicyText：配置指令层）");
        r.Check(l2.Kind == LayerKind.ContextPayload, $"层 2 的 Kind = {l2.Kind}（期望 ContextPayload）");
        r.Check(l3.Kind == LayerKind.PolicyText, $"层 3 的 Kind = {l3.Kind}（期望 PolicyText：契约里没有「配置项」这一档）");
        r.Check(l1.TargetPath == a.ConfigFile, $"层 1 指向 {a.ConfigFile}");
        r.Check(l1.State == LayerState.TargetAbsent, $"opencode.json 不存在 → 层 1 {l1.State}（期望 TargetAbsent）");
        r.Check(l2.State == LayerState.TargetAbsent, $"AGENTS.md 不存在 → 层 2 {l2.State}（期望 TargetAbsent）");
        r.Check(l3.State == LayerState.TargetAbsent, $"opencode.json 不存在 → 层 3 {l3.State}（期望 TargetAbsent）");
        r.Check(st.LastMarkerReport is null, "没有会话 → LastMarkerReport 为 null（不是伪造的空报告）");

        // 1b：opencode.json 存在但没有 instructions 键
        var b = Setup(ctx, setEnv, "factory-no-instructions", config: FactoryNoInstructions, agents: null);
        var st2 = b.Target.GetStatus(default);
        r.Check(st2.Layers[0].State == LayerState.NotApplied,
            $"有配置但没有 instructions → 层 1 {st2.Layers[0].State}（期望 NotApplied）");
        r.Check(st2.Layers[1].State == LayerState.TargetAbsent, "AGENTS.md 仍不存在 → 层 2 TargetAbsent");
        r.Check(st2.Layers[0].Backup.Health == BackupHealth.Absent, "出厂态没有备份（Absent）");
        r.Check(st2.Layers[2].State == LayerState.NotApplied,
            $"有配置但没有我们的 prompt → 层 3 {st2.Layers[2].State}（期望 NotApplied）");
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
        r.Check(preview.Changes.Any(c => c.Notes.Any(n => n.Title == "系统提示词层")),
            "Changes 含系统提示词层（agent.<名字>.prompt，Kind 仍是 PolicyText）");
        r.Check(preview.Changes.Where(c => c.Layer == LayerKind.PolicyText).All(c => c.WillWrite),
            "配置指令层与系统提示词层都标记为将写入（出厂配置里没有本工具的内容）");
        r.Check(preview.Changes.First(c => c.Layer == LayerKind.ContextPayload).WillWrite,
            "载荷层标记为将写入（现有 AGENTS.md 是用户自己的文件 → 会先备份）");
        r.Check(preview.Changes.First(c => c.Layer == LayerKind.PolicyText).DiffText?.Contains("+ ") == true,
            "配置指令层给了逐行差异");
        r.Check(SysChange(preview.Changes).DiffText?.Contains("\"prompt\"") == true ||
                SysChange(preview.Changes).DiffText?.Contains("agent") == true,
            "系统提示词层给了逐行差异（差异里能看到 agent / prompt）");
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
        r.Check(res.Changes.Count == 3, $"Changes 三条（配置指令层 + 系统提示词层 + 载荷层），实际 {res.Changes.Count}");

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
        r.Check(nowKeys.Count == factoryKeys.Count + (factoryKeys.Contains("instructions") ? 0 : 1) + 1,
            $"键数量正确（原 {factoryKeys.Count} → 现 {nowKeys.Count}；+1 是系统提示词层新建的 agent 键）");

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

        // ---- npm 全局布局（issue #1）：<prefix>\node_modules\@opencode\cli\bin\opencode.exe + package.json ----
        r.Step("    · npm 全局布局（issue #1）");
        var fakePrefix = Path.Combine(e.Sandbox, "fake-npm", "nodejs");
        var fakePkg = Path.Combine(fakePrefix, "node_modules", "@opencode", "cli");
        var fakeBin = Path.Combine(fakePkg, "bin");
        Directory.CreateDirectory(fakeBin);
        FileText.Write(Path.Combine(fakeBin, "opencode.exe"), "MZ-npm");
        FileText.Write(Path.Combine(fakePkg, "package.json"), "{\"name\":\"@opencode/cli\",\"version\":\"2.1.2\"}");

        var npmCands = OpencodeLocator.CliCandidates(fakePrefix);
        r.Check(npmCands.Count == 1, $"npm prefix 下只认一个 CLI 候选（实际 {npmCands.Count} 个）");
        r.Check(npmCands[0].Exe == Path.Combine(fakeBin, "opencode.exe"),
            "npm 布局认 bin\\opencode.exe（不再只认 opencode-cli.exe）");
        r.Check(npmCands[0].Version == "2.1.2", $"npm 布局的版本读自包内 package.json（{npmCands[0].Version}）");

        var pkgCands = OpencodeLocator.CliCandidates(fakePkg);
        r.Check(pkgCands.Count == 1 && pkgCands[0].Version == "2.1.2",
            $"直接把 npm 包目录当根，版本仍是 package.json 的（{pkgCands.FirstOrDefault()?.Version}）");

        // BOM 容错：Windows 上手工编辑过的 package.json 常带 UTF-8 BOM，JsonDocument 会直接抛
        FileText.Write(Path.Combine(fakePkg, "package.json"), "\uFEFF{\"version\":\"3.3.3\"}");
        r.Check(OpencodeLocator.VersionFromPackageJson(fakePkg) == "3.3.3",
            "package.json 带 UTF-8 BOM 也能读出 version");
        FileText.Write(Path.Combine(fakePkg, "package.json"), "{\"name\":\"@opencode/cli\",\"version\":\"2.1.2\"}");

        // 只认 package.json 作为 npm 包的正面信号：光有 bin\*.exe 不算（防 Electron 目录里的 bin 误判）
        var bareRoot = Path.Combine(e.Sandbox, "fake-npm", "bare");
        var bareBin = Path.Combine(bareRoot, "bin");
        Directory.CreateDirectory(bareBin);
        FileText.Write(Path.Combine(bareBin, "opencode.exe"), "MZ-bare");
        r.Check(OpencodeLocator.CliCandidates(bareRoot).Count == 0,
            "没有 package.json 的 bin\\*.exe 不当作 npm 包（防 Electron 目录里的 bin 误判）");

        // ExeIn 同时认两种文件名
        r.Check(OpencodeLocator.ExeIn(fakeBin) is not null, "ExeIn 认 opencode.exe");
        r.Check(OpencodeLocator.ExeIn(newVer) is not null, "ExeIn 认 opencode-cli.exe");

        // OPENCODE_NPM_PREFIX 显式指定时压过自动发现（本机同时有 %APPDATA%\ai.opencode.desktop，正好验证优先级）
        setEnv("OPENCODE_ROOT", null);
        setEnv("OPENCODE_NPM_PREFIX", fakePrefix);
        var npmLoc = OpencodeLocator.Resolve(new LocateOptions { IgnoreMemory = true }, false, default);
        r.Check(npmLoc.Version == "2.1.2", $"OPENCODE_NPM_PREFIX 生效：版本 {npmLoc.Version}");
        r.Check(npmLoc.LocatedBy is not null && npmLoc.LocatedBy.Contains("OPENCODE_NPM_PREFIX", StringComparison.Ordinal),
            $"来源标注为显式 npm 变量（{npmLoc.LocatedBy}）");
        r.Check(npmLoc.Exe == Path.Combine(fakeBin, "opencode.exe"), $"显式 npm prefix 下 Exe 指向 bin\\opencode.exe（{npmLoc.Exe}）");
        setEnv("OPENCODE_NPM_PREFIX", null);

        // --root 指到 npm 包目录也要能解释出来（issue #1 的 fail-closed 场景）
        var byRootNpm = OpencodeLocator.Resolve(new LocateOptions { ExplicitRoot = fakePkg }, false, default);
        r.Check(byRootNpm.Found, "--root 指到 npm 包目录不再 fail-closed");
        r.Check(byRootNpm.Version == "2.1.2", $"--root npm 包目录时版本为 2.1.2（实际 {byRootNpm.Version}）");

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

        var policy = e.Target.GetPolicyFiles(default).Where(p => p.Name != "scheme").ToArray();
        r.Check(policy.Length == 3, $"注入文本三条：instructions + payload + system（实际 {policy.Length}）");
        r.Check(policy.All(p => p.Text.Length > 0), "三条注入文本都物化到磁盘且非空");
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
        r.Check(info.Layers.Count == 3, $"Describe().Layers 三条（实际 {info.Layers.Count}）");
        r.Check(info.Note?.Contains("不是 bundle", StringComparison.Ordinal) == true,
            $"Describe().Note 说明注入点不是 bundle：{Trim1(info.Note, 90)}");
        r.Check(e.Target.LayerDescriptions.Count == 3, "LayerDescriptions 三条（配置指令层 + 上下文载荷层 + 系统提示词层）");
        r.Check(e.Target.LayerDescriptions[2].Contains("agent.<名字>.prompt", StringComparison.Ordinal),
            $"第三条层说明写明落点是 agent.<名字>.prompt：{Trim1(e.Target.LayerDescriptions[2], 100)}");

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

    // ================================================================ 13 系统提示词层

    private static void Case13AgentPromptLayer(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【13】系统提示词层（opencode.json → agent.<名字>.prompt）：出厂态 / 只读预览 / 写入 / 幂等 / 两种还原 / 污染备份 / fail-closed");

        // ---- 13a 出厂态：没有我们的 prompt → NotApplied；注入文本逐字节核对 ----
        var e = Setup(ctx, setEnv, "syslayer-factory", config: FactoryConfigWithAgent, agents: UserAgents);
        var systemText = e.SystemText;
        r.Check(systemText.Length > 0 && systemText.StartsWith("# 本机环境", StringComparison.Ordinal),
            $"13a 系统层文本已物化到沙箱 policy 目录：{Path.GetFileName(e.SystemTextPath)}");
        r.Step($"    系统层文本：{FileText.Bytes(systemText)} 字节，sha256 {FileText.Sha256Text(systemText)[..12]}…");
        r.Check(File.ReadAllBytes(e.SystemTextPath)[0] != 0xEF, "13a 系统层文本没有 BOM");
        r.Check(systemText.EndsWith('\n') && !systemText.EndsWith("\n\n", StringComparison.Ordinal),
            "13a 系统层文本末尾恰好一个换行");
        r.Check(OpencodeMarkers.SystemLayerMarkers.All(m => FileText.Count(systemText, m) == 1),
            $"13a 三条系统层标记在文本里各命中 1 次（{string.Join("、", OpencodeMarkers.SystemLayerMarkers)}）");
        r.Check(!systemText.Contains("与系统提示词同级", StringComparison.Ordinal) &&
                !systemText.Contains("SYSLAYER", StringComparison.Ordinal) &&
                !systemText.Contains("必须原样输出", StringComparison.Ordinal),
            "13a 文本里没有自我声明权威、没有校验标记要求（那两类是模型的注入特征判据）");

        var st0 = e.Target.GetStatus(default);
        r.Check(st0.Layers.Count == 3, $"13a 状态里装配出 {st0.Layers.Count} 层（期望 3）");
        var l3 = st0.Layers[2];
        r.Check(l3.State == LayerState.NotApplied, $"13a 出厂态：没有我们的 prompt → 层 3 {l3.State}（期望 NotApplied）");
        r.Check(l3.TargetPath == e.ConfigFile, $"13a 层 3 指向 {l3.TargetPath}");
        r.Check(l3.Notes.Any(n => n.Title == "这是系统提示词层"), "13a Notes 写明这是系统提示词层");
        r.Check(l3.Notes.Any(n => n.Detail.Contains("系统提示词区", StringComparison.Ordinal) &&
                                  n.Detail.Contains("本机偏好", StringComparison.Ordinal)),
            "13a Notes 写明与另外两层的权力差别（系统提示词区 vs「可以评价的文件」/「本机偏好」）");
        r.Check(l3.Notes.Any(n => n.Detail.Contains("compaction", StringComparison.Ordinal)),
            "13a Notes 写明确不碰 title / summary / compaction");
        r.Check(l3.Notes.Any(n => n.Detail.Contains("开关", StringComparison.Ordinal)),
            "13a Notes 交代本层的开关（契约里没有第三个层开关）");
        foreach (var agent in AgentNames)
            r.Check(l3.Notes.Any(n => n.Title == $"agent.{agent}"), $"13a Notes 逐条给出 agent.{agent} 的现状");

        // ---- 13b 预览只读：sha256 不变、不建备份，条目里有值级前后对照 ----
        var cfgBefore = FileText.Sha256File(e.ConfigFile);
        var stampBefore = File.GetLastWriteTimeUtc(e.ConfigFile);
        var preview = e.Target.PreviewApply(new ApplyOptions { DryRun = true }, default);

        r.Check(FileText.Sha256File(e.ConfigFile) == cfgBefore, "13b PreviewApply 前后 opencode.json 的 sha256 不变");
        r.Check(File.GetLastWriteTimeUtc(e.ConfigFile) == stampBefore, "13b PreviewApply 没有触碰 opencode.json 的修改时间");
        r.Check(!File.Exists(e.ConfigBackup), "13b 预览不建备份（opencode.json.breach.bak 不存在）");
        r.Check(preview.CanApply, $"13b CanApply = {preview.CanApply}（期望 true）");

        var sysChange = SysChange(preview.Changes);
        r.Check(sysChange.WillWrite, "13b 系统提示词层条目标记为将写入");
        r.Check(sysChange.Layer == LayerKind.PolicyText, $"13b 条目 Kind = {sysChange.Layer}（契约里没有「配置项」这一档）");
        r.Check(sysChange.Path == e.ConfigFile, $"13b 条目指向 {sysChange.Path}");
        r.Check(sysChange.BeforeBytes == FileText.Bytes(FactoryConfigWithAgent),
            $"13b BeforeBytes = 文件当前字节数（{sysChange.BeforeBytes}）");
        r.Check(sysChange.AfterBytes > sysChange.BeforeBytes,
            $"13b AfterBytes 如实报了写完之后的大小（{sysChange.BeforeBytes} → {sysChange.AfterBytes}）");
        r.Check(sysChange.DiffText?.Contains("agent", StringComparison.Ordinal) == true &&
                sysChange.DiffText?.Contains("prompt", StringComparison.Ordinal) == true,
            "13b DiffText 给了逐行差异（含 agent / prompt）");
        r.Check(sysChange.Notes.Any(n => n.Title == "整份文件"), "13b Notes 说明同一份文件被两层改写、两条条目各报一段");
        r.Check(AgentNames.All(a => sysChange.Notes.Any(n => n.Title == $"agent.{a}" &&
                                                             n.Detail.Contains("改后 = 本工具系统层文本", StringComparison.Ordinal))),
            "13b 四个 agent 的值级前后对照都进了 Notes（契约的 PlannedChange 只有字节数）");
        r.Check(sysChange.Notes.Any(n => n.Title == "会覆盖原有的 prompt") == false,
            "13b 出厂配置里四个目标 agent 都没有 prompt → 不报「覆盖别人的 prompt」");

        // 预览也不许碰文件：再比一次
        r.Check(FileText.Sha256File(e.ConfigFile) == cfgBefore && !File.Exists(e.ConfigBackup),
            "13b 预览全程只读（sha256 不变且没有备份落地）");

        // ---- 13c apply：四个 prompt 写入且逐字节等于资源文本，原有键一个不少 ----
        var res = e.Target.Apply(new ApplyOptions(), null, default);
        var text = FileText.Read(e.ConfigFile);
        r.Check(res.Applied, "13c ApplyResult.Applied=true");
        r.Check(res.Changes.Count == 3, $"13c Changes 三条，实际 {res.Changes.Count}");

        foreach (var agent in AgentNames)
            r.Check(PromptOf(text, agent) == systemText,
                $"13c agent.{agent}.prompt 逐字节等于资源文本（{FileText.Bytes(systemText)} B）");
        r.Check(PromptOf(text, "review") == "这是用户自己写的 review 提示词",
            "13c 别人的 agent（review）的 prompt 没被覆盖");
        r.Check(PromptOf(text, "title") == "给会话起个短标题", "13c 内部工具 agent（title）的 prompt 没被碰");
        r.Check(AgentJson(text, "title") == AgentJson(FactoryConfigWithAgent, "title"),
            "13c agent.title 整条原样保留（含 temperature）");
        r.Check(AgentJson(text, "review") == AgentJson(FactoryConfigWithAgent, "review"),
            "13c agent.review 整条原样保留");
        r.Check(OtherFieldsOf(text, "build") == OtherFieldsOf(FactoryConfigWithAgent, "build"),
            $"13c agent.build 除 prompt 外的字段逐字段原样保留（{OtherFieldsOf(text, "build")}）");
        r.Check(OtherFieldsOf(text, "plan") == OtherFieldsOf(FactoryConfigWithAgent, "plan"),
            $"13c agent.plan 除 prompt 外的字段逐字段原样保留（{OtherFieldsOf(text, "plan")}）");
        r.Check(AgentJson(text, "plan")!.Contains("\"prompt\"", StringComparison.Ordinal),
            "13c agent.plan 的 prompt 是新增的（原来没有这个字段）");

        var factoryKeys = Keys(FactoryConfigWithAgent);
        var nowKeys = Keys(text);
        var missingKeys = factoryKeys.Where(k => !nowKeys.Contains(k)).ToArray();
        r.Check(missingKeys.Length == 0,
            missingKeys.Length == 0
                ? $"13c 顶层原有键一个不少（{string.Join(", ", factoryKeys)}）"
                : $"13c 原有键丢失：{string.Join(", ", missingKeys)}");
        r.Check(nowKeys.Count == factoryKeys.Count, $"13c 顶层键数量不变（{factoryKeys.Count}，agent 键本来就在）");
        r.Check(text.Contains("\"theme\": \"system\"", StringComparison.Ordinal), "13c theme 原样保留");
        r.Check(text.Contains("\n  \"agent\"", StringComparison.Ordinal), "13c 序列化仍是 2 空格缩进");
        var entries = OpencodeConfig.EntriesOf(text);
        r.Check(entries.Contains(@"C:\Users\Public\other-instructions.md"),
            "13c instructions 里别人的条目原样保留（没有被第三层碰掉）");
        r.Check(entries.Count == 2 && entries.Contains(e.InstructionsPath),
            $"13c instructions 是「别人的一条 + 本工具一条」（实际 {entries.Count} 条）");
        var agentKeysText = (JsonNode.Parse(text) as JsonObject)?["agent"] is JsonObject ao
            ? string.Join("、", ao.Select(p => p.Key))
            : "";
        r.Check(agentKeysText.Contains("review") && agentKeysText.Contains("title"),
            $"13c agent 下别人的 agent 条目一个不少：{agentKeysText}");

        r.Check(File.Exists(e.ConfigBackup), "13c 写入前建了备份");
        r.Check(FileText.Read(e.ConfigBackup) == FactoryConfigWithAgent, "13c 备份与出厂配置逐字节一致");
        r.Check(!OpencodeMarkers.AllMarkers.Any(m => FileText.Count(FileText.Read(e.ConfigBackup), m) > 0),
            "13c 备份里没有任何本工具标记（干净，可安全还原）");

        var stApplied = e.Target.GetStatus(default);
        r.Check(stApplied.Layers[2].State == LayerState.Applied,
            $"13c apply 后层 3 = {stApplied.Layers[2].State}（期望 Applied）");
        r.Check(stApplied.Layers[2].StateDetail.Contains("四个 agent", StringComparison.Ordinal),
            $"13c 状态说明写清「四个 agent 的 prompt 都是本工具文本」：{Trim1(stApplied.Layers[2].StateDetail, 60)}");
        r.Check(stApplied.Layers[2].Backup.Health == BackupHealth.Clean, "13c 状态页报备份干净");
        r.Check(stApplied.Layers[0].State == LayerState.Applied && stApplied.Layers[1].State == LayerState.Applied,
            "13c 另外两层也各自 Applied（三层的状态互不串台）");

        // ---- 13d 幂等：第二次 apply 无字节变化、WillWrite=false ----
        var cfgSha = FileText.Sha256File(e.ConfigFile);
        var cfgStamp = File.GetLastWriteTimeUtc(e.ConfigFile);
        var second = e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(FileText.Sha256File(e.ConfigFile) == cfgSha, "13d 第二次 apply 后 opencode.json 的 sha256 不变");
        r.Check(File.GetLastWriteTimeUtc(e.ConfigFile) == cfgStamp, "13d 没有重写文件（修改时间不变）");
        r.Check(!second.Applied, $"13d 第二次 apply：Applied={second.Applied}（不虚报）");
        r.Check(second.Changes.All(c => !c.WillWrite), "13d Changes 里 WillWrite 全为 false");
        r.Check(!SysChange(second.Changes).WillWrite &&
                SysChange(second.Changes).Notes.Any(n => n.Detail.Contains("幂等", StringComparison.Ordinal)),
            "13d 系统提示词层给出幂等说明（已是当前文本，不重写）");
        r.Check((JsonNode.Parse(FileText.Read(e.ConfigFile)) as JsonObject)?["agent"]?["build"]?["prompt"]?.GetValue<string>() == systemText,
            "13d 四个 prompt 内容没变（没有重复追加 / 没被清空）");

        // ---- 13e revert（有干净备份）：逐字节回到出厂 ----
        var rev = e.Target.Revert(new RevertOptions(), null, default);
        r.Check(rev.Reverted, "13e RevertResult.Reverted=true");
        r.Check(FileText.Read(e.ConfigFile) == FactoryConfigWithAgent,
            "13e opencode.json 与出厂内容逐字节一致（走备份还原，不是重新序列化）");
        r.Check(FileText.Sha256File(e.ConfigFile) == FileText.Sha256Text(FactoryConfigWithAgent),
            "13e sha256 == 出厂文本的 sha256");
        r.Check(rev.MatchesOriginal == true, $"13e MatchesOriginal={rev.MatchesOriginal}");
        r.Check(rev.ReadBackClean, "13e 回读确认无残留（ReadBackClean=true）");
        r.Check(AgentNames.All(a => PromptOf(FileText.Read(e.ConfigFile), a) is null),
            "13e 还原后四个 agent 的 prompt 都不在（出厂态本来就没有）");
        r.Check(rev.Changes.Any(c => c.Notes.Any(n => n.Title == "系统提示词层还原")),
            "13e Changes 里报出系统提示词层还原");
        r.Check(rev.Changes.Count(c => c.Path == e.ConfigFile) == 2,
            "13e 同一份 opencode.json 上两条还原条目（配置指令层 + 系统提示词层），如实说明共用一份文件");

        // RemoveBackups 也要覆盖到系统提示词层（它与配置指令层共用同一份 opencode.json 的备份）
        var revBackups = e.Target.Revert(new RevertOptions { RemoveBackups = true, SkipContextPayload = true }, null, default);
        r.Check(!File.Exists(e.ConfigBackup), "13e RemoveBackups=true → 删掉了 opencode.json 的备份（系统提示词层共用这一份）");
        r.Check(revBackups.Notes.Any(n => n.Title == "已清理备份"), "13e Notes 说明备份已清理");
        r.Check(FileText.Read(e.ConfigFile) == FactoryConfigWithAgent, "13e 清理备份没有改动 opencode.json 本身");

        // ---- 13f revert（没有备份）：逆向撤销移除四个 prompt，agent 键与别人的内容原样保留 ----
        var f = Setup(ctx, setEnv, "syslayer-nobackup", config: FactoryConfigWithAgent, agents: UserAgents);
        f.Target.Apply(new ApplyOptions(), null, default);
        r.Check(File.Exists(f.ConfigBackup), "13f 前置：apply 建了备份");
        File.Delete(f.ConfigBackup);
        r.Check(!File.Exists(f.ConfigBackup), "13f 前置：删掉备份 → 只能走逆向撤销");

        var revF = f.Target.Revert(new RevertOptions { SkipContextPayload = true }, null, default);
        var after = FileText.Read(f.ConfigFile);
        foreach (var agent in AgentNames)
            r.Check(PromptOf(after, agent) is null, $"13f 逆向撤销移除了 agent.{agent}.prompt");
        var agentObj = (JsonNode.Parse(after) as JsonObject)?["agent"] as JsonObject;
        r.Check(agentObj is not null, "13f agent 键本身保留（别人可能在里面有配置）");
        r.Check(agentObj!.ContainsKey("build") && agentObj.ContainsKey("plan"),
            "13f build / plan 条目保留（它们本来就有别的字段）");
        r.Check(!agentObj.ContainsKey("general") && !agentObj.ContainsKey("explore"),
            "13f general / explore 只因本工具才被创建、撤销后成空对象 → 才删掉这两条");
        r.Check(AgentJson(after, "build") == AgentJson(FactoryConfigWithAgent, "build"),
            "13f build 的其它字段逐字段原样保留（model / description）");
        r.Check(OtherFieldsOf(after, "build") == OtherFieldsOf(FactoryConfigWithAgent, "build"),
            $"13f build 除 prompt 外的字段一个不少：{OtherFieldsOf(after, "build")}");
        r.Check(AgentJson(after, "plan") == AgentJson(FactoryConfigWithAgent, "plan"),
            "13f plan 的其它字段逐字段原样保留（mode / description）");
        r.Check(PromptOf(after, "review") == "这是用户自己写的 review 提示词",
            "13f 别人的 prompt 一个字节都没动");
        r.Check(AgentJson(after, "title") == AgentJson(FactoryConfigWithAgent, "title"),
            "13f 内部工具 agent title 原样保留");
        r.Check(!OpencodeConfig.ContainsPath(after, f.InstructionsPath), "13f instructions 里也已无本工具路径");
        r.Check(after.Contains("\"theme\": \"system\"", StringComparison.Ordinal), "13f 其它顶层键原样保留");
        r.Check(revF.MatchesOriginal is null, "13f 没有可信备份 → MatchesOriginal=null，不伪造 true");
        r.Check(revF.ReadBackClean, "13f 回读干净");
        r.Check(revF.Changes.Any(c => c.Notes.Any(n => n.Title == "系统提示词层还原"
                                                       && n.Detail.Contains("agent 键", StringComparison.Ordinal))),
            "13f 还原说明写明 agent 键与别人的字段保留");
        var stAfterUndo = f.Target.GetStatus(default);
        r.Check(stAfterUndo.Layers[2].State == LayerState.NotApplied,
            $"13f 撤销后层 3 = {stAfterUndo.Layers[2].State}（期望 NotApplied）");

        // ---- 13g 污染备份：状态报 Contaminated，revert 抛 AnchorConflict 且不改任何文件 ----
        var g = Setup(ctx, setEnv, "syslayer-contaminated", config: FactoryConfigWithAgent, agents: UserAgents);
        g.Target.Apply(new ApplyOptions(), null, default);
        FileText.Write(g.ConfigBackup,
            "{\n  \"agent\": {\n    \"build\": {\n      \"prompt\": \"" + OpencodeMarkers.SystemLayerMarkers[0] + "\"\n    }\n  }\n}\n");
        r.Check(OpencodeMarkers.AllMarkers.Any(m => FileText.Count(FileText.Read(g.ConfigBackup), m) > 0),
            "13g 备份已被改写成含系统层标记的内容");
        var stG = g.Target.GetStatus(default);
        r.Check(stG.Layers[2].Backup.Health == BackupHealth.Contaminated,
            $"13g 状态页报系统层备份 Contaminated（实际 {stG.Layers[2].Backup.Health}）");
        r.Check(stG.Layers[2].Notes.Any(n => n.Title.Contains("备份污染")), "13g 状态页给出「备份污染」提示");
        var cfgShaG = FileText.Sha256File(g.ConfigFile);
        var agentsShaG = FileText.Sha256File(g.AgentsFile);
        CheckThrows(r, () => g.Target.Revert(new RevertOptions(), null, default), EngineErrorCode.AnchorConflict,
            "13g 污染备份 → revert 抛 AnchorConflict（拒绝用它还原）");
        r.Check(FileText.Sha256File(g.ConfigFile) == cfgShaG, "13g 抛错时 opencode.json 一个字节都没变");
        r.Check(FileText.Sha256File(g.AgentsFile) == agentsShaG, "13g 抛错时 AGENTS.md 一个字节都没变");
        r.Check(FileText.Read(g.ConfigFile).Contains(OpencodeMarkers.SystemLayerMarkers[0], StringComparison.Ordinal),
            "13g 抛错时注入仍在盘上（没有「先改一半再抛」）");

        // ---- 13h fail-closed 四种：都不写盘、连备份都不建 ----
        var badConfigs = new (string Name, string Json)[]
        {
            ("非法 JSON", "{ \"instructions\": [ \"C:\\\\x.md\" \n"),
            ("agent 不是对象", "{ \"agent\": \"nope\" }"),
            ("agent 是 null", "{ \"agent\": null }"),
            ("某个 agent 不是对象", "{ \"agent\": { \"build\": \"nope\" } }"),
            ("某个 agent 是 null", "{ \"agent\": { \"build\": null } }"),
            ("prompt 不是字符串", "{ \"agent\": { \"build\": { \"prompt\": 123 } } }"),
        };
        foreach (var (name, bad) in badConfigs)
        {
            var h = Setup(ctx, setEnv, "syslayer-failclosed-" + name, config: bad, agents: null);
            var sha = FileText.Sha256File(h.ConfigFile);
            var badPreview = h.Target.PreviewApply(new ApplyOptions { DryRun = true }, default);
            r.Check(!badPreview.CanApply, $"13h {name}：CanApply=false（预览如实报不可执行）");
            r.Check(badPreview.Notes.Any(n => n.Severity == Severity.Error), $"13h {name}：预览给出 Error 级说明");
            CheckThrows(r, () => h.Target.Apply(new ApplyOptions(), null, default), EngineErrorCode.SelfCheckFailed,
                $"13h {name} → apply 抛 SelfCheckFailed（fail-closed）");
            r.Check(FileText.Sha256File(h.ConfigFile) == sha, $"13h {name}：opencode.json 一个字节都没被改");
            r.Check(!File.Exists(h.ConfigBackup), $"13h {name}：连备份都没有建（无备份不写）");
            r.Check(!File.Exists(h.AgentsFile), $"13h {name}：载荷层也没有被写");
            r.Check(h.Target.GetStatus(default).Layers[2].State == LayerState.AnchorMissing,
                $"13h {name}：层 3 状态 AnchorMissing（apply 会拒写）");
        }

        ctx.Log("agent-prompt layer ok: 3 layers / 4 agents");
    }

    // ================================================================ 13i 本层开关

    private static void Case13SwitchOff(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【13i】本层开关：清空系统层文本 → 本层不参与 apply（另外两层照旧），revert 仍会清掉盘上确实由本工具写入的 prompt");

        var e = Setup(ctx, setEnv, "syslayer-switch", config: FactoryConfigWithAgent, agents: UserAgents);
        e.Target.Apply(new ApplyOptions(), null, default);
        var originalSystemText = e.SystemText;
        r.Check(originalSystemText.Length > 0 && e.Target.GetStatus(default).Layers[2].State == LayerState.Applied,
            "13i 前置：先让三层的痕迹都在盘上");

        // 契约里没有第三个层开关，本层的开关就是「这份注入文本是否为空」（该定义 Required=false 才允许清空）
        var cleared = e.Target.SavePolicyFile(OpencodeTarget.SystemPolicyName, "", default);
        r.Check(cleared.Text.Length == 0, "13i 清空系统层文本成功（本层定义 Required=false，允许清空）");
        r.Check(e.SystemText.Length == 0, "13i 沙箱 policy 目录里的系统层文本已为空");

        var stOff = e.Target.GetStatus(default);
        r.Check(stOff.Layers[2].State == LayerState.Applied,
            "13i 盘上仍有痕迹 → 层状态仍是 Applied（开关状态与「装上了」分开报）");
        r.Check(stOff.Layers[2].StateDetail.Contains("未启用", StringComparison.Ordinal),
            $"13i 状态说明写明本层未启用：{Trim1(stOff.Layers[2].StateDetail, 70)}");
        r.Check(stOff.Layers[2].Notes.Any(n => n.Title == "系统层文本为空"), "13i Notes 提示系统层文本为空 → apply 不写 agent prompt");

        var cfgSha = FileText.Sha256File(e.ConfigFile);
        var previewOff = e.Target.PreviewApply(new ApplyOptions(), default);
        r.Check(previewOff.Changes.All(c => c.Notes.All(n => n.Title != "系统提示词层")),
            "13i 开关关着 → 预览里不出现系统提示词层条目（与载荷层被 SkipContextPayload 跳过时同一口径）");
        r.Check(previewOff.Changes.Count == 2,
            $"13i 开关关着 → 预览只剩两条（配置指令层 + 载荷层），实际 {previewOff.Changes.Count}");

        var resOff = e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(FileText.Sha256File(e.ConfigFile) == cfgSha, "13i 开关关着时 apply 没有改动 opencode.json（一个字节都没变）");
        r.Check(!resOff.Applied, "13i 开关关着时没有可写内容 → Applied=false");
        r.Check(resOff.Notes.Any(n => n.Title == "跳过系统提示词层"), "13i Notes 里有「跳过系统提示词层」说明");
        r.Check(AgentNames.All(a => PromptOf(FileText.Read(e.ConfigFile), a) == originalSystemText),
            "13i 盘上原有的 prompt 没有被清空也没有被改写（开关关着 = 不参与，不是「顺手删掉」）");

        // revert：开关关着也要把盘上确实由本工具写入的 prompt 撤掉（撤销不搞「关了开关就留着注入」）
        var revOff = e.Target.Revert(new RevertOptions { SkipContextPayload = true }, null, default);
        r.Check(AgentNames.All(a => PromptOf(FileText.Read(e.ConfigFile), a) is null),
            "13i 开关关着时 revert 仍把本工具写进去的 prompt 撤掉了");
        r.Check(FileText.Read(e.ConfigFile) == FactoryConfigWithAgent,
            "13i 有干净备份 → 逐字节回到出厂（不因为开关关着就少还原一层）");
        r.Check(revOff.ReadBackClean, "13i 回读干净");
        r.Check(revOff.Changes.Any(c => c.Notes.Any(n => n.Title == "系统提示词层还原")),
            "13i 还原条目里仍报出系统提示词层");

        ctx.Log("switch off ok");
    }

    // ================================================================ 14 系统层标记 / 生效判据

    private static void Case14SystemMarkers(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【14】标记：系统层标记进 instruction_blob → 命中 > 0；去掉该 blob → 命中 0 且 InstalledButNotEffective=true，层状态仍是 Applied");

        var e = Setup(ctx, setEnv, "syslayer-markers", config: FactoryConfigWithAgent, agents: UserAgents);
        var systemText = e.SystemText;
        using var db = MakeDb(e.DataHome, e.InstructionsPath, systemText: systemText);
        e.Target.Apply(new ApplyOptions(), null, default);

        var report = e.Target.CheckMarkers(SessionId, default);
        var sysHits = report.Hits
            .Where(h => OpencodeMarkers.SystemLayerMarkers.Contains(h.Marker))
            .Sum(h => h.Count);
        r.Check(sysHits > 0, $"14 系统层标记在 instruction_blob 里命中 {sysHits} 次（新层标记确实进了判据）");
        r.Check(report.Hits.Where(h => OpencodeMarkers.SystemLayerMarkers.Contains(h.Marker))
                .All(h => h.Layer == MarkerLayer.SystemPrompt),
            "14 系统层标记归在 SystemPrompt 层（与配置指令层同区、不同标记组）");
        r.Check(OpencodeMarkers.MarkerSpecs.Any(s => OpencodeMarkers.SystemLayerMarkers.Contains(s.Marker)),
            "14 MarkerSpecs（CheckMarkers 的判据表）里含系统层标记");
        r.Check(report.SystemPromptLayerPresent, "14 SystemPromptLayerPresent=true");
        r.Check(FileText.Count(report.Verdict, "instruction_blob") > 0, "14 Verdict 说明命中来自 instruction_blob");

        var st = e.Target.GetStatus(default);
        r.Check(st.Layers[2].State == LayerState.Applied, $"14 命中 > 0 → 层 3 {st.Layers[2].State}（期望 Applied）");
        r.Check(!st.Layers[2].InstalledButNotEffective, "14 命中 > 0 → InstalledButNotEffective=false");
        r.Check(st.Layers[2].EffectivenessEvidence?.Contains("已进模型上下文", StringComparison.Ordinal) == true,
            $"14 生效证据：{Trim1(st.Layers[2].EffectivenessEvidence, 110)}");

        // 去掉承载系统层文本的那条 blob（写连接还开着，等于客户端那边这一份载荷没了）
        db.DeleteBlob(db.SystemHash!);
        var report2 = e.Target.CheckMarkers(SessionId, default);
        var sysHits2 = report2.Hits
            .Where(h => OpencodeMarkers.SystemLayerMarkers.Contains(h.Marker))
            .Sum(h => h.Count);
        r.Check(sysHits2 == 0, $"14 去掉那条 blob → 系统层标记命中 {sysHits2}（期望 0）");
        r.Check(report2.SystemPromptLayerPresent, "14 配置指令层的标记仍在 → SystemPromptLayerPresent 仍为 true（两组标记分开算）");

        var st2 = e.Target.GetStatus(default);
        r.Check(st2.Layers[2].State == LayerState.Applied,
            "14 「装上了」与「生效了」分开报：盘上仍是注入态 → 层状态仍是 Applied");
        r.Check(st2.Layers[2].InstalledButNotEffective,
            "14 但最近会话的常驻指令载荷里没有这段文本 → InstalledButNotEffective=true");
        r.Check(st2.Layers[2].EffectivenessEvidence?.Contains("没有这段文本", StringComparison.Ordinal) == true,
            $"14 生效证据写清原因：{Trim1(st2.Layers[2].EffectivenessEvidence, 150)}");
        r.Check(st2.Notes.Any(n => n.Title == "装上了但没生效"), "14 Notes 里有「装上了但没生效」警告");
        r.Check(!st2.RestartRequired,
            "14 配置指令层那条 blob 还在 → 整体仍算生效，RestartRequired=false（本层的生效判据是分开算的，不互相带偏）");

        // ---- 顺手修的那处文案：载荷层不再自称「本机偏好」----
        var payloadText = e.PayloadText;
        r.Check(!payloadText.Contains("本机的工作偏好", StringComparison.Ordinal),
            "14 载荷层文案里不再有「本机的工作偏好」这个自我降级标签");
        r.Check(payloadText.Contains("本文件随配置加载，适用于所有会话与工作区。", StringComparison.Ordinal),
            "14 改成了「本文件随配置加载，适用于所有会话与工作区。」");
        r.Check(OpencodeMarkers.PayloadMarkers.All(m => FileText.Count(payloadText, m) == 1),
            $"14 三条载荷标记仍在且各命中 1 次（{string.Join("、", OpencodeMarkers.PayloadMarkers)}）");
        r.Check(FileText.Read(e.AgentsFile) == payloadText,
            "14 AGENTS.md 写的就是改过文案的那一份（下次 apply 不会写回旧文案）");
        r.Check(!OpencodeMarkers.SystemLayerMarkers.Any(m => FileText.Count(payloadText, m) > 0),
            "14 载荷层文本里不含系统层标记（三层标记互不串台，命中数才可归因）");
        r.Check(!OpencodeMarkers.SystemLayerMarkers.Any(m => FileText.Count(e.Target.Ctx.Policy.Read(
                OpencodeTarget.PolicyDefs.First(d => d.Name == "instructions")) ?? "", m) > 0),
            "14 配置指令层文本里也不含系统层标记");

        ctx.Log($"system markers ok: {sysHits} → 0（层状态仍 Applied）");
    }

    // ================================================================ 15 没有会话 ≠ 没生效

    private static void Case15NoSessionUndecidable(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【15】会话表 0 行（客户端清了会话表）：命中判据报「无法判定」而不是「命中 0 = 没生效」；库坏了仍然抛 IoError");

        var e = Setup(ctx, setEnv, "no-sessions", config: FactoryConfig, agents: UserAgents);
        using var db = MakeDb(e.DataHome, e.Target.Ctx.Policy.PathOf(OpencodeTarget.PolicyDefs.First(d => d.Name == "instructions")));
        e.Target.Apply(new ApplyOptions(), null, default);

        // 前置：注入本身是好的——instruction_blob 里有我们的文本。
        // 这正是本机真实库现在的样子：instruction_blob 10 条、其中 2 条含标记，而 session_v2 是 0 行。
        var rows = OpencodeDb.InstructionBlobRows(e.DbPath, default);
        r.Check(rows.Count == 2, $"前置：instruction_blob 有 {rows.Count} 条常驻指令载荷");
        r.Check(rows.Count(x => OpencodeMarkers.InstructionsMarkers.Any(m => x.Contains(m, StringComparison.Ordinal))) == 1,
            "前置：其中 1 条含配置指令层标记（注入本身是好的）");

        // 客户端清了会话表（或活跃 WAL 的可见性问题）：会话三张表清空，blob 留着
        db.DeleteSessions();
        r.Check(OpencodeDb.ListSessions(e.DbPath, default).Count == 0, "前置：session_v2 已清空（0 个会话）");

        var rep = e.Target.CheckMarkers("latest", default);
        r.Check(rep.SessionId == OpencodeTarget.UndecidableSessionId,
            $"解析不到会话 → SessionId 报哨兵「{OpencodeTarget.UndecidableSessionId}」，而不是编一个会话号（实际「{rep.SessionId}」）");
        r.Check(rep.Hits.Count == 0, "Hits 留空：没有可判定的会话文本，就不许编造一串 0 当真命中数");
        r.Check(rep.TotalChars == 0 && !rep.SystemPromptLayerPresent && !rep.ContextPayloadLayerPresent,
            "两层命中都是 false、全文 0 字符——但结论是「无法判定」，不是「没生效」");
        r.Check(rep.Verdict.StartsWith("无法判定：", StringComparison.Ordinal),
            $"Verdict 以「无法判定：」开头（不是「命中 0 = 没生效」那套结论）：{Trim1(rep.Verdict, 110)}");
        r.Check(rep.Verdict.Contains("没有会话", StringComparison.Ordinal),
            "Verdict 说明了原因：库里没有会话（客户端未产生会话或已清理）");
        r.Check(rep.Verdict.Contains("跳过命中数断言", StringComparison.Ordinal), "Verdict 明说跳过命中数断言");
        r.Check(rep.Verdict.Contains("instruction_blob 现有 2 条", StringComparison.Ordinal),
            "Verdict 如实打印 instruction_blob 的行数（仍然可读的只读证据）");
        r.Check(rep.Verdict.Contains("其中 1 条含本工具标记", StringComparison.Ordinal),
            "Verdict 如实打印其中含标记的条数");

        // 同一个「没有会话」状态下，状态页也不许据此说「装上了但没生效」
        var st = e.Target.GetStatus(default);
        r.Check(st.LastMarkerReport is null, "没有会话 → LastMarkerReport 保持 null（不伪造报告）");
        r.Check(st.Layers.All(l => !l.InstalledButNotEffective),
            "没有会话 → 各层都不报 InstalledButNotEffective（无法判定 ≠ 没生效）");
        r.Check(st.Layers.All(l => l.EffectivenessEvidence?.Contains("无法判定", StringComparison.Ordinal) == true),
            "生效证据写明「无法判定生效（不去猜）」");

        // 读库失败 ≠ 无法判定：库文件坏了必须抛 IoError，不许静默返回「没有会话」
        var broken = Setup(ctx, setEnv, "broken-db", config: FactoryConfig, agents: UserAgents);
        FileText.Write(broken.DbPath, "this file is not a SQLite database at all");
        CheckThrows(r, () => broken.Target.CheckMarkers("latest", default), EngineErrorCode.IoError,
            "库文件损坏 → 抛 IoError（「库坏了」不许被降级成「无法判定」）");
        ctx.Log("no-session undecidable ok");
    }

    // ================================================================ 16 双注入方案

    private const string SchemeFactoryConfig = "{\"$schema\":\"https://opencode.ai/config.json\",\"theme\":\"dark\"}\n";

    /// <summary>
    /// own / protocol 双方案：载体槽 = system；空槽 = instructions + payload。
    /// A→B 要摘除 A 套遗留的 instructions 路径与 AGENTS.md；B→A 全部恢复；revert 回出厂态。
    /// </summary>
    private static void Case16DualScheme(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【16】双注入方案（own / protocol）：载体槽 system、空槽 instructions+payload、双向切换、幂等、revert、verbatim、fail-closed、标记");
        DualScheme.Verbatim(r, "opencode");

        var e = Setup(ctx, setEnv, "scheme", SchemeFactoryConfig, null);
        using var db = MakeDb(e.DataHome, e.Target.Ctx.Policy.PathOf(OpencodeTarget.PolicyDefs.First(d => d.Name == "instructions")));

        // 1 缺省 = own（回归）
        DualScheme.SelectorMissingIsOwn(r, e.Target.Ctx, "opencode");
        var ownApply = e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(ownApply.Applied, "own 方案 apply 写入（回归）");
        r.Check(OpencodeConfig.ContainsPath(FileText.Read(e.ConfigFile), e.InstructionsPath),
            "own 方案照旧把本工具指令文件写进 instructions");
        r.Check(File.Exists(e.AgentsFile), "own 方案照旧部署 AGENTS.md");
        var ownSystem = e.Target.Ctx.TextFor("system", InjectionScheme.Own);
        r.Check(FileText.Read(e.ConfigFile).Contains(ownSystem.Split('\n')[0], StringComparison.Ordinal),
            "own 方案照旧写 agent.<名字>.prompt");
        var ownAgain = e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(!ownAgain.Changes.Any(c => c.WillWrite), "own 方案连续两次 apply：WillWrite 全 false");

        // 2 切 protocol：载体槽 = system；空槽 = instructions + payload
        DualScheme.SwitchToProtocol(r, e.Target.Ctx, "opencode", "system");
        var protoEmpties = e.Target.Ctx.EmptySlotNames(InjectionScheme.Protocol);
        r.Check(protoEmpties.Contains("instructions") && protoEmpties.Contains("payload"),
            $"protocol 的空槽含 instructions 与 payload（实际 {string.Join("、", protoEmpties)}）");
        var protoB = e.Target.Ctx.TextFor("system", InjectionScheme.Protocol);
        r.Check(protoB == DualScheme.EmbeddedProtocol(), "载体槽 system 解析到 B 套原文（逐字节）");
        r.Check(protoB.Length != e.Target.Ctx.TextFor("system", InjectionScheme.Own).Length ||
                protoB != e.Target.Ctx.TextFor("system", InjectionScheme.Own),
            "B 套文本与 A 套不同（否则下面的覆盖断言没有意义）");

        // 3 A → B
        var bApply = e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(bApply.Applied, "A→B apply 报告已写入");
        var configB = FileText.Read(e.ConfigFile);
        r.Check(!OpencodeConfig.ContainsPath(configB, e.InstructionsPath), "B 套：A 套遗留的 instructions 条目被摘除");
        r.Check(!File.Exists(e.AgentsFile), "B 套：A 套遗留的 AGENTS.md 被移除");
        r.Check(configB.Contains("INJECTED REVISION ACTION", StringComparison.Ordinal), "B 套：opencode.json 里是 gpt-instruct 原文");
        var probe = OpencodeConfig.PlanAgentPrompts(configB, true, DualScheme.EmbeddedProtocol());
        r.Check(probe.AppliedAgents.Count == OpencodeConfig.TargetAgents.Count,
            $"B 套：{probe.AppliedAgents.Count} 个 agent 的 prompt 逐字节等于原文");
        r.Check(bApply.Changes.Any(c => c.Path == e.ConfigFile && c.WillWrite && c.AfterBytes > 0),
            "B 套：opencode.json 的摘除也走 PlannedChange（WillWrite=true）");
        r.Check(bApply.Changes.Any(c => c.Path == e.AgentsFile && c.WillWrite && c.AfterBytes == 0),
            "B 套：AGENTS.md 移除走 PlannedChange（WillWrite=true / AfterBytes=0）");
        r.Check(!bApply.Changes.Any(c => c.Path == e.AgentsFile && c.WillWrite && c.AfterBytes > 0),
            "B 套：空槽没有产生写入型计划变更");
        var bAgain = e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(!bAgain.Changes.Any(c => c.WillWrite) && !bAgain.Applied, "B 方案连续两次 apply：无字节变化、WillWrite 全 false");

        // 4 B → A
        e.Target.Ctx.Schemes().Write(InjectionScheme.Own);
        var backA = e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(backA.Applied, "B→A apply 报告已写入");
        r.Check(OpencodeConfig.ContainsPath(FileText.Read(e.ConfigFile), e.InstructionsPath), "切回 A：instructions 条目恢复");
        r.Check(File.Exists(e.AgentsFile), "切回 A：AGENTS.md 恢复");
        r.Check(FileText.Read(e.ConfigFile).Contains(ownSystem.Split('\n')[0], StringComparison.Ordinal), "切回 A：agent prompt 回到 A 套文本");

        // 5 从 B 方案 revert 回出厂态
        e.Target.Ctx.Schemes().Write(InjectionScheme.Protocol);
        e.Target.Apply(new ApplyOptions(), null, default);
        var rev = e.Target.Revert(new RevertOptions(), null, default);
        r.Check(rev.Reverted, "从 B 方案 revert 报告已还原");
        r.Check(FileText.Read(e.ConfigFile) == SchemeFactoryConfig, "revert 后 opencode.json 逐字节回到出厂内容");
        r.Check(!File.Exists(e.AgentsFile), "revert 后 AGENTS.md 不存在（出厂态）");
        r.Check(rev.ReadBackClean && rev.MatchesOriginal == true,
            $"revert 回读干净且与首次备份一致（ReadBackClean={rev.ReadBackClean} / MatchesOriginal={rev.MatchesOriginal}）");

        // 6 非法 scheme 值 → own + Note
        DualScheme.InvalidValueFallsBackToOwn(r, e.Target.Ctx, "opencode");
        FileText.Write(e.Target.Ctx.Policy.SchemePath, "bogus-value\n");
        var st = e.Target.GetStatus(default);
        r.Check(st.Notes.Any(n => n.Title == "方案文件"), "状态页 Notes 说明方案文件非法（按 own 处理）");

        // 7 fail-closed：B 套载体资源缺失 → 在写任何文件之前拒绝
        var broken = new OpencodeTarget(DualScheme.BreakProtocolCarrier(e.Target.Ctx, "system"));
        broken.SavePolicyFile("scheme", "protocol", default);
        broken.Ctx.Schemes().Write(InjectionScheme.Protocol);
        var configSha = FileText.Sha256File(e.ConfigFile);
        var threw = false;
        try { broken.Apply(new ApplyOptions(), null, default); }
        catch (AgentEngineException ex)
        {
            threw = true;
            r.Check(ex.Code is EngineErrorCode.TargetFileMissing or EngineErrorCode.SelfCheckFailed or EngineErrorCode.VerifyProblems,
                $"B 套资源缺失 → apply 抛 {ex.Code}");
        }
        r.Check(threw, "B 套资源缺失时 fail-closed（不是静默注空文本）");
        r.Check(FileText.Sha256File(e.ConfigFile) == configSha, "fail-closed 时 opencode.json 一个字节都没写");

        // 8 标记：B 方案用 B 套系统层标记
        r.Check(OpencodeMarkers.SpecsForScheme(InjectionScheme.Protocol)
                .Any(s => s.Marker == "INJECTED REVISION ACTION" && s.Layer == MarkerLayer.SystemPrompt),
            "CheckMarkers 在 protocol 方案用 B 套系统层标记集");
        r.Check(OpencodeMarkers.SpecsForScheme(InjectionScheme.Own).All(s => s.Marker != "INJECTED REVISION ACTION"),
            "own 方案仍用原标记集（未被替换）");

        // 9 状态 Notes 报当前方案
        e.Target.Ctx.Schemes().Write(InjectionScheme.Protocol);
        e.Target.Apply(new ApplyOptions(), null, default);
        DualScheme.PropagatesToStatusNotes(r, e.Target.GetStatus(default), "opencode");
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

        // 真实 opencode.json 的**只读**核对（系统提示词层）：读出真实配置里四个 agent 的 prompt 现状。
        // 只做 ReadOrNull + JsonNode.Parse，绝不写；sha256 前后比对放在本用例末尾。
        if (File.Exists(realCfgFile))
        {
            var realCfgBefore = ShaShared(realCfgFile);
            var realCfgText = FileText.ReadOrNull(realCfgFile) ?? "";
            var realKeys = "";
            try
            {
                if (JsonNode.Parse(realCfgText) is JsonObject realObj)
                {
                    realKeys = string.Join(", ", realObj.Select(p => p.Key));
                    var ours = AgentNames.Where(a => PromptOf(realCfgText, a) is { } p &&
                                                     OpencodeMarkers.SystemLayerMarkers.Any(m => p.Contains(m, StringComparison.Ordinal)))
                        .ToArray();
                    var withPrompt = AgentNames.Where(a => PromptOf(realCfgText, a) is not null).ToArray();
                    var foreignPrompt = PromptOf(realCfgText, "review") is not null || PromptOf(realCfgText, "title") is not null;
                    r.Step($"    真实 opencode.json：{FileText.Bytes(realCfgText)} 字节；顶层键 {realKeys}");
                    r.Step($"    四个目标 agent 里带 prompt 的：{(withPrompt.Length == 0 ? "(无)" : string.Join("、", withPrompt))}；" +
                           $"其中内容是本工具系统层文本的：{(ours.Length == 0 ? "(无)" : string.Join("、", ours))}");
                    r.Check(realKeys.Length > 0, "真实 opencode.json 顶层是合法 JSON 对象（只读解析成功）");
                    if (foreignPrompt)
                        r.Step("    真实配置里 title / review 也带 prompt：本工具不碰这些 agent（写入范围只有四个目标 agent）。");
                }
            }
            catch (Exception ex)
            {
                r.Step($"    真实 opencode.json 解析失败（只读核对跳过细节）：{ex.GetType().Name}");
            }
            r.Check(ShaShared(realCfgFile) == realCfgBefore,
                $"真实 opencode.json 只读核对前后 sha256 不变（{Short(realCfgBefore)}）");
            var realBak = realCfgFile + ".breach.bak";
            r.Step($"    真实 opencode.json 的备份：{(File.Exists(realBak) ? realBak + "（存在，本用例不建也不删）" : "不存在")}");
        }

        var sessions = OpencodeDb.ListSessions(realDb, default);
        const string realSessionId = "ses_eb8ef608fffeDDKiwgOntuAsAr";
        var realClientRunning = PathProbe.ProcessIds(OpencodeLocator.ProcessName).Count > 0;
        if (sessions.Count == 0)
        {
            // 本机客户端正在跑时，会话行可能还在活动 WAL 里而只读连接看不到（读失败会抛 IoError，这里没有抛）。
            // 按本用例「本机存在就做，不存在就跳过」的口径：只记录数字，不据此断言「没有会话」，更不据此说注入没生效。
            r.Step($"    真实库里现在列出 0 个会话（客户端在跑={realClientRunning}）：不断言 >0，也不据此说「注入没生效」；" +
                   "盘上文件与 instruction_blob 的只读核对照做。");
            var blobOnly = OpencodeDb.InstructionBlobText(realDb, default);
            r.Step($"    真实 instruction_blob 仍能读出 {blobOnly.Blobs} 条 / {blobOnly.Text.Length} 字符" +
                   "（只读连接是通的，空的是会话表这一侧的视图）。");
            r.Step($"    其中：配置指令层标记命中 {OpencodeMarkers.CountHits(blobOnly.Text, OpencodeMarkers.InstructionsMarkers)} 次，" +
                   $"系统层标记命中 {OpencodeMarkers.CountHits(blobOnly.Text, OpencodeMarkers.SystemLayerMarkers)} 次，" +
                   $"载荷层标记命中 {OpencodeMarkers.CountHits(blobOnly.Text, OpencodeMarkers.PayloadMarkers)} 次（只读参考值）");
        }
        else
        {
            r.Check(sessions.Count > 0, $"真实库里列出 {sessions.Count} 个会话");
        }

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
        public string SystemTextPath => Path.Combine(Target.Ctx.DataDir, "policy", "opencode.system.md");
        /// <summary>系统提示词层的注入文本（从沙箱 policy 目录读，和适配器写入 opencode.json 的是同一份）。</summary>
        public string SystemText => Target.Ctx.Policy.Read(
            OpencodeTarget.PolicyDefs.First(d => d.Name == OpencodeTarget.SystemPolicyName)) ?? "";
        public string PayloadText => Target.Ctx.Policy.Read(
            OpencodeTarget.PolicyDefs.First(d => d.Name == "payload")) ?? "";
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
        /// <summary>系统提示词层的 blob（只有用例 14 会加；为 null 表示这个库里没有它）。</summary>
        public string? SystemHash { get; init; }

        public void DeleteBlob(string hash)
        {
            using var cmd = Writer.CreateCommand();
            cmd.CommandText = "DELETE FROM instruction_blob WHERE hash = @h";
            cmd.Parameters.AddWithValue("@h", hash);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// 清空会话三张表（session_v2 / session_message / instruction_state），**留着 instruction_blob**：
        /// 复现「客户端清了会话表，而常驻指令载荷还在库里」这个真实状态——
        /// 这时「命中 0」不是结论，「无法判定」才是（见用例 15）。
        /// </summary>
        public void DeleteSessions()
        {
            foreach (var table in new[] { "session_v2", "session_message", "instruction_state" })
            {
                using var cmd = Writer.CreateCommand();
                cmd.CommandText = $"DELETE FROM {table}";
                cmd.ExecuteNonQuery();
            }
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

    private static DbFixture MakeDb(string dataHome, string instructionsFilePath, string? systemText = null)
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

        var mapping = new Dictionary<string, string>
        {
            ["file/opencode-instructions.md"] = instructionsHash,
            ["core/environment"] = envHash,
        };

        // 系统提示词层那份载荷（用例 14 才加）：模拟 opencode 把 agent prompt 也装配进常驻指令载荷。
        string? systemHash = null;
        if (systemText is not null)
        {
            systemHash = Sha256Text(systemText);
            Exec(connection, "INSERT INTO instruction_blob (hash, value) VALUES (@h, @v)",
                ("@h", systemHash), ("@v", JsonSerializer.Serialize(systemText)));
            mapping["agent/build/prompt"] = systemHash;
        }

        Exec(connection,
            "INSERT INTO instruction_state (session_id, epoch_start, through_seq, initial_values, current_values) " +
            "VALUES (@sid, 3, 3, @v, @v)",
            ("@sid", SessionId), ("@v", JsonSerializer.Serialize(mapping)));

        return new DbFixture
        {
            DbPath = dbPath,
            Writer = connection,
            Worktree = worktree,
            ExpectedBytes = bytes,
            InstructionsHash = instructionsHash,
            EnvHash = envHash,
            SystemHash = systemHash,
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

    /// <summary>从 Changes 里挑出系统提示词层那一条（两条 PolicyText 条目靠 Notes 标题区分）。</summary>
    private static PlannedChange SysChange(IEnumerable<PlannedChange> changes) =>
        changes.Last(c => c.Notes.Any(n => n.Title is "系统提示词层" or "系统提示词层还原"));

    /// <summary>读出 agent.&lt;名字&gt;.prompt（不存在 / 非字符串一律给 null）。</summary>
    private static string? PromptOf(string? json, string agent)
    {
        try { return (JsonNode.Parse(json ?? "") as JsonObject)?["agent"]?[agent]?["prompt"]?.GetValue<string>(); }
        catch { return null; }
    }

    /// <summary>agent.&lt;名字&gt; 整个对象的规范 JSON（用来比对"别人的字段一个不少"）。</summary>
    private static string? AgentJson(string? json, string agent)
    {
        try { return (JsonNode.Parse(json ?? "") as JsonObject)?["agent"]?[agent]?.ToJsonString(Readable); }
        catch { return null; }
    }

    /// <summary>子节点转 JSON 时用的选项：不转义非 ASCII，断言消息里能直接看中文。</summary>
    private static readonly JsonSerializerOptions Readable = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// agent.&lt;名字&gt; 下**除 prompt 之外**的字段（键 = 规范 JSON 值）。
    /// 用 JsonNode 取值而不是在文本里 Contains：默认的 ToJsonString 会把中文转义成 \uXXXX，文本包含判断会假失败。
    /// </summary>
    private static string OtherFieldsOf(string? json, string agent)
    {
        try
        {
            if ((JsonNode.Parse(json ?? "") as JsonObject)?["agent"]?[agent] is not JsonObject obj) return "(缺)";
            return string.Join("|", obj.Where(p => p.Key != "prompt").Select(p => p.Key + "=" + p.Value?.ToJsonString(Readable)));
        }
        catch { return "(解析失败)"; }
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
