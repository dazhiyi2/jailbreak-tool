// === SPEC §4.1 注入文本（用户扩展）；调用 GetPolicyFilesAsync、SavePolicyFileAsync。
// === 加载：进度；空：无文本；错误：中文错误与 Notes 且保留草稿；正常：编辑、上限与保存。
// === 双注入方案：用保留名 "scheme" 切换（own / protocol）；切换后重新拉取并按当前方案渲染，
// === 载体槽可编辑可保存；空槽逐条列出并说明「该方案下此槽位不注入」。
using AgentToolkit.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentToolkit.Ui.ViewModels;

/// <summary>方案下拉的一项（契约里 scheme 是自由字符串，UI 只给两个合法值）。</summary>
public sealed class SchemeOption
{
    public required string Value { get; init; }
    public required string Label { get; init; }
    public override string ToString() => Label;
}

public partial class PolicyPageViewModel(IAgentEngine engine) : PageViewModel(engine)
{
    private readonly Dictionary<string, PolicyFileItem> _drafts = new();
    private string _currentScheme = "own";
    [ObservableProperty] public partial IReadOnlyList<PolicyFileItem> Files { get; set; } = Array.Empty<PolicyFileItem>();
    [ObservableProperty] public partial PolicyFileItem? SelectedFile { get; set; }
    [ObservableProperty] public partial IReadOnlyList<SchemeOption> SchemeOptions { get; set; } = new[]
    {
        new SchemeOption { Value = "own", Label = "本工具方案（own）" },
        new SchemeOption { Value = "protocol", Label = "gpt-instruct 方案（protocol）" },
    };
    [ObservableProperty] public partial SchemeOption? SelectedScheme { get; set; }
    [ObservableProperty] public partial string SchemeSummary { get; set; } = "当前方案：own（本工具方案）";
    [ObservableProperty] public partial string EmptySlotsText { get; set; } = "";
    [ObservableProperty] public partial bool HasEmptySlots { get; set; }
    public bool CanSave => !IsLoading && SelectedFile is { Editable: true, HasChanges: true, OverCap: false };
    public bool CanSwitchScheme => !IsLoading && SelectedScheme is not null &&
                                   !string.Equals(SelectedScheme.Value, _currentScheme, StringComparison.Ordinal);
    partial void OnSelectedFileChanged(PolicyFileItem? value) => SaveCommand.NotifyCanExecuteChanged();
    partial void OnSelectedSchemeChanged(SchemeOption? value) => SwitchSchemeCommand.NotifyCanExecuteChanged();
    protected override void OnBusyChanged()
    {
        SaveCommand.NotifyCanExecuteChanged();
        SwitchSchemeCommand.NotifyCanExecuteChanged();
    }

    public override Task LoadAsync() => LoadAsync(async ct =>
    {
        var files = await Engine.GetPolicyFilesAsync(ct);
        var selectedName = SelectedFile?.Name;
        var schemeEntry = files.FirstOrDefault(f => string.Equals(f.Name, "scheme", StringComparison.OrdinalIgnoreCase));
        _currentScheme = schemeEntry?.Text.Trim().ToLowerInvariant() == "protocol" ? "protocol" : "own";
        var items = new List<PolicyFileItem>();
        var retained = false;
        foreach (var file in files)
        {
            var key = _currentScheme + ":" + file.Name;
            if (_drafts.TryGetValue(key, out var draft))
            { var keepDraft = draft.HasChanges; retained |= keepDraft; draft.Update(file, keepDraft); }
            else
            {
                draft = new PolicyFileItem(file);
                draft.PropertyChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();
                _drafts.Add(key, draft);
            }
            items.Add(draft);
        }
        Files = items;

        var empties = files
            .Where(f => !f.Editable && !string.Equals(f.Name, "scheme", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.DisplayName)
            .ToArray();
        HasEmptySlots = empties.Length > 0;
        EmptySlotsText = empties.Length == 0
            ? "该方案没有空槽：所有槽位都注入。"
            : "该方案下这些槽位不注入（空槽不写文件、不产生计划变更）：" + string.Join("；", empties);

        SchemeSummary = _currentScheme == "protocol"
            ? "当前方案：gpt-instruct 方案（protocol）——载体槽写入原文（逐字节不动）"
            : "当前方案：本工具方案（own）——现有注入文本，一个字未改";
        SelectedScheme = SchemeOptions.FirstOrDefault(o => o.Value == _currentScheme) ?? SchemeOptions.FirstOrDefault();

        SelectedFile = Files.FirstOrDefault(f => f.Name == selectedName && !string.Equals(f.Name, "scheme", StringComparison.OrdinalIgnoreCase))
                       ?? Files.FirstOrDefault(f => !string.Equals(f.Name, "scheme", StringComparison.OrdinalIgnoreCase))
                       ?? Files.FirstOrDefault();
        SetNotes(files.SelectMany(f => f.Notes).GroupBy(n => (n.Severity, n.Title, n.Detail, n.Source)).Select(g => g.First()));
        if (retained) ShowMessage("保留未保存草稿", "刷新更新了文件信息；已编辑文本仍保留在界面内存中。");
        return Files.Count > 0;
    }, "引擎没有提供注入文本文件。");

    /// <summary>切换方案 = SavePolicyFileAsync("scheme", ...) → 重新 GetPolicyFilesAsync → 按新方案渲染。</summary>
    [RelayCommand(CanExecute = nameof(CanSwitchScheme))]
    public async Task SwitchSchemeAsync()
    {
        var option = SelectedScheme ?? throw new InvalidOperationException("请选择注入方案。");
        await Engine.SavePolicyFileAsync("scheme", option.Value);
        await LoadAsync();
        ShowMessage("注入方案已切换", SchemeSummary + "。注入 / 预览 / 还原 / 状态都作用于当前方案。");
    }

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
