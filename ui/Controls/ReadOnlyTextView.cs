// 用途：只读等宽文本、搜索位置选择与 WinUI 原生复制交互；不读取或写入任何文件。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentToolkit.Ui.Controls;

public sealed class ReadOnlyTextView : UserControl
{
    private readonly TextBox _editor = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        VerticalAlignment = VerticalAlignment.Stretch,
    };
    private readonly InfoBar _feedback = new() { IsClosable = false, IsOpen = false };
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(ReadOnlyTextView), new PropertyMetadata("", TextChanged));
    public static readonly DependencyProperty MatchStartProperty = DependencyProperty.Register(nameof(MatchStart), typeof(int), typeof(ReadOnlyTextView), new PropertyMetadata(0, MatchChanged));
    public static readonly DependencyProperty MatchLengthProperty = DependencyProperty.Register(nameof(MatchLength), typeof(int), typeof(ReadOnlyTextView), new PropertyMetadata(0, MatchChanged));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public int MatchStart { get => (int)GetValue(MatchStartProperty); set => SetValue(MatchStartProperty, value); }
    public int MatchLength { get => (int)GetValue(MatchLengthProperty); set => SetValue(MatchLengthProperty, value); }

    public ReadOnlyTextView()
    {
        var layout = new Grid { RowSpacing = 8 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var copy = new Button { Content = "复制全部", MinWidth = 90 };
        copy.Click += (_, _) =>
        {
            try
            {
                _editor.Focus(FocusState.Programmatic); _editor.SelectAll();
                _editor.CopySelectionToClipboard();
                _feedback.Severity = InfoBarSeverity.Informational; _feedback.Title = "已复制全部文本";
                _feedback.Message = ""; _feedback.IsOpen = true;
            }
            catch (Exception ex)
            {
                _feedback.Severity = InfoBarSeverity.Error; _feedback.Title = "复制失败";
                _feedback.Message = ex.Message; _feedback.IsOpen = true;
            }
        };
        toolbar.Children.Add(copy);
        toolbar.Children.Add(new TextBlock { Text = "选中文本后可按 Ctrl+C", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        layout.Children.Add(toolbar); Grid.SetRow(_feedback, 1); layout.Children.Add(_feedback);
        Grid.SetRow(_editor, 2); layout.Children.Add(_editor);
        ScrollViewer.SetVerticalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        Content = layout;
    }
    private static void TextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (ReadOnlyTextView)sender; view._editor.Text = (string?)args.NewValue ?? "";
        view._feedback.IsOpen = false; view.SelectMatch();
    }
    private static void MatchChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((ReadOnlyTextView)sender).SelectMatch();
    private void SelectMatch()
    {
        var start = Math.Clamp(MatchStart, 0, _editor.Text.Length);
        _editor.Select(start, Math.Clamp(MatchLength, 0, _editor.Text.Length - start));
    }
}
