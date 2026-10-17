using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets;

/// <summary>目标的运行期上下文：数据目录、注入文本、台账。</summary>
public sealed class TargetContext
{
    public required string Id { get; init; }
    public required string DataDir { get; init; }
    public required PolicyStore Policy { get; init; }
    public required LedgerStore Ledger { get; init; }
    public required IReadOnlyList<PolicyTextDef> PolicyDefs { get; init; }

    public PolicyTextDef? Def(string name) =>
        PolicyDefs.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>一次定位的结果。找不到时 Attempts 里要有"试过哪些路径"，而不是抛一个写死的路径错误。</summary>
public sealed record TargetLocation(
    bool Found,
    string? Root,
    string? Exe,
    string? Bundle,
    string? ConfigHome,
    string? Version,
    string? LocatedBy,
    IReadOnlyList<LocateAttempt> Attempts,
    IReadOnlyList<string> SniffCandidates,
    int SniffHits,
    IReadOnlyList<Note> Notes,
    // 下面三项是可选的补充信息：不填时引擎会退回到 Bundle / ConfigHome / 实时进程探测。
    // 存在的意义是把"承载系统提示词的文件"与"补丁层/profile 目录"分开——对 DSH 两者不是同一个东西。
    string? PatchFile = null,
    string? ProfileDir = null,
    IReadOnlyList<int>? RunningProcessIds = null)
{
    public static TargetLocation NotFound(IReadOnlyList<LocateAttempt> attempts, IReadOnlyList<string> sniff,
        int sniffHits, IReadOnlyList<Note> notes) =>
        new(false, null, null, null, null, null, null, attempts, sniff, sniffHits, notes);
}

/// <summary>
/// 一个目标客户端的适配器。所有目标特有知识（路径、层、补丁表、日志格式、标记表）都收在这里；
/// 引擎只负责路由。换目标 = 加一个适配器，不动 UI。
/// </summary>
public interface ITargetAdapter
{
    string Id { get; }
    string DisplayName { get; }
    TargetKind Kind { get; }
    TargetContext Ctx { get; }

    /// <summary>该目标可用的层（人话描述，用于 UI 展示与状态页分组）。</summary>
    IReadOnlyList<string> LayerDescriptions { get; }

    TargetInfo Describe(CancellationToken ct);
    TargetLocation Locate(LocateOptions? options, bool forceRefresh, CancellationToken ct);

    EngineStatus GetStatus(CancellationToken ct);

    ApplyPreview PreviewApply(ApplyOptions options, CancellationToken ct);
    ApplyResult Apply(ApplyOptions options, IProgress<EngineProgress>? progress, CancellationToken ct);
    RevertResult Revert(RevertOptions options, IProgress<EngineProgress>? progress, CancellationToken ct);

    IReadOnlyList<SessionInfo> ListSessions(CancellationToken ct);
    SessionDetail LoadSession(string sessionId, SessionLoadOptions options, CancellationToken ct);
    MarkerReport CheckMarkers(string sessionId, CancellationToken ct);
    DiffReport GetSystemPromptDiff(string? sessionId, CancellationToken ct);
    ExportResult ExportSession(string sessionId, string? outputPath, CancellationToken ct);
    ExportResult ExportSystemPrompt(string sessionId, string? outputPath, CancellationToken ct);

    IReadOnlyList<PolicyFileInfo> GetPolicyFiles(CancellationToken ct);
    PolicyFileInfo SavePolicyFile(string name, string text, CancellationToken ct);

    IReadOnlyList<ProbeDefinition> GetProbes();
}
