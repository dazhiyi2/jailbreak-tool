using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Cursor;

/// <summary>
/// Cursor 的标记表。
///
/// 这个目标的判据与别的目标不同：**主判据是定量的**——<c>composerData:&lt;id&gt;</c> 的
/// <c>promptTokenBreakdown.categories</c> 里 <c>id=="rules"</c> 那一档的 <c>characterCount</c>
/// （注入后应精确增长我们写入的字符数，见 docs/cursor.md 第二节与第六节）。
/// 命中数判据（下面的 markers）是**次判据**：看会话文本里有没有引用我们写入的规则原文。
///
/// 标记分三组：
///   · PatchMarkers   ：层 1 等长替换后的框架句（WrapperOrGuidance 层）
///   · PersonalMarkers：层 2 写进 state.vscdb 的个人规则正文
///   · PayloadMarkers ：层 3 写进 .cursor\rules\breach.mdc 的项目规则正文
///
/// **不许拿 system_prompt 那一档判生效**：那 4,099 字符由 Cursor 后端下发，本地无明文，
/// 注入前后都不会变（docs/cursor.md 第二节）。
/// </summary>
public static class CursorMarkers
{
    /// <summary>层 1：替换后框架句里我们自己的那半句（ASCII，等长替换后必然在位）。</summary>
    public static IReadOnlyList<string> PatchMarkers { get; } = new[]
    {
        "These are this machine's standing rules.",
        "The rules section contains this machine's standing policy.",
    };

    /// <summary>层 2（个人规则，state.vscdb → aicontext.personalContext）的特征句。</summary>
    public static IReadOnlyList<string> PersonalMarkers { get; } = new[]
    {
        "本机全局工作偏好",
        "加做，不是收缩",
    };

    /// <summary>层 3（项目规则，.cursor\rules\breach.mdc 正文）的特征句。</summary>
    public static IReadOnlyList<string> PayloadMarkers { get; } = new[]
    {
        "本机项目环境",
        "本仓库的工作是逆向工程与安全工具开发",
    };

    /// <summary>全部注入标记。</summary>
    public static IReadOnlyList<string> AllMarkers { get; } =
        PatchMarkers.Concat(PersonalMarkers).Concat(PayloadMarkers).ToArray();

    /// <summary>
    /// 备份污染判据：用**全部**自家注入标记。
    /// 一份备份里出现任意一条，就说明它不是出厂原件（用它还原会把注入"还原"回来）。
    /// 判宽不判窄：漏报污染会把用户文件越改越远。
    /// </summary>
    public static IReadOnlyList<string> BackupMarkers { get; } = AllMarkers;

    /// <summary>会话文本命中数自检用的规格（次判据）。</summary>
    public static IReadOnlyList<MarkerSpec> MarkerSpecs { get; } = new[]
    {
        new MarkerSpec("These are this machine's standing rules.", MarkerLayer.WrapperOrGuidance),
        new MarkerSpec("The rules section contains this machine's standing policy.", MarkerLayer.WrapperOrGuidance),
        new MarkerSpec("本机全局工作偏好", MarkerLayer.ContextPayload),
        new MarkerSpec("加做，不是收缩", MarkerLayer.ContextPayload),
        new MarkerSpec("本机项目环境", MarkerLayer.ContextPayload),
        new MarkerSpec("本仓库的工作是逆向工程与安全工具开发", MarkerLayer.ContextPayload),
    };

    public static int CountHits(string text, IReadOnlyList<string> markers) =>
        markers.Sum(m => FileText.Count(text, m));

    /// <summary>这段文本是不是本工具写的（含任一注入标记）。</summary>
    public static bool IsOurs(string? text) =>
        text is not null && BackupMarkers.Any(m => FileText.Count(text, m) > 0);
}
