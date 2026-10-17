// === SPEC §4.1 注入文本；纯展示模型，不调用引擎。
// === 跟踪 UTF-8 体积、只读限制和未保存草稿；页面四态由 PolicyPageViewModel 提供。
using AgentToolkit.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text;

namespace AgentToolkit.Ui.ViewModels;

public partial class PolicyFileItem : ObservableObject
{
    private PolicyFileInfo _source;
    private string _savedText;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Size), nameof(OverCap), nameof(HasChanges))]
    public partial string Text { get; set; } = "";
    public PolicyFileItem(PolicyFileInfo source)
    { _source = source; _savedText = source.Text; Text = source.Text; }
    public string Name => _source.Name;
    public string DisplayName => _source.DisplayName;
    public string Path => _source.Path;
    public bool Editable => _source.Editable;
    public bool IsReadOnly => !Editable;
    public bool HasChanges => !string.Equals(Text, _savedText, StringComparison.Ordinal);
    public bool OverCap => _source.CapBytes is long cap && Encoding.UTF8.GetByteCount(Text) > cap;
    public string Size => Display.Size(Encoding.UTF8.GetByteCount(Text), _source.CapBytes);
    public string Editability => Editable ? "可编辑 · " + (HasChanges ? "有未保存改动" : "已与引擎返回文本同步") : "引擎标记为只读";
    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(Editability));
    public void Update(PolicyFileInfo source, bool keepDraft)
    {
        _source = source; _savedText = source.Text;
        if (!keepDraft) Text = source.Text;
        foreach (var property in new[] { nameof(Path), nameof(DisplayName), nameof(Editable), nameof(IsReadOnly), nameof(HasChanges), nameof(OverCap), nameof(Size), nameof(Editability) }) OnPropertyChanged(property);
    }
}
