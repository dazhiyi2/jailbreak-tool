// === SPEC §4.1 还原、§3 回读证明；调用 RevertAsync，区分 DryRun 与实际执行。
// === 加载：可取消进度；空：无还原计划；错误：中文错误及 Notes；正常：二次确认、回读与哈希。
using AgentToolkit.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace AgentToolkit.Ui.ViewModels;

public partial class RevertPageViewModel(IAgentEngine engine) : PageViewModel(engine)
{
    private bool _removeBackups;
    [ObservableProperty] public partial IReadOnlyList<ChangeItem> Changes { get; set; } = Array.Empty<ChangeItem>();
    [ObservableProperty] public partial string ReadBack { get; set; } = "预览阶段，尚未回读";
    [ObservableProperty] public partial string MatchesOriginal { get; set; } = "预览阶段，尚未验证";
    [ObservableProperty] public partial string OriginalHash { get; set; } = "尚未预览";
    [ObservableProperty] public partial string CurrentHash { get; set; } = "尚未预览";
    [ObservableProperty] public partial bool RemoveBackupsRequested { get; set; }
    [ObservableProperty] public partial bool BackupConfirmationOpen { get; set; }
    [ObservableProperty] public partial string BackupOptionLabel { get; set; } = "保留备份";
    [ObservableProperty] public partial bool PreviewReady { get; set; }
    public bool CanRevert => !IsLoading && PreviewReady && !BackupConfirmationOpen && Changes.Count > 0;
    partial void OnRemoveBackupsRequestedChanged(bool value)
    {
        BackupConfirmationOpen = value;
        if (!value) { _removeBackups = false; BackupOptionLabel = "保留备份"; }
        else BackupOptionLabel = "等待二次确认，尚未允许移除备份";
        PreviewReady = false;
    }
    partial void OnPreviewReadyChanged(bool value) => RevertCommand.NotifyCanExecuteChanged();
    partial void OnBackupConfirmationOpenChanged(bool value) => RevertCommand.NotifyCanExecuteChanged();
    protected override void OnBusyChanged() => RevertCommand.NotifyCanExecuteChanged();
    [RelayCommand]
    private void ConfirmRemoveBackups()
    { _removeBackups = true; BackupConfirmationOpen = false; BackupOptionLabel = "已确认：还原后移除备份"; PreviewReady = false; }
    [RelayCommand]
    private void CancelRemoveBackups() => RemoveBackupsRequested = false;

    public override Task LoadAsync() => LoadAsync(async ct =>
    {
        PreviewReady = false;
        var result = await Engine.RevertAsync(new RevertOptions { DryRun = true, RemoveBackups = _removeBackups }, CreateProgress(), ct);
        SetResult(result, true);
        PreviewReady = result.Changes.Count > 0;
        return Changes.Count > 0;
    }, "没有需要还原的变更。");

    [RelayCommand(CanExecute = nameof(CanRevert))]
    public Task RevertAsync() => LoadAsync(async ct =>
    {
        PreviewReady = false;
        var result = await Engine.RevertAsync(new RevertOptions { DryRun = false, RemoveBackups = _removeBackups }, CreateProgress(), ct);
        SetResult(result, false);
        ShowMessage(result.Reverted ? "引擎已完成还原" : "引擎未执行还原",
            result.Reverted ? "回读结果：" + ReadBack + "；与原始备份比较：" + MatchesOriginal : "请查看引擎返回的诊断。",
            result.Reverted && result.ReadBackClean ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
        return true;
    }, "没有执行结果。", preserveContent: true);

    private void SetResult(RevertResult result, bool dryRun)
    {
        Changes = result.Changes.Select(c => new ChangeItem(c)).ToArray();
        ReadBack = dryRun ? "预览阶段，尚未执行回读" : result.ReadBackClean ? "回读无残留" : "回读仍有残留";
        MatchesOriginal = dryRun ? "预览阶段，尚未验证" : result.MatchesOriginal.HasValue ? Display.YesNo(result.MatchesOriginal.Value) : "无法比较，不能证明与首次备份一致";
        OriginalHash = Display.Text(result.OriginalSha256); CurrentHash = Display.Text(result.CurrentSha256);
        SetNotes(result.Notes);
    }
}
