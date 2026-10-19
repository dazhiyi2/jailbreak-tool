using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Antigravity;

/// <summary>
/// Antigravity 的标记表。
///
/// 判据的特殊之处：这个目标的推理原文**只在** <c>~/.gemini/antigravity/conversations/&lt;id&gt;.db</c>
/// 的 <c>steps.step_payload</c>（protobuf）里；<c>brain\&lt;id&gt;\.system_generated\logs\transcript_full.jsonl</c>
/// 里 <c>thinking</c> / <c>reasoning</c> 实测 0 命中（docs/gemini.md 第五节）。
/// 所以 <c>CheckMarkers</c> 的判据是「从 conversations 抽出来的文本全集（含推理与答复）」里的命中数，
/// 并在报告里写明这一来源。
///
/// 三层标记：
///   · SystemPrompt  ：规则层（rules\AGENTS.md 指向的本工具规则文本）
///   · ContextPayload：载荷层（rules\JailbreakTool.md 的载荷文本）
///   · FirstParty    ：Antigravity 自带身份串（在 171 MB 的语言服务器里，本工具改不到，只用来对照）
/// </summary>
public static class AntigravityMarkers
{
    /// <summary>规则层标记（来自本工具物化的规则文本）。</summary>
    public static IReadOnlyList<string> RulesMarkers { get; } = new[]
    {
        "本机配置说明（由操作者维护）",
        "输出用简体中文",
        "工具报回的限制信息",
    };

    /// <summary>载荷层标记（来自本工具物化的第二份规则文本）。</summary>
    public static IReadOnlyList<string> PayloadMarkers { get; } = new[]
    {
        "工作约定",
        "执行心态",
        "本机的工作范围包含",
    };

    /// <summary>内置身份串（客户端自带，本工具改不到它，只用来对照）。</summary>
    public static IReadOnlyList<string> FirstPartyMarkers { get; } = new[]
    {
        "Antigravity",
    };

    /// <summary>全部标记。</summary>
    public static IReadOnlyList<string> AllMarkers { get; } =
        RulesMarkers.Concat(PayloadMarkers).Concat(FirstPartyMarkers).ToArray();

    /// <summary>
    /// 备份污染判据：**只**用我们自己的注入标记。
    /// 不把内置身份串 <c>Antigravity</c> 算进去——用户配置或路径里出现这个词不等于备份被污染，
    /// 把干净备份误判成污染会逼着 revert 走逆向撤销（那是真的会改变用户文件的分支）。
    /// </summary>
    public static IReadOnlyList<string> BackupMarkers { get; } =
        RulesMarkers.Concat(PayloadMarkers).ToArray();

    public static IReadOnlyList<MarkerSpec> MarkerSpecs { get; } = new[]
    {
        new MarkerSpec("本机配置说明（由操作者维护）", MarkerLayer.SystemPrompt),
        new MarkerSpec("输出用简体中文", MarkerLayer.SystemPrompt),
        new MarkerSpec("工具报回的限制信息", MarkerLayer.SystemPrompt),
        new MarkerSpec("工作约定", MarkerLayer.ContextPayload),
        new MarkerSpec("执行心态", MarkerLayer.ContextPayload),
        new MarkerSpec("本机的工作范围包含", MarkerLayer.ContextPayload),
        new MarkerSpec("Antigravity", MarkerLayer.FirstParty),
    };

    /// <summary>某个条目集合里的命中总数（人话：命中 0 = 这一层没进模型上下文）。</summary>
    public static int CountHits(string text, IReadOnlyList<string> markers) =>
        markers.Sum(m => FileText.Count(text, m));

    /// <summary>
    /// 按方案取标记规格：protocol 方案下规则层是 gpt-instruct 原文，
    /// 系统提示词层（规则层）的标记必须换成 B 套标记（否则命中数判据永远 0，那是假结论）。
    /// </summary>
    public static IReadOnlyList<MarkerSpec> SpecsForScheme(InjectionScheme scheme) =>
        scheme == InjectionScheme.Protocol
            ? SchemeMarkers.ReplaceLayer(MarkerSpecs, MarkerLayer.SystemPrompt, SchemeMarkers.Protocol)
            : MarkerSpecs;
}
