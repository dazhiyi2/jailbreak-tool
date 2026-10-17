using System.Text;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Cursor;

/// <summary>层 3 一个 .mdc 文件的一次操作结论（预览与注入共用）。</summary>
public sealed class CursorRulesPlan
{
    public required string Path { get; init; }
    public required string Text { get; init; }
    public bool Enabled { get; init; } = true;
    public bool WorkspaceKnown { get; init; } = true;
    public string? Existing { get; init; }
    public bool WillWrite { get; init; }
    public long BeforeBytes { get; init; }
    public long AfterBytes { get; init; }
    public bool NeedsBackup { get; init; }
    public required string Note { get; init; }
}

/// <summary>层 3 一个 .mdc 文件的还原计划。</summary>
public sealed class CursorRulesRevert
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
/// 层 3：项目规则层 —— <c>&lt;工作区&gt;\.cursor\rules\breach.mdc</c>（本工具管理的**唯一一份**）。
///
/// 格式由本地代码 <c>updateRule</c> 的原文定死（docs/cursor.md 第 3.2 节）：
/// <code>
/// ---
/// description: &lt;一句话&gt;
/// alwaysApply: true
/// ---
/// &lt;body&gt;
/// </code>
/// <c>alwaysApply: true</c> 是正文进提示词的开关：源码里是
/// <c>body: i.alwaysApply ? i.body : void 0</c> —— 不是 true 的话正文根本不进 <c>rules</c> 档。
///
/// 纪律：同目录下别人的 <c>.mdc</c> 一个字节都不碰；本工具只增删自己这一份；
/// 工作区解析不到 → 整层 TargetAbsent 跳过（**不往 C:\ 或当前目录写**）。
/// </summary>
public static class CursorRules
{
    public const string FileName = "breach.mdc";
    public const string DirRelative = @".cursor\rules";
    public const string Description = "本机环境与交付偏好（由操作者维护）";
    public const string BodyMarker = "本机项目环境";

    /// <summary>
    /// 自设的保守上限（**客户端没有明文上限**，这是本工具自己的护栏）：
    /// <c>rules</c> 档本机实测 10.5 KB，注入文本必须远小于它，否则会把别的规则挤出预算。
    /// </summary>
    public const long PerFileCapBytes = 32_768;

    public static string DirFor(string workspace) => Path.Combine(workspace, DirRelative);
    public static string PathFor(string workspace) => Path.Combine(DirFor(workspace), FileName);

    /// <summary>按本地 updateRule 的原文拼 frontmatter + 正文（行尾统一 LF）。</summary>
    public static string Render(string description, string body)
    {
        var b = body.Replace("\r\n", "\n").TrimEnd('\n');
        return $"---\ndescription: {description}\nalwaysApply: true\n---\n{b}\n";
    }

    /// <summary>正文部分（frontmatter 之后的内容）；用来算"进 rules 档的字符数"。</summary>
    public static string ExtractBody(string fileText)
    {
        var t = fileText.Replace("\r\n", "\n");
        if (!t.StartsWith("---\n", StringComparison.Ordinal)) return t;
        var end = t.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return t;
        var after = t.IndexOf('\n', end + 1);
        return after < 0 ? "" : t[(after + 1)..];
    }

    public static bool IsOurs(string? text) => CursorMarkers.IsOurs(text);

    /// <summary>写入计划（纯函数，不碰磁盘）。</summary>
    public static CursorRulesPlan Plan(string workspace, string body, bool skip, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(workspace))
            return new CursorRulesPlan
            {
                Path = "", Text = "", WorkspaceKnown = false, WillWrite = false,
                Note = "工作区解析不到：跳过项目规则层（**不猜路径、不往 C:\\ 或当前目录写**）。",
            };

        var path = PathFor(workspace);
        var text = Render(Description, body);
        var existing = FileText.ReadOrNull(path);
        var beforeBytes = existing is null ? 0 : FileText.Bytes(existing);
        var afterBytes = FileText.Bytes(text);

        if (skip)
            return new CursorRulesPlan
            {
                Path = path, Text = text, Enabled = false, Existing = existing,
                WillWrite = false, BeforeBytes = beforeBytes, AfterBytes = beforeBytes,
                Note = "已跳过项目规则层（SkipContextPayload）。",
            };

        if (afterBytes >= PerFileCapBytes)
            problem = $"「{FileName}」有 {FileText.Human(afterBytes)}，超过本工具自设的上限 " +
                      $"{FileText.Human(PerFileCapBytes)}（客户端没有明文上限，这是本工具的护栏）：拒绝写入。";

        if (existing is not null && string.Equals(existing, text, StringComparison.Ordinal))
            return new CursorRulesPlan
            {
                Path = path, Text = text, Existing = existing,
                WillWrite = false, BeforeBytes = beforeBytes, AfterBytes = beforeBytes,
                Note = "已是当前文本，无需写入（幂等）。",
            };

        var ours = IsOurs(existing);
        var needsBackup = existing is not null && !ours;
        return new CursorRulesPlan
        {
            Path = path, Text = text, Existing = existing,
            WillWrite = problem is null, BeforeBytes = beforeBytes, AfterBytes = afterBytes,
            NeedsBackup = needsBackup,
            Note = needsBackup
                ? $"用户自己的 {FileName} 会先备份到 {path}.breach.bak（同目录别人的 .mdc 一律不碰）。"
                : ours
                    ? "现存文件是自家旧规则：不重复备份（备份要留给用户原文件）。"
                    : $"部署项目规则文件（原文件不存在）：{path}",
        };
    }

    /// <summary>还原计划：干净备份 → 逐字节还原；无备份但盘上是自家规则 → 删除；不是自家的 → 不动。</summary>
    public static CursorRulesRevert PlanRevert(string path, BackupAssessment assessment)
    {
        var existing = FileText.ReadOrNull(path);

        if (assessment.Status == BackupStatus.Clean && assessment.Path is not null)
        {
            var saved = FileText.ReadOrNull(assessment.Path);
            if (saved is not null)
                return new CursorRulesRevert
                {
                    Path = path, BackupPath = assessment.Path, Existing = existing, RestoreText = saved,
                    Changed = !string.Equals(existing, saved, StringComparison.Ordinal),
                    Note = $"从首次备份逐字节还原：{assessment.Path} → {path}",
                };
        }

        if (existing is null)
            return new CursorRulesRevert
            {
                Path = path, BackupPath = assessment.Path, Existing = null, NothingToDo = true,
                Note = $"没有可还原的项目规则文件：{path} 不存在。",
            };

        if (IsOurs(existing))
            return new CursorRulesRevert
            {
                Path = path, BackupPath = assessment.Path, Existing = existing, Delete = true, Changed = true,
                Note = "盘上是本工具部署的项目规则且没有可信备份：删除它（恢复到出厂的不存在状态）。",
            };

        return new CursorRulesRevert
        {
            Path = path, BackupPath = assessment.Path, Existing = existing, NothingToDo = true,
            Note = $"{FileName} 不是本工具部署的（不含注入标记）：未改动。",
        };
    }

    /// <summary>
    /// 四层当前盘上状态清单（SystemPromptText / ExportSystemPrompt / 差异页共用）。
    /// 结论写在最前面：本目标的常驻提示词由服务端下发，本地没有明文，不许现抠一段冒充。
    /// </summary>
    public static string StateListing(
        string installRoot, string configHome, IReadOnlyList<(string Relative, string State)> patchFiles,
        string? personalValue, bool personalKeyPresent, string rulesPath, string? rulesText, string? workspace)
    {
        var sb = new StringBuilder();
        sb.Append("# Cursor 的系统提示词：本地没有明文\n");
        sb.Append("#\n");
        sb.Append("# 结论：那 4,099 字符的 system_prompt 由 Cursor 后端下发（docs/cursor.md 第 2.2 节：\n");
        sb.Append("#       out\\main.js、out\\vs\\workbench\\*、cursor-always-local 里搜 'You are (a|an|the)' 与\n");
        sb.Append("#       systemPrompt / SYSTEM_PROMPT 都是 0 命中）。本工具不从别处抠一段冒充它——\n");
        sb.Append("#       下面给的是四层的盘上状态。\n");
        sb.Append('\n');

        sb.Append("## 层 1 补丁层（本地框架句，等长字节替换）\n");
        sb.Append($"安装根：{installRoot}\n");
        foreach (var (rel, state) in patchFiles) sb.Append($"  [{state}] {rel}\n");
        sb.Append("本工具写入的框架句（等长替换：尾部空格是用来补齐长度的，文件长度不变）：\n");
        foreach (var a in CursorPatchTable.Anchors)
        {
            sb.Append("  ").Append(a.Id).Append(" 原文：").Append(a.Anchor).Append('\n');
            sb.Append("  ").Append(a.Id).Append(" 注入：").Append(a.Replacement).Append('\n');
        }
        sb.Append('\n');

        sb.Append("## 层 2 个人规则层（state.vscdb → ItemTable → aicontext.personalContext）\n");
        sb.Append($"会话库：{CursorLocator.DbPath(configHome)}\n");
        if (!personalKeyPresent) sb.Append("键不存在（出厂态就是「键不存在」，不是「键为空」）\n");
        else sb.Append(string.IsNullOrEmpty(personalValue) ? "(空)\n" : personalValue!.TrimEnd('\r', '\n') + "\n");
        sb.Append('\n');

        sb.Append("## 层 3 项目规则层（<工作区>\\.cursor\\rules\\" + FileName + "）\n");
        sb.Append(workspace is null
            ? "工作区：解析不到（层 3 跳过，不往 C:\\ 或当前目录写）\n"
            : $"工作区：{workspace}\n");
        sb.Append($"文件：{(string.IsNullOrEmpty(rulesPath) ? "(不适用)" : rulesPath)}\n");
        sb.Append(string.IsNullOrEmpty(rulesText) ? "(不存在)\n" : rulesText!.TrimEnd('\r', '\n') + "\n");
        sb.Append('\n');

        sb.Append("## 层 4 日志层（只读）\n");
        sb.Append("生效判据：composerData:<id>.promptTokenBreakdown.categories 里 id==\"rules\" 的 characterCount。\n");
        sb.Append("**不拿 system_prompt 那一档判生效**：服务端下发，注入前后不会变。\n");
        return sb.ToString();
    }
}
