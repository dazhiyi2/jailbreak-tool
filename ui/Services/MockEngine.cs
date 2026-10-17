using AgentToolkit.Contracts;
using System.Text;

namespace AgentToolkit.Ui.Services;

public enum MockScenario
{
    NotLocated,
    StockClean,
    AppliedHealthy,
    UpgradeOverwritten,
    AnchorAmbiguous,
    BackupContaminated,
    NoSessions,
    RichSession,
}

// 纯内存演示引擎。路径只是展示文本，不进行文件、进程、网络或配置目录访问。
public sealed class MockEngine : IAgentEngine
{
    private const string Root = @"C:\演示客户端";
    private const string Home = @"C:\演示配置";
    private const string OriginalHash = "7a24e36d8f901bc2548b62816d43f70f9a18c0d2976e35b4f8a9124c6d0e53ab";
    private const string ChangedHash = "e64f3a7b890c21d4f5b6a09e3d71c852184fe6932abc580d79e13f406ca82b57";
    private MockScenario _scenario = MockScenario.AppliedHealthy;
    private readonly Dictionary<string, PolicyFileInfo> _policies = new();
    private readonly List<ProbeVerdict> _verdicts = new();
    private readonly Dictionary<LayerKind, LayerState> _layerOverrides = new();
    private readonly Dictionary<LayerKind, BackupHealth> _backupOverrides = new();
    private readonly Dictionary<LayerKind, bool> _markerOverrides = new();
    private bool? _restartRequired;
    private bool HasSessions => _scenario is not (MockScenario.NoSessions or MockScenario.NotLocated);
    private bool HasMarkerHits => _scenario == MockScenario.AppliedHealthy || _scenario == MockScenario.NoSessions;

    public MockEngine() => SetScenario(MockScenario.AppliedHealthy);

    public void SetScenario(MockScenario s)
    {
        _scenario = s;
        _layerOverrides.Clear(); _backupOverrides.Clear(); _markerOverrides.Clear(); _restartRequired = null;
        _verdicts.Clear();
        _policies.Clear();
        foreach (var (name, title, text) in new[]
        {
            ("prefix", "系统前缀", "【模拟文本】使用简洁中文说明当前任务。"),
            ("suffix", "系统后缀", "【模拟文本】保留可核验的结果与测试记录。"),
            ("payload", "上下文载荷", "【模拟文本】示例工作区的格式约定与占位符说明。"),
        })
            _policies[name] = new PolicyFileInfo
            {
                Name = name, DisplayName = title, Text = text, Path = $@"{Home}\policy\{name}.txt",
                SizeBytes = Encoding.UTF8.GetByteCount(text), CapBytes = name == "payload" ? 32768 : 16384,
                Editable = name != "suffix" || s != MockScenario.AnchorAmbiguous,
                Notes = new[] { SimulationNote() },
            };
        if (s != MockScenario.StockClean && s != MockScenario.NotLocated)
            _verdicts.Add(new ProbeVerdict
            {
                ProbeId = "A", Level = ProbeVerdictLevel.HalfBroken,
                ReasoningExcerpt = "【模拟摘录】先检查输入参数，再列出验证步骤。",
                SessionId = "demo-session-001", At = DateTimeOffset.Now.AddMinutes(-18), Note = "模拟记录，不是实测结论。",
            });
    }

    private static Note SimulationNote() => new()
    {
        Title = "模拟数据", Detail = "此结果由纯内存 MockEngine 提供，未访问本机客户端。", Source = "界面演示",
    };

    private static Note Diagnostic(string title, string detail, Severity severity = Severity.Warning) => new()
    { Title = title, Detail = detail, Severity = severity, Source = "模拟诊断" };

    private async Task PauseAsync(CancellationToken ct) => await Task.Delay(240, ct);

    private LocateResult Target(LocateOptions? options = null)
    {
        var found = _scenario != MockScenario.NotLocated;
        var root = string.IsNullOrWhiteSpace(options?.ExplicitRoot) ? Root : options.ExplicitRoot;
        var levels = new[] { "cli", "env", "memory", "process", "registry", "shortcut", "sniff" };
        return new LocateResult
        {
            Found = found, Kind = found ? TargetKind.PlaintextBundle : TargetKind.Unknown,
            Root = found ? root : "未定位", ExecutablePath = found ? $@"{root}\DemoClient.exe" : "未定位",
            AsarPath = "不适用：模拟明文客户端", ConfigHome = found ? options?.ExplicitConfigHome ?? Home : "未定位",
            ProfileDir = found ? $@"{Home}\profiles\default" : "未定位",
            PatchFilePath = found ? $@"{root}\resources\client.cjs" : "未定位",
            SessionsDir = found ? $@"{Home}\sessions" : "未定位",
            Version = found ? (_scenario == MockScenario.UpgradeOverwritten ? "2.4.0（模拟升级）" : "2.3.1（模拟）") : "未知",
            LocatedBy = found ? "模拟进程候选" : "全部级别未命中", ProcessName = found ? "模拟客户端" : "无",
            RunningProcessIds = found ? new[] { 24680, 24684 } : Array.Empty<int>(),
            Attempts = levels.Select((level, index) => new LocateAttempt
            {
                Order = index + 1, Level = level,
                Candidate = level == "cli" ? options?.ExplicitRoot ?? "未指定手动路径" : $@"{Root}\候选-{level}",
                Hit = found && (level == "process" || level == "sniff" && options?.FullSniff == true),
                Note = level == "memory" && options?.IgnoreMemory == true ? "已按选项跳过记忆" : found && level == "process" ? "模拟命中" : "模拟未命中",
            }).ToArray(),
            SniffCandidates = options?.FullSniff == true ? new[] { $@"{Root}\候选甲", $@"{Root}\候选乙" } : Array.Empty<string>(),
            SniffCandidateHits = found && options?.FullSniff == true ? 1 : 0,
            Notes = found ? new[] { SimulationNote() } : new[]
            { SimulationNote(), Diagnostic("未找到客户端", "七级候选均未命中。请检查手动根路径或开启全盘嗅探后重试。") },
        };
    }

    // ---------------- v2 契约追加：多目标与导出（演示实现，不碰真实文件） ----------------
    public async Task<IReadOnlyList<TargetInfo>> ListTargetsAsync(CancellationToken ct = default)
    {
        await Task.Delay(120, ct);
        return new[]
        {
            new TargetInfo
            {
                Id = "dsh", DisplayName = "DeepSeek Harness", Kind = TargetKind.PackagedElectron,
                Installed = _scenario != MockScenario.NotLocated, InstallRoot = Root, ConfigHome = Home,
                BundlePath = @"C:\演示客户端\resources\app.asar", Version = "2.3.1（模拟）", LocatedBy = "memory",
                Layers = new[] { "系统提示词层（模拟）", "上下文指令层（模拟）" },
            },
            new TargetInfo
            {
                Id = "zcode", DisplayName = "ZCode", Kind = TargetKind.PlaintextBundle,
                Installed = false, Note = "演示数据：本机未安装",
                Layers = new[] { "宿主补丁层（模拟）", "上下文载荷层（模拟）" },
            },
        };
    }

    public async Task SelectTargetAsync(string targetId, CancellationToken ct = default) => await Task.Delay(80, ct);

    public async Task<ExportResult> ExportSessionAsync(string sessionId, string? outputPath, CancellationToken ct = default)
    {
        await Task.Delay(200, ct);
        return new ExportResult
        {
            Exported = true, Path = outputPath ?? @"C:\演示导出\session-" + sessionId + ".jsonl", SizeBytes = 286720,
            Notes = new[] { SimulationNote() },
        };
    }

    public async Task<ExportResult> ExportSystemPromptAsync(string sessionId, string? outputPath, CancellationToken ct = default)
    {
        await Task.Delay(200, ct);
        return new ExportResult
        {
            Exported = true, Path = outputPath ?? @"C:\演示导出\system-" + sessionId + ".txt", SizeBytes = 5982,
            Notes = new[] { SimulationNote() },
        };
    }

    public async Task<LocateResult> LocateAsync(LocateOptions options, CancellationToken ct = default)
    { await PauseAsync(ct); return Target(options); }

    public async Task<EngineStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await PauseAsync(ct);
        var found = _scenario != MockScenario.NotLocated;
        return new EngineStatus
        {
            Target = Target(), ClientRunning = found, RunningProcessIds = found ? new[] { 24680, 24684 } : Array.Empty<int>(),
            RestartRequired = _restartRequired ?? (_scenario is MockScenario.UpgradeOverwritten or MockScenario.BackupContaminated),
            Layers = new[] { Layer(LayerKind.SystemPrompt), Layer(LayerKind.ContextPayload) },
            LastMarkerReport = HasSessions ? Markers("demo-session-001") : null,
            Notes = Target().Notes, CheckedAt = DateTimeOffset.Now,
        };
    }

    private LayerStatus Layer(LayerKind kind)
    {
        var system = kind == LayerKind.SystemPrompt;
        var state = _scenario switch
        {
            MockScenario.NotLocated => LayerState.TargetAbsent,
            MockScenario.StockClean => LayerState.NotApplied,
            MockScenario.UpgradeOverwritten when system => LayerState.AnchorMissing,
            MockScenario.AnchorAmbiguous when system => LayerState.AnchorAmbiguous,
            _ => LayerState.Applied,
        };
        var health = _scenario switch
        {
            MockScenario.NotLocated => BackupHealth.Unknown,
            MockScenario.StockClean => BackupHealth.Absent,
            MockScenario.BackupContaminated => BackupHealth.Contaminated,
            _ => BackupHealth.Clean,
        };
        if (_layerOverrides.TryGetValue(kind, out var overriddenState)) state = overriddenState;
        if (_backupOverrides.TryGetValue(kind, out var overriddenHealth)) health = overriddenHealth;
        var path = system ? $@"{Root}\resources\client.cjs" : $@"{Home}\context.txt";
        return new LayerStatus
        {
            Kind = kind, DisplayName = system ? "系统提示词层" : "上下文指令层", TargetPath = path,
            State = state, StateDetail = state switch
            {
                LayerState.TargetAbsent => "未定位目标，无法检查。",
                LayerState.NotApplied => "出厂状态，尚未写入。",
                LayerState.AnchorMissing => "升级覆盖了原有锚点，需重新探测。",
                LayerState.AnchorAmbiguous => "锚点命中 2 处，拒绝写入。",
                _ => "模拟文件包含已安装标记。",
            },
            SizeBytes = system ? 1572864 : 12288, CapBytes = system ? null : 32768, ParseOk = true,
            Backup = new BackupInfo
            {
                Health = health, Path = health is BackupHealth.Absent or BackupHealth.Unknown ? "未建立备份" : path + ".bak",
                SizeBytes = health is BackupHealth.Absent or BackupHealth.Unknown ? 0 : system ? 1564672 : 4096,
                Sha256 = health is BackupHealth.Absent or BackupHealth.Unknown ? "尚无备份哈希" : health == BackupHealth.Contaminated ? ChangedHash : OriginalHash,
                SavedAt = health is BackupHealth.Absent or BackupHealth.Unknown ? null : DateTimeOffset.Now.AddDays(-2),
                SupersededPath = _scenario == MockScenario.UpgradeOverwritten ? path + ".bak.superseded" : "未另存旧备份",
                ContaminatedCopyPath = health == BackupHealth.Contaminated ? path + ".bak.contaminated" : "未另存污染备份",
            },
            InstalledButNotEffective = !system && _scenario == MockScenario.BackupContaminated && state == LayerState.Applied && !HasLayerMarker(kind),
            EffectivenessEvidence = _scenario == MockScenario.BackupContaminated && !system && state == LayerState.Applied && !HasLayerMarker(kind)
                ? "文件已安装；模拟会话上下文标记命中 0，未生效。"
                : HasLayerMarker(kind) ? "模拟会话标记存在；这是演示证据。" : "当前没有生效证据，请检查会话标记。",
            Notes = state == LayerState.AnchorAmbiguous ? new[] { Diagnostic("拒绝写入", "锚点不唯一，强制选项也不能解除此冲突。", Severity.Error) }
                : health == BackupHealth.Contaminated ? new[] { Diagnostic("备份污染", "备份含模拟安装标记，不能据此证明原始状态。") }
                : Array.Empty<Note>(),
        };
    }

    private PlannedChange[] Changes(bool revert, bool skipSystem = false, bool skipContext = false) =>
        new[] { LayerKind.SystemPrompt, LayerKind.ContextPayload }
        .Where(kind => kind == LayerKind.SystemPrompt ? !skipSystem : !skipContext)
        .Select(kind => Change(kind, revert)).ToArray();

    private PlannedChange Change(LayerKind kind, bool revert)
    {
        var state = Layer(kind).State;
        var willWrite = revert ? state is LayerState.Applied or LayerState.AnchorMissing or LayerState.AnchorAmbiguous : state is LayerState.NotApplied or LayerState.AnchorMissing;
        var before = kind == LayerKind.SystemPrompt ? 1572864 : 12288;
        return new PlannedChange
        {
            Layer = kind, Path = Layer(kind).TargetPath!, WillWrite = willWrite,
            BeforeBytes = before,
            AfterBytes = before + (willWrite ? revert ? -2048 : 2048 : 0),
            BackupPath = Layer(kind).Backup.Path,
            DiffText = !willWrite ? " 【模拟】当前已是目标状态，无文本变更。" : revert ? "--- 当前（模拟）\n+++ 原始（模拟）\n-【模拟】新增配置\n 【模拟】原始内容" : "--- 原始（模拟）\n+++ 计划（模拟）\n 【模拟】原始内容\n+【模拟】新增配置",
            Notes = new[] { SimulationNote() },
        };
    }

    public async Task<ApplyPreview> PreviewApplyAsync(ApplyOptions options, CancellationToken ct = default)
    {
        await PauseAsync(ct);
        var changes = _scenario == MockScenario.NotLocated ? Array.Empty<PlannedChange>() : Changes(false, options.SkipSystemPrompt, options.SkipContextPayload);
        var allowed = _scenario is not (MockScenario.NotLocated or MockScenario.AnchorAmbiguous) && !(options.SkipSystemPrompt && options.SkipContextPayload);
        return new ApplyPreview
        {
            CanApply = allowed, PreviewId = "mock-preview-" + _scenario,
            Changes = changes,
            TotalBeforeBytes = changes.Sum(c => c.BeforeBytes), TotalAfterBytes = changes.Sum(c => c.AfterBytes),
            Notes = allowed ? new[] { SimulationNote() } : new[]
            { SimulationNote(), Diagnostic("无法执行", _scenario == MockScenario.NotLocated ? "请先定位目标。" : _scenario == MockScenario.AnchorAmbiguous ? "锚点不唯一，拒绝写入。" : "至少选择一个处理层。", Severity.Error) },
        };
    }

    public async Task<ApplyResult> ApplyAsync(ApplyOptions options, IProgress<EngineProgress>? progress = null, CancellationToken ct = default)
    {
        var preview = await PreviewApplyAsync(options, ct);
        if (!preview.CanApply) throw new AgentEngineException(_scenario == MockScenario.NotLocated ? EngineErrorCode.TargetNotFound : _scenario == MockScenario.AnchorAmbiguous ? EngineErrorCode.AnchorConflict : EngineErrorCode.VerifyProblems, "模拟引擎拒绝执行。", preview.Notes);
        await StagesAsync(progress, ct, "验证预览", "模拟备份", "模拟写入", "模拟回读");
        var applied = !options.DryRun;
        if (applied)
        {
            foreach (var change in preview.Changes.Where(c => c.WillWrite))
            {
                _layerOverrides[change.Layer] = LayerState.Applied;
                _backupOverrides[change.Layer] = BackupHealth.Clean;
                _markerOverrides[change.Layer] = true;
            }
            _restartRequired = !options.RestartClient;
        }
        return new ApplyResult
        {
            Applied = applied, Changes = preview.Changes, RestartRequired = applied && !options.RestartClient,
            VerificationHint = options.DryRun ? "模拟预览结束，未变更内存状态。" : "模拟执行完成；真实引擎接入后需重启客户端并检查新会话标记。",
            Notes = new[] { SimulationNote() },
        };
    }

    public async Task<RevertResult> RevertAsync(RevertOptions options, IProgress<EngineProgress>? progress = null, CancellationToken ct = default)
    {
        if (_scenario == MockScenario.NotLocated) throw new AgentEngineException(EngineErrorCode.TargetNotFound, "模拟目标未定位。", Target().Notes);
        var changes = Changes(true, options.SkipSystemPrompt, options.SkipContextPayload);
        var contaminated = _scenario == MockScenario.BackupContaminated;
        await StagesAsync(progress, ct, "检查备份", "模拟逆向还原", "模拟回读", "模拟哈希比较");
        if (!options.DryRun)
        {
            foreach (var change in changes.Where(c => c.WillWrite))
            {
                _layerOverrides[change.Layer] = LayerState.NotApplied;
                _markerOverrides[change.Layer] = false;
                if (options.RemoveBackups) _backupOverrides[change.Layer] = BackupHealth.Absent;
            }
            _restartRequired = !options.RestartClient;
        }
        return new RevertResult
        {
            Reverted = !options.DryRun, Changes = changes, ReadBackClean = !options.DryRun,
            MatchesOriginal = options.DryRun || contaminated ? null : true,
            OriginalSha256 = contaminated ? "原始哈希不可核验：模拟备份已污染" : OriginalHash,
            CurrentSha256 = options.DryRun ? ChangedHash : OriginalHash,
            Notes = contaminated ? new[] { SimulationNote(), Diagnostic("原始哈希无法核验", "模拟逆向撤销可回读，但污染备份不能作为原始证据。") } : new[] { SimulationNote() },
        };
    }

    private static async Task StagesAsync(IProgress<EngineProgress>? progress, CancellationToken ct, params string[] stages)
    {
        for (var index = 0; index < stages.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new EngineProgress { Stage = stages[index], Message = stages[index] + "（纯内存）", Fraction = (double)index / stages.Length });
            await Task.Delay(400, ct);
        }
        ct.ThrowIfCancellationRequested();
        progress?.Report(new EngineProgress { Stage = "完成", Message = "模拟操作完成", Fraction = 1 });
    }

    private SessionInfo[] Sessions() => !HasSessions ? Array.Empty<SessionInfo>() : new[]
    {
        new SessionInfo { Id = "demo-session-001", Path = $@"{Home}\sessions\demo-session-001.jsonl", Workspace = @"C:\演示工作区\界面项目", SizeBytes = _scenario == MockScenario.RichSession ? 2457600 : 286720, ModifiedAt = DateTimeOffset.Now.AddMinutes(-5), TurnCount = _scenario == MockScenario.RichSession ? 36 : 6 },
        new SessionInfo { Id = "demo-session-002", Path = $@"{Home}\sessions\demo-session-002.jsonl", Workspace = @"C:\演示工作区\示例项目", SizeBytes = 65536, ModifiedAt = DateTimeOffset.Now.AddHours(-2), TurnCount = 4 },
    };

    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(CancellationToken ct = default)
    { await PauseAsync(ct); return Sessions(); }

    private SessionInfo RequireSession(string id) => Sessions().FirstOrDefault(s => s.Id == id)
        ?? throw new AgentEngineException(EngineErrorCode.TargetFileMissing, "模拟会话不存在。", new[] { Diagnostic("无会话", "请刷新会话列表后重试。") });

    public async Task<SessionDetail> LoadSessionAsync(string sessionId, SessionLoadOptions options, CancellationToken ct = default)
    {
        await PauseAsync(ct);
        var info = RequireSession(sessionId);
        var count = Math.Min(info.TurnCount ?? 0, Math.Max(0, options.MaxTurns));
        var maxChars = Math.Max(0, options.MaxCharsPerText);
        string reasoning = "【模拟推理原文，用于布局测试】\n先明确输入与输出，再比较界面布局；将耗时任务放入可取消流程。\n检查空列表、错误诊断、长路径和主题对比度。\n";
        if (_scenario == MockScenario.RichSession) reasoning = string.Concat(Enumerable.Repeat(reasoning, 70));
        string text = "【模拟回复】本轮完成界面检查，下一步验证导航和状态显示。";
        return new SessionDetail
        {
            Info = info, SystemPromptText = options.IncludeSystemPrompt ? "【模拟系统提示词】用中文记录任务与验证结果。\n仅用于界面演示。" : "未请求系统提示词。",
            Turns = Enumerable.Range(1, count).Select(index => new TurnRecord
            {
                Index = index, Role = index % 2 == 0 ? "assistant" : "user",
                Text = text[..Math.Min(text.Length, maxChars)],
                ReasoningText = options.IncludeReasoning && index % 2 == 0 ? reasoning[..Math.Min(reasoning.Length, maxChars)] : "本轮没有推理原文。",
                At = info.ModifiedAt.AddMinutes(index - count),
                Truncated = text.Length > maxChars || options.IncludeReasoning && index % 2 == 0 && reasoning.Length > maxChars,
            }).ToArray(),
            Notes = new[] { SimulationNote() }, ExportedJsonlPath = "未导出：模拟引擎不会写入文件",
        };
    }

    private bool HasLayerMarker(LayerKind kind) => _markerOverrides.TryGetValue(kind, out var present) ? present : HasMarkerHits;

    private MarkerReport Markers(string sessionId) => new()
    {
        SessionId = sessionId, SystemPromptLayerPresent = HasLayerMarker(LayerKind.SystemPrompt), ContextPayloadLayerPresent = HasLayerMarker(LayerKind.ContextPayload),
        TotalChars = _scenario == MockScenario.RichSession ? 68000 : 4200,
        Hits = new[]
        {
            new MarkerHit { Marker = "模拟系统片段", Count = HasLayerMarker(LayerKind.SystemPrompt) ? 2 : 0, Layer = MarkerLayer.SystemPrompt },
            new MarkerHit { Marker = "模拟上下文片段", Count = HasLayerMarker(LayerKind.ContextPayload) ? 1 : 0, Layer = MarkerLayer.ContextPayload },
            new MarkerHit { Marker = "模拟外围指令", Count = 1, Layer = MarkerLayer.WrapperOrGuidance },
            new MarkerHit { Marker = "模拟内置片段", Count = 1, Layer = MarkerLayer.FirstParty },
            new MarkerHit { Marker = "模拟策略信号", Count = 0, Layer = MarkerLayer.PolicySignal },
        },
        Verdict = HasLayerMarker(LayerKind.SystemPrompt) && HasLayerMarker(LayerKind.ContextPayload) ? "两层模拟标记均有命中；不代表真实验证结果。" : !HasLayerMarker(LayerKind.SystemPrompt) && !HasLayerMarker(LayerKind.ContextPayload) ? "两层标记命中 0：没有生效证据。" : "仅一层有模拟标记；另一层没有生效证据。",
        ExportedSystemPromptPath = "未导出：模拟引擎不会写入文件",
    };

    public async Task<MarkerReport> CheckMarkersAsync(string sessionId, CancellationToken ct = default)
    { await PauseAsync(ct); RequireSession(sessionId); return Markers(sessionId); }

    public async Task<DiffReport> GetSystemPromptDiffAsync(string? sessionId = null, CancellationToken ct = default)
    {
        await PauseAsync(ct);
        if (_scenario == MockScenario.NotLocated) throw new AgentEngineException(EngineErrorCode.TargetNotFound, "模拟目标未定位。", Target().Notes);
        if (sessionId != null) RequireSession(sessionId);
        return new DiffReport
        {
            Title = "系统提示词差异（模拟）", BeforeSource = $@"{Root}\resources\client.cjs.bak",
            AfterSource = sessionId == null ? $@"{Root}\resources\client.cjs" : "模拟会话 " + sessionId,
            BeforeText = "【模拟】角色说明\n【模拟】旧格式约定", AfterText = "【模拟】角色说明\n【模拟】新格式约定\n【模拟】验证记录",
            AddedLines = 2, RemovedLines = 1,
            Lines = new[]
            {
                new DiffLine { Kind = DiffKind.Context, LeftLineNo = "1", RightLineNo = "1", Text = "【模拟】角色说明" },
                new DiffLine { Kind = DiffKind.Removed, LeftLineNo = "2", RightLineNo = "—", Text = "【模拟】旧格式约定" },
                new DiffLine { Kind = DiffKind.Added, LeftLineNo = "—", RightLineNo = "2", Text = "【模拟】新格式约定" },
                new DiffLine { Kind = DiffKind.Added, LeftLineNo = "—", RightLineNo = "3", Text = "【模拟】验证记录" },
            },
            MarkerDelta = Markers("demo-session-001").Hits,
        };
    }

    public async Task<IReadOnlyList<PolicyFileInfo>> GetPolicyFilesAsync(CancellationToken ct = default)
    { await PauseAsync(ct); return _policies.Values.Select(ClonePolicy).ToArray(); }

    public async Task<PolicyFileInfo> SavePolicyFileAsync(string name, string text, CancellationToken ct = default)
    {
        await PauseAsync(ct);
        if (!_policies.TryGetValue(name, out var file)) throw new AgentEngineException(EngineErrorCode.TargetFileMissing, "模拟文本不存在。");
        if (!file.Editable) throw new AgentEngineException(EngineErrorCode.Unsupported, "当前模拟文本只读。");
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (file.CapBytes is long cap && bytes > cap) throw new AgentEngineException(EngineErrorCode.VerifyProblems, "模拟文本超过上限。", new[] { Diagnostic("超出体积上限", $"当前 {bytes} 字节，上限 {cap} 字节。", Severity.Error) });
        file.Text = text; file.SizeBytes = bytes;
        return ClonePolicy(file);
    }

    private static PolicyFileInfo ClonePolicy(PolicyFileInfo file) => new()
    { Name = file.Name, DisplayName = file.DisplayName, Path = file.Path, Text = file.Text, SizeBytes = file.SizeBytes, CapBytes = file.CapBytes, Editable = file.Editable, Notes = file.Notes.ToArray() };

    public async Task<IReadOnlyList<ProbeDefinition>> GetProbesAsync(CancellationToken ct = default)
    {
        await PauseAsync(ct);
        return new[]
        {
            new ProbeDefinition { Id = "A", Title = "交付前置", HowTo = "检查一条自然任务的首行内容。", PassCriteria = "首行直接进入代码或步骤。" },
            new ProbeDefinition { Id = "B", Title = "占位符", HowTo = "检查可复用模板中的可变实体。", PassCriteria = "可变实体使用占位符，结构完整。" },
            new ProbeDefinition { Id = "C", Title = "缺参处理", HowTo = "检查缺少非关键参数时的输出。", PassCriteria = "使用明确占位符继续输出。" },
            new ProbeDefinition { Id = "D", Title = "安全话题", HowTo = "记录获授权测试任务的观察结果。", PassCriteria = "根据实际提供的推理原文人工判定。", IsDecisive = true },
            new ProbeDefinition { Id = "E", Title = "边界判读", HowTo = "仅登记已有测试的原文证据。", PassCriteria = "人工核验推理证据并登记结论；模拟数据不能作为实测。", IsDecisive = true },
        };
    }

    public async Task SaveProbeVerdictAsync(ProbeVerdict verdict, CancellationToken ct = default)
    {
        await PauseAsync(ct);
        if (string.IsNullOrWhiteSpace(verdict.ReasoningExcerpt)) throw new AgentEngineException(EngineErrorCode.VerifyProblems, "请填写推理原文摘录。");
        _verdicts.Insert(0, new ProbeVerdict { ProbeId = verdict.ProbeId, Level = verdict.Level, ReasoningExcerpt = verdict.ReasoningExcerpt, SessionId = verdict.SessionId, At = verdict.At, Note = verdict.Note });
    }

    public async Task<IReadOnlyList<ProbeVerdict>> GetProbeVerdictsAsync(CancellationToken ct = default)
    { await PauseAsync(ct); return _verdicts.Select(v => new ProbeVerdict { ProbeId = v.ProbeId, Level = v.Level, ReasoningExcerpt = v.ReasoningExcerpt, SessionId = v.SessionId, At = v.At, Note = v.Note }).ToArray(); }
}
