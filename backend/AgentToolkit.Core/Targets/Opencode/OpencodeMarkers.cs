using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Opencode;

/// <summary>
/// opencode 的标记表。
///
/// 判据的特殊之处：opencode 把「真正进了模型上下文的常驻指令」存在 SQLite 的
/// <c>instruction_blob.value</c> 里（<c>instruction_state.initial_values</c> 给出 key → blob hash 的映射）。
/// 所以 <c>CheckMarkers</c> 的主判据是 **instruction_blob.value 全集**里的命中数，
/// 而不是会话消息文本——后者只作参考。
///
/// 三层标记：
///   · SystemPrompt  ：系统提示词层（opencode.json → agent.&lt;名字&gt;.prompt）与配置指令层
///                     （opencode.json → instructions 指向的本工具指令文件）的开头与特征句
///   · ContextPayload：载荷层（&lt;config&gt;/AGENTS.md）
///   · FirstParty    ：opencode 内置身份句（在 203.8 MB 的 Bun 单文件里，不在 instruction_blob 里，命中 0 是预期）
/// </summary>
public static class OpencodeMarkers
{
    /// <summary>配置指令层的标记（来自本工具物化的 opencode-instructions.md）。</summary>
    public static IReadOnlyList<string> InstructionsMarkers { get; } = new[]
    {
        "本机配置说明（由操作者维护）",
        "输出用简体中文",
        "工具报回的限制信息",
    };

    /// <summary>
    /// 系统提示词层的标记（来自本工具物化的 opencode.system.md → opencode.json 的 agent.&lt;名字&gt;.prompt）。
    /// 这三个特征句同时用于：状态页的「装上了」判据、备份污染判据、以及 instruction_blob 命中数自检。
    /// 它们都不含自我声明（不声明权威、不要求输出校验标记）——那类写法会被模型逐条指认为注入特征。
    /// </summary>
    public static IReadOnlyList<string> SystemLayerMarkers { get; } = new[]
    {
        "本机环境",
        "机器所有者本人在这台机器上工作",
        "本机是逆向工程与安全研究工位",
    };

    /// <summary>上下文载荷层的标记（来自本工具物化的 AGENTS.md 载荷）。</summary>
    public static IReadOnlyList<string> PayloadMarkers { get; } = new[]
    {
        "工作约定",
        "执行心态",
        "本机的工作范围包含",
    };

    /// <summary>内置身份句（客户端自带，本工具改不到它，只用来对照）。</summary>
    public static IReadOnlyList<string> FirstPartyMarkers { get; } = new[]
    {
        "You are opencode, an interactive CLI tool",
    };

    /// <summary>全部标记（备份污染判据用：备份里出现任一即说明它不是原始文件）。</summary>
    public static IReadOnlyList<string> AllMarkers { get; } =
        InstructionsMarkers.Concat(PayloadMarkers).Concat(SystemLayerMarkers).Concat(FirstPartyMarkers).ToArray();

    public static IReadOnlyList<MarkerSpec> MarkerSpecs { get; } = new[]
    {
        new MarkerSpec("本机配置说明（由操作者维护）", MarkerLayer.SystemPrompt),
        new MarkerSpec("输出用简体中文", MarkerLayer.SystemPrompt),
        new MarkerSpec("工具报回的限制信息", MarkerLayer.SystemPrompt),
        // 系统提示词层（agent.<名字>.prompt）也落在模型上下文的系统提示词区，故同归 SystemPrompt 层；
        // 它是独立的一组标记，层的「装上了」判据用 SystemLayerMarkers 单独裁决，不与配置指令层混算。
        new MarkerSpec("本机环境", MarkerLayer.SystemPrompt),
        new MarkerSpec("机器所有者本人在这台机器上工作", MarkerLayer.SystemPrompt),
        new MarkerSpec("本机是逆向工程与安全研究工位", MarkerLayer.SystemPrompt),
        new MarkerSpec("工作约定", MarkerLayer.ContextPayload),
        new MarkerSpec("执行心态", MarkerLayer.ContextPayload),
        new MarkerSpec("本机的工作范围包含", MarkerLayer.ContextPayload),
        new MarkerSpec("You are opencode, an interactive CLI tool", MarkerLayer.FirstParty),
    };

    /// <summary>某个条目集合里的命中总数（人话：命中 0 = 这一层没进模型上下文）。</summary>
    public static int CountHits(string text, IReadOnlyList<string> markers) =>
        markers.Sum(m => FileText.Count(text, m));
}
