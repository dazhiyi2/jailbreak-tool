// FROZEN CONTRACT v1 — 由后端负责人维护。UI 侧只读。
// 需要新字段时写进 UI-NOTES.md 的「契约缺口」一节，不要在这里加。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentToolkit.Contracts
{
    public enum Severity { Info = 0, Warning = 1, Error = 2 }

    public sealed class Note
    {
        public Severity Severity { get; set; } = Severity.Info;
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string? Source { get; set; }
    }

    public enum EngineErrorCode
    {
        Unknown = 1, VerifyProblems = 2, SelfCheckFailed = 3, TargetNotFound = 4,
        AnchorConflict = 6, TargetFileMissing = 7, NotApplied = 8, IoError = 9, Unsupported = 10,
    }

    public sealed class AgentEngineException : Exception
    {
        public EngineErrorCode Code { get; }
        public IReadOnlyList<Note> Notes { get; }
        public AgentEngineException(EngineErrorCode code, string message,
            IReadOnlyList<Note>? notes = null, Exception? inner = null) : base(message, inner)
        { Code = code; Notes = notes ?? Array.Empty<Note>(); }
    }

    public sealed class EngineProgress
    {
        public string Stage { get; set; } = "";
        public string Message { get; set; } = "";
        public double? Fraction { get; set; }
    }

    // ---------------- 探测 ----------------
    public enum TargetKind { Unknown, PlaintextBundle, PackagedElectron, CloudAssembled, CustomHarness }

    public sealed class LocateOptions
    {
        public string? ExplicitRoot { get; set; }
        public string? ExplicitConfigHome { get; set; }
        public bool IgnoreMemory { get; set; }
        public bool FullSniff { get; set; }
    }

    public sealed class LocateAttempt
    {
        public int Order { get; set; }
        public string Level { get; set; } = "";     // cli/env/memory/process/registry/shortcut/sniff
        public string Candidate { get; set; } = "";
        public bool Hit { get; set; }
        public string? Note { get; set; }
    }

    public sealed class LocateResult
    {
        public bool Found { get; set; }
        public TargetKind Kind { get; set; }
        // v2 追加：多目标（dsh / zcode）——当前生效目标的标识与显示名
        public string? TargetId { get; set; }
        public string? TargetDisplayName { get; set; }
        public string? Root { get; set; }
        public string? ExecutablePath { get; set; }
        public string? AsarPath { get; set; }
        public string? ConfigHome { get; set; }
        public string? ProfileDir { get; set; }
        public string? PatchFilePath { get; set; }
        public string? SessionsDir { get; set; }
        public string? Version { get; set; }
        public string? LocatedBy { get; set; }
        public string? ProcessName { get; set; }
        public IReadOnlyList<int> RunningProcessIds { get; set; } = Array.Empty<int>();
        public IReadOnlyList<LocateAttempt> Attempts { get; set; } = Array.Empty<LocateAttempt>();
        public IReadOnlyList<string> SniffCandidates { get; set; } = Array.Empty<string>();
        public int SniffCandidateHits { get; set; }
        public IReadOnlyList<Note> Notes { get; set; } = Array.Empty<Note>();
    }

    // ---------------- 状态 ----------------
    public enum LayerKind { SystemPrompt, PresetOverride, ContextPayload, PolicyText }

    // 四种状态，状态页必须都能渲染
    public enum LayerState
    {
        Unknown = 0, NotApplied = 1, Applied = 2,
        AnchorMissing = 3,      // 锚点失配（多半被升级覆盖）
        AnchorAmbiguous = 4,    // 锚点不唯一 → 拒绝写入
        TargetAbsent = 5,
    }

    public enum BackupHealth { Unknown = 0, Absent = 1, Clean = 2, Contaminated = 3, Unreadable = 4 }

    public sealed class BackupInfo
    {
        public BackupHealth Health { get; set; } = BackupHealth.Absent;
        public string? Path { get; set; }
        public long SizeBytes { get; set; }
        public string? Sha256 { get; set; }
        public DateTimeOffset? SavedAt { get; set; }
        public string? SupersededPath { get; set; }
        public string? ContaminatedCopyPath { get; set; }
    }

    public sealed class LayerStatus
    {
        public LayerKind Kind { get; set; }
        public string DisplayName { get; set; } = "";
        public string? TargetPath { get; set; }
        public LayerState State { get; set; }
        public string StateDetail { get; set; } = "";
        public long SizeBytes { get; set; }
        public long? CapBytes { get; set; }
        public bool ParseOk { get; set; } = true;
        public BackupInfo Backup { get; set; } = new BackupInfo();
        public IReadOnlyList<Note> Notes { get; set; } = Array.Empty<Note>();
        // 「装上了」不等于「生效了」，这两个字段必须单独显示
        public bool InstalledButNotEffective { get; set; }
        public string? EffectivenessEvidence { get; set; }
    }

    public sealed class EngineStatus
    {
        public LocateResult Target { get; set; } = new LocateResult();
        public IReadOnlyList<LayerStatus> Layers { get; set; } = Array.Empty<LayerStatus>();
        public bool ClientRunning { get; set; }
        public IReadOnlyList<int> RunningProcessIds { get; set; } = Array.Empty<int>();
        public bool RestartRequired { get; set; }
        public MarkerReport? LastMarkerReport { get; set; }
        public IReadOnlyList<Note> Notes { get; set; } = Array.Empty<Note>();
        public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.Now;
    }

    // ---------------- 注入 / 还原 ----------------
    public sealed class ApplyOptions
    {
        public bool DryRun { get; set; }
        public bool Force { get; set; }
        public string? PolicyPrefixPath { get; set; }
        public string? PolicySuffixPath { get; set; }
        public string? PayloadPath { get; set; }
        public bool SkipContextPayload { get; set; }
        public bool SkipSystemPrompt { get; set; }
        public bool RestartClient { get; set; }
    }

    public sealed class PlannedChange
    {
        public string Path { get; set; } = "";
        public LayerKind Layer { get; set; }
        public bool WillWrite { get; set; }
        public long BeforeBytes { get; set; }
        public long AfterBytes { get; set; }
        public string? BackupPath { get; set; }
        public string? DiffText { get; set; }
        public IReadOnlyList<Note> Notes { get; set; } = Array.Empty<Note>();
    }

    public sealed class ApplyPreview
    {
        public bool CanApply { get; set; }
        public string PreviewId { get; set; } = "";
        public IReadOnlyList<PlannedChange> Changes { get; set; } = Array.Empty<PlannedChange>();
        public long TotalBeforeBytes { get; set; }
        public long TotalAfterBytes { get; set; }
        public IReadOnlyList<Note> Notes { get; set; } = Array.Empty<Note>();
    }

    public sealed class ApplyResult
    {
        public bool Applied { get; set; }
        public IReadOnlyList<PlannedChange> Changes { get; set; } = Array.Empty<PlannedChange>();
        public bool RestartRequired { get; set; } = true;
        public string VerificationHint { get; set; } = "";
        public IReadOnlyList<Note> Notes { get; set; } = Array.Empty<Note>();
    }

    public sealed class RevertOptions
    {
        public bool DryRun { get; set; }
        public bool RemoveBackups { get; set; }
        public bool SkipContextPayload { get; set; }
        public bool SkipSystemPrompt { get; set; }
        public bool RestartClient { get; set; }
    }

    public sealed class RevertResult
    {
        public bool Reverted { get; set; }
        public IReadOnlyList<PlannedChange> Changes { get; set; } = Array.Empty<PlannedChange>();
        public bool ReadBackClean { get; set; }          // 还原后回读无残留
        public bool? MatchesOriginal { get; set; }       // 与首次备份哈希比对
        public string? OriginalSha256 { get; set; }
        public string? CurrentSha256 { get; set; }
        public IReadOnlyList<Note> Notes { get; set; } = Array.Empty<Note>();
    }

    // ---------------- 日志 / 判读 ----------------
    public sealed class SessionInfo
    {
        public string Id { get; set; } = "";
        public string Path { get; set; } = "";
        public string? Workspace { get; set; }
        public long SizeBytes { get; set; }
        public DateTimeOffset ModifiedAt { get; set; }
        public int? TurnCount { get; set; }
    }

    public sealed class SessionLoadOptions
    {
        public int MaxTurns { get; set; } = 60;
        public int MaxCharsPerText { get; set; } = 20000;
        public bool IncludeSystemPrompt { get; set; } = true;
        public bool IncludeReasoning { get; set; } = true;
    }

    public sealed class TurnRecord
    {
        public int Index { get; set; }
        public string Role { get; set; } = "";
        public string? Text { get; set; }
        public string? ReasoningText { get; set; }   // 模型内部推理原文，判读只看这里
        public DateTimeOffset? At { get; set; }
        public bool Truncated { get; set; }
    }

    public sealed class SessionDetail
    {
        public SessionInfo Info { get; set; } = new SessionInfo();
        public string? SystemPromptText { get; set; }
        public IReadOnlyList<TurnRecord> Turns { get; set; } = Array.Empty<TurnRecord>();
        public IReadOnlyList<Note> Notes { get; set; } = Array.Empty<Note>();
        public string? ExportedJsonlPath { get; set; }
    }

    public enum MarkerLayer { SystemPrompt = 0, ContextPayload = 1, WrapperOrGuidance = 2, FirstParty = 3, PolicySignal = 4 }

    public sealed class MarkerHit
    {
        public string Marker { get; set; } = "";
        public int Count { get; set; }
        public MarkerLayer Layer { get; set; }
    }

    public sealed class MarkerReport
    {
        public string SessionId { get; set; } = "";
        public IReadOnlyList<MarkerHit> Hits { get; set; } = Array.Empty<MarkerHit>();
        public bool SystemPromptLayerPresent { get; set; }
        public bool ContextPayloadLayerPresent { get; set; }
        public int TotalChars { get; set; }
        public string Verdict { get; set; } = "";   // 命中 0 = 没生效
        public string? ExportedSystemPromptPath { get; set; }
    }

    public enum DiffKind { Context = 0, Added = 1, Removed = 2 }

    public sealed class DiffLine
    {
        public DiffKind Kind { get; set; }
        public string? LeftLineNo { get; set; }
        public string? RightLineNo { get; set; }
        public string Text { get; set; } = "";
    }

    public sealed class DiffReport
    {
        public string Title { get; set; } = "";
        public string? BeforeSource { get; set; }
        public string? AfterSource { get; set; }
        public string BeforeText { get; set; } = "";
        public string AfterText { get; set; } = "";
        public IReadOnlyList<DiffLine> Lines { get; set; } = Array.Empty<DiffLine>();
        public int AddedLines { get; set; }
        public int RemovedLines { get; set; }
        public IReadOnlyList<MarkerHit> MarkerDelta { get; set; } = Array.Empty<MarkerHit>();
    }

    // ---------------- 注入文本 ----------------
    public sealed class PolicyFileInfo
    {
        public string Name { get; set; } = "";        // prefix / suffix / payload
        public string DisplayName { get; set; } = "";
        public string Path { get; set; } = "";
        public string Text { get; set; } = "";
        public long SizeBytes { get; set; }
        public long? CapBytes { get; set; }
        public bool Editable { get; set; } = true;
        public IReadOnlyList<Note> Notes { get; set; } = Array.Empty<Note>();
    }

    // ---------------- 行为探针台账 ----------------
    public enum ProbeVerdictLevel { NotRun = 0, Failed = 1, HalfBroken = 2, Broken = 3 }

    public sealed class ProbeDefinition
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string HowTo { get; set; } = "";
        public string PassCriteria { get; set; } = "";
        public bool IsDecisive { get; set; }
    }

    public sealed class ProbeVerdict
    {
        public string ProbeId { get; set; } = "";
        public ProbeVerdictLevel Level { get; set; }
        public string? ReasoningExcerpt { get; set; }
        public string? SessionId { get; set; }
        public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
        public string? Note { get; set; }
    }

    // ---------------- 多目标（v2 追加） ----------------
    /// <summary>一个可操作的目标客户端。引擎同时支持多个目标，靠 SelectTargetAsync 切换。</summary>
    public sealed class TargetInfo
    {
        public string Id { get; set; } = "";            // "dsh" / "zcode"
        public string DisplayName { get; set; } = "";   // "DeepSeek Harness" / "ZCode"
        public TargetKind Kind { get; set; }
        public bool Installed { get; set; }
        public string? InstallRoot { get; set; }
        public string? ConfigHome { get; set; }
        public string? BundlePath { get; set; }         // 承载系统提示词的文件（asar / cjs）
        public string? Version { get; set; }
        public string? LocatedBy { get; set; }
        public string? Note { get; set; }
        public IReadOnlyList<string> Layers { get; set; } = Array.Empty<string>();  // 该目标可用的层说明
    }

    // ---------------- 导出（v2 追加，补 v1 缺口） ----------------
    public sealed class ExportResult
    {
        public bool Exported { get; set; }
        public string? Path { get; set; }
        public long SizeBytes { get; set; }
        public IReadOnlyList<Note> Notes { get; set; } = Array.Empty<Note>();
    }

    // ---------------- 唯一接缝 ----------------
    public interface IAgentEngine
    {
        // 多目标：列出全部可识别目标（含未安装的，用于诊断）
        Task<IReadOnlyList<TargetInfo>> ListTargetsAsync(CancellationToken ct = default);
        // 切换当前目标；其后所有调用都作用于它
        Task SelectTargetAsync(string targetId, CancellationToken ct = default);

        // 导出（v1 只给路径、无法发起导出；这里补上）
        Task<ExportResult> ExportSessionAsync(string sessionId, string? outputPath, CancellationToken ct = default);
        Task<ExportResult> ExportSystemPromptAsync(string sessionId, string? outputPath, CancellationToken ct = default);

        Task<LocateResult> LocateAsync(LocateOptions options, CancellationToken ct = default);
        Task<EngineStatus> GetStatusAsync(CancellationToken ct = default);

        Task<ApplyPreview> PreviewApplyAsync(ApplyOptions options, CancellationToken ct = default);
        Task<ApplyResult> ApplyAsync(ApplyOptions options, IProgress<EngineProgress>? progress = null, CancellationToken ct = default);
        Task<RevertResult> RevertAsync(RevertOptions options, IProgress<EngineProgress>? progress = null, CancellationToken ct = default);

        Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(CancellationToken ct = default);
        Task<SessionDetail> LoadSessionAsync(string sessionId, SessionLoadOptions options, CancellationToken ct = default);
        Task<MarkerReport> CheckMarkersAsync(string sessionId, CancellationToken ct = default);
        Task<DiffReport> GetSystemPromptDiffAsync(string? sessionId = null, CancellationToken ct = default);

        Task<IReadOnlyList<PolicyFileInfo>> GetPolicyFilesAsync(CancellationToken ct = default);
        Task<PolicyFileInfo> SavePolicyFileAsync(string name, string text, CancellationToken ct = default);

        Task<IReadOnlyList<ProbeDefinition>> GetProbesAsync(CancellationToken ct = default);
        Task SaveProbeVerdictAsync(ProbeVerdict verdict, CancellationToken ct = default);
        Task<IReadOnlyList<ProbeVerdict>> GetProbeVerdictsAsync(CancellationToken ct = default);
    }
}
