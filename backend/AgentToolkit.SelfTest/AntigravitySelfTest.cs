using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentToolkit.Contracts;
using AgentToolkit.Core;
using AgentToolkit.Core.Support;
using AgentToolkit.Core.Targets;
using AgentToolkit.Core.Targets.Antigravity;
using Microsoft.Data.Sqlite;

namespace AgentToolkit.SelfTest;

/// <summary>
/// Antigravity 适配器的回归自检。硬性纪律：**只在沙箱副本上操作**，
/// 绝不写 ~/.gemini 或 %LOCALAPPDATA%\Programs\antigravity 下的任何真实文件（用例 10 只读核对，
/// 并在前后对两棵真实目录树做 sha256 快照比对）。
///
/// 沙箱里有：假安装根（Antigravity.exe + resources\bin\language_server.exe 占位）、
/// 假配置根（config.json 里带**别人的键与别人的放行条目** + rules\AGENTS.md）、假数据根，
/// 以及一个用 Microsoft.Data.Sqlite **现建的合成会话库**——里面的 step_payload 由本文件里的
/// 小 protobuf 编码器编出来（step_type 14/15/132，带 .19.2 / .20.3 / .20.1 / .20.8 / .20.7.3 / .140.2.1，
/// 外加 wt=0/1/5 的干扰字段），这样"抽取器真的被验过"，而不是只在真机上撞对一次。
/// </summary>
public sealed class AntigravitySelfTest : ITargetSelfTest
{
    public string TargetId => "antigravity";
    public string Name => "Antigravity 适配器回归（沙箱合成 config.json / rules / SQLite+protobuf 会话库）";

    private const string SessionId = "ae6a2d8d-0000-4000-8000-5e1f7e570001";
    private const string ForeignGrant = "command(Get-ChildItem -Recurse move_contract)";
    private const string ForeignGrant2 = "command(Get-Content -Raw C:\\Users\\Public\\other.txt)";

    private const string Reasoning1 =
        "先看一眼目录结构，再决定读哪个文件。FIXTURE-REASONING-FEATURE\n\n" +
        "payload 看起来是 protobuf：字段 20 里套字段 3。这条路只能在 DB 里走通。";
    private const string Answer1 = "第一步：列目录，按长度降序取前五。";
    private const string ToolJson1 =
        "{\"CommandLine\":\"Get-ChildItem -Force\",\"Cwd\":\"D:\\\\沙箱\",\"toolAction\":\"Listing files in the project folder\",\"toolSummary\":\"List project files\"}";
    private const string ToolResult1 =
        "PS D:\\沙箱> Get-ChildItem -Force\n\n    目录: D:\\沙箱\n\n" +
        "-a----  1234  2026/10/17  9:44  AGENTS.md\n-a----  5678  2026/10/17  9:44  SKILL.md\n" +
        "（这段是工具输出，不是模型思考：它只做 Text 摘录，永远不进 ReasoningText。）";

    /// <summary>超过 256 字符的第二段推理（验证 MaxCharsPerText 截断与 256 下限保护）。</summary>
    private const string Reasoning2 =
        "FIXTURE-THINK-2：第二次推理。要把长度撑过两百五十六个字符，才能真的走到截断分支。" +
        "先确认目录存在，再确认权限足够，再确认没有符号链接把大小算重，最后才排序取前五。" +
        "任何一步跳过都会让结论失真，宁可多跑一遍也不要给一个看起来对的结果。" +
        "截断判定用的是字符数而不是字节数，中文一个字算一个字符，所以必须真的写够长度才能触发。" +
        "这一段继续补充与结论无关的说明文字，把长度稳定地推过三百字符，确保 MaxCharsPerText 的路径被覆盖到。" +
        "最后再强调一次：推理原文只在 conversations 的 step_type=15 的 .20.3 里。";

    private const string Answer2NoMarker = "交付：三条命令，逐行可跑。";
    // 这一份带规则层与载荷层的特征句：用来验证"抽出的文本里命中标记 → 已进上下文"。
    private const string Answer2WithMarker =
        "交付：三条命令，逐行可跑。\n\n（这一段刻意引用规则层的特征句：输出用简体中文；以及载荷层的：工作约定。）";

    private const string UserTurn1 = "列出当前目录下最大的 5 个文件，一行一个。";
    private const string UserTurn2 = "再来一次，把结果一行一个输出。";

    /// <summary>出厂配置：带别人的键（含一个陌生顶层键）与两条别人的放行条目——apply 之后必须一个不少。</summary>
    private const string FactoryConfig =
        """
        {
          "userSettings": {
            "artifactReviewMode": "ARTIFACT_REVIEW_MODE_TURBO",
            "autoExecutionPolicy": "CASCADE_COMMANDS_AUTO_EXECUTION_OFF",
            "browserJsExecutionPolicy": "BROWSER_JS_EXECUTION_POLICY_TURBO",
            "enableTerminalSandbox": true,
            "globalPermissionGrants": {
              "allow": [
                "command(Get-ChildItem -Recurse move_contract)",
                "command(Get-Content -Raw C:\\Users\\Public\\other.txt)"
              ]
            },
            "nonWorkspaceFileAccessPolicy": "AGENT_SETTING_POLICY_ASK",
            "queuedMessageDeliveryStrategy": "MESSAGE_DELIVERY_STRATEGY_NEXT_INVOCATION",
            "remoteControlHostname": "sandbox-host-01",
            "themeMode": "THEME_MODE_INHERIT"
          },
          "someOtherTopLevelKey": {
            "keep": true
          }
        }
        """;

    private const string UserAgents = "# 我自己的规则\n\n这是用户自己的 rules/AGENTS.md，工具不得直接覆盖（必须先备份）。\n";

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

        // 真实路径在任何 env 覆盖之前抓下来，供用例 10 的只读核对与"没碰真实目标"的哈希比对用。
        var realHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var realGemini = Path.Combine(realHome, ".gemini");
        var realConversations = Path.Combine(realGemini, "antigravity", "conversations");
        var realConfig = Path.Combine(realGemini, "config");
        var realInstall = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "antigravity");

        r.Step($"真实配置根（只读比对）：{realConfig}");
        r.Step($"真实会话目录（只读比对）：{realConversations}");
        r.Step($"真实安装根（只读比对）：{realInstall}");

        // 快照口径：~/.gemini 下的 conversations\*.db 是 SQLite 库，本自检要用只读连接读它们，
        // 而只读连接会创建 / 更新 *.db-shm（wal-index 共享内存），所以比对排除 -shm；
        // -db 与 -wal 是数据本体，仍然逐个 sha256 比对（下面的 DbAndWalDiff 单独再比一次，一条都不放宽）。
        var geminiBefore = SnapshotTree(realGemini, skipWalIndexShm: true);
        var installBefore = SnapshotTree(realInstall);
        var shmBefore = WalIndexShmCount(realGemini);
        // 真实客户端的只读快照（整个 ~/.gemini + 安装根）：自检结束时变化项必须为 0。
        var realSnapshot = DualScheme.CaptureReal("antigravity");
        r.Step($"    快照：~/.gemini {geminiBefore.Count} 个文件（另有 {shmBefore} 个 *.db-shm 不参与比对）、安装根 {installBefore.Count} 个文件");

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
            Case11ProtoNegative(ctx, r, SetEnv);
            Case12LocateDiffExportProbes(ctx, r, SetEnv);
            Case16DualScheme(ctx, r, SetEnv);
        }
        finally
        {
            foreach (var kv in saved) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        }

        Case10RealDataReadOnly(ctx, r, realConversations);
        DualScheme.CheckRealReadOnly(r, "antigravity", realSnapshot);

        // 收尾：真实的 ~/.gemini 与安装目录必须一个字节都没变（自检全程只读它们）。
        var geminiAfter = SnapshotTree(realGemini, skipWalIndexShm: true);
        var installAfter = SnapshotTree(realInstall);
        var dbWal = DbAndWalDiff(geminiBefore, geminiAfter);
        r.Check(SameSnapshot(geminiBefore, geminiAfter),
            $"自检全程没有改动真实 ~/.gemini（{geminiBefore.Count} 个文件，sha256 全部不变；" +
            $"快照排除了 {shmBefore} 个 *.db-shm —— 那是 SQLite 的 wal-index 共享内存文件，任何读者（包括本自检的只读连接）" +
            "都可能创建 / 更新它，把它算进来等于拿自检自己的足迹当成改动；-db 与 -wal 不在此列，见下一条）");
        r.Check(dbWal.Changed == 0,
            $"真实 ~/.gemini 下 {dbWal.Total} 个 *.db / *.db-wal 逐字节不变（未被排除、未放宽）" +
            (dbWal.Changed == 0 ? "" : $"：变了 {dbWal.Changed} 个（{dbWal.Detail}）"));
        r.Check(SameSnapshot(installBefore, installAfter),
            $"自检全程没有改动真实安装目录（{installBefore.Count} 个文件，sha256 全部不变）");

        return r;
    }

    // ================================================================ 1 出厂状态

    private static void Case01FactoryState(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【1】出厂状态：config.json 里没有我们的项、rules\\AGENTS.md 不存在 → 三层分别 TargetAbsent / NotApplied");

        var a = Setup(ctx, setEnv, "factory", config: FactoryConfig, agents: null);
        var st = a.Target.GetStatus(default);
        r.Check(st.Layers.Count == 3, $"状态里装配出 {st.Layers.Count} 层（期望 3：规则 / 技能 / 权限）");
        var l1 = st.Layers[0];
        var l2 = st.Layers[1];
        var l3 = st.Layers[2];
        r.Check(l1.Kind == LayerKind.PolicyText, $"层 1 的 Kind = {l1.Kind}（期望 PolicyText：规则层）");
        r.Check(l2.Kind == LayerKind.ContextPayload, $"层 2 的 Kind = {l2.Kind}（期望 ContextPayload：技能层）");
        r.Check(l3.Kind == LayerKind.PolicyText, $"层 3 的 Kind = {l3.Kind}（期望 PolicyText：权限层，契约里没有配置项这一档）");
        r.Check(l1.TargetPath == a.AgentsFile, $"层 1 指向 {a.AgentsFile}");
        r.Check(l2.TargetPath == a.SkillFile, $"层 2 指向 {a.SkillFile}");
        r.Check(l3.TargetPath == a.ConfigFile, $"层 3 指向 {a.ConfigFile}");
        r.Check(l1.State == LayerState.TargetAbsent, $"rules\\AGENTS.md 不存在 → 层 1 {l1.State}（期望 TargetAbsent）");
        r.Check(l2.State == LayerState.TargetAbsent, $"skills 未投放 → 层 2 {l2.State}（期望 TargetAbsent）");
        r.Check(l3.State == LayerState.NotApplied, $"config.json 在、但没有我们的项 → 层 3 {l3.State}（期望 NotApplied）");
        r.Check(l3.ParseOk, "层 3 的 ParseOk=true（文件是合法 JSON）");
        r.Check(st.LastMarkerReport is null, "没有会话 → LastMarkerReport 为 null（不是伪造的空报告）");
        r.Check(!st.ClientRunning, $"本机 Antigravity 没在跑 → ClientRunning={st.ClientRunning}");
        r.Check(st.Target.Found && st.Target.TargetId == "antigravity", "EngineStatus.Target 由 ToLocateResult 装配且 Found");
        r.Check(st.Target.ProfileDir == a.DataHome, $"ProfileDir = 数据根（{st.Target.ProfileDir}）");
        r.Check(st.Target.AsarPath == a.Bundle, $"Bundle（AsarPath）= language_server.exe（{st.Target.AsarPath}）");
        r.Check(st.Target.PatchFilePath == a.Bundle,
            "契约兜底：引擎按 loc.PatchFile ?? loc.Bundle 填 PatchFilePath，所以这里显示的是语言服务器——" +
            $"TargetLocation.PatchFile 本身是 null（没有补丁层，见用例 12 的断言）");
        r.Check(l3.Notes.Any(n => n.Title == "权限项"), "层 3 的 Notes 逐项列出权限项的当前值");
        r.Check(l3.Notes.Any(n => n.Title == "枚举来源"), "层 3 的 Notes 说明允许值的来源与出厂档未实测");
        r.Check(l1.Notes.Any(n => n.Title == "单文件上限"), "层 1 的 Notes 说明 24 KB 单文件上限");
        r.Check(l1.Notes.Any(n => n.Title == "加载顺序"), "层 1 的 Notes 说明向上逐级加载的规则");
        r.Check(l2.Notes.Any(n => n.Title == "技能格式"), "层 2 的 Notes 说明 SKILL.md 的 frontmatter 要求");

        // 1b：config.json 不存在 → 权限层 TargetAbsent
        var b = Setup(ctx, setEnv, "factory-no-config", config: null, agents: null);
        var st2 = b.Target.GetStatus(default);
        r.Check(st2.Layers[2].State == LayerState.TargetAbsent,
            $"config.json 不存在 → 层 3 {st2.Layers[2].State}（期望 TargetAbsent）");
        r.Check(st2.Layers[2].Backup.Health == BackupHealth.Absent, "出厂态没有备份（Absent）");
        ctx.Log($"factory: {l1.State}/{l2.State}/{l3.State} → {st2.Layers[2].State}");
    }

    // ================================================================ 2 预览只读

    private static void Case02PreviewReadOnly(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【2】预览只读：PreviewApply 前后三个文件的 sha256 都不变，且不建备份");

        var e = Setup(ctx, setEnv, "preview", config: FactoryConfig, agents: UserAgents);
        var cfgBefore = FileText.Sha256File(e.ConfigFile);
        var agentsBefore = FileText.Sha256File(e.AgentsFile);
        var cfgStamp = File.GetLastWriteTimeUtc(e.ConfigFile);

        var preview = e.Target.PreviewApply(new ApplyOptions { DryRun = true }, default);

        r.Check(FileText.Sha256File(e.ConfigFile) == cfgBefore, "PreviewApply 前后 config.json 的 sha256 不变");
        r.Check(FileText.Sha256File(e.AgentsFile) == agentsBefore, "PreviewApply 前后 rules\\AGENTS.md 的 sha256 不变");
        r.Check(File.GetLastWriteTimeUtc(e.ConfigFile) == cfgStamp, "PreviewApply 没有触碰 config.json 的修改时间");
        r.Check(!File.Exists(e.ConfigBackup), "预览没有建备份（config.json.breach.bak 不存在）");
        r.Check(!File.Exists(e.AgentsBackup), "预览没有建备份（AGENTS.md.breach.bak 不存在）");
        r.Check(!File.Exists(e.ExtraFile), "预览没有创建 rules\\JailbreakTool.md");
        r.Check(preview.CanApply, $"CanApply = {preview.CanApply}（期望 true）");
        r.Check(preview.Changes.Count == 3, $"Changes 三条（两份规则 + config.json），实际 {preview.Changes.Count}");
        r.Check(preview.Changes.All(c => c.WillWrite), "三条都标记为将写入");
        r.Check(preview.Changes.All(c => c.Layer == LayerKind.PolicyText), "三条的 Layer 都是 PolicyText（契约里没有配置项这一档）");
        r.Check(preview.Changes.First(c => c.Path == e.ConfigFile).DiffText?.Contains("+ ") == true, "config.json 给了逐行差异");
        r.Check(preview.Changes.First(c => c.Path == e.ConfigFile).Notes.Any(n => n.Title == "权限项原值 → 注入值"),
            "config.json 的改动里如实列出每个权限项的原值与注入值");
        r.Check(preview.PreviewId.Length == 16, $"PreviewId 取输入内容 sha256 前 16 位：{preview.PreviewId}");
        r.Check(preview.TotalAfterBytes > preview.TotalBeforeBytes,
            $"体积 {preview.TotalBeforeBytes} → {preview.TotalAfterBytes} B");
        r.Check(preview.Notes.Any(n => n.Title == "注入文本来源" && n.Detail.Contains("opencode", StringComparison.Ordinal)),
            "预览的 Notes 说明复用上一目标注入文本（本轮唯一允许的将就）");

        // DryRun 的 apply 同样一个字节都不写
        var dry = e.Target.Apply(new ApplyOptions { DryRun = true }, null, default);
        r.Check(!dry.Applied, "DryRun=true 的 apply：Applied=false");
        r.Check(!dry.RestartRequired, "DryRun=true 的 apply：RestartRequired=false");
        r.Check(FileText.Sha256File(e.ConfigFile) == cfgBefore && FileText.Sha256File(e.AgentsFile) == agentsBefore,
            "DryRun=true 的 apply 没有写任何文件（两个 sha256 都不变）");
        r.Check(!File.Exists(e.ConfigBackup) && !File.Exists(e.AgentsBackup) && !File.Exists(e.ExtraFile),
            "DryRun=true 的 apply 不建备份、不创建规则文件");
        ctx.Log($"preview: canApply={preview.CanApply} id={preview.PreviewId}");
    }

    // ================================================================ 3 apply

    private static void Case03Apply(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【3】apply：rules\\AGENTS.md == 注入文本、config.json 三项被改且原有键一个不少、allow 里加我们的条目、写后能真实解析");

        var e = Setup(ctx, setEnv, "apply", config: FactoryConfig, agents: UserAgents);
        var factoryKeys = Keys(FactoryConfig);
        var factorySettingsKeys = SettingsKeys(FactoryConfig);
        var factoryAllow = AntigravityPermissions.AllowEntriesOf(FactoryConfig);

        var res = e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(res.Applied, "ApplyResult.Applied=true");
        r.Check(res.RestartRequired, "RestartRequired=true（规则在会话启动时装配）");
        r.Check(res.VerificationHint.Contains("新会话", StringComparison.Ordinal) &&
                res.VerificationHint.Contains("不必重启进程", StringComparison.Ordinal),
            "VerificationHint 写明「开新会话才生效，不必重启进程」");
        r.Check(res.Changes.Count == 3, $"Changes 三条，实际 {res.Changes.Count}");

        // ---- 规则层两份 Markdown ----
        r.Check(FileText.Read(e.AgentsFile) == e.RulesText, "rules\\AGENTS.md 内容 == 规则注入文本（逐字节）");
        r.Check(FileText.Read(e.ExtraFile) == e.PayloadText, "rules\\JailbreakTool.md 内容 == 载荷注入文本（逐字节）");
        r.Check(File.Exists(e.AgentsBackup), "用户自己的 AGENTS.md 已备份为 .breach.bak");
        r.Check(FileText.Read(e.AgentsBackup) == UserAgents, "用户文件备份逐字节一致");
        r.Check(!File.Exists(e.ExtraBackup), "JailbreakTool.md 出厂不存在 → 没有多余备份");
        r.Check(File.ReadAllBytes(e.AgentsFile)[0] != 0xEF, "写入的规则文本没有 BOM");

        // ---- config.json ----
        var text = FileText.Read(e.ConfigFile);
        JsonNode? root = null;
        var parseOk = true;
        try { root = JsonNode.Parse(text); }
        catch (Exception ex) { parseOk = false; r.Fail($"写入后 config.json 解析失败：{ex.Message}"); }
        r.Check(parseOk, "写入后 config.json 能被 JsonNode 真实解析");
        r.Check(root is JsonObject, "写入后顶层是 JSON 对象");
        r.Check(text.Contains("\n  \"userSettings\"", StringComparison.Ordinal), "序列化用 2 空格缩进");

        var settings = (root as JsonObject)?["userSettings"] as JsonObject;
        r.Check(settings is not null, "写入后 userSettings 仍是对象");
        foreach (var item in AntigravityPermissions.Items)
            r.Check(settings is not null && AntigravityPermissions.JsonEquals(settings[item.Key], JsonNode.Parse(item.InjectedJson)),
                $"{item.Key} 被改成注入态 {item.Injected}");

        var nowKeys = Keys(text);
        var missing = factoryKeys.Where(k => !nowKeys.Contains(k)).ToArray();
        r.Check(missing.Length == 0,
            missing.Length == 0
                ? $"原有顶层键一个不少（{string.Join(", ", factoryKeys)}）"
                : $"原有顶层键丢失：{string.Join(", ", missing)}");
        var nowSettingsKeys = SettingsKeys(text);
        var missingSettings = factorySettingsKeys.Where(k => !nowSettingsKeys.Contains(k)).ToArray();
        r.Check(missingSettings.Length == 0,
            missingSettings.Length == 0
                ? $"原有 userSettings 键一个不少（{string.Join(", ", factorySettingsKeys)}）"
                : $"原有 userSettings 键丢失：{string.Join(", ", missingSettings)}");
        r.Check(nowKeys.Count == factoryKeys.Count, $"顶层键数量不变（{factoryKeys.Count} → {nowKeys.Count}）");

        var allow = AntigravityPermissions.AllowEntriesOf(text);
        foreach (var g in AntigravityPermissions.GrantEntries)
            r.Check(allow.Contains(g, StringComparer.Ordinal), $"allow 里有本工具的条目：{g}");
        foreach (var g in factoryAllow)
            r.Check(allow.Contains(g, StringComparer.Ordinal), $"别人的放行条目原样保留：{g}");
        r.Check(allow.Count == factoryAllow.Count + AntigravityPermissions.GrantEntries.Count,
            $"allow 条目数 = 原有 {factoryAllow.Count} + 本工具 {AntigravityPermissions.GrantEntries.Count}（实际 {allow.Count}）");

        // ---- 备份 ----
        r.Check(FileText.Read(e.ConfigBackup) == FactoryConfig, "config.json 的首次备份与出厂配置逐字节一致");
        r.Check(!AntigravityMarkers.BackupMarkers.Any(m => FileText.Count(FileText.Read(e.ConfigBackup), m) > 0),
            "config.json 备份里没有本工具注入标记（干净，可安全还原）");

        // ---- 状态 ----
        var st = e.Target.GetStatus(default);
        r.Check(st.Layers[0].State == LayerState.Applied, $"apply 后层 1 = {st.Layers[0].State}（期望 Applied）");
        r.Check(st.Layers[2].State == LayerState.Applied, $"apply 后层 3 = {st.Layers[2].State}（期望 Applied）");
        r.Check(st.Layers[2].Backup.Health == BackupHealth.Clean, $"层 3 备份报告干净（{st.Layers[2].Backup.Path}）");
        r.Check(st.Layers[0].SizeBytes == FileText.Bytes(e.RulesText), "层 1 的 SizeBytes 与规则文本字节数一致");
        ctx.Log($"apply ok: {e.ConfigFile}");
    }

    // ================================================================ 4 幂等

    private static void Case04Idempotent(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【4】幂等：第二次 apply 三个文件都无字节变化、Applied=false、WillWrite 全为 false");

        var e = Setup(ctx, setEnv, "idempotent", config: FactoryConfig, agents: UserAgents);
        e.Target.Apply(new ApplyOptions(), null, default);

        var cfgSha = FileText.Sha256File(e.ConfigFile);
        var agentsSha = FileText.Sha256File(e.AgentsFile);
        var extraSha = FileText.Sha256File(e.ExtraFile);
        var cfgStamp = File.GetLastWriteTimeUtc(e.ConfigFile);
        var agentsStamp = File.GetLastWriteTimeUtc(e.AgentsFile);

        var second = e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(FileText.Sha256File(e.ConfigFile) == cfgSha, "第二次 apply 后 config.json 的 sha256 不变");
        r.Check(FileText.Sha256File(e.AgentsFile) == agentsSha, "第二次 apply 后 AGENTS.md 的 sha256 不变");
        r.Check(FileText.Sha256File(e.ExtraFile) == extraSha, "第二次 apply 后 JailbreakTool.md 的 sha256 不变");
        r.Check(File.GetLastWriteTimeUtc(e.ConfigFile) == cfgStamp, "没有重写 config.json（修改时间不变）");
        r.Check(File.GetLastWriteTimeUtc(e.AgentsFile) == agentsStamp, "没有重写 AGENTS.md（修改时间不变）");
        r.Check(!second.Applied, $"第二次 apply 没有可写内容 → Applied={second.Applied}（不虚报）");
        r.Check(second.Changes.All(c => !c.WillWrite), "第二次 apply 的 Changes 里 WillWrite 全为 false");
        r.Check(second.Notes.Any(n => n.Title == "幂等"), "Notes 里有「幂等」说明");
        r.Check(second.Notes.Count(n => n.Title == "幂等") >= 3, "三层都给出了幂等说明");

        var preview = e.Target.PreviewApply(new ApplyOptions(), default);
        r.Check(!preview.CanApply, "三层都已到位 → CanApply=false（没有要写的东西）");
        r.Check(preview.Changes.All(c => !c.WillWrite), "预览的所有行 WillWrite=false");
        r.Check(preview.TotalBeforeBytes == 0 && preview.TotalAfterBytes == 0,
            $"无改动时体积统计不虚报（{preview.TotalBeforeBytes} → {preview.TotalAfterBytes}）");
        ctx.Log("idempotent ok");
    }

    // ================================================================ 5 revert

    private static void Case05Revert(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【5】revert：有备份 → 逐字节还原（config.json 的 sha256 与出厂相等、MatchesOriginal=true）；"
             + "无备份 → 逆向撤销（三项回出厂档、摘掉我们的 allow 条目、别人的条目与键都在、MatchesOriginal=null）");

        // ---- 5a：出厂态有 config.json + 用户自己的 AGENTS.md ----
        var e = Setup(ctx, setEnv, "revert", config: FactoryConfig, agents: UserAgents);
        e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(File.Exists(e.ConfigBackup) && File.Exists(e.AgentsBackup), "5a 前置：apply 时拍了两份首次备份");

        var rev = e.Target.Revert(new RevertOptions(), null, default);
        var backCfg = FileText.ReadOrNull(e.ConfigFile);
        r.Check(rev.Reverted, "5a RevertResult.Reverted=true");
        r.Check(backCfg == FactoryConfig, "5a config.json 与出厂内容逐字节一致（走备份还原，不是重新序列化）");
        r.Check(FileText.Sha256File(e.ConfigFile) == FileText.Sha256Text(FactoryConfig),
            "5a config.json 的 sha256 == 出厂文本的 sha256");
        r.Check(rev.MatchesOriginal == true, $"5a 与首次备份 sha256 比对一致（MatchesOriginal={rev.MatchesOriginal}）");
        r.Check(rev.OriginalSha256 is not null && rev.CurrentSha256 == rev.OriginalSha256,
            $"5a OriginalSha256 == CurrentSha256（{Short(rev.CurrentSha256)}）");
        r.Check(FileText.Read(e.AgentsFile) == UserAgents, "5a rules\\AGENTS.md 逐字节回到用户自己的原文");
        r.Check(!File.Exists(e.ExtraFile), "5a rules\\JailbreakTool.md 已删除（出厂即不存在）");
        r.Check(rev.ReadBackClean, "5a 回读确认无残留（ReadBackClean=true）");
        r.Check(rev.Changes.Any(c => c.WillWrite), "5a Changes 记录了还原动作");
        r.Check(rev.Notes.Any(n => n.Title.Contains("生效时机", StringComparison.Ordinal)), "5a 提示生效时机（新会话才回到出厂状态）");
        r.Check(File.Exists(e.ConfigBackup) && File.Exists(e.AgentsBackup), "5a 默认保留备份（RemoveBackups=false）");

        // ---- 5b：没有备份 → 逆向撤销 ----
        var f = Setup(ctx, setEnv, "revert-no-backup", config: FactoryConfig, agents: UserAgents);
        f.Target.Apply(new ApplyOptions(), null, default);
        File.Delete(f.ConfigBackup);
        File.Delete(f.ConfigBackup + ".meta.json");
        File.Delete(f.AgentsBackup);
        File.Delete(f.AgentsBackup + ".meta.json");

        var rev2 = f.Target.Revert(new RevertOptions(), null, default);
        var afterText = FileText.ReadOrNull(f.ConfigFile);
        r.Check(rev2.Reverted, "5b 没有备份时走逆向撤销路径");
        r.Check(afterText is not null, "5b config.json 还在（有别人的键，不能删）");
        var afterObj = afterText is null ? null : JsonNode.Parse(afterText) as JsonObject;
        var afterSettings = afterObj?["userSettings"] as JsonObject;
        foreach (var item in AntigravityPermissions.Items)
            r.Check(afterSettings is not null &&
                    string.Equals(AntigravityPermissions.RenderPlain(afterSettings[item.Key]), item.Factory, StringComparison.Ordinal),
                $"5b {item.Key} 回到出厂档 {item.Factory}（实际 {(afterSettings is null ? "?" : AntigravityPermissions.RenderPlain(afterSettings[item.Key]))}）");
        var afterAllow = AntigravityPermissions.AllowEntriesOf(afterText);
        r.Check(!afterAllow.Any(g => AntigravityPermissions.GrantEntries.Contains(g, StringComparer.Ordinal)),
            "5b allow 里本工具的条目已摘除");
        r.Check(afterAllow.Contains(ForeignGrant, StringComparer.Ordinal) && afterAllow.Contains(ForeignGrant2, StringComparer.Ordinal),
            $"5b 别人的放行条目原样保留（{afterAllow.Count} 条）");
        r.Check(afterText is not null && afterText.Contains("\"themeMode\": \"THEME_MODE_INHERIT\"", StringComparison.Ordinal) &&
                afterText.Contains("\"remoteControlHostname\": \"sandbox-host-01\"", StringComparison.Ordinal) &&
                afterText.Contains("someOtherTopLevelKey", StringComparison.Ordinal),
            "5b 别人的键一个没丢（含陌生顶层键）");
        r.Check(afterText is not null && afterText.Contains("\"globalPermissionGrants\"", StringComparison.Ordinal) &&
                afterText.Contains("\"allow\"", StringComparison.Ordinal),
            "5b 键没被删（globalPermissionGrants / allow 仍在）");
        r.Check(rev2.MatchesOriginal is null, "5b 没有可信备份 → MatchesOriginal 报 null，不伪造 true");
        r.Check(rev2.ReadBackClean, "5b 逆向撤销后回读干净");
        r.Check(!File.Exists(f.AgentsFile), "5b 本工具部署的 AGENTS.md 被删除（没有备份可还原，回到不存在状态）");
        r.Check(!File.Exists(f.ExtraFile), "5b JailbreakTool.md 也被删除");

        // ---- 5c：config.json 是本工具从零创建的 → 无备份时删除文件 ----
        var b = Setup(ctx, setEnv, "revert-created", config: null, agents: null);
        b.Target.Apply(new ApplyOptions(), null, default);
        r.Check(File.Exists(b.ConfigFile) && File.Exists(b.AgentsFile) && File.Exists(b.ExtraFile),
            "5c 前置：三个文件被本工具从零创建");
        File.Delete(b.ConfigBackup);
        File.Delete(b.ConfigBackup + ".meta.json");
        var rev3 = b.Target.Revert(new RevertOptions(), null, default);
        r.Check(rev3.Reverted, "5c revert 执行");
        r.Check(!File.Exists(b.ConfigFile),
            "5c config.json 已删除（只有 userSettings 三项 + 我们条目的最小骨架，恢复出厂的不存在状态）");
        r.Check(!File.Exists(b.AgentsFile) && !File.Exists(b.ExtraFile), "5c 两份规则文件已删除（出厂即不存在）");
        r.Check(rev3.MatchesOriginal is null, "5c 没有备份时 MatchesOriginal 报 null");
        r.Check(rev3.ReadBackClean, "5c 回读干净");
        ctx.Log("revert ok");
    }

    // ================================================================ 6 污染备份

    private static void Case06ContaminatedBackup(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【6】污染备份：把 .breach.bak 改写成含 marker 的内容 → 状态报 Contaminated，revert 抛 AnchorConflict");

        // 6a：config.json 的备份被污染
        var a = Setup(ctx, setEnv, "contaminated-cfg", config: FactoryConfig, agents: UserAgents);
        a.Target.Apply(new ApplyOptions(), null, default);
        FileText.Write(a.ConfigBackup,
            "{\n  \"userSettings\": {\n    \"themeMode\": \"" + AntigravityMarkers.RulesMarkers[0] + "\"\n  }\n}\n");
        r.Check(AntigravityMarkers.BackupMarkers.Any(m => FileText.Count(FileText.Read(a.ConfigBackup), m) > 0),
            "6a 备份已被改写成含 marker 的内容");

        var st = a.Target.GetStatus(default);
        r.Check(st.Layers[2].Backup.Health == BackupHealth.Contaminated,
            $"6a 状态页报权限层备份 Contaminated（实际 {st.Layers[2].Backup.Health}）");
        r.Check(st.Layers[2].Notes.Any(n => n.Title.Contains("备份污染", StringComparison.Ordinal)), "6a 状态页给出「备份污染」提示");
        var cfgSha = FileText.Sha256File(a.ConfigFile);
        var agentsSha = FileText.Sha256File(a.AgentsFile);
        CheckThrows(r, () => a.Target.Revert(new RevertOptions(), null, default), EngineErrorCode.AnchorConflict,
            "6a 污染备份 → revert 抛 AnchorConflict（拒绝用它还原）");
        r.Check(FileText.Sha256File(a.ConfigFile) == cfgSha, "6a 抛错时没有改动 config.json");
        r.Check(FileText.Sha256File(a.AgentsFile) == agentsSha, "6a 抛错时也没有改动规则文件（预检在任何写操作之前）");

        // 6b：规则层的备份被污染
        var b = Setup(ctx, setEnv, "contaminated-rules", config: FactoryConfig, agents: UserAgents);
        b.Target.Apply(new ApplyOptions(), null, default);
        FileText.Write(b.AgentsBackup, "# 被改坏的备份\n\n" + AntigravityMarkers.PayloadMarkers[0] + "\n");
        var st2 = b.Target.GetStatus(default);
        r.Check(st2.Layers[0].Backup.Health == BackupHealth.Contaminated,
            $"6b 规则层备份报 Contaminated（实际 {st2.Layers[0].Backup.Health}）");
        var cfgSha2 = FileText.Sha256File(b.ConfigFile);
        CheckThrows(r, () => b.Target.Revert(new RevertOptions(), null, default), EngineErrorCode.AnchorConflict,
            "6b 污染备份 → revert 抛 AnchorConflict");
        r.Check(FileText.Sha256File(b.ConfigFile) == cfgSha2, "6b 抛错时没有改动 config.json");
        ctx.Log("contaminated ok");
    }

    // ================================================================ 7 fail-closed

    private static void Case07FailClosed(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【7】fail-closed：config.json 是非法 JSON → apply 抛 SelfCheckFailed 且文件与规则层都未被修改");

        var e = Setup(ctx, setEnv, "failclosed", config: "{ \"userSettings\": { \"themeMode\": \"X\" \n", agents: UserAgents);
        var before = FileText.Sha256File(e.ConfigFile);

        var preview = e.Target.PreviewApply(new ApplyOptions { DryRun = true }, default);
        r.Check(!preview.CanApply, "非法 JSON → CanApply=false（预览如实报不可执行）");
        r.Check(preview.Notes.Any(n => n.Severity == Severity.Error), "预览给出 Error 级说明");

        CheckThrows(r, () => e.Target.Apply(new ApplyOptions(), null, default), EngineErrorCode.SelfCheckFailed,
            "非法 JSON 时 apply 抛 SelfCheckFailed");
        r.Check(FileText.Sha256File(e.ConfigFile) == before, "config.json 未被修改（sha256 不变）");
        r.Check(!File.Exists(e.ConfigBackup), "fail-closed 时连备份都没有建（无备份不写）");
        r.Check(FileText.Read(e.AgentsFile) == UserAgents, "fail-closed 时规则层也没有被写（AGENTS.md 保持原样）");
        r.Check(!File.Exists(e.ExtraFile), "fail-closed 时 JailbreakTool.md 也没有被创建");

        // 类型不对：userSettings 是数组 / allow 是对象 → 同样拒写
        var e2 = Setup(ctx, setEnv, "failclosed-settings", config: "{ \"userSettings\": [1,2,3] }", agents: null);
        var sha2 = FileText.Sha256File(e2.ConfigFile);
        CheckThrows(r, () => e2.Target.Apply(new ApplyOptions(), null, default), EngineErrorCode.SelfCheckFailed,
            "userSettings 是数组 → apply 抛 SelfCheckFailed（硬改会丢用户配置）");
        r.Check(FileText.Sha256File(e2.ConfigFile) == sha2, "userSettings 类型不对时文件同样未被修改");

        var e3 = Setup(ctx, setEnv, "failclosed-allow",
            config: "{ \"userSettings\": { \"globalPermissionGrants\": { \"allow\": { \"a\": 1 } } } }", agents: null);
        var sha3 = FileText.Sha256File(e3.ConfigFile);
        CheckThrows(r, () => e3.Target.Apply(new ApplyOptions(), null, default), EngineErrorCode.SelfCheckFailed,
            "allow 是对象 → apply 抛 SelfCheckFailed（不覆盖用户已有的放行清单）");
        r.Check(FileText.Sha256File(e3.ConfigFile) == sha3, "allow 类型不对时文件同样未被修改");
        ctx.Log("fail-closed ok");
    }

    // ================================================================ 8 日志

    private static void Case08Logs(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【8】日志（SQLite 只读 + protobuf 抽取）：ListSessions / LoadSession（ReasoningText、Text、工具调用计数）");

        var e = Setup(ctx, setEnv, "logs", config: FactoryConfig, agents: UserAgents);
        using var db = MakeDb(e.ConversationsDir, includeMarkers: false);
        r.Check(File.Exists(db.DbPath), $"合成会话库已建：{db.DbPath}");
        r.Check(File.Exists(db.DbPath + "-wal"), "数据留在 -wal 里（只读连接必须能读 WAL，immutable=1 会读成空）");
        r.Check(db.DbPath.StartsWith(e.Sandbox, StringComparison.OrdinalIgnoreCase) &&
                !db.DbPath.StartsWith(Path.Combine(RealUserHome(), ".gemini"), StringComparison.OrdinalIgnoreCase),
            "会话库在沙箱里，不在真实 ~/.gemini 下");

        var sessions = e.Target.ListSessions(default);
        r.Check(sessions.Count == 1, $"ListSessions 返回 {sessions.Count} 个会话（-shm / -wal 不算会话）");
        var info = sessions[0];
        r.Check(info.Id == SessionId, $"Id 取文件名（去 .db）：{info.Id}");
        r.Check(info.Path == db.DbPath, "Path 指向库文件");
        r.Check(info.SizeBytes == new FileInfo(db.DbPath).Length,
            $"SizeBytes = 库文件大小（{info.SizeBytes}）");
        r.Check(info.ModifiedAt == File.GetLastWriteTimeUtc(db.DbPath),
            $"ModifiedAt = 文件 mtime（{info.ModifiedAt:o}）");
        r.Check(string.IsNullOrEmpty(info.Workspace),
            $"Workspace 留空（trajectory_meta 里没有路径列 → 不编）：{info.Workspace}");
        r.Check(info.TurnCount == 2, $"TurnCount = step_type=14 的步数（{info.TurnCount}）");

        var detail = e.Target.LoadSession(SessionId, new SessionLoadOptions(), default);
        r.Check(detail.Info.Id == SessionId, "LoadSession 解析到同一会话");
        r.Check(detail.Turns.Count == db.ExpectedTurns, $"记录 {detail.Turns.Count} 条（期望 {db.ExpectedTurns}）");
        r.Check(detail.Turns[0].Role == "user" && detail.Turns[0].Text == UserTurn1,
            "第一轮是 user，Text 取 .19.2");
        var firstPlanner = detail.Turns.First(t => t.Role == "assistant");
        r.Check(firstPlanner.ReasoningText?.Contains("FIXTURE-REASONING-FEATURE", StringComparison.Ordinal) == true,
            "planner 轮的推理原文进 ReasoningText（.20.3）");
        r.Check(firstPlanner.Text?.Contains("第一步", StringComparison.Ordinal) == true, "planner 轮的答复正文进 Text（.20.1）");
        r.Check(firstPlanner.Text?.Contains("[工具调用 1]", StringComparison.Ordinal) == true,
            "工具调用（.20.7.3）解析成 Text 的附注");
        r.Check(firstPlanner.Text?.Contains("List project files", StringComparison.Ordinal) == true,
            "附注里带出 toolSummary（人话摘要）");
        r.Check(FileText.Count(firstPlanner.Text ?? "", "第一步") == 1,
            "同一份答复在 .20.1/.20.8 里各出现一次 → 拼接时去重，只计一次");

        var toolTurn = detail.Turns.First(t => t.Role == "tool");
        r.Check(toolTurn.ReasoningText is null, "工具结果（step_type=132）**不进** ReasoningText");
        r.Check(toolTurn.Text?.StartsWith("[工具结果]", StringComparison.Ordinal) == true, "工具结果以 [工具结果] 前缀做摘录进 Text");
        r.Check(toolTurn.Text?.Contains("AGENTS.md", StringComparison.Ordinal) == true, "摘录里能看到命令输出的内容");
        r.Check(detail.Turns.All(t => !t.Truncated), "未触发截断（MaxCharsPerText 默认 20000）");
        r.Check(detail.Notes.Any(n => n.Title == "解析口径"), "Notes 说明解析口径（字段路径映射）");
        r.Check(detail.Notes.Any(n => n.Title == "推理原文"), "Notes 报告推理原文的步数 / 字符数 / 最长");
        r.Check(detail.Notes.Any(n => n.Title == "工具调用"), "Notes 报告工具调用次数");
        r.Check(detail.Notes.Any(n => n.Title == "工具结果"), "Notes 说明工具结果只做摘录");
        r.Check(detail.Notes.Any(n => n.Title == "有抽不出文本的步骤"), "Notes 如实报告抽不出文本的步骤数");
        r.Check(detail.SystemPromptText?.Contains("不在日志里", StringComparison.Ordinal) == true,
            "SystemPromptText 如实给出「系统提示词不在日志中」的结论");
        r.Check(detail.SystemPromptText?.Contains("规则层", StringComparison.Ordinal) == true &&
                detail.SystemPromptText.Contains("权限层", StringComparison.Ordinal),
            "SystemPromptText 里拼进了三层的盘上状态");

        // latest 别名与唯一前缀
        r.Check(e.Target.LoadSession("latest", new SessionLoadOptions(), default).Info.Id == SessionId, "latest 解析到同一会话");
        r.Check(e.Target.LoadSession("", new SessionLoadOptions(), default).Info.Id == SessionId, "空字符串按最新会话处理");
        r.Check(e.Target.LoadSession(SessionId[..12], new SessionLoadOptions(), default).Info.Id == SessionId, "唯一前缀可解析");

        // MaxTurns / MaxCharsPerText
        var limited = e.Target.LoadSession(SessionId, new SessionLoadOptions { MaxTurns = 2 }, default);
        r.Check(limited.Turns.Count == 2, $"MaxTurns=2 → 只载入最近 2 条（实际 {limited.Turns.Count}）");
        r.Check(limited.Notes.Any(n => n.Title.Contains("轮次已限制", StringComparison.Ordinal)), "Notes 说明只载入了最近 N 条");

        var truncated = e.Target.LoadSession(SessionId, new SessionLoadOptions { MaxCharsPerText = 12, MaxTurns = 0 }, default);
        r.Check(truncated.Turns.Any(t => t.Truncated), "超过 MaxCharsPerText 的文本被截断并置 Truncated=true");
        r.Check(truncated.Turns.Where(t => t.Truncated).All(t => (t.Text?.Length ?? 0) <= 256),
            "截断带 256 字符下限保护（传 12 这种荒谬值不会把日志切碎）");
        var cut = truncated.Turns.First(t => t.Truncated && t.ReasoningText is not null).ReasoningText ?? "";
        r.Check(cut.Length == 256, $"推理原文截断点正好是 256 字符（实际 {cut.Length}）");
        r.Check(Reasoning2.StartsWith(cut, StringComparison.Ordinal), "截断结果是原文前缀（不是乱码）");
        r.Check(truncated.Notes.Any(n => n.Title.Contains("截断", StringComparison.Ordinal)), "Notes 明示发生了截断");

        // 只读纪律：库不存在 → 空列表（不是抛异常，也不是伪造会话）；库坏了 → 抛 IoError
        var missing = Path.Combine(e.Sandbox, "no-such-dir");
        r.Check(AntigravityDb.ListSessions(missing, default).Count == 0, "目录不存在 → ListSessions 给空列表");
        CheckThrows(r, () => AntigravityDb.Extract(missing, SessionId, default), EngineErrorCode.NotApplied,
            "会话目录里没有库时 Extract 抛 NotApplied（不是静默返回空文本）");
        CheckThrows(r, () => e.Target.CheckMarkers("no-such-session", default), EngineErrorCode.TargetFileMissing,
            "会话不存在 → TargetFileMissing，并列出目录里现有的会话");

        var brokenDir = Path.Combine(e.Sandbox, "broken-conversations");
        Directory.CreateDirectory(brokenDir);
        FileText.Write(Path.Combine(brokenDir, "not-a-db.db"), "这不是数据库，只是一段文本。" + new string('x', 4000));
        CheckThrows(r, () => AntigravityDb.ListSessions(brokenDir, default), EngineErrorCode.IoError,
            "库打不开 → 抛 IoError（不静默跳过）");
        ctx.Log($"logs ok: turns={detail.Turns.Count} size={info.SizeBytes}");
    }

    // ================================================================ 9 命中数 / 生效判据

    private static void Case09Effectiveness(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【9】命中数：抽出的文本含我们标记 → 已进上下文；把标记从 fixture 去掉 → 没生效 + InstalledButNotEffective=true，"
             + "但层状态仍是 Applied（「装上了」与「生效了」分开报）");

        var e = Setup(ctx, setEnv, "effectiveness", config: FactoryConfig, agents: UserAgents);
        using var db = MakeDb(e.ConversationsDir, includeMarkers: true);
        e.Target.Apply(new ApplyOptions(), null, default);

        var report = e.Target.CheckMarkers(SessionId, default);
        r.Check(report.SessionId == SessionId, $"报告里是解析后的真会话号（{report.SessionId}）");
        r.Check(report.SystemPromptLayerPresent, "规则层标记在抽出的文本里命中 → SystemPromptLayerPresent=true");
        r.Check(report.ContextPayloadLayerPresent, "载荷层标记也命中 → ContextPayloadLayerPresent=true");
        r.Check(report.Verdict.Contains("step_payload", StringComparison.Ordinal),
            "Verdict 说明命中来自 conversations 的 step_payload 抽取");
        r.Check(report.Verdict.Contains("transcript_full.jsonl", StringComparison.Ordinal),
            "Verdict 点明 transcript 那条通道没有推理（判读只走 DB）");
        r.Check(report.ExportedSystemPromptPath is not null && File.Exists(report.ExportedSystemPromptPath),
            "CheckMarkers 顺带导出三层状态清单（供 UI 打开）");

        var st = e.Target.GetStatus(default);
        r.Check(st.Layers[0].InstalledButNotEffective == false, "命中 > 0 → 规则层 InstalledButNotEffective=false");
        r.Check(st.Layers[0].EffectivenessEvidence?.Contains("已进模型上下文", StringComparison.Ordinal) == true,
            $"生效证据：{Trim1(st.Layers[0].EffectivenessEvidence, 120)}");
        r.Check(st.RestartRequired == false, "已经进了上下文 → RestartRequired=false");

        // 换一个不含标记的会话库（等于"会话早于注入"）
        var e2 = Setup(ctx, setEnv, "effectiveness-empty", config: FactoryConfig, agents: UserAgents);
        using var db2 = MakeDb(e2.ConversationsDir, includeMarkers: false);
        e2.Target.Apply(new ApplyOptions(), null, default);

        var report2 = e2.Target.CheckMarkers(SessionId, default);
        r.Check(!report2.SystemPromptLayerPresent, "去掉标记 → 规则层命中 0");
        r.Check(!report2.ContextPayloadLayerPresent, "去掉标记 → 载荷层命中 0");
        r.Check(report2.Verdict.Contains("没生效", StringComparison.Ordinal),
            $"Verdict 按 MarkerScanner 语义给出「没生效」结论：{Trim1(report2.Verdict, 140)}");

        var st2 = e2.Target.GetStatus(default);
        r.Check(st2.Layers[0].State == LayerState.Applied, "层 1 仍是 Applied——「装上了」与「生效了」必须分开报");
        r.Check(st2.Layers[2].State == LayerState.Applied, "层 3 也仍是 Applied");
        r.Check(st2.Layers[0].InstalledButNotEffective, "规则在盘上、但最近会话的 step_payload 里没有我们的标记 → true");
        r.Check(st2.Layers[0].EffectivenessEvidence?.Contains("没有这段文本", StringComparison.Ordinal) == true,
            $"生效证据写清了原因：{Trim1(st2.Layers[0].EffectivenessEvidence, 160)}");
        r.Check(st2.Notes.Any(n => n.Title == "装上了但没生效"), "Notes 里有「装上了但没生效」警告");
        r.Check(st2.RestartRequired, "盘上是注入态但最近会话读不到 → RestartRequired=true（开新会话才生效）");
        r.Check(st2.LastMarkerReport?.SessionId == SessionId, $"LastMarkerReport 取最近一次会话（{st2.LastMarkerReport?.SessionId}）");
        ctx.Log($"effectiveness ok: with={report.Hits.Sum(h => h.Count)} → without={report2.Hits.Sum(h => h.Count)}");
    }

    // ================================================================ 11 protobuf 负例

    private static void Case11ProtoNegative(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【11】protobuf 抽取器的负例：随机字节 / 截断的 payload / 病态嵌套 → 不崩、不抛未捕获异常");

        r.Check(AntigravityProto.Extract(null).Count == 0, "null payload → 空结果");
        r.Check(AntigravityProto.Extract(Array.Empty<byte>()).Count == 0, "空 payload → 空结果");

        var rnd = new Random(20261017);
        var threw = false;
        var maxFields = 0;
        for (var i = 0; i < 200; i++)
        {
            var buf = new byte[rnd.Next(1, 4096)];
            rnd.NextBytes(buf);
            try
            {
                var fields = AntigravityProto.Extract(buf);
                maxFields = Math.Max(maxFields, fields.Count);
            }
            catch (Exception ex)
            {
                threw = true;
                r.Fail($"随机字节让抽取器抛了 {ex.GetType().Name}: {ex.Message}");
                break;
            }
        }
        r.Check(!threw, "200 组随机字节：一次都没抛未捕获异常");
        r.Check(maxFields <= 8, $"随机字节最多抽出 {maxFields} 段文本（随机数据本来就不该有像样的文本）");

        var full = PlannerPayload(Reasoning1, Answer1, ToolJson1);
        var fullFields = AntigravityProto.Extract(full);
        var fullReasoning = AntigravityProto.Join(fullFields, "20.3");
        r.Check(fullReasoning == Reasoning1, "完整 payload 的 .20.3 == 原文（抽取器能正确地认出字段路径）");
        r.Check(fullFields.Any(f => f.Path == "20.7.3"), "嵌套三层的 .20.7.3 也被抽到（递归下钻有效）");

        var truncatedOk = true;
        var cutChars = 0;
        for (var cut = 1; cut < full.Length; cut += 7)
        {
            try
            {
                var part = new byte[cut];
                Buffer.BlockCopy(full, 0, part, 0, cut);
                var fields = AntigravityProto.Extract(part);
                var text = AntigravityProto.Join(fields, "20.3");
                cutChars = Math.Max(cutChars, text.Length);
            }
            catch (Exception ex)
            {
                truncatedOk = false;
                r.Fail($"截断到 {cut} 字节时抛了 {ex.GetType().Name}: {ex.Message}");
                break;
            }
        }
        r.Check(truncatedOk, "把 payload 逐段截断（每 7 字节一个测点）：一次都没抛");
        r.Check(cutChars <= Reasoning1.Length, $"截断后最多只抽出 {cutChars} 字符（不超过原文 {Reasoning1.Length}）");

        // 病态嵌套：20 层套娃，深度上限必须挡住（不炸栈）
        byte[]? deep = Encoding.UTF8.GetBytes("深度".PadRight(64, '深'));
        for (var i = 0; i < 20; i++) deep = Pb.Bytes(1, deep!);
        var deepThrew = false;
        try
        {
            var fields = AntigravityProto.Extract(deep);
            r.Step($"    20 层套娃：抽出 {fields.Count} 段文本（深度上限 {AntigravityProto.MaxDepth} 生效）");
        }
        catch (Exception ex)
        {
            deepThrew = true;
            r.Fail($"病态嵌套让抽取器抛了 {ex.GetType().Name}: {ex.Message}");
        }
        r.Check(!deepThrew, "20 层嵌套的 payload 不炸栈、不抛异常");

        // 长度前缀撒谎：声明 1 MB 实际只有几个字节
        var liar = Pb.Cat(Pb.Tag(20, 2), Pb.Varint(1_000_000), Encoding.UTF8.GetBytes("短"));
        var liarThrew = false;
        try { AntigravityProto.Extract(liar); }
        catch (Exception ex) { liarThrew = true; r.Fail($"长度越界的 payload 抛了 {ex.GetType().Name}: {ex.Message}"); }
        r.Check(!liarThrew, "长度前缀越界的 payload 不抛异常（直接停止这一层）");
        ctx.Log("proto negatives ok");
    }

    // ================================================================ 12 定位 / 差异 / 导出 / 探针

    private static void Case12LocateDiffExportProbes(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【12】定位（安装根 / exe / language_server.exe / 配置根 / 数据根 / .gemini）+ fail-closed + 差异 / 导出 / 探针");

        var e = Setup(ctx, setEnv, "locate", config: FactoryConfig, agents: UserAgents);

        var byInstall = e.Target.Locate(new LocateOptions { ExplicitRoot = e.InstallRoot }, true, default);
        r.Check(byInstall.Found && byInstall.Root == e.InstallRoot, $"传安装根 → 命中（{byInstall.Root}）");
        r.Check(byInstall.Exe == e.Exe, $"Exe 指向 Antigravity.exe（{byInstall.Exe}）");
        r.Check(byInstall.Bundle == e.Bundle, $"Bundle 指向 resources\\bin\\language_server.exe（{byInstall.Bundle}）");
        r.Check(byInstall.PatchFile is null, "PatchFile = null（本轮不做补丁层）");
        var engineLevel = MultiTargetEngine.ToLocateResult(e.Target, byInstall);
        r.Check(engineLevel.PatchFilePath == e.Bundle,
            $"契约兜底：MultiTargetEngine 按 loc.PatchFile ?? loc.Bundle 填 PatchFilePath（{engineLevel.PatchFilePath}）" +
            "——PatchFile=null 在契约里显示成语言服务器，这是契约缺口，见报告");
        r.Check(byInstall.ProfileDir == e.DataHome, $"ProfileDir = 数据根（{byInstall.ProfileDir}）");
        r.Check(byInstall.ConfigHome == e.ConfigHome, $"ConfigHome = 配置根（{byInstall.ConfigHome}）");
        r.Check(byInstall.Attempts.Count > 3, $"Attempts 记录试过的路径（{byInstall.Attempts.Count} 条）");
        r.Check(byInstall.Notes.Any(n => n.Title == "本工具约定变量"), "Notes 说明 ANTIGRAVITY_* 是本工具约定，不是官方变量");
        r.Check(byInstall.Notes.Any(n => n.Title == "提示词载体"), "Notes 说明 language_server.exe 是提示词载体且本轮不打补丁");
        r.Check(byInstall.Notes.Any(n => n.Title == "会话目录"), "Notes 说明会话目录在数据根下（不是 <ConfigHome>\\sessions）");

        var byExe = e.Target.Locate(new LocateOptions { ExplicitRoot = e.Exe }, true, default);
        r.Check(byExe.Found && byExe.Exe == e.Exe && byExe.Root == e.InstallRoot, "传 Antigravity.exe → 命中并由它反推安装根");

        var byBundle = e.Target.Locate(new LocateOptions { ExplicitRoot = e.Bundle }, true, default);
        r.Check(byBundle.Found && byBundle.Root == e.InstallRoot, "传 language_server.exe → 命中并由它反推安装根");

        var byConfig = e.Target.Locate(new LocateOptions { ExplicitRoot = e.ConfigHome }, true, default);
        r.Check(byConfig.Found && byConfig.ConfigHome == e.ConfigHome, "传配置根 → 命中（Root 退化为配置根）");
        r.Check(byConfig.Exe == e.Exe,
            "三根是三条独立的轴：--root 只钉住它声明的那一层，安装根继续按 环境变量 → 记忆 → 进程 → 安装目录 解析（不因为给了配置根就丢掉安装根）");

        var byData = e.Target.Locate(new LocateOptions { ExplicitRoot = e.DataHome }, true, default);
        r.Check(byData.Found && byData.ProfileDir == e.DataHome, "传数据根 → 命中（ProfileDir = 数据根）");

        var byGemini = e.Target.Locate(new LocateOptions { ExplicitRoot = Path.Combine(e.Sandbox, "gemini") }, true, default);
        r.Check(byGemini.Found && byGemini.ConfigHome == e.ConfigHome && byGemini.ProfileDir == e.DataHome,
            "传 ~/.gemini → 同时解释出配置根与数据根");

        var bad = e.Target.Locate(new LocateOptions { ExplicitRoot = Path.Combine(e.Sandbox, "no-such-dir") }, true, default);
        r.Check(!bad.Found, "显式路径无效 → fail-closed（不回退、不猜）");
        r.Check(bad.Attempts.Any(a => a.Level == "cli" && !a.Hit), "Attempts 记下 cli 级的无效尝试");
        r.Check(bad.Notes.Any(n => n.Title.Contains("fail-closed", StringComparison.Ordinal)), "Notes 说明 fail-closed 中止原因");

        // 自动链：环境变量命中 → 记记忆文件 → 清掉环境变量后落到记忆级
        var auto = e.Target.Locate(null, true, default);
        r.Check(auto.Found && auto.Root == e.InstallRoot, "无显式参数 → 环境变量 ANTIGRAVITY_ROOT 命中");
        r.Check(auto.ConfigHome == e.ConfigHome, "无显式参数 → 环境变量 ANTIGRAVITY_CONFIG 决定配置根");
        r.Check(auto.Attempts.Any(a => a.Level == "env" && a.Hit), "Attempts 标出 env 级命中");
        r.Check(File.Exists(e.MemoryFile), $"定位成功后写记忆文件（{Path.GetFileName(e.MemoryFile)}）");
        r.Check(PathMemory.Load(e.MemoryFile, "root") == e.InstallRoot, "记忆文件里的 root 是本次命中的安装根");
        r.Check(PathMemory.Load(e.MemoryFile, "configHome") == e.ConfigHome, "记忆文件里的 configHome 一并记下");
        r.Check(PathMemory.Load(e.MemoryFile, "dataHome") == e.DataHome, "记忆文件里的 dataHome 一并记下");

        setEnv("ANTIGRAVITY_ROOT", null);
        setEnv("ANTIGRAVITY_CONFIG", null);
        setEnv("ANTIGRAVITY_HOME", null);
        var byMemory = new AntigravityTarget(e.Target.Ctx).Locate(null, true, default);
        r.Check(byMemory.LocatedBy?.StartsWith("memory", StringComparison.Ordinal) == true,
            $"清掉环境变量后落到记忆级（LocatedBy={byMemory.LocatedBy}）");
        r.Check(byMemory.Root == e.InstallRoot && byMemory.ConfigHome == e.ConfigHome && byMemory.ProfileDir == e.DataHome,
            "记忆级同时还原安装根 / 配置根 / 数据根");
        setEnv("ANTIGRAVITY_ROOT", e.InstallRoot);
        setEnv("ANTIGRAVITY_CONFIG", e.ConfigHome);
        setEnv("ANTIGRAVITY_HOME", e.DataHome);

        // ---- Describe ----
        var info = e.Target.Describe(default);
        r.Check(info.Id == "antigravity", $"Describe().Id = {info.Id}");
        r.Check(info.DisplayName.Contains("Antigravity", StringComparison.Ordinal) && info.Kind == TargetKind.PackagedElectron,
            $"Describe() 带显示名与 Kind={info.Kind}");
        r.Check(info.Installed, "Describe().Installed 反映定位结果");
        r.Check(info.BundlePath == e.Bundle, "Describe().BundlePath = language_server.exe");
        r.Check(info.ConfigHome == e.ConfigHome, $"Describe().ConfigHome = 配置根（{info.ConfigHome}）");
        r.Check(info.Layers.Count == 3, $"Describe().Layers 三条（实际 {info.Layers.Count}）");
        r.Check(e.Target.LayerDescriptions.Count == 3, "LayerDescriptions 三条（规则 / 技能 / 权限）");

        // ---- 差异 ----
        using var db = MakeDb(e.ConversationsDir, includeMarkers: true);
        e.Target.Apply(new ApplyOptions(), null, default);
        var diff = e.Target.GetSystemPromptDiff(SessionId, default);
        r.Check(diff.BeforeSource is not null && diff.AfterSource is not null, "差异报告带来源（首次备份 / 当前值）");
        r.Check(diff.BeforeSource!.Contains("备份", StringComparison.Ordinal), $"BeforeSource 说明基线来自首次备份：{Trim1(diff.BeforeSource, 100)}");
        r.Check(diff.BeforeText.Contains("工具不得直接覆盖", StringComparison.Ordinal), "BeforeText 的规则层是用户自己的 AGENTS.md");
        r.Check(diff.BeforeText.Contains("CASCADE_COMMANDS_AUTO_EXECUTION_OFF", StringComparison.Ordinal),
            "BeforeText 的权限层是注入前的原值（autoExecutionPolicy=OFF）");
        r.Check(!diff.BeforeText.Contains("本机配置说明（由操作者维护）", StringComparison.Ordinal), "BeforeText 不含注入文本（基线是出厂态）");
        r.Check(diff.AfterText.Contains(e.RulesText.Trim(), StringComparison.Ordinal), "AfterText 含当前规则正文");
        r.Check(diff.AfterText.Contains("CASCADE_COMMANDS_AUTO_EXECUTION_EAGER", StringComparison.Ordinal), "AfterText 的权限层是注入后的值");
        r.Check(diff.Lines.Count > 0 && diff.AddedLines + diff.RemovedLines > 0, $"逐行差异非空（+{diff.AddedLines}/-{diff.RemovedLines}）");
        r.Check(diff.MarkerDelta.Any(h => h.Count > 0), "MarkerDelta 记录注入前后标记变化");

        // ---- 导出 ----
        var expSession = e.Target.ExportSession(SessionId, Path.Combine(e.Sandbox, "export.jsonl"), default);
        r.Check(expSession.Exported && File.Exists(expSession.Path), "ExportSession 写出文件");
        var lines = File.ReadAllLines(expSession.Path!);
        r.Check(lines.Length == db.StepCount, $"逐行 JSON 输出 {db.StepCount} 行（每步一行，实际 {lines.Length}）");
        var allJson = true;
        foreach (var line in lines)
        {
            try { using var _ = JsonDocument.Parse(line); }
            catch { allJson = false; }
        }
        r.Check(allJson, "每一行都是合法 JSON");
        r.Check(lines.Any(l => l.Contains("\"step_type\":15", StringComparison.Ordinal)), "导出里能按 step_type 区分步骤");
        r.Check(lines.Any(l => l.Contains("20.3", StringComparison.Ordinal)), "导出里保留字段路径（20.3 这种）");
        r.Check(lines.Any(l => l.Contains("FIXTURE-REASONING-FEATURE", StringComparison.Ordinal)), "导出里含推理原文");

        var expPrompt = e.Target.ExportSystemPrompt(SessionId, Path.Combine(e.Sandbox, "state.txt"), default);
        r.Check(expPrompt.Exported && File.Exists(expPrompt.Path), "ExportSystemPrompt 写出文件");
        var stateText = FileText.Read(expPrompt.Path!);
        r.Check(stateText.Contains("不在日志里", StringComparison.Ordinal), "导出的三层状态清单开头就说明「系统提示词不在日志里」");
        r.Check(stateText.Contains("language_server.exe", StringComparison.Ordinal), "清单里给出提示词载体的路径");
        r.Check(stateText.Contains("权限层", StringComparison.Ordinal) && stateText.Contains("技能层", StringComparison.Ordinal),
            "清单里三层都在（规则 / 技能 / 权限）");

        // ---- 探针 ----
        var probes = e.Target.GetProbes();
        r.Check(probes.Count == 6, $"探针 A–F 共 6 条（实际 {probes.Count}）");
        r.Check(probes.Select(p => p.Id).SequenceEqual(new[] { "A", "B", "C", "D", "E", "F" }), "Id 依次是 A–F");
        r.Check(probes.Count(p => p.IsDecisive) == 3, "D/E/F 三条 IsDecisive=true");
        r.Check(probes.Where(p => p.Id is "D" or "E" or "F").All(p => p.IsDecisive), "D/E/F 都标了 IsDecisive");
        r.Check(probes.All(p => p.HowTo.Length > 10 && p.PassCriteria.Length > 30), "每条都有 HowTo 与三级判据");
        r.Check(probes.First(p => p.Id == "E").PassCriteria.Contains(".20.3", StringComparison.Ordinal),
            "E 的判据指向 step_type=15 的 .20.3 推理原文");
        r.Check(probes.First(p => p.Id == "D").PassCriteria.Contains(".20.3", StringComparison.Ordinal),
            "D 的判据也指向 .20.3");

        // ---- 注入文本 ----
        var policy = e.Target.GetPolicyFiles(default);
        var schemeEntry = policy.First(p => p.Name == "scheme");
        r.Check(schemeEntry is { Text: "own", Editable: false }, $"方案选择器作为只读条目一并返回（Text={schemeEntry.Text}）");
        policy = policy.Where(p => p.Name != "scheme").ToArray();
        r.Check(policy.Count == 2, $"注入文本两条：rules + payload（实际 {policy.Count}）");
        r.Check(policy.All(p => p.Text.Length > 0), "两条注入文本都物化到磁盘且非空");
        r.Check(policy.First(p => p.Name == "rules").Path.EndsWith("antigravity.rules.md", StringComparison.Ordinal),
            "rules 文本落在 antigravity.rules.md");
        r.Check(policy.First(p => p.Name == "payload").Path.EndsWith("antigravity.extra.md", StringComparison.Ordinal),
            "payload 文本落在 antigravity.extra.md");
        r.Check(policy.All(p => p.Notes.Any(n => n.Title == "复用上一目标的文案")), "两条都标注了「复用上一目标文案」");
        r.Check(policy.All(p => p.CapBytes == AntigravityRules.PerFileCapBytes), "两条都带 24 KB 单文件上限");

        var original = FileText.Read(policy.First(p => p.Name == "rules").Path);
        var edited = original.TrimEnd('\r', '\n') + "\n\n（自检追加的一行：保存路径可用。）\n";
        var saved = e.Target.SavePolicyFile("rules", edited, default);
        r.Check(FileText.Read(saved.Path) == edited, "SavePolicyFile 保存后回读逐字节一致");
        CheckThrows(r, () => e.Target.SavePolicyFile("no-such-policy", "x", default),
            EngineErrorCode.TargetFileMissing, "保存不存在的注入文本 → TargetFileMissing");
        CheckThrows(r, () => e.Target.SavePolicyFile("rules", "   \n", default),
            EngineErrorCode.VerifyProblems, "保存空白文本 → VerifyProblems");
        e.Target.SavePolicyFile("rules", original, default);
        r.Check(FileText.Read(saved.Path) == original, "改回原样后与初始文本逐字节一致");

        // 超 24 KB 的规则 → fail-closed 拒写（客户端会按行截断，等于半个注入）
        var huge = Path.Combine(e.Sandbox, "huge.md");
        FileText.Write(huge, new string('规', 9000));
        CheckThrows(r, () => e.Target.Apply(new ApplyOptions { PayloadPath = huge }, null, default),
            EngineErrorCode.SelfCheckFailed, "载荷文本超过 24 KB → apply 抛 SelfCheckFailed（fail-closed）");
        ctx.Log($"locate/diff/export ok: {e.Bundle}");
    }

    // ================================================================ 10 真实数据（只读）

    private static void Case10RealDataReadOnly(SelfTestContext ctx, SelfTestResult r, string realConversations)
    {
        r.Step("");
        r.Step("【10】真实数据只读核对（本机存在就做，不存在就跳过；不写任何真实文件）");

        const string realSessionId = "ae6a2d8d-2c1a-41cf-8976-14cec982d8d3";
        var realDb = Path.Combine(realConversations, realSessionId + ".db");
        if (!File.Exists(realDb))
        {
            r.Step($"    本机没有真实会话库（{realDb}），跳过。");
            return;
        }

        r.Step($"    真实会话库：{AntigravityDb.DescribeDb(realDb)}");
        var dbShaBefore = ShaShared(realDb);
        var walShaBefore = ShaShared(realDb + "-wal");

        var sessions = AntigravityDb.ListSessions(realConversations, default);
        r.Check(sessions.Count >= 1, $"真实 conversations 里列出 {sessions.Count} 个会话库");
        r.Check(sessions.All(s => !s.Id.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) &&
                                  !s.Id.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)),
            "列表里没有 -wal / -shm（它们不是会话）");
        var target = sessions.FirstOrDefault(s => s.Id == realSessionId);
        r.Check(target is not null, $"列表里有样本会话 {realSessionId}");
        if (target is not null)
        {
            r.Check(target.SizeBytes > 0, $"SizeBytes 取库文件大小：{FileText.Human(target.SizeBytes)}");
            r.Check(target.TurnCount == 4, $"TurnCount = step_type=14 的步数：{target.TurnCount}（与 docs/gemini.md 一致）");
        }

        var extract = AntigravityDb.Extract(realConversations, realSessionId, default);
        r.Check(extract.Steps == 128, $"真实会话 {extract.Steps} 步（docs/gemini.md：128 步）");
        r.Check(extract.ReasoningSteps == 22, $"step_type=15 里 {extract.ReasoningSteps} 步有 .20.3 推理原文（docs：22 步）");
        r.Check(extract.ReasoningChars == 11372, $"推理原文合计 {extract.ReasoningChars} 字符（docs：11,372）");
        r.Check(extract.LongestReasoning == 2275, $"最长一段推理 {extract.LongestReasoning} 字符（docs：2,275）");
        r.Check(extract.ToolCalls == 60, $"工具调用 .20.7.3 共 {extract.ToolCalls} 次（docs：60）");
        r.Check(extract.ToolResultSteps == 60, $"工具结果 step_type=132 共 {extract.ToolResultSteps} 步（docs：60）");
        r.Check(extract.Text.Contains("RAND_bytes", StringComparison.Ordinal),
            "真实推理原文里含特征串 RAND_bytes（docs/gemini.md 第六节引用的那段）");
        r.Check(extract.StepsWithText > 100,
            $"真实会话里 {extract.StepsWithText} 步抽出了文本，文本全集 {extract.Text.Length} 字符（推理 {extract.ReasoningChars} 字符）");

        var detail = AntigravityDb.LoadSession(realConversations, realSessionId,
            new SessionLoadOptions { MaxTurns = 0 }, default);
        var reasoningTurn = detail.Turns.FirstOrDefault(t => !string.IsNullOrEmpty(t.ReasoningText));
        r.Check(reasoningTurn is not null, "真实会话里至少一条轮次带推理原文");
        r.Check(reasoningTurn?.ReasoningText?.Length > 0, $"真实推理原文非空（{reasoningTurn?.ReasoningText?.Length ?? 0} 字符）");
        var randTurn = detail.Turns.FirstOrDefault(t => t.ReasoningText?.Contains("RAND_bytes", StringComparison.Ordinal) == true);
        r.Check(randTurn is not null, "22 步推理里那一步的 .20.3 含 RAND_bytes（字段映射在真机上成立）");
        r.Check(randTurn?.ReasoningText?.Length > 0, $"RAND_bytes 那一步的推理非空（{randTurn?.ReasoningText?.Length ?? 0} 字符）");
        r.Check(detail.Turns.Any(t => t.Role == "tool" && t.ReasoningText is null),
            "真实工具结果同样不进 ReasoningText");
        r.Step($"    真实推理开头：{Trim1(reasoningTurn?.ReasoningText, 90)}");

        r.Check(ShaShared(realDb) == dbShaBefore, $"真实会话库的 sha256 读前读后不变（{Short(dbShaBefore)}）");
        r.Check(ShaShared(realDb + "-wal") == walShaBefore, "真实会话库的 -wal 读前读后不变");
        r.Step("    注：-shm 是 SQLite 的 wal-index 共享内存文件，只读连接可能创建 / 更新它（本用例上面两条断言只覆盖 -db 与 -wal）；" +
               "收尾的整棵树快照同样排除 *.db-shm，而 -db / -wal 在那边另有一条逐字节断言。");
        ctx.Log($"real read-only ok: {realSessionId}");
    }

    // ================================================================ 沙箱

    private sealed class Env
    {
        public required string Sandbox { get; init; }
        public required string InstallRoot { get; init; }
        public required string Exe { get; init; }
        public required string Bundle { get; init; }
        public required string ConfigHome { get; init; }
        public required string DataHome { get; init; }
        public required string MemoryFile { get; init; }
        public required AntigravityTarget Target { get; init; }

        public string ConfigFile => Path.Combine(ConfigHome, "config.json");
        public string RulesDir => Path.Combine(ConfigHome, "rules");
        public string AgentsFile => Path.Combine(RulesDir, "AGENTS.md");
        public string ExtraFile => Path.Combine(RulesDir, "JailbreakTool.md");
        public string SkillFile => Path.Combine(ConfigHome, "skills", "breach", "SKILL.md");
        public string ConversationsDir => Path.Combine(DataHome, "conversations");
        public string ConfigBackup => ConfigFile + ".breach.bak";
        public string AgentsBackup => AgentsFile + ".breach.bak";
        public string ExtraBackup => ExtraFile + ".breach.bak";

        public string RulesText => Target.Ctx.Policy.Read(AntigravityTarget.PolicyDefs.First(d => d.Name == "rules")) ?? "";
        public string PayloadText => Target.Ctx.Policy.Read(AntigravityTarget.PolicyDefs.First(d => d.Name == "payload")) ?? "";
    }

    private static Env Setup(SelfTestContext ctx, Action<string, string?> setEnv, string name, string? config, string? agents)
    {
        var dir = ctx.MakeSandbox($"antigravity-{name}");
        var installRoot = Path.Combine(dir, "install");
        var exe = Path.Combine(installRoot, "Antigravity.exe");
        var bundle = Path.Combine(installRoot, "resources", "bin", "language_server.exe");
        var configHome = Path.Combine(dir, "gemini", "config");
        var dataHome = Path.Combine(dir, "gemini", "antigravity");
        var memoryFile = Path.Combine(dir, "breach-antigravity-path.json");
        var dataDir = Path.Combine(dir, "toolkit-data");

        Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
        FileText.Write(exe, "MZ-dummy Antigravity 246 MB 占位（沙箱）");
        FileText.Write(bundle, "MZ-dummy language_server 171 MB 占位（沙箱）");
        Directory.CreateDirectory(configHome);
        Directory.CreateDirectory(Path.Combine(dataHome, "conversations"));
        Directory.CreateDirectory(Path.Combine(dataHome, "builtin", "skills"));
        Directory.CreateDirectory(dataDir);

        if (config is not null) FileText.Write(Path.Combine(configHome, "config.json"), config);
        if (agents is not null)
        {
            Directory.CreateDirectory(Path.Combine(configHome, "rules"));
            FileText.Write(Path.Combine(configHome, "rules", "AGENTS.md"), agents);
        }

        // 三个根 + 记忆文件全部指到沙箱：绝不污染真实 ~/.gemini 或 ~/.breach-antigravity-path.json
        setEnv("ANTIGRAVITY_ROOT", installRoot);
        setEnv("ANTIGRAVITY_CONFIG", configHome);
        setEnv("ANTIGRAVITY_HOME", dataHome);
        setEnv("ANTIGRAVITY_PATH_MEMORY_FILE", memoryFile);

        var policy = new PolicyStore(dataDir);
        policy.Materialize(AntigravityTarget.PolicyDefs);
        var targetCtx = new TargetContext
        {
            Id = "antigravity",
            DataDir = dataDir,
            Policy = policy,
            PolicyDefs = AntigravityTarget.PolicyDefs,
            Ledger = new LedgerStore(Path.Combine(dataDir, "probe-ledger.json")),
        };

        ctx.Log($"沙箱 {Path.GetFileName(dir)}：config={Path.GetFileName(configHome)} data={Path.GetFileName(dataHome)}");
        return new Env
        {
            Sandbox = dir,
            InstallRoot = installRoot,
            Exe = exe,
            Bundle = bundle,
            ConfigHome = configHome,
            DataHome = dataHome,
            MemoryFile = memoryFile,
            Target = new AntigravityTarget(targetCtx),
        };
    }

    // ================================================================ 16 双注入方案

    /// <summary>
    /// own / protocol 双方案：载体槽 = rules（rules\AGENTS.md）；空槽 = payload（rules\JailbreakTool.md）。
    /// A→B 要移除 A 套遗留的第二份规则；B→A 全部恢复；revert 回出厂态。
    /// </summary>
    private static void Case16DualScheme(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【16】双注入方案（own / protocol）：载体槽 rules、空槽 payload、双向切换、幂等、revert、verbatim、fail-closed、标记");
        DualScheme.Verbatim(r, "antigravity");

        var e = Setup(ctx, setEnv, "scheme", config: FactoryConfig, agents: null);

        // 1 缺省 = own（回归）
        DualScheme.SelectorMissingIsOwn(r, e.Target.Ctx, "antigravity");
        var ownApply = e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(ownApply.Applied, "own 方案 apply 写入（回归）");
        var ownRules = e.Target.Ctx.TextFor("rules", InjectionScheme.Own);
        var ownExtra = e.Target.Ctx.TextFor("payload", InjectionScheme.Own);
        r.Check(FileText.Read(e.AgentsFile) == ownRules, "own 方案照旧把 A 套规则写进 rules\\AGENTS.md");
        r.Check(FileText.Read(e.ExtraFile) == ownExtra, "own 方案照旧写第二份规则 rules\\JailbreakTool.md");
        var ownAgain = e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(!ownAgain.Changes.Any(c => c.WillWrite), "own 方案连续两次 apply：WillWrite 全 false");

        // 2 切 protocol
        DualScheme.SwitchToProtocol(r, e.Target.Ctx, "antigravity", "rules");
        r.Check(e.Target.Ctx.EmptySlotNames(InjectionScheme.Protocol).Contains("payload"), "protocol 的空槽含 payload");

        // 3 A → B
        var bApply = e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(bApply.Applied, "A→B apply 报告已写入");
        r.Check(FileText.Read(e.AgentsFile) == DualScheme.EmbeddedProtocol(), "B 套：rules\\AGENTS.md 逐字节等于 gpt-instruct 原文");
        r.Check(FileText.Bytes(FileText.Read(e.AgentsFile)) == DualScheme.ProtocolBytes, $"B 套：AGENTS.md {DualScheme.ProtocolBytes} 字节");
        r.Check(!FileText.Read(e.AgentsFile).Contains("本机配置说明", StringComparison.Ordinal), "B 套：A 套文本没有残留");
        r.Check(!File.Exists(e.ExtraFile), "B 套：A 套遗留的 rules\\JailbreakTool.md 被移除");
        r.Check(bApply.Changes.Any(c => c.Path == e.ExtraFile && c.WillWrite && c.AfterBytes == 0),
            "B 套：第二份规则的移除走 PlannedChange（WillWrite=true / AfterBytes=0）");
        r.Check(!bApply.Changes.Any(c => c.Path == e.ExtraFile && c.WillWrite && c.AfterBytes > 0),
            "B 套：空槽没有产生写入型计划变更");
        var bAgain = e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(!bAgain.Changes.Any(c => c.WillWrite) && !bAgain.Applied, "B 方案连续两次 apply：无字节变化、WillWrite 全 false");

        // 4 B → A
        e.Target.Ctx.Schemes().Write(InjectionScheme.Own);
        var backA = e.Target.Apply(new ApplyOptions(), null, default);
        r.Check(backA.Applied, "B→A apply 报告已写入");
        r.Check(FileText.Read(e.AgentsFile) == ownRules, "切回 A：rules\\AGENTS.md 恢复为 A 套规则");
        r.Check(FileText.Read(e.ExtraFile) == ownExtra, "切回 A：rules\\JailbreakTool.md 恢复");

        // 5 从 B 方案 revert 回出厂态
        e.Target.Ctx.Schemes().Write(InjectionScheme.Protocol);
        e.Target.Apply(new ApplyOptions(), null, default);
        var rev = e.Target.Revert(new RevertOptions(), null, default);
        r.Check(rev.Reverted, "从 B 方案 revert 报告已还原");
        r.Check(!File.Exists(e.AgentsFile), "revert 后 rules\\AGENTS.md 不存在（出厂态）");
        r.Check(!File.Exists(e.ExtraFile), "revert 后 rules\\JailbreakTool.md 不存在（出厂态）");
        r.Check(FileText.Read(e.ConfigFile) == FactoryConfig, "revert 后 config.json 逐字节回到出厂内容");
        r.Check(rev.ReadBackClean && rev.MatchesOriginal == true,
            $"revert 回读干净且与首次备份一致（ReadBackClean={rev.ReadBackClean} / MatchesOriginal={rev.MatchesOriginal}）");

        // 6 非法 scheme 值 → own + Note
        DualScheme.InvalidValueFallsBackToOwn(r, e.Target.Ctx, "antigravity");
        FileText.Write(e.Target.Ctx.Policy.SchemePath, "bogus-value\n");
        var st = e.Target.GetStatus(default);
        r.Check(st.Notes.Any(n => n.Title == "方案文件"), "状态页 Notes 说明方案文件非法（按 own 处理）");

        // 7 fail-closed：B 套载体资源缺失 → 规则文本为空 → 拒绝写入
        e.Target.Ctx.Schemes().Write(InjectionScheme.Protocol);
        var broken = new AntigravityTarget(DualScheme.BreakProtocolCarrier(e.Target.Ctx, "rules"));
        var agentsSha = FileText.Exists(e.AgentsFile) ? FileText.Sha256File(e.AgentsFile) : "(无)";
        var configSha = FileText.Sha256File(e.ConfigFile);
        var threw = false;
        try { broken.Apply(new ApplyOptions(), null, default); }
        catch (AgentEngineException ex)
        {
            threw = true;
            r.Check(ex.Code is EngineErrorCode.SelfCheckFailed or EngineErrorCode.TargetFileMissing or EngineErrorCode.VerifyProblems,
                $"B 套资源缺失 → apply 抛 {ex.Code}");
        }
        r.Check(threw, "B 套资源缺失时 fail-closed（不是静默注空文本）");
        r.Check((FileText.Exists(e.AgentsFile) ? FileText.Sha256File(e.AgentsFile) : "(无)") == agentsSha, "fail-closed 时 rules\\AGENTS.md 一个字节都没写");
        r.Check(FileText.Sha256File(e.ConfigFile) == configSha, "fail-closed 时 config.json 一个字节都没写");

        // 8 标记：B 方案用 B 套规则层标记
        r.Check(AntigravityMarkers.SpecsForScheme(InjectionScheme.Protocol)
                .Any(s => s.Marker == "INJECTED REVISION ACTION" && s.Layer == MarkerLayer.SystemPrompt),
            "CheckMarkers 在 protocol 方案用 B 套规则层标记集");
        r.Check(AntigravityMarkers.SpecsForScheme(InjectionScheme.Own).All(s => s.Marker != "INJECTED REVISION ACTION"),
            "own 方案仍用原标记集（未被替换）");

        // 9 状态 Notes 报当前方案
        var target2 = new AntigravityTarget(e.Target.Ctx);
        target2.Apply(new ApplyOptions(), null, default);
        DualScheme.PropagatesToStatusNotes(r, target2.GetStatus(default), "antigravity");
    }

    // ================================================================ 合成会话库

    private sealed class DbFixture : IDisposable
    {
        public required string DbPath { get; init; }
        public required SqliteConnection Writer { get; init; }
        public required int StepCount { get; init; }
        public required int ExpectedTurns { get; init; }

        public void Dispose()
        {
            // 先清连接池再 Dispose：池里留着连接的话文件句柄不放，沙箱目录删不干净。
            try { SqliteConnection.ClearPool(Writer); } catch { /* 尽力而为 */ }
            try { Writer.Dispose(); } catch { /* 尽力而为 */ }
        }
    }

    /// <summary>
    /// 现建一个合成 Antigravity 会话库：表结构与真机一致（steps / trajectory_meta），
    /// step_payload 由本文件里的 protobuf 编码器编出来：
    ///   14 → .19.2 用户输入；15 → .20.3 推理 + .20.1/.20.8 答复 + .20.7.3 工具调用；
    ///   132 → .140.2.1 工具输出；另有一步两种文本都没有（验证"抽不出文本"的计数）。
    /// WAL 模式且写连接全程不关：数据留在 -wal 里，只读连接必须读 WAL 才看得见。
    /// </summary>
    private static DbFixture MakeDb(string conversationsDir, bool includeMarkers)
    {
        Directory.CreateDirectory(conversationsDir);
        var dbPath = Path.Combine(conversationsDir, SessionId + ".db");

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
            CREATE TABLE steps (
              idx integer PRIMARY KEY, step_type integer NOT NULL, status integer DEFAULT 0,
              has_subtrajectory integer DEFAULT 0, metadata blob, error_details blob, permissions blob,
              task_details blob, render_info blob, step_payload blob, step_format integer DEFAULT 0);
            CREATE TABLE trajectory_meta (
              trajectory_id text, cascade_id text, trajectory_type integer, source integer);
            """);

        Exec(connection,
            "INSERT INTO trajectory_meta (trajectory_id, cascade_id, trajectory_type, source) VALUES (@t, @c, 4, 1)",
            ("@t", "11111111-2222-4333-8444-555555555555"), ("@c", SessionId));

        var rows = new (int Idx, int Type, byte[] Payload)[]
        {
            (1, AntigravitySteps.UserInput, UserPayload(UserTurn1)),
            (2, AntigravitySteps.PlannerResponse, PlannerPayload(Reasoning1, Answer1, ToolJson1)),
            (3, AntigravitySteps.ToolResult, ToolResultPayload(ToolResult1)),
            (4, AntigravitySteps.UserInput, UserPayload(UserTurn2)),
            (5, AntigravitySteps.PlannerResponse,
                PlannerPayload(Reasoning2, includeMarkers ? Answer2WithMarker : Answer2NoMarker, null)),
            (6, AntigravitySteps.PlannerResponse, PlannerPayload(null, null, null)),
        };

        foreach (var (idx, type, payload) in rows)
            Exec(connection,
                "INSERT INTO steps (idx, step_type, status, has_subtrajectory, step_payload, step_format) " +
                "VALUES (@i, @t, 1, 0, @p, 0)",
                ("@i", idx), ("@t", type), ("@p", payload));

        return new DbFixture
        {
            DbPath = dbPath,
            Writer = connection,
            StepCount = rows.Length,
            // 6 步里 5 步有文本（最后一步两种文本都没有）
            ExpectedTurns = 5,
        };
    }

    // ---- protobuf 编码器（只为本自检服务：证明抽取器真的按 wire format 走） ----

    private static class Pb
    {
        public static byte[] Varint(ulong v)
        {
            var list = new List<byte>();
            do
            {
                var b = (byte)(v & 0x7F);
                v >>= 7;
                if (v != 0) b |= 0x80;
                list.Add(b);
            } while (v != 0);
            return list.ToArray();
        }

        public static byte[] Tag(int field, int wire) => Varint((ulong)((field << 3) | wire));

        public static byte[] Str(int field, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return Cat(Tag(field, 2), Varint((ulong)bytes.Length), bytes);
        }

        public static byte[] Bytes(int field, byte[] payload) =>
            Cat(Tag(field, 2), Varint((ulong)payload.Length), payload);

        public static byte[] U64(int field, ulong value) => Cat(Tag(field, 1), BitConverter.GetBytes(value));
        public static byte[] U32(int field, uint value) => Cat(Tag(field, 5), BitConverter.GetBytes(value));
        public static byte[] V(int field, ulong value) => Cat(Tag(field, 0), Varint(value));

        public static byte[] Cat(params byte[][] parts)
        {
            var total = parts.Sum(p => p.Length);
            var buf = new byte[total];
            var off = 0;
            foreach (var p in parts)
            {
                Buffer.BlockCopy(p, 0, buf, off, p.Length);
                off += p.Length;
            }
            return buf;
        }
    }

    /// <summary>
    /// step_type=14：.19.2 = 用户输入正文。
    /// 外层字段里塞一段二进制（不是合法 UTF-8 起头），复刻真机 payload 的样子：
    /// 外层若整段都能当作文本解码，抽取器就会把外层本身当成一段文本，不会再往下钻到 .19.2。
    /// </summary>
    private static byte[] UserPayload(string text) =>
        Pb.Bytes(19, Pb.Cat(Pb.Str(2, text), Pb.Str(3, text), Pb.Bytes(12, BinaryNoise()), Pb.V(7, 1)));

    /// <summary>
    /// step_type=15：.20.3 = 推理、.20.1/.20.8 = 答复（真机里同一份答复两处都放）、
    /// .20.7.3 = 工具调用 JSON、.20.6 = 短文本字段、.20.30/31/32 = wt=0/1/5 的干扰字段。
    /// </summary>
    private static byte[] PlannerPayload(string? reasoning, string? answer, string? toolJson)
    {
        var parts = new List<byte[]>();
        if (reasoning is not null) parts.Add(Pb.Str(3, reasoning));
        if (answer is not null)
        {
            parts.Add(Pb.Str(1, answer));
            parts.Add(Pb.Str(8, answer));
        }
        if (toolJson is not null) parts.Add(Pb.Bytes(7, Pb.Str(3, toolJson)));
        parts.Add(Pb.Str(6, "bot-selftest-0001"));
        parts.Add(Pb.Bytes(12, BinaryNoise()));
        parts.Add(Pb.V(30, 12345));                        // varint：跳过
        parts.Add(Pb.U64(31, 0x1122334455667788));         // 定长 64：跳 8 字节
        parts.Add(Pb.U32(32, 0xAABBCCDD));                 // 定长 32：跳 4 字节
        return Pb.Bytes(20, Pb.Cat(parts.ToArray()));
    }

    /// <summary>一段不是合法 UTF-8 起头的二进制（0x80..0xBF 全是续接字节）。</summary>
    private static byte[] BinaryNoise() =>
        Enumerable.Range(0, 48).Select(i => (byte)(0x80 + i % 0x40)).ToArray();

    /// <summary>step_type=132：.140.2.1 = 工具输出。</summary>
    private static byte[] ToolResultPayload(string output) =>
        Pb.Bytes(140, Pb.Bytes(2, Pb.Str(1, output)));

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

    private static List<string> SettingsKeys(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("userSettings", out var s) || s.ValueKind != JsonValueKind.Object)
            return new List<string>();
        return s.EnumerateObject().Select(p => p.Name).ToList();
    }

    /// <summary>真实文件可能被正在运行的客户端占着，必须用宽松共享模式读。</summary>
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

    private static Dictionary<string, string?> SnapshotTree(string dir, bool skipWalIndexShm = false)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(dir)) return map;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                // -shm 是 SQLite 的 wal-index 共享内存文件：WAL 库上**任何读者**（包括本自检的只读连接）
                // 都可能创建 / 更新它。把它算进"整棵树 sha256 不变"就是把自检自己的足迹当成了别人改的。
                if (skipWalIndexShm && IsWalIndexShm(f)) continue;
                map[f] = ShaShared(f);
            }
        }
        catch { /* 权限 */ }
        return map;
    }

    /// <summary>SQLite 的 wal-index 共享内存文件（conversations/*.db-shm）。</summary>
    private static bool IsWalIndexShm(string path) =>
        path.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// -db 与 -wal 的逐字节比对——**这两类一条都不放宽**：单独再比一次，免得"排除了 -shm"
    /// 被误读成"整棵树都不比了"。返回（参与比对的文件数，变化项数，变化项清单）。
    /// </summary>
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
            if (!Directory.Exists(dir)) return 0;
            return Directory.EnumerateFiles(dir, "*.db-shm", SearchOption.AllDirectories).Count();
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
