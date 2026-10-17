// 用途：按系统浅深色主题呈现状态、备份健康度与差异类型的文字徽章。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentToolkit.Ui.Controls;

public enum BadgeTone { Neutral, Success, Warning, Error }

public sealed class StatusBadge : UserControl
{
    private readonly TextBlock _label = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Border _border = new() { Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(5) };
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusBadge), new PropertyMetadata("", OnChanged));
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(nameof(Tone), typeof(BadgeTone), typeof(StatusBadge), new PropertyMetadata(BadgeTone.Neutral, OnChanged));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public BadgeTone Tone { get => (BadgeTone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    public StatusBadge()
    {
        _border.Child = _label;
        Content = _border;
        HorizontalAlignment = HorizontalAlignment.Left;
        Loaded += (_, _) => Update();
        ActualThemeChanged += (_, _) => Update();
    }

    private static void OnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((StatusBadge)sender).Update();
    private void Update()
    {
        _label.Text = Text;
        var theme = ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        var dictionary = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[theme];
        _border.Background = (Brush)dictionary[$"Badge{Tone}Background"];
        _label.Foreground = (Brush)dictionary[$"Badge{Tone}Foreground"];
    }
}
