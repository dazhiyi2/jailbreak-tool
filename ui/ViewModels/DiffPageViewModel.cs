// === SPEC §4.1 差异视图；调用 GetSystemPromptDiffAsync。
// === 加载：进度环；空：没有差异行；错误：中文错误及 Notes；正常：来源、逐行差异与标记变化。
using AgentToolkit.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentToolkit.Ui.ViewModels;

public partial class DiffPageViewModel(IAgentEngine engine) : PageViewModel(engine)
{
    [ObservableProperty] public partial string BeforeSource { get; set; } = "未提供";
    [ObservableProperty] public partial string AfterSource { get; set; } = "未提供";
    [ObservableProperty] public partial string Summary { get; set; } = "尚未加载";
    [ObservableProperty] public partial IReadOnlyList<DiffItem> Lines { get; set; } = Array.Empty<DiffItem>();
    [ObservableProperty] public partial IReadOnlyList<MarkerItem> Markers { get; set; } = Array.Empty<MarkerItem>();

    public override Task LoadAsync() => LoadAsync(async ct =>
    {
        var result = await Engine.GetSystemPromptDiffAsync(ct: ct);
        BeforeSource = Display.Text(result.BeforeSource); AfterSource = Display.Text(result.AfterSource);
        Summary = $"新增 {result.AddedLines} 行 · 删除 {result.RemovedLines} 行";
        Lines = result.Lines.Select(l => new DiffItem(l)).ToArray();
        Markers = result.MarkerDelta.Select(m => new MarkerItem(m)).ToArray();
        return Lines.Count > 0;
    }, "两份系统提示词没有差异，或引擎未返回逐行差异。");
}
