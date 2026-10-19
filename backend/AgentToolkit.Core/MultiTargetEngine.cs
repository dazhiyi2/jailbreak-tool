using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;
using AgentToolkit.Core.Targets;
using AgentToolkit.Core.Targets.Antigravity;
using AgentToolkit.Core.Targets.Codex;
using AgentToolkit.Core.Targets.Cursor;
using AgentToolkit.Core.Targets.Dsh;
using AgentToolkit.Core.Targets.Opencode;
using AgentToolkit.Core.Targets.Zcode;

namespace AgentToolkit.Core;

/// <summary>
/// 多目标引擎：UI 只见这一个 IAgentEngine。当前目标由 SelectTargetAsync 决定，
/// 其余所有方法都路由到该目标的适配器；换目标不动 UI，加目标只加适配器。
/// </summary>
public sealed class MultiTargetEngine : IAgentEngine
{
    private readonly Dictionary<string, ITargetAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);
    private string _currentId;

    public MultiTargetEngine(string? dataRoot = null)
    {
        var root = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentToolkit");

        Register(new DshTarget(BuildContext("dsh", root)));
        Register(new ZcodeTarget(BuildContext("zcode", root)));
        Register(new OpencodeTarget(BuildContext("opencode", root)));
        Register(new AntigravityTarget(BuildContext("antigravity", root)));
        Register(new CursorTarget(BuildContext("cursor", root)));
        Register(new CodexTarget(BuildContext("codex", root)));

        _currentId = _adapters.ContainsKey("dsh") ? "dsh" : _adapters.Keys.First();
    }

    public string CurrentTargetId => _currentId;
    public IReadOnlyList<string> TargetIds => _adapters.Keys.ToArray();

    private static TargetContext BuildContext(string id, string root)
    {
        var dataDir = Path.Combine(root, id);
        Directory.CreateDirectory(dataDir);
        var defs = id.ToLowerInvariant() switch
        {
            "dsh" => DshTarget.PolicyDefs,
            "zcode" => ZcodeTarget.PolicyDefs,
            "opencode" => OpencodeTarget.PolicyDefs,
            "cursor" => CursorTarget.PolicyDefs,
            // 必须显式列出：下面的 `_ =>` 兜底是 Antigravity，漏了 codex 会让它拿到 Antigravity 的注入文本。
            "codex" => CodexTarget.PolicyDefs,
            _ => AntigravityTarget.PolicyDefs,
        };
        var policy = new PolicyStore(dataDir);
        policy.Materialize(defs);
        return new TargetContext
        {
            Id = id,
            DataDir = dataDir,
            Policy = policy,
            PolicyDefs = defs,
            Ledger = new LedgerStore(Path.Combine(dataDir, "probe-ledger.json")),
        };
    }

    private void Register(ITargetAdapter adapter) => _adapters[adapter.Id] = adapter;

    private ITargetAdapter Current =>
        _adapters.TryGetValue(_currentId, out var a)
            ? a
            : throw new AgentEngineException(EngineErrorCode.Unsupported, $"当前目标「{_currentId}」未注册。");

    // ---------------------------------------------------------------- 多目标
    public Task<IReadOnlyList<TargetInfo>> ListTargetsAsync(CancellationToken ct = default) =>
        Run(() => (IReadOnlyList<TargetInfo>)_adapters.Values.Select(a => a.Describe(ct)).ToArray(), ct);

    public Task SelectTargetAsync(string targetId, CancellationToken ct = default) => Run(() =>
    {
        if (string.IsNullOrWhiteSpace(targetId))
            throw new AgentEngineException(EngineErrorCode.Unsupported, "目标标识为空。");
        if (!_adapters.ContainsKey(targetId))
            throw new AgentEngineException(EngineErrorCode.Unsupported,
                $"没有名为「{targetId}」的目标；可用：{string.Join(", ", _adapters.Keys)}。");
        _currentId = targetId;
        return true;
    }, ct);

    // ---------------------------------------------------------------- 导出
    public Task<ExportResult> ExportSessionAsync(string sessionId, string? outputPath, CancellationToken ct = default) =>
        Run(() => Current.ExportSession(sessionId, outputPath, ct), ct);

    public Task<ExportResult> ExportSystemPromptAsync(string sessionId, string? outputPath, CancellationToken ct = default) =>
        Run(() => Current.ExportSystemPrompt(sessionId, outputPath, ct), ct);

    // ---------------------------------------------------------------- 其余路由
    public Task<LocateResult> LocateAsync(LocateOptions options, CancellationToken ct = default) =>
        Run(() =>
        {
            var loc = Current.Locate(options, forceRefresh: true, ct);
            return ToLocateResult(Current, loc);
        }, ct);

    public Task<EngineStatus> GetStatusAsync(CancellationToken ct = default) => Run(() => Current.GetStatus(ct), ct);

    public Task<ApplyPreview> PreviewApplyAsync(ApplyOptions options, CancellationToken ct = default) =>
        Run(() => Current.PreviewApply(options, ct), ct);

    public Task<ApplyResult> ApplyAsync(ApplyOptions options, IProgress<EngineProgress>? progress = null, CancellationToken ct = default) =>
        Run(() =>
        {
            var result = Current.Apply(options, progress, ct);
            if (options.RestartClient && result.Applied) result.Notes = WithRestartNote(Current.Id, result.Notes, progress);
            return result;
        }, ct);

    public Task<RevertResult> RevertAsync(RevertOptions options, IProgress<EngineProgress>? progress = null, CancellationToken ct = default) =>
        Run(() =>
        {
            var result = Current.Revert(options, progress, ct);
            if (options.RestartClient && result.Reverted) result.Notes = WithRestartNote(Current.Id, result.Notes, progress);
            return result;
        }, ct);

    /// <summary>目标 id → 进程名。重启只在显式要求时执行，失败如实写进 Notes。</summary>
    private static string? ProcessNameOf(string targetId) => targetId.ToLowerInvariant() switch
    {
        "dsh" => "DeepSeek Harness",
        "zcode" => "ZCode",
        "opencode" => "opencode-cli",
        "antigravity" => "Antigravity",
        "cursor" => "Cursor",
        // Codex 的客户端是 ChatGPT 桌面应用，进程名是 ChatGPT（不是 codex）。
        "codex" => "ChatGPT",
        _ => null,
    };

    private static IReadOnlyList<Note> WithRestartNote(string targetId, IReadOnlyList<Note> notes, IProgress<EngineProgress>? progress)
    {
        var name = ProcessNameOf(targetId);
        if (name is null)
            return notes.Append(Notes.Warn("未能自动重启", $"不认识目标「{targetId}」对应的进程名，请手工重启客户端。", targetId)).ToArray();

        progress?.Report(new EngineProgress { Stage = "重启客户端", Message = name });
        var (ok, detail) = ProcessControl.Restart(name, null);
        return notes.Append(ok
            ? Notes.Info("已重启客户端", detail, targetId)
            : Notes.Warn("未能自动重启", detail, targetId)).ToArray();
    }

    public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(CancellationToken ct = default) =>
        Run(() => Current.ListSessions(ct), ct);

    public Task<SessionDetail> LoadSessionAsync(string sessionId, SessionLoadOptions options, CancellationToken ct = default) =>
        Run(() => Current.LoadSession(sessionId, options, ct), ct);

    public Task<MarkerReport> CheckMarkersAsync(string sessionId, CancellationToken ct = default) =>
        Run(() => Current.CheckMarkers(sessionId, ct), ct);

    public Task<DiffReport> GetSystemPromptDiffAsync(string? sessionId = null, CancellationToken ct = default) =>
        Run(() => Current.GetSystemPromptDiff(sessionId, ct), ct);

    // 注入方案选择器（保留名 scheme）：目标适配器通过 StatusBuilder 提供它（<dataDir>\policy\scheme.txt）。
    // 这里再显式兜底一次，保证界面 / CLI 无论走哪个适配器都拿得到、也切得动当前方案。
    public Task<IReadOnlyList<PolicyFileInfo>> GetPolicyFilesAsync(CancellationToken ct = default) =>
        Run<IReadOnlyList<PolicyFileInfo>>(() =>
        {
            var files = Current.GetPolicyFiles(ct).ToList();
            if (!files.Any(f => string.Equals(f.Name, SchemeText.EntryName, StringComparison.OrdinalIgnoreCase)))
                files.Insert(0, StatusBuilder.SchemeFile(Current.Ctx));
            return files;
        }, ct);

    public Task<PolicyFileInfo> SavePolicyFileAsync(string name, string text, CancellationToken ct = default) =>
        Run(() =>
        {
            if (!string.Equals(name, SchemeText.EntryName, StringComparison.OrdinalIgnoreCase))
                return Current.SavePolicyFile(name, text, ct);

            // 方案选择器统一在引擎这一层落盘：非法值 fail-closed（不猜、不写），合法值规范化后写 scheme.txt。
            if (!SchemeText.TryParse(text, out var scheme))
                throw new AgentEngineException(EngineErrorCode.VerifyProblems,
                    $"注入方案只认 own / protocol（收到「{(text ?? "").Trim()}」）：拒绝写入。",
                    new[]
                    {
                        Notes.Info("注入方案",
                            "own = 本工具方案（现有注入文本，一个字不改）；protocol = gpt-instruct 原文（逐字节不动）。",
                            Current.Ctx.Policy.SchemePath),
                    });

            Current.Ctx.Schemes().Write(scheme);
            return StatusBuilder.SchemeFile(Current.Ctx);
        }, ct);

    public Task<IReadOnlyList<ProbeDefinition>> GetProbesAsync(CancellationToken ct = default) =>
        Run(() => Current.GetProbes(), ct);

    public Task SaveProbeVerdictAsync(ProbeVerdict verdict, CancellationToken ct = default) => Run(() =>
    {
        if (string.IsNullOrWhiteSpace(verdict.ReasoningExcerpt))
            throw new AgentEngineException(EngineErrorCode.VerifyProblems, "必须填写推理原文摘录：判读依据是内部推理，不是表面措辞。");
        Current.Ctx.Ledger.Append(verdict);
        return true;
    }, ct);

    public Task<IReadOnlyList<ProbeVerdict>> GetProbeVerdictsAsync(CancellationToken ct = default) =>
        Run(() => Current.Ctx.Ledger.Load(), ct);

    // ---------------------------------------------------------------- 工具
    public static LocateResult ToLocateResult(ITargetAdapter adapter, TargetLocation loc)
    {
        var processName = ProcessNameOf(adapter.Id);
        return new LocateResult
        {
            Found = loc.Found,
            Kind = adapter.Kind,
            TargetId = adapter.Id,
            TargetDisplayName = adapter.DisplayName,
            Root = loc.Root,
            ExecutablePath = loc.Exe,
            AsarPath = loc.Bundle,
            ConfigHome = loc.ConfigHome,
            // 补丁层文件与承载系统提示词的文件不是同一个东西（DSH 是 cordis.patch.yml vs app.asar）
            ProfileDir = loc.ProfileDir ?? loc.ConfigHome,
            PatchFilePath = loc.PatchFile ?? loc.Bundle,
            SessionsDir = loc.ConfigHome is null ? null : Path.Combine(loc.ConfigHome, "sessions"),
            Version = loc.Version,
            LocatedBy = loc.LocatedBy,
            ProcessName = processName,
            RunningProcessIds = loc.RunningProcessIds
                ?? (processName is null ? Array.Empty<int>() : ProcessControl.Pids(processName)),
            Attempts = loc.Attempts,
            SniffCandidates = loc.SniffCandidates,
            SniffCandidateHits = loc.SniffHits,
            Notes = loc.Notes,
        };
    }

    private static Task<T> Run<T>(Func<T> work, CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try { return work(); }
            catch (AgentEngineException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException ex)
            {
                throw new AgentEngineException(EngineErrorCode.IoError, $"没有权限访问目标文件：{ex.Message}", null, ex);
            }
            catch (IOException ex)
            {
                throw new AgentEngineException(EngineErrorCode.IoError, $"读写失败：{ex.Message}", null, ex);
            }
            catch (Exception ex)
            {
                throw new AgentEngineException(EngineErrorCode.Unknown, ex.Message, null, ex);
            }
        }, ct);
}
