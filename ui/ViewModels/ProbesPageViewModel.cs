// === SPEC §5.1–§5.2 验收台账；调用 GetProbesAsync、SaveProbeVerdictAsync、GetProbeVerdictsAsync。
// === 加载：进度；空：无探针/无历史；错误：中文错误及 Notes；正常：人工三级判定、摘录、历史。
using AgentToolkit.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentToolkit.Ui.ViewModels;

public partial class ProbesPageViewModel(IAgentEngine engine) : PageViewModel(engine)
{
    [ObservableProperty] public partial IReadOnlyList<ProbeItem> Probes { get; set; } = Array.Empty<ProbeItem>();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HowTo), nameof(Criteria))]
    public partial ProbeItem? SelectedProbe { get; set; }
    [ObservableProperty] public partial IReadOnlyList<VerdictItem> Verdicts { get; set; } = Array.Empty<VerdictItem>();
    [ObservableProperty] public partial string ReasoningExcerpt { get; set; } = "";
    [ObservableProperty] public partial int SelectedLevel { get; set; } = -1;
    [ObservableProperty] public partial string SessionId { get; set; } = "";
    [ObservableProperty] public partial string NoteText { get; set; } = "";
    [ObservableProperty] public partial bool HistoryEmpty { get; set; }
    public string HowTo => SelectedProbe?.HowTo ?? "请选择探针。";
    public string Criteria => SelectedProbe?.Criteria ?? "请选择探针。";
    public IReadOnlyList<string> Levels { get; } = new[] { "没破", "半破", "破了" };
    public bool CanSave => !IsLoading && SelectedProbe != null && SelectedLevel is >= 0 and <= 2 && !string.IsNullOrWhiteSpace(ReasoningExcerpt);
    partial void OnSelectedProbeChanged(ProbeItem? value) => SaveCommand.NotifyCanExecuteChanged();
    partial void OnSelectedLevelChanged(int value) => SaveCommand.NotifyCanExecuteChanged();
    partial void OnReasoningExcerptChanged(string value) => SaveCommand.NotifyCanExecuteChanged();
    protected override void OnBusyChanged() => SaveCommand.NotifyCanExecuteChanged();

    public override Task LoadAsync() => LoadAsync(async ct =>
    {
        var selectedId = SelectedProbe?.Id;
        Probes = (await Engine.GetProbesAsync(ct)).Select(p => new ProbeItem(p)).ToArray();
        Verdicts = (await Engine.GetProbeVerdictsAsync(ct)).Select(v => new VerdictItem(v)).ToArray();
        SelectedProbe = Probes.FirstOrDefault(p => p.Id == selectedId) ?? Probes.FirstOrDefault();
        HistoryEmpty = Verdicts.Count == 0;
        return Probes.Count > 0;
    }, "引擎未提供任何探针定义。");

    [RelayCommand(CanExecute = nameof(CanSave))]
    public Task SaveAsync() => LoadAsync(async ct =>
    {
        var verdict = new ProbeVerdict
        {
            ProbeId = SelectedProbe!.Id, Level = (ProbeVerdictLevel)(SelectedLevel + 1),
            ReasoningExcerpt = ReasoningExcerpt, SessionId = string.IsNullOrWhiteSpace(SessionId) ? null : SessionId,
            Note = string.IsNullOrWhiteSpace(NoteText) ? null : NoteText, At = DateTimeOffset.Now,
        };
        await Engine.SaveProbeVerdictAsync(verdict, ct);
        ReasoningExcerpt = "";
        ShowMessage("记录已保存", "正在更新台账历史；本页只保存人工判定。");
        Verdicts = (await Engine.GetProbeVerdictsAsync(ct)).Select(v => new VerdictItem(v)).ToArray();
        HistoryEmpty = Verdicts.Count == 0;
        return true;
    }, "没有可保存的探针。", preserveContent: true);
}
