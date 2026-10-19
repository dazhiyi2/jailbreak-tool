using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Antigravity;

/// <summary>规则层一个 Markdown 文件的一次操作结论（预览与注入共用）。</summary>
public sealed class RulesFilePlan
{
    public required string Path { get; init; }
    public required string Text { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Existing { get; init; }
    public bool WillWrite { get; init; }
    public long BeforeBytes { get; init; }
    public long AfterBytes { get; init; }
    /// <summary>需要先备份的现存文件（用户自己的 AGENTS.md / JailbreakTool.md）。</summary>
    public bool NeedsBackup { get; init; }
    public string BackupPath { get; init; } = "";
    public required string Note { get; init; }
}

/// <summary>规则层一个 Markdown 文件的还原计划。</summary>
public sealed class RulesFileRevert
{
    public required string Path { get; init; }
    public string? BackupPath { get; init; }
    public string? Existing { get; init; }
    public string? RestoreText { get; init; }
    public bool Delete { get; init; }
    public bool Changed { get; init; }
    public bool NothingToDo { get; init; }
    public required string Note { get; init; }
}

/// <summary>
/// 规则层：<c>~/.gemini/config/rules/</c> 下的两份 Markdown。
///
/// 路径与加载规则来自语言服务器内嵌文档（原文已读，另见
/// <c>~/.gemini/antigravity/builtin/skills/agy-customizations/docs/rules.md</c>）：
///   · <c>rules/</c>（相对 customization root = <c>~/.gemini/config/</c>）里的规则会并入活动规则集；
///   · 任意目录下的 <c>GEMINI.md</c> / <c>AGENTS.md</c> 从工作目录**向上走到仓库根**逐级加载；
///   · 标准 <c>GEMINI.md</c> / <c>AGENTS.md</c> 不支持 frontmatter，对其目录范围**始终生效**；
///   · **每个规则文件上限 24,000 字节**（超出按行边界截断），全局规则另有 20,000 token 预算；
///     超预算的规则会从"内联全文"降级成"文件路径指针"——所以本工具写的东西必须远小于 24 KB。
///
/// 纪律：逐字节相同 → 不写（<c>WillWrite=false</c>）；用户自己的文件 → 先备份再覆盖；
/// 内容超过 24 KB → fail-closed 拒写（写下去也会被客户端截断，等于半个注入）。
/// </summary>
public static class AntigravityRules
{
    public const string AgentsFileName = "AGENTS.md";
    public const string ExtraFileName = "JailbreakTool.md";
    public const string SkillName = "breach";

    /// <summary>内嵌文档的硬上限：单个规则文件 24,000 字节（超出按行边界截断）。</summary>
    public const long PerFileCapBytes = 24_000;

    /// <summary>这份文本是不是本工具写的（含任一注入标记；B 套标记也算）。</summary>
    public static bool IsOurs(string? text) =>
        text is not null && SchemeMarkers.Union(AntigravityMarkers.BackupMarkers)
            .Any(m => FileText.Count(text, m) > 0);

    /// <summary>磁盘现有内容与将要写入的内容是否逐字节相同（幂等判据，全工具统一）。</summary>
    public static bool SameContent(string? existing, string text) =>
        existing is not null && string.Equals(existing, text, StringComparison.Ordinal);

    /// <summary>
    /// 写入计划（纯函数，不碰磁盘）：
    ///   · 逐字节相同 → <c>WillWrite=false</c>（<c>Applied</c> 也不许报 true）；
    ///   · 现存文件不是自家规则 → 需要先备份（本工具只增不毁）；
    ///   · 现存文件是自家旧规则 → 不重复备份（备份要保留用户原文件）；
    ///   · 文本超 24 KB → 记进 Problems，调用方 fail-closed。
    /// </summary>
    public static RulesFilePlan Plan(string path, string text, bool skip, out string? problem)
    {
        problem = null;
        var existing = FileText.ReadOrNull(path);
        var beforeBytes = existing is null ? 0 : FileText.Bytes(existing);
        var afterBytes = FileText.Bytes(text);

        if (skip)
            return new RulesFilePlan
            {
                Path = path, Text = text, Enabled = false, Existing = existing,
                WillWrite = false, BeforeBytes = beforeBytes, AfterBytes = beforeBytes,
                Note = "已跳过规则层（SkipSystemPrompt）。",
            };

        if (afterBytes >= PerFileCapBytes)
            problem = $"「{Path.GetFileName(path)}」有 {FileText.Human(afterBytes)}，超过 Antigravity 的规则文件上限 " +
                      $"{FileText.Human(PerFileCapBytes)}（内嵌文档：超出按行边界截断，超预算还会降级成路径指针）：拒绝写入。";

        if (SameContent(existing, text))
            return new RulesFilePlan
            {
                Path = path, Text = text, Existing = existing,
                WillWrite = false, BeforeBytes = beforeBytes, AfterBytes = beforeBytes,
                Note = "已是当前文本，无需写入（幂等）。",
            };

        var ours = IsOurs(existing);
        var needsBackup = existing is not null && !ours;
        return new RulesFilePlan
        {
            Path = path, Text = text, Existing = existing,
            WillWrite = problem is null, BeforeBytes = beforeBytes, AfterBytes = afterBytes,
            NeedsBackup = needsBackup,
            BackupPath = path + ".breach.bak",
            Note = needsBackup
                ? $"用户自己的 {Path.GetFileName(path)} 将先备份到 {path}.breach.bak。"
                : ours
                    ? "现存文件是自家旧规则：不重复备份（备份要留给用户原文件）。"
                    : $"部署规则文件（原文件不存在）：{path}",
        };
    }

    /// <summary>
    /// 还原计划：备份干净 → 逐字节还原；没有备份但盘上是自家规则 → 删除（恢复到"不存在"）；
    /// 盘上不是自家文件 → 不动。备份污染由调用方先行拦截（抛 AnchorConflict）。
    /// </summary>
    public static RulesFileRevert PlanRevert(string path, BackupAssessment assessment)
    {
        var existing = FileText.ReadOrNull(path);

        if (assessment.Status == BackupStatus.Clean && assessment.Path is not null)
        {
            var saved = FileText.ReadOrNull(assessment.Path);
            if (saved is not null)
                return new RulesFileRevert
                {
                    Path = path, BackupPath = assessment.Path, Existing = existing, RestoreText = saved,
                    Changed = !string.Equals(existing, saved, StringComparison.Ordinal),
                    Note = $"从首次备份逐字节还原：{assessment.Path} → {path}",
                };
        }

        if (existing is null)
            return new RulesFileRevert
            {
                Path = path, BackupPath = assessment.Path, Existing = null, NothingToDo = true,
                Note = $"没有可还原的规则文件：{path} 不存在。",
            };

        if (IsOurs(existing))
            return new RulesFileRevert
            {
                Path = path, BackupPath = assessment.Path, Existing = existing, Delete = true, Changed = true,
                Note = "盘上是本工具部署的规则且没有可信备份：删除它（恢复到出厂的不存在状态）。",
            };

        return new RulesFileRevert
        {
            Path = path, BackupPath = assessment.Path, Existing = existing, NothingToDo = true,
            Note = $"{Path.GetFileName(path)} 不是本工具部署的（不含注入标记）：未改动。",
        };
    }

    /// <summary>
    /// 三层当前盘上状态清单（SystemPromptText / ExportSystemPrompt 共用）。
    /// 结论必须写在最前面：本目标的常驻提示词不在日志里，也不许从二进制里现抠一段冒充。
    /// </summary>
    public static string StateListing(string configHome, string dataHome, string? bundlePath,
        string rulesBody, string? extraBody, string? configText, string? skillText, int builtinSkills)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# Antigravity 的系统提示词：不在日志里\n");
        sb.Append("#\n");
        sb.Append("# 结论：常驻提示词编译在原生语言服务器里（resources\\bin\\language_server.exe，实测 171 MB），\n");
        sb.Append("#       conversations 的 step_payload 与 brain 的 transcript_full.jsonl 都只装「每步的模型输出」，\n");
        sb.Append("#       不装常驻提示词本身。本工具不从那个二进制里现抠一段冒充系统提示词——\n");
        sb.Append("#       下面给的是三层（规则 / 技能 / 权限）的当前盘上状态，作为参考基线。\n");
        sb.Append('\n');
        sb.Append("## 提示词载体\n");
        sb.Append(bundlePath is null
            ? "language_server.exe：（没定位到）\n"
            : $"language_server.exe：{bundlePath}{DescribeFile(bundlePath)}\n");
        sb.Append('\n');
        sb.Append("## 规则层（~/.gemini/config/rules/，逐级向上加载）\n");
        sb.Append($"配置文件根：{configHome}\n");
        sb.Append("--- rules/AGENTS.md ---\n");
        sb.Append(string.IsNullOrEmpty(rulesBody) ? "(不存在)\n" : rulesBody.TrimEnd('\r', '\n') + "\n");
        sb.Append("--- rules/" + ExtraFileName + " ---\n");
        sb.Append(string.IsNullOrEmpty(extraBody) ? "(不存在)\n" : extraBody!.TrimEnd('\r', '\n') + "\n");
        sb.Append('\n');
        sb.Append("## 技能层（~/.gemini/config/skills/<name>/SKILL.md，按需加载；本轮只报告）\n");
        sb.Append($"数据根里的内置技能：{dataHome}\\builtin\\skills（{builtinSkills} 个）\n");
        sb.Append("--- skills/" + SkillName + "/SKILL.md ---\n");
        sb.Append(string.IsNullOrEmpty(skillText) ? "(未投放)\n" : skillText!.TrimEnd('\r', '\n') + "\n");
        sb.Append('\n');
        sb.Append("## 权限层（~/.gemini/config/config.json 的 userSettings）\n");
        sb.Append(string.IsNullOrEmpty(configText) ? "(config.json 不存在)\n" : configText!.TrimEnd('\r', '\n') + "\n");
        return sb.ToString();
    }

    private static string DescribeFile(string path)
    {
        try { return $"；{FileText.Human(new FileInfo(path).Length)}"; }
        catch { return ""; }
    }
}
