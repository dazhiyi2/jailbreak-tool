// === SPEC §4.1 注入预览与执行、§3 自检；调用 PreviewApplyAsync、ApplyAsync。
// === 加载：可取消进度；空：无变更；错误：中文错误及 Notes；正常：预览、二次确认、结果与重启提示。
using AgentToolkit.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace AgentToolkit.Ui.ViewModels;

public partial class InjectPageViewModel(IAgentEngine engine) : PageViewModel(engine)
{
    private bool _force;
    private ApplyOptions? _previewOptions;
    [ObservableProperty] public partial bool ForceRequested { get; set; }
    [ObservableProperty] public partial bool ForceConfirmationOpen { get; set; }
    [ObservableProperty] public partial string ForceLabel { get; set; } = "未启用强制处理";
    [ObservableProperty] public partial bool SkipSystemPrompt { get; set; }
    [ObservableProperty] public partial bool SkipContextPayload { get; set; }
    [ObservableProperty] public partial bool RestartClient { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "尚未生成预览";
    [ObservableProperty] public partial string PreviewId { get; set; } = "尚未生成预览";
    [ObservableProperty] public partial IReadOnlyList<ChangeItem> Changes { get; set; } = Array.Empty<ChangeItem>();
    [ObservableProperty] public partial string DiffText { get; set; } = "";
    [ObservableProperty] public partial bool PreviewBlocked { get; set; }
    [ObservableProperty] public partial bool PreviewStale { get; set; } = true;
    [ObservableProperty] public partial bool HasResult { get; set; }
    [ObservableProperty] public partial bool RestartRequired { get; set; }
    [ObservableProperty] public partial string VerificationHint { get; set; } = "";
    public bool CanApply => !IsLoading && _previewOptions != null && !PreviewBlocked && !PreviewStale && !ForceConfirmationOpen && Changes.Count > 0;

    partial void OnForceRequestedChanged(bool value)
    {
        ForceConfirmationOpen = value;
        if (!value) { _force = false; ForceLabel = "未启用强制处理"; }
        else ForceLabel = "等待二次确认，尚未启用";
        InvalidatePreview();
    }
    partial void OnSkipSystemPromptChanged(bool value) => InvalidatePreview();
    partial void OnSkipContextPayloadChanged(bool value) => InvalidatePreview();
    partial void OnRestartClientChanged(bool value) => InvalidatePreview();
    partial void OnPreviewStaleChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();
    partial void OnPreviewBlockedChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();
    partial void OnForceConfirmationOpenChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();
    protected override void OnBusyChanged() => ApplyCommand.NotifyCanExecuteChanged();
    private void InvalidatePreview() { PreviewStale = true; HasResult = false; RestartRequired = false; ApplyCommand.NotifyCanExecuteChanged(); }

    [RelayCommand]
    private void ConfirmForce()
    {
        _force = true; ForceConfirmationOpen = false; ForceLabel = "已确认强制处理；仍由引擎检查锚点与备份";
        InvalidatePreview();
    }
    [RelayCommand]
    private void CancelForce() => ForceRequested = false;

    private ApplyOptions Options(bool dryRun) => new()
    {
        DryRun = dryRun, Force = _force, SkipSystemPrompt = SkipSystemPrompt,
        SkipContextPayload = SkipContextPayload, RestartClient = RestartClient,
    };

    public override Task LoadAsync() => LoadAsync(async ct =>
    {
        HasResult = false; RestartRequired = false; _previewOptions = null; PreviewStale = true;
        if (SkipSystemPrompt && SkipContextPayload)
            throw new AgentEngineException(EngineErrorCode.VerifyProblems, "至少选择一个需要处理的层。");
        var options = Options(true);
        var result = await Engine.PreviewApplyAsync(options, ct);
        ct.ThrowIfCancellationRequested();
        _previewOptions = options;
        PreviewId = Display.Text(result.PreviewId);
        Summary = Display.Bytes(result.TotalBeforeBytes) + " → " + Display.Bytes(result.TotalAfterBytes) + (result.CanApply ? " · 预览检查通过" : " · 预览不允许执行");
        PreviewBlocked = !result.CanApply;
        Changes = result.Changes.Select(c => new ChangeItem(c)).ToArray();
        DiffText = string.Join("\n\n", Changes.Select(c => c.Path + "\n" + c.Diff));
        SetNotes(result.Notes);
        PreviewStale = false;
        ApplyCommand.NotifyCanExecuteChanged();
        return Changes.Count > 0;
    }, "没有计划变更，请检查目标是否已定位及诊断信息。");

    [RelayCommand(CanExecute = nameof(CanApply))]
    public Task ApplyAsync() => LoadAsync(async ct =>
    {
        var preview = _previewOptions ?? throw new InvalidOperationException("请先重新预览。");
        PreviewStale = true;
        var result = await Engine.ApplyAsync(new ApplyOptions
        {
            DryRun = false, Force = preview.Force, SkipSystemPrompt = preview.SkipSystemPrompt,
            SkipContextPayload = preview.SkipContextPayload, RestartClient = preview.RestartClient,
            PolicyPrefixPath = preview.PolicyPrefixPath, PolicySuffixPath = preview.PolicySuffixPath, PayloadPath = preview.PayloadPath,
        }, CreateProgress(), ct);
        Changes = result.Changes.Select(c => new ChangeItem(c)).ToArray();
        RestartRequired = result.RestartRequired; VerificationHint = result.VerificationHint; HasResult = true;
        SetNotes(result.Notes);
        ShowMessage(result.Applied ? "引擎已完成注入" : "引擎未执行写入", result.VerificationHint,
            result.Applied ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
        return true;
    }, "没有执行结果。", preserveContent: true);
}
