using System.Text;
using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Codex;

/// <summary>
/// Codex 目标的固定事实与判据表。
///
/// 提示词文本 = gpt-instruct 原文（内嵌资产 <c>Assets\schemeB\protocol.md</c>，7,575 字节，
/// sha256 <c>37382fb7082652e5b18c99fa5086fbc6d414993eaeae2ec7852ba372b031acac</c>），
/// 一个字都不改；本工具只决定它落到哪个文件名、由哪个官方配置键指向它。
/// </summary>
public static class CodexRules
{
    /// <summary>本工具自己的提示词文件名（**刻意不用上游工具认的 9 个旧名**）。</summary>
    public const string PromptFileName = "jailbreak-tool-instructions.md";

    /// <summary>gpt-instruct 原文的 sha256（自检里对资源与落盘文件都断言这个值）。</summary>
    public const string ProtocolSha256 = "37382fb7082652e5b18c99fa5086fbc6d414993eaeae2ec7852ba372b031acac";

    /// <summary>原文字节数（7,575）。用 long 是为了和 FileInfo.Length 直接比。</summary>
    public const long ProtocolBytes = 7575;

    /// <summary>内嵌资源名后缀（Assets\schemeB\protocol.md）。</summary>
    public const string ProtocolResourceSuffix = "schemeB.protocol.md";

    /// <summary>
    /// 体积护栏。<b>客户端没有给 model_instructions_file 明文上限</b>（docs/codex.md 未记录任何上限），
    /// 256 KB 是**本工具自设**的保守上限，用来在写盘前挡住明显的错误载荷——不是官方限制。
    /// </summary>
    public const long PerFileCapBytes = 256 * 1024;

    public static string PromptPath(string configHome) => Path.Combine(configHome, PromptFileName);

    /// <summary>
    /// 上游工具（codex-instruct / gpt-instruct 那一套）认的 9 个旧文件名。
    /// 本工具**只报告、不冒充**：状态页看到它们会说"检测到同类工具留下的痕迹"，
    /// revert 只删自己那一个文件、只恢复自己那一个键。
    /// </summary>
    public static IReadOnlyList<string> UpstreamPromptFileNames { get; } = new[]
    {
        "gpt-5.6-sol-unrestricted-v45.md",
        "gpt-5.6-sol-unrestricted-v5.md",
        "gpt-5.6-sol-unrestricted-v35.md",
        "gpt-5.6-sol-unrestricted-v41.md",
        "gpt-5.6-sol-unrestricted-v41-skills.md",
        "gpt-5.6-sol-unrestricted-v42.md",
        "gpt-6-astra-v1-rc1.md",
        "gpt-6-astra-v1.md",
        "gpt-6.1-sol-v1-rc1.md",
    };

    /// <summary>
    /// 标记表（4~6 条，全部取自原文里足够唯一的协议段名/绑定名）。
    /// 层归属统一按 SystemPrompt 报：本目标只有一层可写（指令文件），
    /// 契约的四档里没有"配置键指向的指令文件"这一档（写进 Notes，不改契约）。
    /// </summary>
    public static IReadOnlyList<string> ProtocolMarkers { get; } = new[]
    {
        "INJECTED REVISION ACTION",
        "TRANSACTION TURN COMMIT",
        "ROLE: PROJECT EXECUTOR",
        "OUTPUT_CONTRACT",
        "VERIFIED TRANSACTION",
        "ROLLBACK.sh",
    };

    /// <summary>备份污染判据只取三段段头：它们短、唯一、不可能在别的上下文里自然出现。</summary>
    public static IReadOnlyList<string> BackupMarkers { get; } = new[]
    {
        "INJECTED REVISION ACTION",
        "TRANSACTION TURN COMMIT",
        "ROLE: PROJECT EXECUTOR",
    };

    /// <summary>
    /// 行为级判据（从 agentMessage 正文直接可判读的特征，全部来自原文）：
    ///   · 后续回合以 <c>Current:</c> 或 <c>当前：</c> 开头（原文 "begin with `Current:` or `当前：`"）
    ///   · 原文规定的四个绑定名与 <c>OUTPUT_CONTRACT</c>
    /// </summary>
    public static IReadOnlyList<string> BehaviorFeatures { get; } = new[]
    {
        "Current:",
        "当前：",
        "OUTPUT_CONTRACT",
        "MODIFIED_FILE",
        "DIFF_FILE",
        "VERIFICATION.txt",
        "ROLLBACK.sh",
    };

    /// <summary>原文要求"以 Current: 或 当前： 开头"的两条，单独拎出来做"首行"判读。</summary>
    public static IReadOnlyList<string> StatusLinePrefixes { get; } = new[] { "Current:", "当前：" };

    public static int CountHits(string? text, string needle) =>
        string.IsNullOrEmpty(text) || needle.Length == 0 ? 0 : FileText.Count(text, needle);

    /// <summary>这个文件是上游工具留下的那几个旧名之一吗（只用于报告）。</summary>
    public static bool IsUpstreamName(string fileName) =>
        UpstreamPromptFileNames.Any(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>主目录里检测到的上游痕迹（文件存在才算）。</summary>
    public static IReadOnlyList<string> DetectUpstreamTraces(string configHome)
    {
        var found = new List<string>();
        foreach (var name in UpstreamPromptFileNames)
        {
            try
            {
                if (File.Exists(Path.Combine(configHome, name))) found.Add(name);
            }
            catch { /* 权限 */ }
        }
        return found;
    }

    /// <summary>四层状态清单（导出 / SystemPromptText 用；日志层只读，单独一段）。</summary>
    public static string StateListing(
        string configHome, string configText, CodexConfigSnapshot snap, string promptPath, string? promptText,
        string? promptSha, bool keyIsOurs, string? keyValue, IReadOnlyList<string> upstreamTraces,
        string? logSummary)
    {
        var sb = new StringBuilder();
        sb.Append("# Codex 注入状态清单（本工具生成）\n\n");
        sb.Append("## 主目录\n  ").Append(configHome).Append('\n');
        sb.Append("## 配置文件\n  ").Append(CodexConfig.PathOf(configHome)).Append('\n');
        sb.Append("  行数 ").Append(snap.LineCount)
          .Append("（顶层 ").Append(snap.TopLevelLineCount)
          .Append(" 行 / 表 ").Append(snap.TableLineCount).Append(" 行）\n");
        sb.Append("  行尾 ").Append(snap.Eol == "\r\n" ? "CRLF" : "LF")
          .Append(snap.EndsWithNewline ? "，以换行结尾" : "，不以换行结尾").Append('\n');
        sb.Append("  其它顶层键指纹 ").Append(Short(snap.OtherTopDigest)).Append('\n');
        sb.Append("  表区指纹 ").Append(Short(snap.TablesDigest)).Append('\n');

        sb.Append("\n## 层 1：系统提示词层\n");
        sb.Append("  键 ").Append(CodexConfig.KeyName).Append(snap.Key.Present
            ? $"（第 {snap.Key.LineIndex + 1} 行）= {snap.Key.RawValue}"
            : "（顶层没有这个键）").Append('\n');
        if (snap.Key.InTableOnly)
            sb.Append("  另有同名键出现在表内第 ").Append(snap.Key.InTableLineIndex + 1)
              .Append(" 行：那不是本工具的键（本工具只写顶层）。\n");
        sb.Append("  指向本工具的提示词文件：").Append(keyIsOurs ? "是" : "否")
          .Append("（读到的值 ").Append(keyValue ?? "（无）").Append("）\n");
        sb.Append("  提示词文件 ").Append(promptPath).Append('\n');
        sb.Append("  文件在盘上：").Append(promptText is null ? "否" : $"是（{FileText.Bytes(promptText)} 字节，sha256 {Short(promptSha)}）").Append('\n');
        if (promptText is not null)
        {
            sb.Append("\n  ---- 文件正文开始 ----\n");
            sb.Append(promptText.EndsWith('\n') ? promptText : promptText + "\n");
            sb.Append("  ---- 文件正文结束 ----\n");
        }

        sb.Append("\n## 层 2：日志层（只读，不进 Layers）\n");
        sb.Append("  ").Append(logSummary ?? "（未读取）").Append('\n');
        sb.Append("  会话索引 ").Append(CodexDb.StateDbPath(configHome)).Append('\n');
        sb.Append("  会话正文 ").Append(CodexDb.ThreadDbPath(configHome)).Append('\n');
        sb.Append("  原始转录 ").Append(Path.Combine(configHome, "sessions")).Append("\\YYYY\\MM\\DD\\rollout-*.jsonl\n");

        sb.Append("\n## 同类工具痕迹（只报告，不动）\n");
        if (upstreamTraces.Count == 0) sb.Append("  （主目录里没有上游 9 个旧文件名）\n");
        else foreach (var t in upstreamTraces) sb.Append("  检测到 ").Append(t).Append("（不是本工具的文件，revert 不删）\n");

        sb.Append("\n## 契约缺口\n");
        sb.Append("  · LayerKind 四档里没有「配置键指向的指令文件」这一档 → 按 SystemPrompt 报；\n");
        sb.Append("  · 没有「只读日志层」这一档 → 日志层不进 Layers，判读写进 EffectivenessEvidence / Notes；\n");
        sb.Append("  · LocateResult.SessionsDir 被引擎固定拼成 <ConfigHome>\\sessions，对 Codex 只对一半（见上）。\n");

        sb.Append("\n## 未验证\n");
        sb.Append("  · 本工具没有在真实客户端上注入过：写 model_instructions_file 后客户端是否真的加载这份指令，未实测。\n");
        sb.Append("  · 客户端是每次启动重读 config.toml，还是要完全退出（含托盘）才重读：未实测。\n");
        sb.Append("  · 注入后是否需要新开线程才生效：未实测（旧线程的提示词已经组装过）。\n");

        // configText 只用来做一次一致性自查（调用方传的是同一份解析来源）。
        _ = configText;
        return sb.ToString();
    }

    private static string Short(string? sha) =>
        sha is null ? "(无)" : sha[..Math.Min(12, sha.Length)] + "…";
}
