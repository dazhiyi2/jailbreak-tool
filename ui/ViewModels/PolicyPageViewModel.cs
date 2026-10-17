// === SPEC §4.1 注入文本（用户扩展）；调用 GetPolicyFilesAsync、SavePolicyFileAsync。
// === 加载：进度；空：无文本；错误：中文错误与 Notes 且保留草稿；正常：编辑、上限与保存。
using AgentToolkit.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentToolkit.Ui.ViewModels;

public partial class PolicyPageViewModel(IAgentEngine engine) : PageViewModel(engine)
{
    private readonly Dictionary<string, PolicyFileItem> _drafts = new();
    [ObservableProperty] public partial IReadOnlyList<PolicyFileItem> Files { get; set; } = Array.Empty<PolicyFileItem>();
    [ObservableProperty] public partial PolicyFileItem? SelectedFile { get; set; }
    public bool CanSave => !IsLoading && SelectedFile is { Editable: true, HasChanges: true, OverCap: false };
    partial void OnSelectedFileChanged(PolicyFileItem? value) => SaveCommand.NotifyCanExecuteChanged();
    protected override void OnBusyChanged() => SaveCommand.NotifyCanExecuteChanged();

    public override Task LoadAsync() => LoadAsync(async ct =>
    {
        var files = await Engine.GetPolicyFilesAsync(ct);
        var selectedName = SelectedFile?.Name;
        var items = new List<PolicyFileItem>();
        var retained = false;
        foreach (var file in files)
        {
            if (_drafts.TryGetValue(file.Name, out var draft))
            { var keepDraft = draft.HasChanges; retained |= keepDraft; draft.Update(file, keepDraft); }
            else
            {
                draft = new PolicyFileItem(file);
                draft.PropertyChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();
                _drafts.Add(file.Name, draft);
            }
            items.Add(draft);
        }
        Files = items;
        SelectedFile = Files.FirstOrDefault(f => f.Name == selectedName) ?? Files.FirstOrDefault();
        SetNotes(files.SelectMany(f => f.Notes).GroupBy(n => (n.Severity, n.Title, n.Detail, n.Source)).Select(g => g.First()));
        if (retained) ShowMessage("保留未保存草稿", "刷新更新了文件信息；已编辑文本仍保留在界面内存中。");
        return Files.Count > 0;
    }, "引擎没有提供注入文本文件。");

    [RelayCommand(CanExecute = nameof(CanSave))]
    public Task SaveAsync() => LoadAsync(async ct =>
    {
        var draft = SelectedFile ?? throw new InvalidOperationException("请选择文本文件。");
        var result = await Engine.SavePolicyFileAsync(draft.Name, draft.Text, ct);
        draft.Update(result, false); SetNotes(result.Notes);
        ShowMessage("文本已保存", result.DisplayName + " · " + Display.Bytes(result.SizeBytes));
        return Files.Count > 0;
    }, "没有可保存的文件。", preserveContent: true);
}
