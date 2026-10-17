// === SPEC §4.1 状态、§1.5 生效证据、§3 备份；调用 GetStatusAsync、CheckMarkersAsync。
// === 加载：进度环；空：无状态层；错误：中文错误及 Notes；正常：目标、全部层徽章与标记报告。
using AgentToolkit.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentToolkit.Ui.ViewModels;

public partial class StatusPageViewModel(IAgentEngine engine) : PageViewModel(engine)
{
    private string? _sessionId;
    [ObservableProperty] public partial string Version { get; set; } = "未知";
    [ObservableProperty] public partial string Root { get; set; } = "未定位";
    [ObservableProperty] public partial string ConfigHome { get; set; } = "未定位";
    [ObservableProperty] public partial string Processes { get; set; } = "未运行";
    [ObservableProperty] public partial string CheckedAt { get; set; } = "尚未检查";
    [ObservableProperty] public partial bool RestartRequired { get; set; }
    [ObservableProperty] public partial IReadOnlyList<LayerItem> Layers { get; set; } = Array.Empty<LayerItem>();
    [ObservableProperty] public partial IReadOnlyList<MarkerItem> Markers { get; set; } = Array.Empty<MarkerItem>();
    [ObservableProperty] public partial string Verdict { get; set; } = "暂无会话标记报告。";
    [ObservableProperty] public partial string MarkerSummary { get; set; } = "尚未检查";

    public override Task LoadAsync() => LoadAsync(async ct =>
    {
        var result = await Engine.GetStatusAsync(ct);
        Version = Display.Text(result.Target.Version, "未知");
        Root = Display.Text(result.Target.Root, "未定位");
        ConfigHome = Display.Text(result.Target.ConfigHome, "未定位");
        Processes = result.ClientRunning ? "运行中 · 进程编号：" + string.Join("、", result.RunningProcessIds) : "客户端未运行";
        CheckedAt = Display.Time(result.CheckedAt);
        RestartRequired = result.RestartRequired;
        Layers = result.Layers.Select(l => new LayerItem(l)).ToArray();
        SetNotes(result.Notes);
        SetReport(result.LastMarkerReport);
        return Layers.Count > 0;
    }, "引擎未返回任何层状态，可重新探测后刷新。");

    [RelayCommand]
    public Task LoadAsync(string? action) => LoadAsync(async ct =>
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            var result = await Engine.GetStatusAsync(ct);
            SetNotes(result.Notes);
            SetReport(result.LastMarkerReport);
        }
        if (!string.IsNullOrWhiteSpace(_sessionId)) SetReport(await Engine.CheckMarkersAsync(_sessionId, ct));
        return Layers.Count > 0;
    }, "没有层状态，请先刷新。");

    private void SetReport(MarkerReport? report)
    {
        _sessionId = report?.SessionId;
        Markers = report?.Hits.Select(m => new MarkerItem(m)).ToArray() ?? Array.Empty<MarkerItem>();
        Verdict = report?.Verdict ?? "暂无会话报告，请先产生一条新会话。";
        MarkerSummary = report == null ? "无会话报告" : $"会话 {report.SessionId} · {report.TotalChars} 字符 · 系统层：{Display.YesNo(report.SystemPromptLayerPresent)} · 上下文层：{Display.YesNo(report.ContextPayloadLayerPresent)}";
    }
}
