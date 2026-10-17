using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Opencode;

/// <summary>载荷层（&lt;config&gt;/AGENTS.md）一次操作的结论。</summary>
public sealed class OpencodePayloadPlan
{
    public required string Path { get; init; }
    public required string Text { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Existing { get; init; }
    public bool WillWrite { get; init; }
    public long BeforeBytes { get; init; }
    public long AfterBytes { get; init; }
    /// <summary>需要先备份的现存文件（用户自己的 AGENTS.md）。</summary>
    public bool NeedsBackup { get; init; }
    public string BackupPath { get; init; } = "";
    public required string Note { get; init; }
}

/// <summary>载荷层还原计划：从备份逐字节还原 / 本工具部署过且无备份 → 删除 / 不是自家文件 → 不动。</summary>
public sealed class OpencodePayloadRevert
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
/// 上下文载荷层：<c>&lt;config&gt;/AGENTS.md</c>（全局）。
/// 源码级证据（从 203.8 MB 的 Bun 单文件里读出的 ConfigInstructionPlugin）：
/// <c>R = resolve(H5(l.config, "AGENTS.md"))</c>，项目级再由 <c>X5(T,C).map(Ne =&gt; H5(Ne,"AGENTS.md"))</c> 从工作目录向上搜到项目根。
/// </summary>
public static class OpencodePayload
{
    public const string FileName = "AGENTS.md";

    public static string TargetOf(string configHome) => Path.Combine(configHome, FileName);

    /// <summary>磁盘现有内容与将要写入的内容是否逐字节相同（幂等判据，全工具统一）。</summary>
    public static bool SameContent(string? existing, string payloadText) =>
        existing is not null && string.Equals(existing, payloadText, StringComparison.Ordinal);

    /// <summary>这份文本是不是本工具部署的载荷（含任一载荷标记）。</summary>
    public static bool IsOurs(string? text) =>
        text is not null && OpencodeMarkers.PayloadMarkers.Any(m => FileText.Count(text, m) > 0);

    /// <summary>
    /// 载荷层写入计划。纯函数（不写盘）：
    ///   · 内容逐字节相同 → WillWrite=false（Applied 也不许报 true，体积 0→0）；
    ///   · 现存文件不是自家载荷 → 需要先备份（本工具只增不毁）；
    ///   · 现存文件是自家旧载荷 → 不重复备份（备份要保留用户原文件）。
    /// </summary>
    public static OpencodePayloadPlan Plan(string configHome, string payloadText, bool skip)
    {
        var path = TargetOf(configHome);
        var existing = FileText.ReadOrNull(path);
        var beforeBytes = existing is null ? 0 : FileText.Bytes(existing);
        var afterBytes = FileText.Bytes(payloadText);

        if (skip)
            return new OpencodePayloadPlan
            {
                Path = path, Text = payloadText, Enabled = false, Existing = existing,
                WillWrite = false, BeforeBytes = beforeBytes, AfterBytes = beforeBytes,
                Note = "已跳过载荷层（SkipContextPayload）。",
            };

        if (SameContent(existing, payloadText))
            return new OpencodePayloadPlan
            {
                Path = path, Text = payloadText, Existing = existing,
                WillWrite = false, BeforeBytes = beforeBytes, AfterBytes = beforeBytes,
                Note = "已是当前文本，无需写入（幂等）。",
            };

        var ours = IsOurs(existing);
        var needsBackup = existing is not null && !ours;
        return new OpencodePayloadPlan
        {
            Path = path, Text = payloadText, Existing = existing,
            WillWrite = true, BeforeBytes = beforeBytes, AfterBytes = afterBytes,
            NeedsBackup = needsBackup,
            BackupPath = path + ".breach.bak",
            Note = needsBackup
                ? $"用户自己的 AGENTS.md 将先备份到 {path}.breach.bak。"
                : ours
                    ? "现存 AGENTS.md 是自家旧载荷：不重复备份（备份要留给用户原文件）。"
                    : "部署载荷层（原文件不存在）。",
        };
    }

    /// <summary>
    /// 载荷层还原计划。备份干净 → 逐字节还原；没有备份但盘上是自家载荷 → 删除（恢复到"不存在"）；
    /// 盘上不是自家文件 → 不动。备份污染由调用方先行拦截（抛 AnchorConflict）。
    /// </summary>
    public static OpencodePayloadRevert PlanRevert(string configHome, BackupAssessment assessment)
    {
        var path = TargetOf(configHome);
        var existing = FileText.ReadOrNull(path);

        if (assessment.Status == BackupStatus.Clean && assessment.Path is not null)
        {
            var saved = FileText.ReadOrNull(assessment.Path);
            if (saved is not null)
                return new OpencodePayloadRevert
                {
                    Path = path, BackupPath = assessment.Path, Existing = existing, RestoreText = saved,
                    Changed = !string.Equals(existing, saved, StringComparison.Ordinal),
                    Note = $"从首次备份逐字节还原：{assessment.Path} → {path}",
                };
        }

        if (existing is null)
            return new OpencodePayloadRevert
            {
                Path = path, BackupPath = assessment.Path, Existing = null, NothingToDo = true,
                Note = $"没有可还原的载荷：{path} 不存在。",
            };

        if (IsOurs(existing))
            return new OpencodePayloadRevert
            {
                Path = path, BackupPath = assessment.Path, Existing = existing, Delete = true, Changed = true,
                Note = "盘上是本工具部署的载荷且没有可信备份：删除它（恢复到出厂的不存在状态）。",
            };

        return new OpencodePayloadRevert
        {
            Path = path, BackupPath = assessment.Path, Existing = existing, NothingToDo = true,
            Note = "AGENTS.md 不是本工具部署的（不含载荷标记）：未改动。",
        };
    }
}
