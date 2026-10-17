// === SPEC §4.1 日志、§1.5 标记证据；调用 ListSessionsAsync、LoadSessionAsync、CheckMarkersAsync。
// === 加载：可取消进度；空：无会话；错误：中文错误及 Notes；正常：分栏、推理搜索复制与已导出文件。
using AgentToolkit.Contracts;
using AgentToolkit.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace AgentToolkit.Ui.ViewModels;

public partial class LogsPageViewModel(IAgentEngine engine) : PageViewModel(engine)
{
    private bool _selecting;
    private readonly List<(TurnItem Turn, int Start, int Length)> _matches = new();
    private int _matchIndex = -1;
    private string? _exportedJsonl;
    private string? _exportedSystem;
    [ObservableProperty] public partial IReadOnlyList<SessionItem> Sessions { get; set; } = Array.Empty<SessionItem>();
    [ObservableProperty] public partial SessionItem? SelectedSession { get; set; }
    [ObservableProperty] public partial IReadOnlyList<TurnItem> Turns { get; set; } = Array.Empty<TurnItem>();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasoningText), nameof(ReplyText), nameof(IsTruncated))]
    public partial TurnItem? SelectedTurn { get; set; }
    [ObservableProperty] public partial string SystemPromptText { get; set; } = "暂无系统提示词";
    [ObservableProperty] public partial IReadOnlyList<MarkerItem> Markers { get; set; } = Array.Empty<MarkerItem>();
    [ObservableProperty] public partial string Verdict { get; set; } = "暂无标记报告";
    [ObservableProperty] public partial string SessionSummary { get; set; } = "请选择会话";
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial int SelectedTab { get; set; }
    [ObservableProperty] public partial string SearchSummary { get; set; } = "搜索当前已载入的全部推理原文，不区分大小写。";
    [ObservableProperty] public partial string SearchExcerpt { get; set; } = "";
    [ObservableProperty] public partial int MatchStart { get; set; }
    [ObservableProperty] public partial int MatchLength { get; set; }
    [ObservableProperty] public partial string ExportSummary { get; set; } = "可把当前会话的原文与系统提示词导出到本机目录，再交给记事本打开。";
    public string ReasoningText => SelectedTurn?.Reasoning ?? "请选择一轮记录。";
    public string ReplyText => SelectedTurn?.Text ?? "请选择一轮记录。";
    public bool IsTruncated => SelectedTurn?.Truncated ?? false;
    public bool CanSearch => !IsLoading && _matches.Count > 0;
    public bool CanOpenJsonl => !IsLoading && Engine is not MockEngine && ShellOpen.IsLocalPath(_exportedJsonl);
    public bool CanOpenSystem => !IsLoading && Engine is not MockEngine && ShellOpen.IsLocalPath(_exportedSystem);

    partial void OnSelectedSessionChanged(SessionItem? value)
    {
        if (!_selecting && value != null && !IsLoading) LoadCommand.Execute("session");
    }
    partial void OnSelectedTurnChanged(TurnItem? value)
    {
        MatchStart = MatchLength = 0;
    }
    partial void OnSearchTextChanged(string value) => UpdateSearch();
    protected override void OnBusyChanged()
    {
        NextMatchCommand.NotifyCanExecuteChanged(); PreviousMatchCommand.NotifyCanExecuteChanged();
        OpenJsonlCommand.NotifyCanExecuteChanged(); OpenSystemCommand.NotifyCanExecuteChanged();
    }

    public override Task LoadAsync() => LoadAsync(async ct =>
    {
        var selectedId = SelectedSession?.Id;
        var sessions = await Engine.ListSessionsAsync(ct);
        _selecting = true;
        try
        {
            Sessions = sessions.Select(s => new SessionItem(s)).ToArray();
            SelectedSession = Sessions.FirstOrDefault(s => s.Id == selectedId) ?? Sessions.FirstOrDefault();
        }
        finally { _selecting = false; }
        if (SelectedSession == null)
        {
            SelectedTurn = null; Turns = Array.Empty<TurnItem>(); Markers = Array.Empty<MarkerItem>();
            SystemPromptText = "没有会话。"; Verdict = "没有会话，无法检查标记。";
            _exportedJsonl = _exportedSystem = null; UpdateSearch(); return false;
        }
        await LoadAsync(SelectedSession, ct);
        return true;
    }, "暂无会话日志。请先在客户端产生一条会话，然后刷新。");

    [RelayCommand]
    public Task LoadAsync(string? action) => SelectedSession is not SessionItem session ? Task.CompletedTask :
        LoadAsync(async ct => { await LoadAsync(session, ct); return true; }, "所选会话没有数据。");

    private async Task LoadAsync(SessionItem session, CancellationToken ct)
    {
        var detail = await Engine.LoadSessionAsync(session.Id, new SessionLoadOptions(), ct);
        var report = await Engine.CheckMarkersAsync(session.Id, ct);
        ct.ThrowIfCancellationRequested();
        Turns = detail.Turns.Select(t => new TurnItem(t)).ToArray();
        SelectedTurn = Turns.FirstOrDefault(t => t.HasReasoning) ?? Turns.FirstOrDefault();
        SystemPromptText = Display.Text(detail.SystemPromptText, "当前会话没有系统提示词。");
        Markers = report.Hits.Select(m => new MarkerItem(m)).ToArray();
        Verdict = report.Verdict;
        SessionSummary = session.Id + " · " + session.Workspace;
        _exportedJsonl = detail.ExportedJsonlPath; _exportedSystem = report.ExportedSystemPromptPath;
        ExportSummary = "会话文件：" + Display.Text(_exportedJsonl, "后端未提供导出文件") + "\n系统提示词：" + Display.Text(_exportedSystem, "后端未提供导出文件") + "\n当前契约没有发起导出的方法；只能打开后端已导出的本机文件。";
        SetNotes(detail.Notes); UpdateSearch();
    }

    private void UpdateSearch()
    {
        _matches.Clear(); _matchIndex = -1; MatchStart = MatchLength = 0; SearchExcerpt = "";
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            foreach (var turn in Turns.Where(t => t.HasReasoning))
            {
                var offset = 0;
                while (offset <= turn.Reasoning.Length - SearchText.Length)
                {
                    var start = turn.Reasoning.IndexOf(SearchText, offset, StringComparison.OrdinalIgnoreCase);
                    if (start < 0) break;
                    _matches.Add((turn, start, SearchText.Length)); offset = start + SearchText.Length;
                }
            }
        }
        if (_matches.Count > 0) SelectMatch(0);
        else SearchSummary = string.IsNullOrWhiteSpace(SearchText) ? "搜索当前已载入的全部推理原文，不区分大小写。" : "未找到匹配；搜索仅覆盖引擎已返回的文本。";
        NextMatchCommand.NotifyCanExecuteChanged(); PreviousMatchCommand.NotifyCanExecuteChanged();
    }
    private void SelectMatch(int index)
    {
        _matchIndex = (index + _matches.Count) % _matches.Count;
        var match = _matches[_matchIndex]; SelectedTurn = match.Turn; SelectedTab = 0;
        MatchStart = match.Start; MatchLength = match.Length;
        var excerptStart = Math.Max(0, match.Start - 50);
        SearchExcerpt = match.Turn.Reasoning.Substring(excerptStart, Math.Min(180, match.Turn.Reasoning.Length - excerptStart));
        SearchSummary = $"第 {_matchIndex + 1} / {_matches.Count} 处 · {match.Turn.Title}";
    }
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private void NextMatch() => SelectMatch(_matchIndex + 1);
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private void PreviousMatch() => SelectMatch(_matchIndex - 1);
    [RelayCommand(CanExecute = nameof(CanOpenJsonl))]
    private void OpenJsonl() => OpenExport(_exportedJsonl);
    [RelayCommand(CanExecute = nameof(CanOpenSystem))]
    private void OpenSystem() => OpenExport(_exportedSystem);
    private void OpenExport(string? path)
    {
        try { ShellOpen.OpenText(path!); }
        catch (Exception ex) { ShowMessage("无法打开已导出文件", ex.Message, InfoBarSeverity.Error); }
    }

    // v2 契约补齐：界面可以自己发起导出，不再只能打开后端预先给好的路径。
    [RelayCommand]
    private Task ExportSession() => SelectedSession is not SessionItem session ? Task.CompletedTask :
        LoadAsync(async ct =>
        {
            var result = await Engine.ExportSessionAsync(session.Id, null, ct);
            _exportedJsonl = result.Path;
            ExportSummary = BuildExportSummary("会话原文", result, _exportedSystem);
            OpenJsonlCommand.NotifyCanExecuteChanged();
            return true;
        }, "导出没有返回结果。", preserveContent: true);

    [RelayCommand]
    private Task ExportSystem() => SelectedSession is not SessionItem session ? Task.CompletedTask :
        LoadAsync(async ct =>
        {
            var result = await Engine.ExportSystemPromptAsync(session.Id, null, ct);
            _exportedSystem = result.Path;
            ExportSummary = BuildExportSummary("系统提示词", result, _exportedJsonl);
            OpenSystemCommand.NotifyCanExecuteChanged();
            return true;
        }, "导出没有返回结果。", preserveContent: true);

    private static string BuildExportSummary(string what, ExportResult result, string? other) =>
        $"刚导出{what}：{Display.Text(result.Path)}（{Display.Bytes(result.SizeBytes)}）\n" +
        $"当前会话文件：{Display.Text(other, "未导出")}\n" +
        "导出的是本机日志副本，不含任何远端数据。";
}
