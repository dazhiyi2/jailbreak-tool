// === SPEC §4.1 目标探测；调用 LocateAsync。
// === 加载：进度环；空：无候选说明；错误：中文错误及 Notes；正常：路径、逐级探测与嗅探候选。
using AgentToolkit.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentToolkit.Ui.ViewModels;

public partial class LocatePageViewModel(IAgentEngine engine) : PageViewModel(engine)
{
    [ObservableProperty] public partial string ExplicitRoot { get; set; } = "";
    [ObservableProperty] public partial string ExplicitConfigHome { get; set; } = "";
    [ObservableProperty] public partial bool IgnoreMemory { get; set; }
    [ObservableProperty] public partial bool FullSniff { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "尚未探测";
    [ObservableProperty] public partial string TargetRoot { get; set; } = "尚未探测";
    // 客户端分类（SPEC §2 Phase 0）：决定这个目标能用哪一层，必须显示出来
    [ObservableProperty] public partial string TargetName { get; set; } = "尚未识别";
    [ObservableProperty] public partial string KindLabel { get; set; } = "未分类";
    [ObservableProperty] public partial string ClassNote { get; set; } = "尚未探测，无法判断可用层。";
    [ObservableProperty] public partial IReadOnlyList<AttemptItem> Attempts { get; set; } = Array.Empty<AttemptItem>();
    [ObservableProperty] public partial IReadOnlyList<string> SniffCandidates { get; set; } = Array.Empty<string>();
    [ObservableProperty] public partial string SniffSummary { get; set; } = "未开启全盘嗅探。";

    public override Task LoadAsync() => LoadAsync(async ct =>
    {
        var result = await Engine.LocateAsync(new LocateOptions
        { ExplicitRoot = string.IsNullOrWhiteSpace(ExplicitRoot) ? null : ExplicitRoot, ExplicitConfigHome = string.IsNullOrWhiteSpace(ExplicitConfigHome) ? null : ExplicitConfigHome, IgnoreMemory = IgnoreMemory, FullSniff = FullSniff }, ct);
        Summary = result.Found ? $"已找到客户端 · {Display.Text(result.Version)} · {Display.Text(result.LocatedBy)}" : "未找到客户端 · 可调整路径后重试";
        TargetRoot = Display.Text(result.Root);
        TargetName = Display.Text(result.TargetDisplayName, "未识别");
        KindLabel = Display.TargetKindName(result.Kind);
        ClassNote = result.Kind switch
        {
            TargetKind.PlaintextBundle => "提示词明文在本地文件里：补丁层最省事，字符串替换即可。",
            TargetKind.PackagedElectron => "提示词在 app.asar 里：先解包，注意分段注册表与 agent 作用域遮蔽。",
            TargetKind.CloudAssembled => "提示词由服务端组装：只能走载荷层，补丁层无从下手。",
            TargetKind.CustomHarness => "自建 harness：直接改源码，不需要这套工具。",
            _ => "分类未知：安装根未定位时无法判断可用层。",
        };
        Attempts = result.Attempts.Select(a => new AttemptItem(a)).ToArray();
        SniffCandidates = result.SniffCandidates;
        SniffSummary = FullSniff ? $"候选 {SniffCandidates.Count} 个，命中 {result.SniffCandidateHits} 个。" : "未开启全盘嗅探；当前没有嗅探候选。";
        SetNotes(result.Notes);
        return Attempts.Count > 0 || result.Found;
    }, "引擎未返回任何探测候选，请指定路径后重试。");
}
