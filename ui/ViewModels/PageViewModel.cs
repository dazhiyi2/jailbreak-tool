// === 公共页面状态；对应 SPEC §4.1 各页；此基类不直接调用引擎。
// === 加载：ProgressRing；空：空态说明；错误：中文 InfoBar 与 Notes；正常：正文。
using AgentToolkit.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentToolkit.Ui.ViewModels;

public abstract partial class PageViewModel : ObservableObject
{
    protected readonly IAgentEngine Engine;
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource<object?>? _idle;
    private long _operationVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingVisibility))]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContentVisibility))]
    public partial bool HasContent { get; set; }

    [ObservableProperty] public partial bool HasError { get; set; }
    [ObservableProperty] public partial string ErrorTitle { get; set; } = "";
    [ObservableProperty] public partial string ErrorDetail { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility))]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty] public partial string EmptyMessage { get; set; } = "当前没有可显示的数据。";
    [ObservableProperty] public partial IReadOnlyList<NoteItem> Notes { get; set; } = Array.Empty<NoteItem>();
    [ObservableProperty] public partial bool HasMessage { get; set; }
    [ObservableProperty] public partial string MessageTitle { get; set; } = "";
    [ObservableProperty] public partial string MessageDetail { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity MessageSeverity { get; set; } = InfoBarSeverity.Informational;
    [ObservableProperty] public partial double ProgressValue { get; set; }
    [ObservableProperty] public partial bool IsProgressIndeterminate { get; set; } = true;
    [ObservableProperty] public partial string ProgressMessage { get; set; } = "正在加载引擎数据…";

    protected PageViewModel(IAgentEngine engine) => Engine = engine;
    public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ContentVisibility => HasContent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public bool IsInteractive => !IsLoading;
    public Task WhenIdle => _idle?.Task ?? Task.CompletedTask;
    public abstract Task LoadAsync();

    [RelayCommand(CanExecute = nameof(IsInteractive))]
    public Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    public void Cancel() => _cancellation?.Cancel();

    partial void OnIsLoadingChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        OnBusyChanged();
    }

    protected virtual void OnBusyChanged() { }

    protected async Task LoadAsync(Func<CancellationToken, Task<bool>> load, string emptyMessage, bool preserveContent = false)
    {
        if (IsLoading) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _idle = completion;
        _operationVersion++;
        IsLoading = true;
        if (!preserveContent) HasContent = false;
        HasError = false;
        IsEmpty = false;
        Notes = Array.Empty<NoteItem>();
        ErrorTitle = ErrorDetail = "";
        HasMessage = false;
        ProgressValue = 0;
        IsProgressIndeterminate = true;
        ProgressMessage = "正在加载引擎数据…";
        EmptyMessage = emptyMessage;
        try
        {
            HasContent = await load(cancellation.Token);
            IsEmpty = !HasContent;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            EmptyMessage = "操作已取消，可点击刷新重新加载。";
            IsEmpty = !HasContent;
            ShowMessage("操作已取消", "若已开始修改，请刷新状态确认引擎的实际结果。", InfoBarSeverity.Warning);
        }
        catch (AgentEngineException ex)
        {
            HasError = true;
            ErrorTitle = ErrorText(ex.Code);
            ErrorDetail = ex.Message;
            SetNotes(ex.Notes);
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorTitle = "操作未完成";
            ErrorDetail = ex.Message;
        }
        finally
        {
            IsLoading = false;
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            completion.TrySetResult(null);
        }
    }

    protected void SetNotes(IEnumerable<Note> source) => Notes = source.Select(n => new NoteItem(n)).ToArray();

    protected void ShowMessage(string title, string detail, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        MessageTitle = title; MessageDetail = detail; MessageSeverity = severity; HasMessage = true;
    }

    protected IProgress<EngineProgress> CreateProgress()
    {
        var version = _operationVersion;
        return new Progress<EngineProgress>(progress =>
        {
            if (!IsLoading || version != _operationVersion) return;
            ProgressMessage = string.IsNullOrWhiteSpace(progress.Message) ? progress.Stage : progress.Stage + " · " + progress.Message;
            IsProgressIndeterminate = !progress.Fraction.HasValue;
            ProgressValue = Math.Clamp(progress.Fraction ?? 0, 0, 1) * 100;
        });
    }

    private static string ErrorText(EngineErrorCode code) => code switch
    {
        EngineErrorCode.VerifyProblems => "验证未通过，请检查输入与诊断",
        EngineErrorCode.SelfCheckFailed => "解析自检失败，操作已停止",
        EngineErrorCode.TargetNotFound => "未找到目标客户端",
        EngineErrorCode.AnchorConflict => "锚点存在冲突，拒绝写入",
        EngineErrorCode.TargetFileMissing => "目标文件或会话不存在",
        EngineErrorCode.NotApplied => "当前尚未安装配置",
        EngineErrorCode.IoError => "引擎读写失败",
        EngineErrorCode.Unsupported => "当前目标不支持此操作",
        _ => "引擎返回未知错误",
    };
}
