using AgentToolkit.Ui.Services;
using AgentToolkit.Ui.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace AgentToolkit.Ui;

public sealed partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;

    public MainWindow()
    {
        _shell = new ShellViewModel(ServiceLocator.CreateEngine());
        InitializeComponent();
        RootGrid.DataContext = _shell;
        AppWindow.Resize(new SizeInt32(1380, 960));
        _ = _shell.InitializeAsync();
#if DEBUG
        // SEAM: mock-only，真引擎落地后删除。
        if (_shell.Engine is MockEngine)
        {
            var scenarios = new ComboBox
            {
                Header = "模拟场景（仅调试版）", DisplayMemberPath = "Label",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = new[]
                {
                    new ScenarioOption(MockScenario.NotLocated, "未找到客户端"),
                    new ScenarioOption(MockScenario.StockClean, "出厂状态"),
                    new ScenarioOption(MockScenario.AppliedHealthy, "两层正常"),
                    new ScenarioOption(MockScenario.UpgradeOverwritten, "升级覆盖"),
                    new ScenarioOption(MockScenario.AnchorAmbiguous, "锚点不唯一"),
                    new ScenarioOption(MockScenario.BackupContaminated, "备份污染"),
                    new ScenarioOption(MockScenario.NoSessions, "无会话日志"),
                    new ScenarioOption(MockScenario.RichSession, "长会话 · 零命中"),
                },
                SelectedIndex = 2,
            };
            scenarios.SelectionChanged += Scenario_SelectionChanged;
            DebugHost.Children.Add(scenarios);
        }
#endif
        Navigation.SelectedItem = Navigation.MenuItems[0];
    }

    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string route) await _shell.LoadAsync(route);
    }

#if DEBUG
    private sealed record ScenarioOption(MockScenario Value, string Label);

    private async void Scenario_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        // SEAM: mock-only，真引擎落地后删除。
        if (_shell.Engine is MockEngine mock && ((ComboBox)sender).SelectedItem is ScenarioOption option)
        {
            _shell.Cancel();
            mock.SetScenario(option.Value);
            await _shell.RefreshAsync();
        }
    }
#endif
}
